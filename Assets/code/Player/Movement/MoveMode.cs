using System;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// One way of moving: walking, noclip, and later ladders, swimming or vehicles. The controller owns the
/// shared state (velocity, duck) and hands each frame to whichever mode is active.
/// </summary>
public abstract class MoveMode
{
    public PlayerController Controller { get; internal set; }

    public virtual string Name => GetType().Name;

    /// <summary>Called when this mode takes over. <paramref name="previous"/> is null on the first mode.</summary>
    public virtual void OnEnter(MoveMode previous) { }

    public virtual void OnExit(MoveMode next) { }

    /// <summary>Advances the movement by <paramref name="dt"/> seconds.</summary>
    public abstract void Simulate(in MoveInput input, float dt);

    /// <summary>The flat forward direction for a view yaw in degrees.</summary>
    protected static Float3 YawForward(float yaw)
    {
        float r = yaw * Maths.Deg2Rad;
        return new Float3(MathF.Sin(r), 0f, MathF.Cos(r));
    }

    protected static Float3 YawRight(float yaw)
    {
        float r = yaw * Maths.Deg2Rad;
        return new Float3(MathF.Cos(r), 0f, -MathF.Sin(r));
    }
}
