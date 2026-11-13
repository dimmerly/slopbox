using System;
using System.Collections.Generic;
using System.Linq;
using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Slopbox.Mounting;

namespace Slopbox;

/// <summary>
/// A map from a mounted game placed as a static object. It hands its lightmaps to the scene's baked lighting while
/// it is in one, and fills itself with the NPCs, items and models the map
/// places, as parts of the map rather than props of their own, so removing the map removes them too.
/// </summary>
[DisallowMultipleComponent]
public sealed class MapProp : Component
{
    /// <summary>Models made per frame, so a busy map does not stall the frame it is placed on.</summary>
    private const int PlacedPerFrame = 8;

    /// <summary>Things that hang, fly or swim, which the game never drops to the floor.</summary>
    private static readonly HashSet<string> s_floating = new(StringComparer.OrdinalIgnoreCase)
    {
        "monster_barnacle", "monster_apache", "monster_osprey", "monster_ichthyosaur", "monster_leech",
        "monster_flyer_flock", "monster_nihilanth", "monster_turret", "monster_miniturret", "monster_alien_controller",
    };

    /// <summary>
    /// The layer the map's own surfaces draw on. Directional lights skip it: the map's sunlight is in its
    /// lightmaps already. The models in it stay on their own layer and take the sun as usual.
    /// </summary>
    public const int Layer = 29;

    [SerializeIgnore] private MapSpawnable _source;
    [SerializeIgnore] private MountedMap _map;
    [SerializeIgnore] private GameObject _geometry;
    [SerializeIgnore] private SceneSection _litSection;

    /// <summary>Every map giving a scene section its light, whose probes together are that section's.</summary>
    private static readonly List<MapProp> s_lit = new();

    /// <param name="geometry">The <see cref="MountedMap.Instantiate"/>d map under this object, which the map's models join.</param>
    internal void Bind(MapSpawnable source, MountedMap map, GameObject geometry)
    {
        _source = source;
        _map = map;
        _geometry = geometry;
    }

    public override void OnEnable()
    {
        // Lightmaps and probes belong to the section the map was loaded or spawned into.
        if (_map == null || _geometry.IsNotValid() || _litSection != null || GameObject.Section == null) return;
        _litSection = GameObject.Section;
        _map.AddLighting(_litSection, _geometry);
        s_lit.Add(this);
        foreach (GameObject part in _geometry.Children.Take(_map.Parts.Count)) part.LayerIndex = Layer;
        UpdateProbes(_litSection);

        TurnOffSuns();
        UseMapSky();
        ShowAsInGame();
    }

    public override void OnDisable() => RemoveLighting();

    protected override void OnDispose()
    {
        RemoveLighting();
        base.OnDispose();
    }

    private void RemoveLighting()
    {
        if (!s_lit.Remove(this)) return;
        if (_map != null && _litSection != null)
        {
            _map.RemoveLighting(_litSection, _geometry);
            UpdateProbes(_litSection);
        }
        _litSection = null;
        if (s_lit.Count == 0)
        {
            RestoreSuns();
            RestoreSky();
            RestoreCameras();
        }
    }

    /// <summary>The sky the scene had before a map put its own up.</summary>
    private static Material s_ownSky;
    private static SceneInstance s_skyScene;

    private void UseMapSky()
    {
        if (_map.Sky.IsNotValid()) return;
        if (s_skyScene.IsNotValid()) { s_ownSky = Scene.Environment.SkyMaterial; s_skyScene = Scene; }
        Scene.Environment.SkyMaterial = _map.Sky;
    }

    private static void RestoreSky()
    {
        if (s_skyScene.IsValid()) s_skyScene.Environment.SkyMaterial = s_ownSky;
        s_skyScene = null;
        s_ownSky = null;
    }

    [SerializeIgnore] private float _nextCameraCheck;

    // Cameras made after the map, such as the player's, get the game's look too
    public override void Update()
    {
        if (_litSection == null || Time.TimeSinceStartup < _nextCameraCheck) return;
        _nextCameraCheck = (float)Time.TimeSinceStartup + 0.5f;
        ShowAsInGame();
    }

