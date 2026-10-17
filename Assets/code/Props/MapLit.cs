using System;
using System.Collections.Generic;
using System.Linq;
using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// Lights a model by the map it stands in, as the game does: from the map's light on the floor under it. Inside a
/// map the model joins the map layer, out of the sun. Where the map can say which luxel that floor light is, the
/// model's renderers read it straight from the map's lightmap as their indirect light, like the map's own
/// surfaces; otherwise they are tinted so the scene's ambient light comes out as the map's. Outside every map it
/// is left as it was.
/// </summary>
[DisallowMultipleComponent]
public sealed class MapLit : Component
{
    /// <summary>Seconds between looks at the light, or sooner once moved further than <see cref="MoveThreshold"/>.</summary>
    private const float Interval = 0.25f, MoveThreshold = 0.1f;

    /// <summary>How far the tint may brighten, against how dim the scene's ambient light is.</summary>
    private const float MaxTint = 4f;

    [SerializeIgnore] private bool _inMap;
    [SerializeIgnore] private int _outsideLayer;
    [SerializeIgnore] private float _nextCheck;
    [SerializeIgnore] private Float3 _checkedAt = new(float.MaxValue);
    [SerializeIgnore] private readonly List<IColorTint> _tints = new();
    [SerializeIgnore] private readonly List<Guid> _placed = new();
    [SerializeIgnore] private Scene _placedScene;

    /// <summary>Each renderer's own materials, kept while it wears its map variants.</summary>
    [SerializeIgnore] private readonly Dictionary<MeshRenderer, Material[]> _ownMaterials = new();

    public override void Update()
    {
        Float3 position = Transform.Position;
        if (Time.TimeSinceStartup < _nextCheck && Float3.Distance(position, _checkedAt) < MoveThreshold) return;
        _nextCheck = (float)Time.TimeSinceStartup + Interval;
        _checkedAt = position;

        // Sampled at the middle of the model, as the game lights a prop from its centre: its origin often sits in
        // the floor or the ledge it stands on, out in solid space where there is no light to find.
        Float3 probe = Transform.TransformPoint(LocalCentre());
        foreach (MapProp map in MapProp.All)
        {
            if (map.IsNotValid()) continue;
            if (TryFindTypicalLuxel(map, probe, out int page, out Float2 uv))
            {
                EnterLightmapped(page, uv);
                return;
            }
            if (map.TrySampleLight(probe, out Float3 light))
            {
                EnterTinted(light);
                return;
            }
        }

        if (_inMap) Leave();
    }

    [SerializeIgnore] private Float3? _localCentre;

    /// <summary>The middle of the model's meshes relative to it, a little above its origin when it has none.</summary>
    private Float3 LocalCentre()
    {
        if (_localCentre is { } known) return known;
        AABB bounds = Spawner.LocalBounds(GameObject);
        Float3 centre = bounds.Size.X > 0f || bounds.Size.Y > 0f ? bounds.Center : Float3.UnitY * 0.2f;
        // Very tall models, such as players, are lit from about chest height rather than their middle.
        centre.Y = MathF.Min(centre.Y, 1.2f);
        _localCentre = centre;
        return centre;
    }

    /// <summary>Offsets around the model the floor is sampled at, in metres.</summary>
    private static readonly Float3[] s_around =
    [
        Float3.Zero, new(0.75f, 0f, 0f), new(-0.75f, 0f, 0f), new(0f, 0f, 0.75f), new(0f, 0f, -0.75f),
    ];

