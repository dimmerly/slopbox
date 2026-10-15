using System;
using System.Collections.Generic;
using Jitter2.Dynamics;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// Half-Life 2's gravity gun. Right mouse pulls a light object in and holds it in front of you, left
/// mouse punts whatever is held or close. Heavy and frozen objects are too much for it.
/// </summary>
public sealed class GravityGun : Equipment
{
    public float MaxMass = 250f;
    public float PullRange = 15f;
    public float PuntRange = 5f;
    public float PuntSpeed = 18f;
    public float PullAcceleration = 30f;

    private readonly PhysicsHold _hold = new() { Stiffness = 0.35f, MaxSpeed = 25f, MaxAngularSpeed = 15f };
    private Quaternion _relativeRotation = Quaternion.Identity;
    private float _holdDistance;
    private Rigidbody3D _pulling;
    private float _sincePunt = 10f;
    private Material _bodyMat, _clawMat, _coreMat;

    public override string Name => "Gravity Gun";
    public override string Description => "Pull, carry and launch physics objects.";
    public override Color Tint => new(1f, 0.55f, 0.1f, 1f);

    public override IEnumerable<InputHint> Hints => _hold.IsHolding
        ? [new("LMB", "Launch"), new("RMB", "Drop")]
        : [new("LMB", "Punt"), new("RMB", "Pull")];

    public override void OnHolster()
    {
        _hold.Release();
        _pulling = null;
        GameAudio.SetLoop(this, "hum", false);
    }

    public override void LateUpdate()
        => GameAudio.SetLoop(this, "hum", Owner.InputEnabled && (_hold.IsHolding || _pulling.IsValid()), gain: 0.1f);

    public override void BuildViewModel(ViewModel vm)
    {
        if (_bodyMat.IsNotValid()) _bodyMat = Materials.Lit(new Color(0.5f, 0.25f, 0.05f, 1f), 0.5f, 0.4f);
        if (_clawMat.IsNotValid()) _clawMat = Materials.Lit(new Color(0.05f, 0.05f, 0.05f, 1f), 0.8f, 0.3f);
        if (_coreMat.IsNotValid()) _coreMat = Materials.Lit(new Color(0f, 0f, 0f, 1f)).Emissive(new Color(1f, 0.5f, 0.1f, 1f), 4f);

        vm.AddPart(Shapes.Cube, _bodyMat, new Float3(0f, 0f, 0f), new Float3(0.11f, 0.1f, 0.28f));
        vm.AddPart(Shapes.Sphere, _coreMat, new Float3(0f, 0f, 0.15f), new Float3(0.05f, 0.05f, 0.05f));
        for (int i = 0; i < 3; i++)
        {
            float a = i * MathF.PI * 2f / 3f + MathF.PI / 2f;
            Float3 at = new(MathF.Cos(a) * 0.05f, MathF.Sin(a) * 0.05f, 0.2f);
            vm.AddPart(Shapes.Cube, _clawMat, at, new Float3(0.02f, 0.02f, 0.12f), new Float3(-MathF.Sin(a) * 15f, MathF.Cos(a) * 15f, 0f));
        }
        vm.SetMuzzle(new Float3(0f, 0f, 0.25f));
    }

    public override void Update()
    {
        _sincePunt += Time.DeltaTime;
        _pulling = null;
        if (!Owner.InputEnabled) return;

        if (_hold.IsHolding)
        {
            if (Attack1Pressed) Launch();
            else if (Attack2Pressed)
            {
                _hold.Release();
                GameAudio.Play("release", 0.25f, 0.8f);
            }
            return;
        }

        if (Attack2Pressed && (!Owner.Trace(PullRange, out RaycastHit pullHit) || !CanMove(pullHit.Rigidbody)))
            GameAudio.Play("error", 0.2f, 0.6f);
        if (Attack1Pressed) Punt();
        else if (Attack2Down && _sincePunt > 0.3f) Pull();
    }

    public override void FixedUpdate()
    {
        if (_hold.IsHolding)
        {
            Float3 target = Owner.EyePosition + Owner.Forward * _holdDistance;
            _hold.Step(target, Quaternion.FromEuler(0f, Owner.Yaw, 0f) * _relativeRotation, Time.FixedDeltaTime);
            return;
        }

        // Drag toward the player while pulling, slowing as it arrives.
        if (_pulling.IsValid())
        {
            Float3 toEye = Owner.EyePosition - _pulling.Position;
            _pulling.AddForce(Float3.Normalize(toEye) * (PullAcceleration * _pulling.Mass));
            _pulling.SetActive(true);
        }
    }

    // ----------------------------------------------------------------

    private bool CanMove(Rigidbody3D body) => body.IsValid() && body.MotionType == MotionType.Dynamic && body.Mass <= MaxMass;

    private void Pull()
    {
        if (!Owner.Trace(PullRange, out RaycastHit hit) || !CanMove(hit.Rigidbody)) return;
        Rigidbody3D body = hit.Rigidbody;

        if (hit.Distance > 2.5f)
        {
            _pulling = body;
            return;
        }

        // Close enough to catch: hold it just beyond its own size, centred on the aim.
        AABB bounds = Spawner.LocalBounds(body.GameObject);
        _holdDistance = 0.9f + Float3.Length(bounds.Extents);
        _hold.Grab(body, body.Transform.TransformPoint(bounds.Center));
        GameAudio.Play("confirm", 0.35f, 0.75f);
        _relativeRotation = Quaternion.Inverse(Quaternion.FromEuler(0f, Owner.Yaw, 0f)) * body.Rotation;
    }

    private void Launch()
    {
        Rigidbody3D body = _hold.Body;
        _hold.Release(keepVelocity: false);
        Throw(body, body.Position);
    }

    private void Punt()
    {
        if (!Owner.Trace(PuntRange, out RaycastHit hit) || !CanMove(hit.Rigidbody))
        {
            GameAudio.Play("error", 0.2f, 0.6f);
            return;
        }
        Throw(hit.Rigidbody, hit.Point);
    }

    private void Throw(Rigidbody3D body, Float3 at)
    {
        body.ApplyImpulse(Owner.Forward * (PuntSpeed * body.Mass), at);
        _sincePunt = 0f;
        GameAudio.Play("launch", 0.65f, 1.1f);
        GameAudio.Play("confirm", 0.35f, 0.6f);
        Owner.ViewModel.Kick(1f);
        Owner.ViewPunch(new Float3(-2f, 0f, 0f));
    }
}
