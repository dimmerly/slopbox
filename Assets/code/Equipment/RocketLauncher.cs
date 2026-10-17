using System.Collections.Generic;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>A reusable launcher whose rockets release and scatter nearby physics objects.</summary>
public sealed class RocketLauncher : Equipment
{
    public float RocketSpeed = 45f;
    public float BlastRadius = 8f;
    public float BlastSpeed = 28f;
    public float FireInterval = 0.2f;

    private double _nextShot;
    private Material _bodyMat, _darkMat, _bandMat;

    public override string Name => "Rocket Launcher";
    public override string Description => "Explosive rockets unfreeze and scatter nearby props.";
    public override Color Tint => new(1f, 0.3f, 0.08f, 1f);
    public override IEnumerable<InputHint> Hints => [new("LMB", "Fire rocket")];

    public override void BuildViewModel(ViewModel vm)
    {
        if (_bodyMat.IsNotValid()) _bodyMat = Materials.Lit(new Color(0.12f, 0.17f, 0.06f, 1f), 0.55f, 0.45f);
        if (_darkMat.IsNotValid()) _darkMat = Materials.Lit(new Color(0.025f, 0.03f, 0.025f, 1f), 0.7f, 0.3f);
        if (_bandMat.IsNotValid()) _bandMat = Materials.Lit(Tint, 0.2f, 0.5f);

        vm.AddPart(Shapes.Cylinder, _bodyMat, new Float3(0f, 0f, 0.04f), new Float3(0.17f, 0.65f, 0.17f), new Float3(90f, 0f, 0f));
        vm.AddPart(Shapes.Cylinder, _darkMat, new Float3(0f, 0f, 0.37f), new Float3(0.2f, 0.06f, 0.2f), new Float3(90f, 0f, 0f));
        vm.AddPart(Shapes.Cylinder, _bandMat, new Float3(0f, 0f, 0.23f), new Float3(0.175f, 0.045f, 0.175f), new Float3(90f, 0f, 0f));
        vm.AddPart(Shapes.Cube, _darkMat, new Float3(0f, -0.13f, -0.08f), new Float3(0.065f, 0.18f, 0.075f), new Float3(-12f, 0f, 0f));
        vm.AddPart(Shapes.Cube, _darkMat, new Float3(0f, 0.12f, 0.19f), new Float3(0.025f, 0.08f, 0.025f));
        vm.SetMuzzle(new Float3(0f, 0f, 0.41f));
        // Shouldered: the tube rests on the shoulder, the grip under it.
        vm.SetGrip(new Float3(0f, -0.17f, -0.08f), new Float3(0f, -0.09f, 0.22f));
        vm.SetHold(new Float3(0.02f, 0.02f, 0.12f));
    }

    public override void Update()
    {
        if (!Attack1Down || Time.TimeSinceStartup < _nextShot) return;
        _nextShot = Time.TimeSinceStartup + FireInterval;

        Float3 muzzle = Owner.MuzzlePosition;
        Float3 target = Owner.Trace(500f, out RaycastHit aim) ? aim.Point : Owner.EyePosition + Owner.Forward * 500f;
        Float3 direction = Float3.Normalize(target - muzzle);
        RocketProjectile.Launch(Owner.EyePosition, muzzle, direction, RocketSpeed, BlastRadius, BlastSpeed);
        GameAudio.Play("rocket_launch", 0.8f, position: muzzle, maxDistance: 70f);
        Owner.Kick(1.5f);
        Owner.ViewPunch(new Float3(-4f, 0f, 0f));
    }
}