    /// <summary>The scene's suns that were on before a map turned them off.</summary>
    private static readonly List<DirectionalLight> s_suns = new();

    /// <summary>
    /// The map lights everything in it itself, its sun included (baked into its lightmaps, and into its probes for
    /// models), so the scene's suns would only light things twice.
    /// </summary>
    private void TurnOffSuns()
    {
        foreach (DirectionalLight light in Scene.GetAll<DirectionalLight>())
        {
            if (!light.Enabled) continue;
            s_suns.Add(light);
            light.Enabled = false;
        }
    }

    private static void RestoreSuns()
    {
        foreach (DirectionalLight light in s_suns)
            if (light.IsValid()) light.Enabled = true;
        s_suns.Clear();
    }

    /// <summary>Each camera's own tonemapper and bloom, kept while it shows a map as its game did.</summary>
    private static readonly Dictionary<Camera, (Prowl.Runtime.Rendering.TonemappingMode Mode, float Bloom)> s_cameras = new();

    /// <summary>
    /// Shows the scene the way the map's game drew it: colour clamped and gamma encoded with no tonemapping curve,
    /// and no bloom, so a lightmapped texture at full light shows as the texture.
    /// </summary>
    private void ShowAsInGame()
    {
        foreach (Camera camera in Scene.GetAll<Camera>(includeDisabled: true))
        {
            if (s_cameras.ContainsKey(camera) || camera.Profile.IsNotValid()) continue;
            if (!camera.Profile.TryGet(out Prowl.Runtime.Rendering.Tonemapping tonemapping)) continue;
            float bloom = camera.Profile.TryGet(out Prowl.Runtime.Rendering.Bloom b) ? b.Intensity.Value : 0f;
            s_cameras[camera] = (tonemapping.Mode.Value, bloom);
            tonemapping.Mode.Set(Prowl.Runtime.Rendering.TonemappingMode.None);
            if (b != null) b.Intensity.Set(0f);
        }
    }

    private static void RestoreCameras()
    {
        foreach ((Camera camera, var own) in s_cameras)
        {
            if (camera.IsNotValid() || camera.Profile.IsNotValid()) continue;
            if (camera.Profile.TryGet(out Prowl.Runtime.Rendering.Tonemapping tonemapping)) tonemapping.Mode.Set(own.Mode);
            if (camera.Profile.TryGet(out Prowl.Runtime.Rendering.Bloom bloom)) bloom.Intensity.Set(own.Bloom);
        }
        s_cameras.Clear();
    }

    /// <summary>
    /// The section's light probes, from every map in it, so models and players are lit like the map around them.
    /// The probes are not tetrahedralised; the renderer blends the nearest ones.
    /// </summary>
    private static void UpdateProbes(SceneSection section)
    {
        var positions = new List<Float3>();
        var sh = new List<Prowl.Runtime.Rendering.SphericalHarmonicsL2>();
        foreach (MapProp map in s_lit)
            if (map._litSection == section && map._geometry.IsValid())
                map._map.AppendProbes(map._geometry.Transform, positions, sh, map.SeesSky);

        SceneLighting baked = section.Lighting;
        baked.ProbePositions = [.. positions];
        baked.ProbeSH = [.. sh];
        baked.ProbeTetrahedra = [];
        baked.ProbeTetNeighbours = [];
        if (section.Instance.IsValid()) section.Instance.InvalidateProbeVolume();
    }

    /// <summary>Whether the map has nothing between <paramref name="at"/> and its sky that way: its sky faces have no colliders.</summary>
    private bool SeesSky(Float3 at, Float3 toSun)
        => !Scene.Physics.Raycast(at, toSun, 400f, out RaycastHit _, new QueryFilter(LayerMask.FromMask(1u << Layer)));

    public override void Start()
    {
        // Again now the map's colliders are in the physics world, for which probes the sun reaches
        if (_litSection != null) UpdateProbes(_litSection);

        if (_map != null && _source != null && _geometry.IsValid() && _map.Models.Count > 0) PlaceModelsAsync();
    }

