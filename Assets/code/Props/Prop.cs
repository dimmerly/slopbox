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
/// A physics object the player spawned or may modify. A spawned prop is a networked shell, the same on every
/// peer: this, its rigidbody and a NetworkTransform, from <see cref="GameManager.PropPrefab"/>. Its model is
/// built under it on each peer from <see cref="SpawnId"/> with that peer's own content, so what each has
/// mounted never changes the networked tree. Tools find it from a trace hit with <see cref="Of"/>.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Slopbox/Prop")]
public sealed class Prop : Component
{
    /// <summary>The <see cref="Spawnable.Id"/> its model is built from, empty for props placed in the editor.</summary>
    [Sync] public string SpawnId { get; set; } = "";

    /// <summary>Multiplied into every material's colour. White leaves the model's own.</summary>
    [Sync] public Color Tint { get; set; } = Color.White;

    /// <summary>The asset id of the material painted over every surface, or empty for the model's own.</summary>
    [Sync] public string MaterialId { get; set; } = "";

    /// <summary>Whether it is held in place, written by whoever drives it. Proxies are always kinematic, so they read this instead.</summary>
    [Sync(SyncFlags.FromOwner)] public bool Frozen { get; set; }

    /// <summary>The body's settings, written by whoever drives it, so a client that takes it over simulates the same body.</summary>
    [Sync(SyncFlags.FromOwner)] public float BodyMass { get; set; }
    [Sync(SyncFlags.FromOwner)] public bool BodyGravity { get; set; } = true;
    [Sync(SyncFlags.FromOwner)] public float BodyFriction { get; set; }
    [Sync(SyncFlags.FromOwner)] public float BodyRestitution { get; set; }

    /// <summary>The model's animation, played by its <see cref="Mounting.ModelAnimator"/> on every peer.</summary>
    [Sync] public int Sequence { get; set; }
    [Sync] public float AnimationSpeed { get; set; } = 1f;

    /// <summary>What each renderer had before any override, and the copies made for tinting.</summary>
    private sealed class RendererState
    {
        /// <summary>A mesh or skinned mesh renderer.</summary>
        public IMaterialRenderer Renderer;
        public List<Material> Original;
        public readonly List<Material> Clones = new();
    }

    [SerializeIgnore] private readonly List<RendererState> _renderers = new();

    /// <summary>Follows a renderer swapped for another showing the same thing, as a model gaining its skeleton does.</summary>
    internal void ReplaceRenderer(IMaterialRenderer from, IMaterialRenderer to)
    {
        foreach (RendererState state in _renderers)
            if (ReferenceEquals(state.Renderer, from)) state.Renderer = to;
    }

    public Rigidbody3D Body => GetComponent<Rigidbody3D>();

    /// <summary>The material painted over every surface by the material tool, or null for the model's own.</summary>
    public Material MaterialOverride => Guid.TryParse(MaterialId, out Guid id) ? AssetDatabase.Get<Material>(id) : null;

    public bool IsFrozen => Body.IsValid() && (IsProxy ? Frozen : Body.MotionType != MotionType.Dynamic);

    /// <summary>Every prop in the current scene.</summary>
    public static Prop[] All => SceneInstance.HasMain ? SceneInstance.Active.GetAll<Prop>() : [];

    [SerializeIgnore] private GameObject _model;
    private string _modelId = "";
    private double _nextModelTry;
    private bool _warnedMissing;
    private Color _appliedTint = Color.White;
    private string _appliedMaterial = "";

    // ----------------------------------------------------------------
    //  Who drives it
    // ----------------------------------------------------------------

    /// <summary>
    /// Host: whether a client may take <paramref name="body"/> over and move it itself, with no delay: a spawned
    /// prop's own body, free of joints, which exist only on the host. It keeps it until the host takes it back.
    /// </summary>
    public static bool ClientMayDrive(Rigidbody3D body)
        => body.IsValid() && body.GameObject.TryGetComponent(out Prop _) && IsNetworkedRoot(body.GameObject)
           && Constraints.On(body).Count == 0;

    /// <summary>A spawned networked object itself, as opposed to a part of one or a local object.</summary>
    internal static bool IsNetworkedRoot(GameObject go) => go.Network.Active && go.Network.Root == go;

    /// <summary>Host: takes <paramref name="body"/> back from the client driving it, before changing how it moves.</summary>
    public static void Reclaim(Rigidbody3D body)
    {
        if (body.IsNotValid() || !Networking.IsHost) return;
        GameObject go = body.GameObject;
        if (go.Network.Active && go.Network.Owner != null) go.Network.RemoveOwnership();
    }

    // ----------------------------------------------------------------
    //  Spawning
    // ----------------------------------------------------------------

    /// <summary>
    /// Host: spawns a networked prop of <paramref name="spawnable"/> at a pose, its model built at once so it can be
    /// placed by its bounds. Outside a session it spawns once this peer hosts. Returns null if there is no model.
    /// </summary>
    public static Prop Spawn(Spawnable spawnable, Float3 position, Quaternion rotation)
    {
        PrefabAsset shell = spawnable.Shell(GameManager.Instance);
        GameObject go = Networking.SpawnPrefab(shell, position, rotation);
        Prop prop = go.GetComponent<Prop>();
        prop.SpawnId = spawnable.Id;
        if (prop.BuildModel()) return prop;

        go.Destroy();
        return null;
    }

