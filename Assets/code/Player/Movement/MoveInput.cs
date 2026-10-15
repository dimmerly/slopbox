using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// What the mover is asked to do this frame. The <see cref="Player"/> fills it from the keyboard, but
/// anything (a bot, a replay, a network snapshot) can drive a <see cref="PlayerController"/> the same way.
/// </summary>
public struct MoveInput
{
    /// <summary>X is strafe right, Y is forward, each -1 to 1.</summary>
    public Float2 Move;

    /// <summary>View angles in degrees. Pitch is positive looking down.</summary>
    public float Yaw, Pitch;

    public bool Jump, JumpPressed;
    public bool Duck;
    public bool Sprint;
    public bool Walk;

    /// <summary>Used by fly modes such as noclip: +1 up (jump), -1 down (duck).</summary>
    public float Vertical;
}