    /// <summary>
    /// Puts the map's models in it, each the spawn menu's model for its path, without physics of its own. Items
    /// and grounded NPCs drop to the floor under them, as in the game.
    /// </summary>
    private async void PlaceModelsAsync()
    {
        Dictionary<string, Spawnable> byModel = ModelIndex(_source.Loader.Mount);
        int missing = 0;

        try
        {
            for (int i = 0; i < _map.Models.Count; i++)
            {
                if (i > 0 && i % PlacedPerFrame == 0) await GameTask.NextFrame(DestroyCancellationToken);
                if (_geometry.IsNotValid() || GameObject.IsDestroyQueued) return;

                MountedMap.PlacedModel model = _map.Models[i];
                if (!byModel.TryGetValue(model.Model, out Spawnable spawnable))
                {
                    missing++;
                    continue;
                }

                GameObject go = spawnable.Create();
                if (go.IsNotValid()) continue;

                // Part of the map: its rigidbody and prop marker go. Solid ones keep the box around them as a
                // static collider; the rest lose their colliders too.
                foreach (Component c in go.GetComponentsInChildren<Component>(true, true).ToList())
                    if (c is Rigidbody3D or Prop || (c is Collider && !model.Solid)) c.GameObject.RemoveComponent(c);
                if (model.Solid && !go.TryGetComponent(out Collider _))
                {
                    AABB bounds = Spawner.LocalBounds(go);
                    var box = go.AddComponent<BoxCollider>();
                    box.Size = Maths.Max(bounds.Size, new Float3(0.02f));
                    box.Center = bounds.Center;
                }

                if (model.Skin != 0 && spawnable is MountSpawnable mounted && mounted.Loader.GetOrCreate<MountedModel>() is { } built)
                    built.ApplySkin(go, model.Skin);

                Transform space = _geometry.Transform;
                Float3 local = model.Position;
                if (!model.Exact && !s_floating.Contains(model.ClassName))
                {
                    Float3 from = space.TransformPoint(local + Float3.UnitY * 0.1f);
                    if (Scene.Physics.Raycast(from, -space.Up, 4f, out RaycastHit hit, QueryFilter.Default))
                        local = space.InverseTransformPoint(hit.Point);
                }

                // Placed before it joins the map: its static colliders attach on joining, and moving it after
                // would tear down and rebuild every triangle of them on the shared static body.
                go.Transform.LocalPosition = local;
                go.Transform.LocalRotation = model.Rotation;
                if (model.Scale != 1f) go.Transform.LocalScale = new Float3(model.Scale);
                go.SetParent(_geometry, false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (missing > 0) Debug.LogWarning($"{_source.Name}: {missing} placed models were not found in {_source.Loader.Mount.Title}");
    }

    /// <summary>
    /// The spawn menu entry for each model of a mount, by its path in the game ("models/scientist.mdl"). Where a
    /// model has a classic and an HD version, the classic one, to match the map. Models the map's own game lacks
    /// come from the other mounted games, as a Garry's Mod map uses Counter-Strike: Source's props.
    /// </summary>
    private static Dictionary<string, Spawnable> ModelIndex(BaseGameMount mount)
    {
        var index = new Dictionary<string, Spawnable>(StringComparer.OrdinalIgnoreCase);
        var others = new Dictionary<string, Spawnable>(StringComparer.OrdinalIgnoreCase);
        foreach (MountSpawnable spawnable in GameManager.Instance.Catalog.All.OfType<MountSpawnable>())
        {
            string path = spawnable.Loader.RelativePath;
            int slash = path.IndexOf('/');
            if (slash < 0) continue;

            string inGame = path[(slash + 1)..];
            if (spawnable.Loader.Mount != mount)
            {
                others.TryAdd(inGame, spawnable);
                continue;
            }

            bool hd = path[..slash].EndsWith("_hd", StringComparison.OrdinalIgnoreCase);
            if (!hd || !index.ContainsKey(inGame)) index[inGame] = spawnable;
        }
        foreach ((string inGame, Spawnable spawnable) in others) index.TryAdd(inGame, spawnable);
        return index;
    }
}
