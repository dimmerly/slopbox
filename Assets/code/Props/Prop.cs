using System;
using System.Collections.Generic;
using Jitter2.Dynamics;
using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Networking;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// Marks a physics object the player spawned or may modify: what it was spawned from (for the
/// duplicator), whether it is frozen, and its colour. Tools find it from a trace hit with <see cref="Of"/>.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Slopbox/Prop")]
public sealed class Prop : Component, INetworkEvents
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

    private bool _receivedFrozen;
    public bool IsFrozen => Body.IsValid() && (IsProxy ? _receivedFrozen : Body.MotionType != MotionType.Dynamic);

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

    [Sync] public string Description { get; set; } = "";
    [Sync] public int Sequence { get; set; }
    [Sync] public float AnimationSpeed { get; set; } = 1f;
    [Sync] public int Thrust { get; set; }
    private string _appliedDescription;
    private bool _hasVisuals;
    private double _nextCapture, _nextRetry;

    internal void SpawnNetwork()
    {
        // Runtime geometry is rebuilt locally. Colliders and simulation remain on this actual prop.
        var renderer = GetComponent<MeshRenderer>();
        if (renderer.IsValid())
        {
            var visual = new GameObject("Visual");
            visual.SetParent(GameObject, false);
            var copy = visual.AddComponent<MeshRenderer>();
            copy.Mesh = renderer.Mesh;
            for (int i = 0; i < ((IMaterialRenderer)renderer).MaterialSlotCount; i++) ((IMaterialRenderer)copy).SetMaterial(i, ((IMaterialRenderer)renderer).GetMaterial(i));
            foreach (RendererState state in _renderers)
                if (ReferenceEquals(state.Renderer, renderer)) state.Renderer = copy;
            GameObject.RemoveComponent(renderer);
        }
        foreach (GameObject child in GameObject.Children) child.Network.Mode = NetworkMode.Never;
        CaptureNetworkState();
        Network.Spawn();
    }

    private void CaptureNetworkState()
    {
        var snapshot = new Snapshot();
        var data = Snapshot.CaptureProp(this, Float3.Zero, Quaternion.Identity);
        snapshot.Props.Add(data);
        data.Position = [0, 0, 0]; data.Rotation = [0, 0, 0, 1]; data.Scale = 1;
        Description = snapshot.ToJson();
        var animator = GetComponentInChildren<Slopbox.Mounting.ModelAnimator>();
        if (animator.IsValid()) { Sequence = animator.Sequence; AnimationSpeed = animator.Speed; }
        if (TryGetComponent(out ThrusterProp thruster)) Thrust = thruster.Thrust;
    }

    public void OnNetworkSpawn() { if (IsProxy) _hasVisuals = false; }

    public override void LateUpdate()
    {
        if (!Network.Active) return;
        if (Networking.IsHost)
        {
            if (Time.TimeSinceStartup >= _nextCapture)
            {
                _nextCapture = Time.TimeSinceStartup + 0.2;
                CaptureNetworkState();
            }
            return;
        }
        if (Description.Length == 0) return;
        if (_appliedDescription != Description && Time.TimeSinceStartup >= _nextRetry)
        {
            _nextRetry = Time.TimeSinceStartup + 1;
            var data = Snapshot.FromJson(Description).Props[0];
            if (!_hasVisuals)
            {
                var staleAnimator = GetComponent<Slopbox.Mounting.ModelAnimator>();
                if (staleAnimator.IsValid()) GameObject.RemoveComponent(staleAnimator);
                var staleMap = GetComponent<MapProp>();
                if (staleMap.IsValid()) GameObject.RemoveComponent(staleMap);
                GameObject visual = Snapshot.Build(GameManager.Instance, data);
                if (visual.IsNotValid())
                {
                    Debug.LogWarning($"Missing shared content: {data.Spawn}. Install the same mounted game as the host.");
                    return;
                }
                foreach (Component c in visual.GetComponents<Component>())
                    if (c is Prop or Rigidbody3D or Collider or Light or LightProp or ThrusterProp)
                        visual.RemoveComponent(c);
                visual.Network.Mode = NetworkMode.Never;
                visual.SetParent(GameObject, false);
                _hasVisuals = true;
            }
            ApplyAppearance(data);
            _appliedDescription = Description;
            _nextRetry = 0;
        }
        var animation = GetComponentInChildren<Slopbox.Mounting.ModelAnimator>();
        if (animation.IsValid())
        {
            animation.Speed = AnimationSpeed;
            if (animation.Sequence != Sequence) animation.Play(Sequence);
        }
        if (TryGetComponent(out ThrusterProp flame)) flame.Thrust = Thrust;
    }

    internal void RestoreSimulation()
    {
        if (Body.IsNotValid() || Description.Length == 0) return;
        Body.MotionType = Snapshot.FromJson(Description).Props[0].Frozen ? MotionType.Kinematic : MotionType.Dynamic;
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

    internal void ApplyAppearance(Snapshot.PropData data)
    {
        _receivedFrozen = data.Frozen;
        if (Body.IsValid()) { Body.Mass = data.Mass > 0 ? data.Mass : Body.Mass; Body.AffectedByGravity = data.Gravity; }
        _tint = data.Tint is { Length: 4 } c ? new Color(c[0], c[1], c[2], c[3]) : Color.White;
        _material = Guid.TryParse(data.Material, out Guid id) ? AssetDatabase.Get<Material>(id) : null;
        Refresh();
        if (TryGetComponent(out ThrusterProp thruster) && data.Thruster != null)
            thruster.Set(data.Thruster.Force, data.Thruster.Forward, data.Thruster.Back, data.Thruster.Toggle);
        if (TryGetComponent(out LightProp light) && data.Light != null)
            light.Set(new Color(data.Light.Color[0], data.Light.Color[1], data.Light.Color[2], data.Light.Color[3]), data.Light.Brightness, data.Light.Range);
    }

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