    /// <summary>
    /// The floor's luxel that best stands for the light around the model: of the floor under it and a little to
    /// each side, the one nearest their average, so a model over a dark planter or a crack still takes the light
    /// of where it stands, as the game lights props by the light around them.
    /// </summary>
    private static bool TryFindTypicalLuxel(MapProp map, Float3 probe, out int page, out Float2 uv)
    {
        page = -1;
        uv = default;
        Span<float> brightness = stackalloc float[s_around.Length];
        Span<int> pages = stackalloc int[s_around.Length];
        Span<Float2> uvs = stackalloc Float2[s_around.Length];
        int count = 0;
        float total = 0f;
        foreach (Float3 offset in s_around)
        {
            Float3 at = probe + offset;
            if (!map.TryFindLuxel(at, out int p, out Float2 u) || !map.TrySampleLight(at, out Float3 light)) continue;
            float value = light.X * 0.3f + light.Y * 0.59f + light.Z * 0.11f;
            brightness[count] = value;
            pages[count] = p;
            uvs[count] = u;
            total += value;
            count++;
        }
        if (count == 0) return false;

        float mean = total / count;
        int best = 0;
        for (int i = 1; i < count; i++)
            if (MathF.Abs(brightness[i] - mean) < MathF.Abs(brightness[best] - mean)) best = i;
        page = pages[best];
        uv = uvs[best];
        return true;
    }

    public override void OnDisable()
    {
        if (_inMap) Leave();
    }

    private void Join()
    {
        if (!_inMap)
        {
            _inMap = true;
            _outsideLayer = GameObject.LayerIndex;
        }

        // Every time, for children made since, such as a view model rebuilt for other equipment.
        MapProp.SetLayer(GameObject, MapProp.Layer);
    }

    /// <summary>Every renderer reads the floor's luxel from the map's lightmap page as its indirect light.</summary>
    private void EnterLightmapped(int page, Float2 uv)
    {
        Join();
        SetTint(Color.White);
        ClearPlacements();

        Scene scene = GameObject.Scene;
        if (scene.IsNotValid()) return;
        _placedScene = scene;
        var placement = new Scene.LightmapPlacement { Index = page, ScaleOffset = new Float4(0f, 0f, uv.X, uv.Y) };
        foreach (Component component in GetComponentsInChildren<Component>())
        {
            if (component is not (MeshRenderer or SkinnedMeshRenderer)) continue;
            Guid id = component.GameObject.Identifier;
            scene.BakedLighting.Placements[id] = placement;
            _placed.Add(id);

            // Drawn like the map's own surfaces, without Lit's sky reflection.
            if (component is MeshRenderer renderer && !_ownMaterials.ContainsKey(renderer) && renderer.Materials is { Count: > 0 } own)
            {
                _ownMaterials[renderer] = [.. own];
                renderer.Materials = [.. own.Select(Slopbox.Mounting.Source.SourceMaterials.MapVariant)];
            }
        }
    }

    private void EnterTinted(Float3 light)
    {
        Join();
        ClearPlacements();
        RestoreMaterials();

        // Out of the sun, the model sees only the ambient light; scale that to the map's.
        Color ambient = Scene.Current.Environment.Ambient.Color;
        float intensity = Scene.Current.Environment.Ambient.Intensity;
        SetTint(new Color(
            Tint(light.X, ambient.R * intensity),
            Tint(light.Y, ambient.G * intensity),
            Tint(light.Z, ambient.B * intensity),
            1f));
    }

    private void Leave()
    {
        _inMap = false;
        MapProp.SetLayer(GameObject, _outsideLayer);
        SetTint(Color.White);
        ClearPlacements();
        RestoreMaterials();
    }

    private void RestoreMaterials()
    {
        foreach ((MeshRenderer renderer, Material[] own) in _ownMaterials)
            if (renderer.IsValid()) renderer.Materials = [.. own];
        _ownMaterials.Clear();
    }

    private void ClearPlacements()
    {
        if (_placedScene.IsValid())
            foreach (Guid id in _placed)
                _placedScene.BakedLighting.Placements.Remove(id);
        _placed.Clear();
        _placedScene = null;
    }

    private static float Tint(float light, float ambient) => Maths.Clamp(light / Maths.Max(ambient, 0.05f), 0f, MaxTint);

    private void SetTint(Color tint)
    {
        _tints.Clear();
        foreach (Component component in GetComponentsInChildren<Component>())
            if (component is IColorTint colorTint and (SkinnedMeshRenderer or MeshRenderer)) _tints.Add(colorTint);
        foreach (IColorTint colorTint in _tints) colorTint.Tint = tint;
    }
}
