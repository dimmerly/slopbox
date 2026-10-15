using System;
using Jitter2.Dynamics;
using Prowl.Runtime;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// Holds a rigidbody so a point on it follows a target and it turns toward a target rotation, by
/// setting its velocities each physics step. Because it drives velocity rather than position, the body
/// still collides and pushes things. Used by the physgun and gravity gun.
/// </summary>
public sealed class PhysicsHold
{
    /// <summary>Fraction of the remaining position error closed per step. 1 snaps, small values feel heavy.</summary>
    public float Stiffness = 0.5f;

    /// <summary>Like <see cref="Stiffness"/>, for turning toward the target rotation.</summary>
    public float AngularStiffness = 0.5f;
    public float MaxSpeed = 60f;
    public float MaxAngularSpeed = 40f;

    public Rigidbody3D Body { get; private set; }
    public bool IsHolding => Body.IsValid() && !Body.GameObject.IsDestroyQueued;

    /// <summary>The held point, in the body's local space.</summary>
    public Float3 LocalAnchor { get; private set; }

    public Float3 WorldAnchor => Body.Transform.Position + Body.Transform.Rotation * LocalAnchor;

    private bool _hadGravity;

    public void Grab(Rigidbody3D body, Float3 worldPoint)
    {
        Release();
        Body = body;
        LocalAnchor = Quaternion.Inverse(body.Rotation) * (worldPoint - body.Position);

        // A frozen prop comes loose when grabbed, as in gmod.
        if (body.MotionType != MotionType.Dynamic) body.MotionType = MotionType.Dynamic;

        _hadGravity = body.AffectedByGravity;
        body.AffectedByGravity = false;
        body.SetActive(true);
    }

    /// <summary>Lets go. With <paramref name="keepVelocity"/> false the body stops dead.</summary>
    public void Release(bool keepVelocity = true)
    {
        if (Body.IsValid())
        {
            Body.AffectedByGravity = _hadGravity;
            if (!keepVelocity && Body.MotionType == MotionType.Dynamic)
            {
                Body.LinearVelocity = Float3.Zero;
                Body.AngularVelocity = Float3.Zero;
            }
            Body.SetActive(true);
        }
        Body = null;
    }

    /// <summary>Drives the body for one physics step so <see cref="LocalAnchor"/> reaches <paramref name="anchorTarget"/>.</summary>
    public void Step(Float3 anchorTarget, Quaternion rotationTarget, float dt)
    {
        if (!IsHolding || dt <= 0f) return;
        Rigidbody3D body = Body;
        if (body.MotionType != MotionType.Dynamic) return;

        Float3 positionTarget = anchorTarget - rotationTarget * LocalAnchor;
        Float3 velocity = (positionTarget - body.Position) * (Stiffness / dt);
        body.LinearVelocity = ClampLength(velocity, MaxSpeed);

        Quaternion delta = rotationTarget * Quaternion.Inverse(body.Rotation);
        if (delta.W < 0f) delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
        float angle = 2f * MathF.Acos(Math.Clamp(delta.W, -1f, 1f));
        float sin = MathF.Sqrt(MathF.Max(0f, 1f - delta.W * delta.W));
        Float3 angular = sin > 1e-4f
            ? new Float3(delta.X, delta.Y, delta.Z) / sin * (angle * AngularStiffness / dt)
            : Float3.Zero;
        body.AngularVelocity = ClampLength(angular, MaxAngularSpeed);

        body.SetActive(true);
    }

    private static Float3 ClampLength(Float3 v, float max)
    {
        float length = Float3.Length(v);
        return length > max ? v * (max / length) : v;
    }
}
