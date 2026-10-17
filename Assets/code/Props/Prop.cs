using System;
using System.Collections.Generic;
using Jitter2.Dynamics;
using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// Marks a physics object the player spawned or may modify: what it was spawned from (for the
/// duplicator), whether it is frozen, and its colour. Tools find it from a trace hit with <see cref="Of"/>.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Slopbox/Prop")]
public sealed class Prop : Component
{
    /// <summary>The <see cref="Spawnable.Id"/> this came from, empty for props placed in the editor.</summary>
    public string SpawnId = "";

    [SerializeField] private Color _tint = Color.White;
    [SerializeField] private Material _material;

    /// <summary>What each renderer had before any override, and the copies made for tinting.</summary>
    private sealed class RendererState
    {
        /// <summary>A mesh or skinned mesh renderer.</summary>
        public IMaterialRenderer Renderer;
        public List<Material> Original;
        public readonly List<Material> Clones = new();
    }

    [SerializeIgnore] private readonly List<RendererState> _renderers = new();

    public Rigidbody3D Body => GetComponent<Rigidbody3D>();
    public Color Tint => _tint;

    /// <summary>The material painted over every surface by the material tool, or null for the model's own.</summary>
    public Material MaterialOverride => _material;

    public bool IsFrozen => Body.IsValid() && Body.MotionType != MotionType.Dynamic;

    public override void OnEnable()
    {
        if (_tint != Color.White || _material.IsValid()) Refresh();
    }

    /// <summary>Every prop in the current scene.</summary>
    public static Prop[] All => Scene.Current.FindObjectsOfType<Prop>();

    protected override void OnDispose()
    {
        _tint = Color.White;
        _material = null;
        Refresh();
        base.OnDispose();
    }

    /// <summary>The prop a trace hit belongs to, adding a Prop to any plain rigidbody so tools work on scene objects too.</summary>
    public static Prop Of(Rigidbody3D body)
    {
        if (body.IsNotValid()) return null;
        if (body.GameObject.TryGetComponent(out Prop prop)) return prop;
        return body.GameObject.AddComponent<Prop>();
    }

    private double _nextImpactSound;

    public override void OnCollisionBegin(Collision collision)
    {
        if (Time.TimeSinceStartup < _nextImpactSound) return;
        if (GameAudio.Impact(this, collision)) _nextImpactSound = Time.TimeSinceStartup + 0.12;
    }

    public void SetFrozen(bool frozen)
    {
        Rigidbody3D body = Body;
        if (body.IsNotValid() || frozen == IsFrozen) return;

        GameAudio.Play("freeze", 0.4f, frozen ? 1.25f : 0.8f, position: Transform.Position);
        body.MotionType = frozen ? MotionType.Kinematic : MotionType.Dynamic;
        body.LinearVelocity = Float3.Zero;
        body.AngularVelocity = Float3.Zero;
        body.SetActive(true);
    }

    /// <summary>Uniform scale relative to the model as spawned.</summary>
    public float Scale => Transform.LocalScale.X;

    /// <summary>Resizes uniformly, keeping the lowest point where it was so the prop does not sink into the floor.</summary>
    public void SetScale(float scale)
    {
        float old = Scale;

        // Lowest point of the rotated bounds per unit of scale.
        Quaternion rotation = Transform.Rotation;
        float bottom = float.MaxValue;
        foreach (Float3 corner in Spawner.LocalBounds(GameObject).GetCorners())
            bottom = MathF.Min(bottom, (rotation * corner).Y);

        Transform.LocalScale = new Float3(scale);
        Transform.Position += Float3.UnitY * ((old - scale) * bottom);

        Rigidbody3D body = Body;
        if (body.IsValid()) body.SetActive(true);
    }

    // ----------------------------------------------------------------
    //  Look: material override and colour
    // ----------------------------------------------------------------

    public void SetTint(Color tint)
    {
        _tint = tint;
        Refresh();
    }

    /// <summary>Paints every surface with <paramref name="material"/>, or restores the model's own with null.</summary>
    public void SetMaterial(Material material)
    {
        _material = material;
        Refresh();
    }

    /// <summary>
    /// Rebuilds what each renderer shows from its original materials, the material override and the tint.
    /// Tinting gives the prop its own copies of the materials (Prowl has no property blocks) so other props
    /// sharing them are untouched.
    /// </summary>
    private void Refresh()
    {
        bool overridden = _material.IsValid();
        bool tinted = _tint != Color.White;

        if (_renderers.Count == 0 && (overridden || tinted))
        {
            foreach (Component component in GetComponentsInChildren<Component>())
            {
                if (component is not (MeshRenderer or SkinnedMeshRenderer) || component is not IMaterialRenderer renderer) continue;
                var original = new List<Material>(renderer.MaterialSlotCount);
                for (int i = 0; i < renderer.MaterialSlotCount; i++) original.Add(renderer.GetMaterial(i));
                _renderers.Add(new RendererState { Renderer = renderer, Original = original });
            }
        }

        foreach (RendererState state in _renderers)
        {
            foreach (Material clone in state.Clones)
                if (clone.IsValid()) clone.Dispose();
            state.Clones.Clear();

            if (state.Renderer is not Component component || component.IsNotValid()) continue;

            var shown = new List<Material>(state.Original.Count);
            foreach (Material original in state.Original)
            {
                Material source = overridden ? _material : original;
                if (!tinted || source.IsNotValid())
                {
                    shown.Add(source);
                    continue;
                }

                Material clone = source.Clone();
                Color c = source._properties.GetColor("_MainColor");
                clone.SetColor("_MainColor", new Color(c.R * _tint.R, c.G * _tint.G, c.B * _tint.B, c.A * _tint.A));
                state.Clones.Add(clone);
                shown.Add(clone);
            }
            for (int i = 0; i < shown.Count; i++) state.Renderer.SetMaterial(i, shown[i]);
        }

        // Back to the model's own look: forget the snapshot so a later change takes a fresh one.
        if (!overridden && !tinted) _renderers.Clear();
    }
}
