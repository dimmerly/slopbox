using System;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Slopbox.Mounting;

namespace Slopbox;

/// <summary>
/// A model of the held equipment. Equipment builds it from primitive parts in <see cref="Equipment.BuildViewModel"/>.
/// The first person one is parented to the camera and adds sway, bob and recoil kick. The player also keeps a
/// <see cref="World"/> one, which it places in the body's hands for third person and for shadows.
/// Swap <see cref="Equipment.BuildViewModel"/> for an imported model and animator when art exists.
/// </summary>
public sealed class ViewModel : Component
{
    public Player Owner;

    /// <summary>Where the model rests relative to the camera.</summary>
    public Float3 Offset = new(0.15f, -0.15f, 0.36f);

    /// <summary>Uniform scale of the whole model. The parts are authored a little large.</summary>
    public float Scale = 0.65f;

    /// <summary>The scale the model is drawn at: <see cref="Scale"/>, or 1 for a mounted game's model, which is real size.</summary>
    public float ModelScale => _realSize ? 1f : Scale;

    /// <summary>Held by the body rather than drawn in front of the camera: no sway or bob, and it casts shadows.</summary>
    public bool World;

    private GameObject _model;
    private Float3 _muzzle, _grip, _hold, _offset;
    private Float3? _support;
    private bool _visible = true;
    private bool _realSize;
    private ShadowCasting _shadows = ShadowCasting.On;
    private float _lastYaw, _lastPitch;
    private Float2 _sway;
    private float _kick;

    /// <summary>The muzzle in world space, where beams start.</summary>
    public Float3 MuzzlePosition => _model.IsValid() ? _model.Transform.TransformPoint(_muzzle) : Owner.EyePosition;

    /// <summary>Where the right hand holds it, in model space before <see cref="ModelScale"/>.</summary>
    public Float3 Grip => _grip;

    /// <summary>Where the left hand steadies it, or null for a one handed hold.</summary>
    public Float3? Support => _support;

    /// <summary>Where it sits relative to the right shoulder, in aim space (x right, y up, z forward), in metres.</summary>
    public Float3 Hold => _hold;

    public bool HasModel => _model.IsValid() && _model.Transform.ChildCount > 0;

    /// <summary>Whether it draws. A hidden model still places its muzzle.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            if (_model.IsValid()) _model.Enabled = value;
        }
    }

    /// <summary>How its parts cast shadows. First person models never do, so no gun shadow floats by the camera.</summary>
    public ShadowCasting Shadows
    {
        get => _shadows;
        set
        {
            _shadows = value;
            if (_model.IsNotValid()) return;
            foreach (MeshRenderer renderer in _model.GetComponentsInChildren<MeshRenderer>())
                renderer.ShadowCasting = PartShadows;
        }
    }

    private ShadowCasting PartShadows => World ? _shadows : ShadowCasting.Off;

    public void Rebuild(Equipment equipment)
    {
        if (_model.IsValid()) _model.Dispose();
        _model = new GameObject("Model");
        _model.SetParent(GameObject, false);
        _model.Transform.LocalScale = new Float3(Scale);
        _realSize = false;
        _muzzle = new Float3(0f, 0f, 0.4f);
        _grip = new Float3(0f, -0.08f, -0.08f);
        _support = new Float3(0f, -0.05f, 0.1f);
        _hold = new Float3(0.02f, -0.14f, 0.42f);
        _offset = Offset;
        equipment?.BuildViewModel(this);
        _model.Enabled = _visible;
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
        renderer.ShadowCasting = PartShadows;
        return part;
    }

    /// <summary>
    /// Adds a model from a mounted game, such as "models/weapons/w_physics.mdl", from the first of
    /// <paramref name="games"/> that has it. Such models are real size, so the whole view model then ignores
    /// <see cref="Scale"/>, and the muzzle and grips are in metres. False if none of the games is mounted with it,
    /// for the equipment to build its own parts instead.
    /// </summary>
    public bool AddMountedModel(string path, string[] games, Float3 position, Float3 euler, int skin = 0)
    {
        MountedModel mounted = MountDirectory.FindModel(path, games);
        if (mounted == null) return false;

        GameObject part = mounted.Instantiate();
        mounted.ApplySkin(part, skin);
        part.SetParent(_model, false);
        part.Transform.LocalPosition = position;
        part.Transform.LocalEulerAngles = euler;
        foreach (MeshRenderer renderer in part.GetComponentsInChildren<MeshRenderer>())
            renderer.ShadowCasting = PartShadows;

        _realSize = true;
        _model.Transform.LocalScale = Float3.One;
        return true;
    }

    public void SetMuzzle(Float3 local) => _muzzle = local;

    /// <summary>Where the hands go, in model space. Leave <paramref name="support"/> null for a one handed hold.</summary>
    public void SetGrip(Float3 hand, Float3? support)
    {
        _grip = hand;
        _support = support;
    }

    /// <summary>Where the body holds it relative to the right shoulder, in aim space. See <see cref="Hold"/>.</summary>
    public void SetHold(Float3 offset) => _hold = offset;

    /// <summary>Where this equipment rests relative to the camera in first person, instead of <see cref="Offset"/>.</summary>
    public void SetViewOffset(Float3 offset) => _offset = offset;

    /// <summary>Knocks the model back, for firing.</summary>
    public void Kick(float amount = 1f) => _kick = MathF.Min(_kick + amount, 1.5f);

    /// <summary>Called by the player after the camera moved.</summary>
    public void Animate(float dt)
    {
        if (_model.IsNotValid() || dt <= 0f) return;

        if (World)
        {
            // The player places the model itself. Only the recoil is added on top.
            _kick = MathF.Max(0f, _kick - dt * 6f);
            _model.Transform.LocalPosition = new Float3(0f, 0f, -_kick * _kick * 0.08f);
            _model.Transform.LocalEulerAngles = new Float3(-_kick * _kick * 10f, 0f, 0f);
            return;
        }

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

        _model.Transform.LocalPosition = _offset + new Float3(_sway.X, -_sway.Y, -kick * 0.06f) + bobOffset;
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
