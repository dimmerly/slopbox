using System;
using Prowl.Runtime;
using Prowl.Vector;
using Jitter2.Dynamics;

namespace Slopbox;

/// <summary>
/// Source engine walking. The structure follows CGameMovement: jump, then friction and acceleration on
/// the ground or air acceleration off it, then gravity, then the move and velocity clipping.
/// </summary>
public sealed class WalkMode : MoveMode
{
    public override string Name => "Walk";

    private float _sinceJumpPressed = float.MaxValue;

    public override void OnEnter(MoveMode previous)
    {
        if (Controller.Body.IsValid()) Controller.Body.Enabled = true;
    }

    public override void Simulate(in MoveInput input, float dt)
    {
        PlayerController c = Controller;
        CharacterController body = c.Body;
        Float3 vel = c.Velocity;

        bool wasGrounded = body.IsGrounded;
        float fallSpeed = -vel.Y;

        UpdateDuck(input.Duck, wasGrounded);

        // Wish direction and speed.
        Float3 wish = YawForward(input.Yaw) * input.Move.Y + YawRight(input.Yaw) * input.Move.X;
        float amount = MathF.Min(1f, Float3.Length(wish));
        Float3 wishDir = amount > 1e-4f ? Float3.Normalize(wish) : Float3.Zero;

        float maxSpeed = input.Sprint ? c.SprintSpeed : input.Walk ? c.SlowWalkSpeed : c.NormalSpeed;
        if (c.IsDucked) maxSpeed *= c.DuckSpeedScale;
        float wishSpeed = maxSpeed * amount;

        // Jump. A press is remembered briefly, so one made just before landing still counts.
        _sinceJumpPressed = input.JumpPressed ? 0f : _sinceJumpPressed + dt;
        bool grounded = wasGrounded;
        if (grounded && _sinceJumpPressed <= c.JumpBuffer)
        {
            vel.Y = MathF.Sqrt(2f * c.Gravity * c.JumpHeight);
            grounded = false;
            _sinceJumpPressed = float.MaxValue;
            c.RaiseJumped();
        }

        if (grounded)
        {
            vel.Y = 0f;
            ApplyFriction(ref vel, dt);
            Accelerate(ref vel, wishDir, wishSpeed, c.Acceleration, dt);
        }
        else
        {
            AirAccelerate(ref vel, wishDir, wishSpeed, dt);
            vel.Y -= c.Gravity * dt;
        }

        CharacterController.CollisionFlags flags = body.Move(vel * dt);

        if ((flags & CharacterController.CollisionFlags.Above) != 0 && vel.Y > 0f) vel.Y = 0f;
        if ((flags & CharacterController.CollisionFlags.Sides) != 0) ClipAgainstWalls(ref vel, body);

        if (body.IsGrounded)
        {
            if (!wasGrounded && fallSpeed > 0f) c.RaiseLanded(fallSpeed);
            if (vel.Y < 0f) vel.Y = 0f;
        }

        PushBodies(body, vel, dt);
        c.Velocity = vel;
    }

    // ----------------------------------------------------------------
    //  Source movement maths
    // ----------------------------------------------------------------

    private void ApplyFriction(ref Float3 vel, float dt)
    {
        float speed = MathF.Sqrt(vel.X * vel.X + vel.Z * vel.Z);
        if (speed < 1e-3f)
        {
            vel.X = vel.Z = 0f;
            return;
        }

        float control = MathF.Max(speed, Controller.StopSpeed);
        float drop = control * Controller.Friction * dt;
        float scale = MathF.Max(speed - drop, 0f) / speed;
        vel.X *= scale;
        vel.Z *= scale;
    }

    private static void Accelerate(ref Float3 vel, Float3 wishDir, float wishSpeed, float accel, float dt)
    {
        float current = Float3.Dot(vel, wishDir);
        float add = wishSpeed - current;
        if (add <= 0f) return;

        float step = MathF.Min(accel * wishSpeed * dt, add);
        vel += wishDir * step;
    }

    private void AirAccelerate(ref Float3 vel, Float3 wishDir, float wishSpeed, float dt)
    {
        // The cap applies to how far along wishDir speed may be added, not to the speed itself. Turning
        // the view while strafing keeps that projection small, which is how air strafing gains speed.
        float capped = MathF.Min(wishSpeed, Controller.AirSpeedCap);
        float current = Float3.Dot(vel, wishDir);
        float add = capped - current;
        if (add <= 0f) return;

        float step = MathF.Min(Controller.AirAcceleration * wishSpeed * dt, add);
        vel += wishDir * step;
    }

    /// <summary>Removes the part of the velocity going into the walls touched this move, so the player slides along them.</summary>
    private static void ClipAgainstWalls(ref Float3 vel, CharacterController body)
    {
        foreach (ShapeCastHit hit in body.Hits)
        {
            Float3 n = hit.Normal;
            if (MathF.Abs(n.Y) > 0.7f) continue;   // floors and ceilings are handled above
            n = Float3.Normalize(new Float3(n.X, 0f, n.Z));
            float into = Float3.Dot(vel, n);
            if (into < 0f) vel -= n * into;
        }
    }

    // ----------------------------------------------------------------
    //  Ducking
    // ----------------------------------------------------------------

    private void UpdateDuck(bool wantDuck, bool grounded)
    {
        PlayerController c = Controller;
        CharacterController body = c.Body;
        float diff = c.StandHeight - c.DuckHeight;

        if (wantDuck && !c.IsDucked)
        {
            if (grounded)
            {
                if (body.TrySetHeight(c.DuckHeight)) c.IsDucked = true;
                return;
            }

            // In the air the legs tuck up: the top of the hull stays put and the feet rise. The eye
            // stays where it was in the world, which is what makes a crouch-jump clear higher ledges.
            if (body.TrySetHeight(c.DuckHeight))
            {
                body.Teleport(c.Transform.Position + Float3.UnitY * diff);
                c.EyeHeight -= diff;
                c.IsDucked = true;
            }
        }
        else if (!wantDuck && c.IsDucked)
        {
            if (!grounded && !body.Cast(-Float3.UnitY, diff, out _))
            {
                // Room below: drop the feet back down rather than growing upward.
                Float3 before = c.Transform.Position;
                body.Teleport(before - Float3.UnitY * diff);
                if (body.TrySetHeight(c.StandHeight))
                {
                    c.EyeHeight += diff;
                    c.IsDucked = false;
                    return;
                }
                body.Teleport(before);
            }

            if (body.TrySetHeight(c.StandHeight)) c.IsDucked = false;
        }
    }

    // ----------------------------------------------------------------
    //  Props
    // ----------------------------------------------------------------

    /// <summary>The engine controller never pushes bodies, so shove what we walked into.</summary>
    private void PushBodies(CharacterController body, Float3 vel, float dt)
    {
        if (Controller.PushForce <= 0f) return;

        foreach (ShapeCastHit hit in body.Hits)
        {
            Rigidbody3D rb = hit.Rigidbody;
            if (rb.IsNotValid() || rb.MotionType != MotionType.Dynamic) continue;

            Float3 push = new(-hit.Normal.X, 0f, -hit.Normal.Z);
            float len = Float3.Length(push);
            if (len < 1e-3f) continue;
            push /= len;

            float into = Float3.Dot(new Float3(vel.X, 0f, vel.Z), push);
            if (into <= 0f) continue;

            rb.ApplyImpulse(push * (Controller.PushForce * dt), hit.Point);
        }
    }
}
