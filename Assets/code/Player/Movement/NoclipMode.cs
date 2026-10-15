using System;
using Prowl.Vector;

namespace Slopbox;

/// <summary>Flies through everything along the view. Sprint is fast, walk is slow, jump and duck rise and sink.</summary>
public sealed class NoclipMode : MoveMode
{
    public override string Name => "Noclip";

    private const float Accel = 8f;

    public override void OnEnter(MoveMode previous)
    {
        if (Controller.IsDucked && Controller.Body.TrySetHeight(Controller.StandHeight)) Controller.IsDucked = false;
        Controller.Body.Enabled = false;
    }

    public override void OnExit(MoveMode next)
    {
        Controller.Body.Enabled = true;
        // Push back out of whatever we stopped inside.
        Controller.Body.Teleport(Controller.Transform.Position);
    }

    public override void Simulate(in MoveInput input, float dt)
    {
        PlayerController c = Controller;

        float yaw = input.Yaw * Maths.Deg2Rad, pitch = input.Pitch * Maths.Deg2Rad;
        Float3 forward = new(MathF.Sin(yaw) * MathF.Cos(pitch), -MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
        Float3 wish = forward * input.Move.Y + YawRight(input.Yaw) * input.Move.X + Float3.UnitY * input.Vertical;
        if (Float3.LengthSquared(wish) > 1f) wish = Float3.Normalize(wish);

        float speed = input.Sprint ? c.NoclipFastSpeed : input.Walk ? c.NoclipSlowSpeed : c.NoclipSpeed;
        c.Velocity += (wish * speed - c.Velocity) * MathF.Min(1f, Accel * dt);
        c.Transform.Position += c.Velocity * dt;
    }
}