    /// <summary>Builds the model for <see cref="SpawnId"/> under this, replacing any built before. False if this peer can't.</summary>
    private bool BuildModel()
    {
        if (_model.IsValid()) _model.Destroy();
        _model = null;
        RestoreMaterials();

        GameManager game = GameManager.Instance;
        Spawnable spawnable = game.IsValid() ? game.Catalog?.Find(SpawnId) : null;
        GameObject model = null;
        float mass = 0f;
        try
        {
            if (spawnable != null) model = spawnable.CreateModel(out mass);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
        if (model.IsNotValid()) return false;

        model.SetParent(GameObject, false);
        model.Transform.LocalPosition = Float3.Zero;
        model.Transform.LocalRotation = Quaternion.Identity;
        model.Transform.LocalScale = Float3.One;
        _model = model;
        _modelId = SpawnId;
        if (!IsProxy && mass > 0f && Body.IsValid()) Body.Mass = mass;

        // The look applies to the renderers just made.
        _appliedTint = Color.White;
        _appliedMaterial = "";
        return true;
    }

    public override void Update()
    {
        // A proxy's model comes once its id arrives, and again whenever this peer's content allows.
        if (SpawnId.Length > 0 && _modelId != SpawnId && Time.TimeSinceStartup >= _nextModelTry && !BuildModel())
        {
            _nextModelTry = Time.TimeSinceStartup + 1;
            if (!_warnedMissing && IsProxy)
                Debug.LogWarning($"Missing shared content: {SpawnId}. Install the same mounted game as the host.");
            _warnedMissing = true;
        }

        if (Tint != _appliedTint || MaterialId != _appliedMaterial) Refresh();

        var animator = _model.IsValid() ? _model.GetComponentInChildren<Mounting.ModelAnimator>() : null;
        if (animator.IsNotValid()) return;
        if (IsProxy)
        {
            animator.Speed = AnimationSpeed;
            if (animator.Sequence != Sequence) animator.Play(Sequence);
        }
        else
        {
            Sequence = animator.Sequence;
            AnimationSpeed = animator.Speed;
        }
    }

    public override void LateUpdate()
    {
        Rigidbody3D body = Body;
        if (body.IsNotValid()) return;
        if (!IsProxy)
        {
            Frozen = body.MotionType != MotionType.Dynamic;
            BodyMass = body.Mass;
            BodyGravity = body.AffectedByGravity;
            BodyFriction = body.Friction;
            BodyRestitution = body.Restitution;
        }
        else if (BodyMass > 0f)
        {
            if (body.Mass != BodyMass) body.Mass = BodyMass;
            if (body.AffectedByGravity != BodyGravity) body.AffectedByGravity = BodyGravity;
            if (body.Friction != BodyFriction) body.Friction = BodyFriction;
            if (body.Restitution != BodyRestitution) body.Restitution = BodyRestitution;
        }
    }

    protected override void OnDispose()
    {
        RestoreMaterials();
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
        Reclaim(Body);
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
        Reclaim(Body);
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
        Tint = tint;
        Refresh();
    }

    /// <summary>Paints every surface with <paramref name="material"/>, or restores the model's own with null.</summary>
    public void SetMaterial(Material material)
    {
        MaterialId = material.IsValid() ? material.AssetID.ToString() : "";
        Refresh();
    }

    /// <summary>
    /// Rebuilds what each renderer shows from its original materials, the material override and the tint.
    /// Tinting gives the prop its own copies of the materials (Prowl has no property blocks) so other props
    /// sharing them are untouched.
    /// </summary>
    private void Refresh()
    {
        _appliedTint = Tint;
        _appliedMaterial = MaterialId;
        Material material = MaterialOverride;
        bool overridden = material.IsValid();
        bool tinted = Tint != Color.White;
        if (!overridden && !tinted)
        {
            // Back to the model's own look: forget the snapshot so a later change takes a fresh one.
            RestoreMaterials();
            return;
        }

        if (_renderers.Count == 0)
        {
            foreach (Component component in GetComponentsInChildren<Component>())
            {
                if (component is not (MeshRenderer or SkinnedMeshRenderer) || component is not IMaterialRenderer renderer) continue;
                var original = new List<Material>(renderer.MaterialSlotCount);
                for (int i = 0; i < renderer.MaterialSlotCount; i++) original.Add(renderer.GetMaterial(i));
                _renderers.Add(new RendererState { Renderer = renderer, Original = original });
            }
        }

        Color tint = Tint;
        foreach (RendererState state in _renderers)
        {
            DisposeClones(state);
            if (state.Renderer is not Component component || component.IsNotValid()) continue;

            var shown = new List<Material>(state.Original.Count);
            foreach (Material original in state.Original)
            {
                Material source = overridden ? material : original;
                if (!tinted || source.IsNotValid())
                {
                    shown.Add(source);
                    continue;
                }

                Material clone = source.Clone();
                Color c = source._properties.GetColor(Materials.BaseColor);
                clone.SetColor(Materials.BaseColor, new Color(c.R * tint.R, c.G * tint.G, c.B * tint.B, c.A * tint.A));
                state.Clones.Add(clone);
                shown.Add(clone);
            }
            for (int i = 0; i < shown.Count; i++) state.Renderer.SetMaterial(i, shown[i]);
        }
    }

    /// <summary>Puts every renderer's own materials back and frees the tinted copies.</summary>
    private void RestoreMaterials()
    {
        foreach (RendererState state in _renderers)
        {
            DisposeClones(state);
            if (state.Renderer is not Component component || component.IsNotValid()) continue;
            for (int i = 0; i < state.Original.Count; i++) state.Renderer.SetMaterial(i, state.Original[i]);
        }
        _renderers.Clear();
    }

    private static void DisposeClones(RendererState state)
    {
        foreach (Material clone in state.Clones)
            if (clone.IsValid()) clone.Dispose();
        state.Clones.Clear();
    }
}
