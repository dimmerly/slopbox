using System;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// The first person model of the held equipment, parented to the camera. Equipment builds it from
/// primitive parts in <see cref="Equipment.BuildViewModel"/>; this adds sway, bob and recoil kick.
/// Swap <see cref="Equipment.BuildViewModel"/> for an imported model and animator when art exists.
/// </summary>
public sealed class ViewModel : Component
{
    public Player Owner;

    /// <summary>Where the model rests relative to the camera.</summary>
    public Float3 Offset = new(0.15f, -0.15f, 0.36f);

    /// <summary>Uniform scale of the whole model. The parts are authored a little large.</summary>
    public float Scale = 0.65f;

    private GameObject _model;
    private Float3 _muzzle;
    private float _lastYaw, _lastPitch;
    private Float2 _sway;
    private float _kick;

    /// <summary>The muzzle in world space, where beams start.</summary>
    public Float3 MuzzlePosition => _model.IsValid() ? _model.Transform.TransformPoint(_muzzle) : Owner.EyePosition;

    public void Rebuild(Equipment equipment)
    {
        if (_model.IsValid()) _model.Dispose();
        _model = new GameObject("Model");
        _model.SetParent(GameObject, false);
        _model.Transform.LocalScale = new Float3(Scale);
        _muzzle = new Float3(0f, 0f, 0.4f);
        equipment?.BuildViewModel(this);
    }

    /// <summary>Adds a primitive part. Meshes are unit sized, so <paramref name="scale"/> is the size in metres.</summary>
    public GameObject AddPart(Mesh mesh, Material material, Float3 position, Float3 scale, Float3 euler = default)
    {
        var part = new GameObject("Part");
        part.SetParent(_model, false);
        part.Transform.LocalPosition = position;
        part.Transform.LocalEulerAngles = euler;
        part.Transform.LocalScale = scale;
        var renderer = part.AddComponent<MeshRenderer>();
        renderer.Mesh = mesh;
        renderer.Material = material;
        return part;
    }

    public void SetMuzzle(Float3 local) => _muzzle = local;

    /// <summary>Knocks the model back, for firing.</summary>
    public void Kick(float amount = 1f) => _kick = MathF.Min(_kick + amount, 1.5f);

    /// <summary>Called by the player after the camera moved.</summary>
    public void Animate(float dt)
    {
        if (_model.IsNotValid() || dt <= 0f) return;

        // Lag behind fast turns, then catch up.
        float dYaw = Maths.DeltaAngle(_lastYaw, Owner.Yaw);
        float dPitch = Owner.Pitch - _lastPitch;
        _lastYaw = Owner.Yaw;
        _lastPitch = Owner.Pitch;
        Float2 target = new(Maths.Clamp(-dYaw * 0.004f, -0.05f, 0.05f), Maths.Clamp(dPitch * 0.004f, -0.05f, 0.05f));
        _sway += (target - _sway) * MathF.Min(1f, dt * 10f);

        (float bobTime, float bobWeight) = Owner.Bob;
        float bob = bobWeight * GameManager.Instance.Settings.ViewBob;
        Float3 bobOffset = new(MathF.Cos(bobTime) * 0.012f * bob, MathF.Abs(MathF.Sin(bobTime)) * -0.01f * bob, 0f);

        _kick = MathF.Max(0f, _kick - dt * 6f);
        float kick = _kick * _kick;

        _model.Transform.LocalPosition = Offset + new Float3(_sway.X, -_sway.Y, -kick * 0.06f) + bobOffset;
        _model.Transform.LocalEulerAngles = new Float3(-kick * 6f + _sway.Y * 40f, _sway.X * 40f, 0f);
    }
}

/// <summary>Unit sized meshes shared by every view model and effect.</summary>
public static class Shapes
{
    private static Mesh s_cube, s_cylinder, s_sphere;

    public static Mesh Cube => s_cube.IsValid() ? s_cube : s_cube = Mesh.CreateCube(Float3.One);
    public static Mesh Cylinder => s_cylinder.IsValid() ? s_cylinder : s_cylinder = Mesh.CreateCylinder(0.5f, 1f, 16);
    public static Mesh Sphere => s_sphere.IsValid() ? s_sphere : s_sphere = Mesh.CreateSphere(0.5f, 12, 16);
}
