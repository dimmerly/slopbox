using System;
using Prowl.Runtime;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// Moves a player like Half-Life 2: Source's ground friction and acceleration, air acceleration you can
/// strafe with, crouch-jumping that tucks the legs up, and a stepped, slope-limited hull. Collision is
/// the engine's <see cref="CharacterController"/>; this owns velocity and decides what to do with it.
/// <para>Feed it a <see cref="MoveInput"/> through <see cref="Simulate"/> once per frame. How it moves is
/// up to the active <see cref="MoveMode"/>.</para>
/// </summary>
[RequireComponent(typeof(CharacterController))]
[DisallowMultipleComponent]
[AddComponentMenu("Slopbox/Player Controller")]
public sealed class PlayerController : Component
{
    [Header("Speeds (m/s)")]
    public float NormalSpeed = 4.5f;
    public float SprintSpeed = 7.5f;
    public float SlowWalkSpeed = 2f;
    [Range(0f, 1f)] public float DuckSpeedScale = 0.34f;

    [Header("Source movement")]
    public float Acceleration = 10f;
    public float AirAcceleration = 10f;
    public float Friction = 6f;
    [Tooltip("Below this speed friction acts as if moving at it, so stopping is snappy.")]
    public float StopSpeed = 1.9f;
    [Tooltip("Air acceleration only adds speed up to this along the wish direction. Small values are what make air strafing work.")]
    public float AirSpeedCap = 0.6f;
    public float Gravity = 15f;
    public float JumpHeight = 0.85f;
    [Tooltip("A jump pressed this long before landing still fires on landing.")]
    public float JumpBuffer = 0.1f;

    [Header("Hull (m)")]
    public float Radius = 0.4f;
    public float StandHeight = 1.83f;
    public float DuckHeight = 1.05f;
    public float StandEyeHeight = 1.65f;
    public float DuckEyeHeight = 0.88f;
    public float StepSize = 0.4f;
    public float MaxSlope = 46f;
    [Tooltip("How fast the eye height follows a duck or stand, per second.")]
    public float EyeSpeed = 10f;

    [Header("Noclip (m/s)")]
    public float NoclipSpeed = 10f;
    public float NoclipFastSpeed = 30f;
    public float NoclipSlowSpeed = 3f;

    [Header("Props")]
    [Tooltip("Force in newtons used to shove dynamic bodies the player walks into.")]
    public float PushForce = 500f;

    public CharacterController Body { get; private set; }
    public Float3 Velocity;
    public bool IsDucked { get; internal set; }
    public bool IsGrounded => Body.IsValid() && Body.Enabled && Body.IsGrounded;

    /// <summary>The eye's height above the feet, eased between standing and ducked.</summary>
    public float EyeHeight { get; internal set; }

    public Float3 EyePosition => Transform.Position + Float3.UnitY * EyeHeight;

    public MoveMode Mode { get; private set; }
    public WalkMode Walk { get; private set; }
    public NoclipMode Noclip { get; private set; }

    /// <summary>Raised on touching down, with the speed the player was falling at.</summary>
    public event Action<float> Landed;

    /// <summary>Raised when a jump leaves the ground.</summary>
    public event Action Jumped;

    public override void OnEnable()
    {
        Body = GetComponent<CharacterController>();
        ApplyHull();

        Walk ??= new WalkMode();
        Noclip ??= new NoclipMode();
        if (Mode == null) SetMode(Walk);
        if (EyeHeight <= 0f) EyeHeight = StandEyeHeight;
    }

    public override void OnValidate()
    {
        if (Body.IsValid()) ApplyHull();
    }

    private void ApplyHull()
    {
        Body.Radius = Radius;
        Body.Height = IsDucked ? DuckHeight : StandHeight;
        Body.StepSize = StepSize;
        Body.MaxSlopeAngle = MaxSlope;
        // Source only snaps down onto stairs, it does not glue the player to the ground off ledges.
        Body.SnapDownDistance = StepSize;
    }

    public void SetMode(MoveMode mode)
    {
        if (mode == Mode) return;
        MoveMode previous = Mode;
        previous?.OnExit(mode);
        Mode = mode;
        mode.Controller = this;
        mode.OnEnter(previous);
    }

    public void ToggleNoclip()
    {
        SetMode(Mode == Noclip ? Walk : Noclip);
        GameAudio.Play("mode", 0.3f, Mode == Noclip ? 1.3f : 0.9f);
    }

    /// <summary>Runs one frame of movement for <paramref name="input"/>.</summary>
    public void Simulate(in MoveInput input, float dt)
    {
        if (dt <= 0f || Mode == null) return;
        Mode.Simulate(input, dt);

        float targetEye = IsDucked ? DuckEyeHeight : StandEyeHeight;
        EyeHeight += (targetEye - EyeHeight) * MathF.Min(1f, EyeSpeed * dt);
    }

    /// <summary>Puts the player somewhere, standing and at rest.</summary>
    public void Teleport(Float3 position)
    {
        SetMode(Walk);
        Velocity = Float3.Zero;
        if (IsDucked && Body.TrySetHeight(StandHeight)) IsDucked = false;
        Body.Teleport(position);
        EyeHeight = IsDucked ? DuckEyeHeight : StandEyeHeight;
    }

    internal void RaiseLanded(float speed) => Landed?.Invoke(speed);
    internal void RaiseJumped() => Jumped?.Invoke();
}
