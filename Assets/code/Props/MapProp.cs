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
/// A map from a mounted game placed as a static object. It hands its lightmaps to the scene while it is in one,
/// and fills itself with the NPCs, items and models the map places, as parts of the map rather than props of
/// their own, so removing the map removes them too.
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
    /// The layer maps and the models standing in them draw on. The scene's sun skips it: a map is lit by its own
    /// baked light, so the sun would light rooms twice and shine through roofs.
    /// </summary>
    public const int Layer = 29;

    private static readonly List<MapProp> s_all = new();

    /// <summary>Every map in the scene.</summary>
    public static IReadOnlyList<MapProp> All => s_all;

    [SerializeIgnore] private MapSpawnable _source;
    [SerializeIgnore] private MountedMap _map;
    [SerializeIgnore] private GameObject _geometry;
    [SerializeIgnore] private Scene _litScene;

    /// <param name="geometry">The <see cref="MountedMap.Instantiate"/>d map under this object, which the map's models join.</param>
    internal void Bind(MapSpawnable source, MountedMap map, GameObject geometry)
    {
        _source = source;
        _map = map;
        _geometry = geometry;
    }

    public override void OnEnable()
    {
        if (!s_all.Contains(this)) s_all.Add(this);
        SetLayer(GameObject, Layer);
        KeepSunOff();

        if (_map == null || _geometry.IsNotValid() || _litScene.IsValid() || GameObject.Scene.IsNotValid()) return;
        _litScene = GameObject.Scene;
        _map.AddLighting(_litScene, _geometry);
        AimSun();
    }

    public override void OnDisable()
    {
        s_all.Remove(this);
        RemoveLighting();
        RestoreSun();
    }

    protected override void OnDispose()
    {
        s_all.Remove(this);
        RemoveLighting();
        RestoreSun();
        base.OnDispose();
    }

    /// <summary>Each sun's rotation from before a map turned it, to give back once no map with a sun is left.</summary>
    private static readonly Dictionary<Light, Quaternion> s_sunRotations = new();

    /// <summary>
    /// Turns the scene's suns to shine the way the map's baked sunlight does, so the sky's sun and anything lit in
    /// real time agree with the map's shadows.
    /// </summary>
    private void AimSun()
    {
        if (_map?.SunDirection is not { } local || _geometry.IsNotValid()) return;
        Float3 direction = Float3.Normalize(_geometry.Transform.TransformDirection(local));
        Float3 up = MathF.Abs(direction.Y) > 0.99f ? Float3.UnitZ : Float3.UnitY;
        foreach (Light light in Scene.Current.FindObjectsOfType<Light>())
        {
            if (light.Type != LightType.Directional) continue;
            s_sunRotations.TryAdd(light, light.Transform.Rotation);
            light.Transform.Rotation = Quaternion.LookRotation(direction, up);
        }
    }

    private void RestoreSun()
    {
        if (_map?.SunDirection == null) return;
        foreach (MapProp other in s_all)
            if (other != this && other.IsValid() && other._map?.SunDirection != null) return;
        foreach ((Light light, Quaternion rotation) in s_sunRotations)
            if (light.IsValid()) light.Transform.Rotation = rotation;
        s_sunRotations.Clear();
    }

    /// <summary>True if the map wants cameras inside it drawn without tonemapping, as its game drew it.</summary>
    public bool SourceLook => _map?.SourceLook ?? false;

    /// <summary>The light of the floor under <paramref name="world"/>, from this map's lightmaps. False off the map.</summary>
    public bool TrySampleLight(Float3 world, out Float3 light)
    {
        light = default;
        if (_map?.Light == null || _geometry.IsNotValid()) return false;
        Transform space = _geometry.Transform;
        return _map.Light.TrySample(space.InverseTransformPoint(world), out light);
    }

    /// <summary>
    /// Where the floor light under <paramref name="world"/> sits in this scene's lightmaps: the page's index in the
    /// scene's baked lighting and the UV in it. False off the map, or for maps that cannot say.
    /// </summary>
    public bool TryFindLuxel(Float3 world, out int sceneIndex, out Float2 uv)
    {
        sceneIndex = -1;
        uv = default;
        if (_map?.Light is not IMapLightmapLight light || _geometry.IsNotValid() || _litScene.IsNotValid()) return false;
        if (!light.TryFindLuxel(_geometry.Transform.InverseTransformPoint(world), out int page, out uv)) return false;
        if (page < 0 || page >= _map.LightmapPages.Count) return false;
        sceneIndex = _litScene.BakedLighting.Lightmaps.IndexOf(_map.LightmapPages[page]);
        return sceneIndex >= 0;
    }

    /// <summary>Puts an object and everything under it on <paramref name="layer"/>.</summary>
    public static void SetLayer(GameObject go, int layer)
    {
        go.LayerIndex = layer;
        foreach (GameObject child in go.Children) SetLayer(child, layer);
    }

    // Directional lights are the sun: none of them reaches the map layer.
    private static void KeepSunOff()
    {
        foreach (Light light in Scene.Current.FindObjectsOfType<Light>())
        {
            if (light.Type != LightType.Directional) continue;
            uint mask = (uint)light.LayerMask.Mask;
            if ((mask & (1u << Layer)) != 0) light.LayerMask = LayerMask.FromMask(mask & ~(1u << Layer));
        }
    }

    private void RemoveLighting()
    {
        if (_map != null && _litScene.IsValid()) _map.RemoveLighting(_litScene, _geometry);
        _litScene = null;
    }

    public override void Start()
    {
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

                // Part of the map: its own physics and prop marker go.
                foreach (Component c in go.GetComponentsInChildren<Component>(true, true).ToList())
                    if (c is Rigidbody3D or Collider or Prop) c.GameObject.RemoveComponent(c);

                if (spawnable is MountSpawnable mounted && mounted.Loader.GetOrCreate<MountedModel>() is { } built)
                {
                    if (model.Skin != 0) built.ApplySkin(go, model.Skin);
                    // Solid props block with their own shape, as the map's static props do.
                    if (model.Solid)
                        foreach (MeshRenderer renderer in go.GetComponentsInChildren<MeshRenderer>())
                            if (renderer.Mesh.IsValid()) renderer.GameObject.AddComponent<MeshCollider>().Mesh = renderer.Mesh;
                }

                Transform space = _geometry.Transform;
                Float3 local = model.Position;
                if (!model.Exact && !s_floating.Contains(model.ClassName))
                {
                    Float3 from = space.TransformPoint(local + Float3.UnitY * 0.1f);
                    if (Scene.Current.Physics.Raycast(from, -space.Up, out RaycastHit hit, 4f, QueryFilter.Default))
                        local = space.InverseTransformPoint(hit.Point);
                }

                go.SetParent(_geometry, false);
                SetLayer(go, Layer);
                go.Transform.LocalPosition = local;
                go.Transform.LocalRotation = model.Rotation;
                if (model.Scale != 1f) go.Transform.LocalScale = new Float3(model.Scale);
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
