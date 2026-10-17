using System;
using System.Collections.Generic;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// Garry's Mod's physics gun. Hold left mouse to grab any physics object, which follows the aim at the
/// distance it was grabbed and turns with the player. The wheel pushes and pulls, E with the mouse
/// rotates it (Shift snaps to 45 degrees), right mouse freezes it in place and R unfreezes what you aim at.
/// </summary>
public sealed class Physgun : Equipment
{
    public float Range = 200f;
    public float MinDistance = 0.6f;

    /// <summary>Rotate mode speed, relative to mouse look. 1 turns the object as fast as the view would turn.</summary>
    public float RotateSpeed = 1f;

    private static readonly Color BeamColor = new(0.3f, 1.1f, 3f, 1f);   // over 1 so it blooms

    private readonly PhysicsHold _hold = new() { Stiffness = 0.25f, AngularStiffness = 0.2f, MaxSpeed = 35f, MaxAngularSpeed = 12f };
    private float _distance;
    private Quaternion _relativeRotation = Quaternion.Identity;   // the body's rotation relative to the player's yaw
    private bool _rotating;
    private Beam _beam;
    private Material _bodyMat, _darkMat, _glowMat;

    public override string Name => "Physics Gun";
    public override string Description => "Pick up, move, rotate and freeze physics objects.";
    public override Color Tint => new(0.35f, 0.75f, 1f, 1f);
    public override bool CapturesScroll => _hold.IsHolding;
    public bool IsHolding => _hold.IsHolding;

    public override IEnumerable<InputHint> Hints => _hold.IsHolding
        ? [new("RMB", "Freeze"), new("Wheel", "Push / pull"), new("E + Mouse", "Rotate"), new("Shift", "Snap rotation")]
        : [new("LMB", "Grab"), new("R", "Unfreeze")];

    public override void OnHolster()
    {
        Drop();
        _beam?.Hide();
    }

    public override void BuildViewModel(ViewModel vm)
    {
        if (_bodyMat.IsNotValid()) _bodyMat = Materials.Lit(new Color(0.12f, 0.13f, 0.15f, 1f), 0.6f, 0.35f);
        if (_darkMat.IsNotValid()) _darkMat = Materials.Lit(new Color(0.03f, 0.03f, 0.035f, 1f), 0.2f, 0.5f);
        if (_glowMat.IsNotValid()) _glowMat = Materials.Lit(new Color(0f, 0f, 0f, 1f)).Emissive(new Color(0.2f, 0.6f, 1f, 1f), 5f);

        vm.AddPart(Shapes.Cube, _bodyMat, new Float3(0f, 0f, 0f), new Float3(0.08f, 0.1f, 0.3f));
        vm.AddPart(Shapes.Cube, _darkMat, new Float3(0f, -0.07f, -0.08f), new Float3(0.05f, 0.1f, 0.06f), new Float3(-15f, 0f, 0f));
        vm.AddPart(Shapes.Cylinder, _glowMat, new Float3(0f, 0.01f, 0.12f), new Float3(0.05f, 0.14f, 0.05f), new Float3(90f, 0f, 0f));
        for (int i = 0; i < 3; i++)
        {
            float a = i * MathF.PI * 2f / 3f;
            Float3 at = new(MathF.Cos(a) * 0.04f, MathF.Sin(a) * 0.04f + 0.01f, 0.21f);
            vm.AddPart(Shapes.Cube, _darkMat, at, new Float3(0.015f, 0.015f, 0.09f));
        }
        vm.SetMuzzle(new Float3(0f, 0.01f, 0.25f));
        vm.SetGrip(new Float3(0f, -0.09f, -0.08f), new Float3(0f, -0.05f, 0.09f));
    }

    public override void Update()
    {
        if (!Owner.InputEnabled)
        {
            Drop();
            return;
        }

        if (!_hold.IsHolding)
        {
            if (Attack1Pressed) TryGrab();
            if (ReloadPressed) Unfreeze();
            return;
        }

        if (!Attack1Down)
        {
            Drop();
            return;
        }

        float wheel = Input.MouseWheelDelta;
        if (wheel != 0f)
        {
            _distance = Math.Clamp(_distance + wheel * MathF.Max(0.25f, _distance * 0.08f), MinDistance, Range);
            GameAudio.Play("hover", 0.1f, wheel > 0f ? 1.3f : 0.9f, cooldown: 0.1f);
        }

        _rotating = UseDown;
        if (_rotating)
        {
            Owner.LookLocked = true;
            Rotate(Input.MouseDelta);
        }

        if (Attack2Pressed) Freeze();
    }

    public override void FixedUpdate()
    {
        if (!_hold.IsHolding) return;
        Float3 target = Owner.EyePosition + Owner.Forward * _distance;
        _hold.Step(target, TargetRotation(), Time.FixedDeltaTime);
    }

    public override void LateUpdate()
    {
        GameAudio.SetLoop(this, "hum", _hold.IsHolding && Owner.InputEnabled);
        if (!_hold.IsHolding)
        {
            _beam?.Hide();
            return;
        }

        if (_beam == null || !_beam.IsValid) _beam = new Beam("Physgun Beam", BeamColor, 0.025f);

        // Bends from the muzzle along the aim toward the held point, the gmod look.
        Float3 muzzle = Owner.MuzzlePosition;
        Float3 end = _hold.WorldAnchor;
        Float3 control = muzzle + Owner.Forward * (Float3.Distance(muzzle, end) * 0.6f);
        _beam.SetWidth(0.02f, 0.006f);
        _beam.Curve(muzzle, control, end);
    }

    // ----------------------------------------------------------------

    private Quaternion YawRotation => Quaternion.FromEuler(0f, Owner.Yaw, 0f);

    private Quaternion TargetRotation()
    {
        Quaternion world = YawRotation * _relativeRotation;
        if (_rotating && Input.IsShiftPressed) world = Snap(world, 45f);
        return world;
    }

    private void TryGrab()
    {
        if (!Owner.Trace(Range, out RaycastHit hit) || hit.Rigidbody.IsNotValid())
            return;
        Rigidbody3D body = hit.Rigidbody;

        _hold.Grab(body, hit.Point);
        _distance = MathF.Max(hit.Distance, MinDistance);
        _relativeRotation = Quaternion.Inverse(YawRotation) * body.Rotation;
    }

    private void Drop()
    {
        if (!_hold.IsHolding) return;

        // Letting go mid-snap keeps the snapped angle.
        if (_rotating && Input.IsShiftPressed) _relativeRotation = Quaternion.Inverse(YawRotation) * TargetRotation();
        _hold.Release();
        GameAudio.SetLoop(this, "hum", false);
        _rotating = false;
    }

    private void Freeze()
    {
        Rigidbody3D body = _hold.Body;
        _hold.Release(keepVelocity: false);
        GameAudio.SetLoop(this, "hum", false);
        _rotating = false;
        if (body.IsValid()) Prop.Of(body).SetFrozen(true);
        Owner.Kick(0.4f);
    }

    private void Unfreeze()
    {
        if (!Owner.Trace(Range, out RaycastHit hit) || hit.Rigidbody.IsNotValid()) return;
        Prop prop = Prop.Of(hit.Rigidbody);
        if (!prop.IsFrozen) return;

        prop.SetFrozen(false);
        Notify.Info("Unfroze 1 prop");
    }

    private void Rotate(Float2 mouse)
    {
        // Same scale as mouse look, so it follows the player's sensitivity setting.
        float radiansPerPixel = 0.022f * GameManager.Instance.Settings.Sensitivity * RotateSpeed * Maths.Deg2Rad;
        Quaternion world = YawRotation * _relativeRotation;
        Float3 right = Owner.EyeRotation * Float3.UnitX;

        // Negative angles so the face toward the player follows the mouse: drag right and it turns right,
        // drag down and it tips toward you.
        world = Quaternion.AxisAngle(Float3.UnitY, -mouse.X * radiansPerPixel)
              * Quaternion.AxisAngle(right, -mouse.Y * radiansPerPixel)
              * world;
        _relativeRotation = Quaternion.Normalize(Quaternion.Inverse(YawRotation) * world);
    }

    /// <summary>Rounds each Euler angle to a multiple of <paramref name="step"/> degrees.</summary>
    private static Quaternion Snap(Quaternion rotation, float step)
    {
        Float3 euler = rotation.EulerAngles;
        euler = new Float3(MathF.Round(euler.X / step) * step, MathF.Round(euler.Y / step) * step, MathF.Round(euler.Z / step) * step);
        return Quaternion.FromEuler(euler);
    }
}
