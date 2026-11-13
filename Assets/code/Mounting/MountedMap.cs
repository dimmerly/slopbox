using System;
using System.Collections.Generic;
using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox.Mounting;

/// <summary>
/// A map built from a mounted game's file: its world and brush entities as meshes, each part optionally solid
/// and lit by one of the map's lightmap pages, and where players start. In Prowl's space (metres, Y up). What a
/// <see cref="ResourceType.Scene"/> loader gives. The meshes, materials and pages are runtime assets shared by
/// every instance; disposing the map frees them.
/// </summary>
public sealed class MountedMap : IDisposable
{
    /// <summary>One renderer of the map.</summary>
    /// <param name="LightmapPage">Index into <see cref="LightmapPages"/>, or -1 for none.</param>
    /// <param name="Solid">Gets a static mesh collider.</param>
    public sealed record Part(string Name, Mesh Mesh, Material[] Materials, int LightmapPage, bool Solid);

    /// <summary>
    /// The light around a point, as the light reaching it from each side: <paramref name="Sides"/> holds +X, -X, +Y,
    /// -Y, +Z and -Z in the map's space, in the lightmaps' units.
    /// </summary>
    public sealed record Probe(Float3 Position, Float3[] Sides);

    /// <summary>A place a player can start, with the way they face.</summary>
    public readonly record struct SpawnPoint(Float3 Position, float Yaw);

    /// <summary>A model the map places, such as an NPC or an item, by its path in the game ("models/scientist.mdl").</summary>
    public sealed record PlacedModel(string ClassName, string Model, Float3 Position, Quaternion Rotation)
    {
        /// <summary>Stays exactly where the map put it, rather than being dropped to the floor.</summary>
        public bool Exact { get; init; }

        /// <summary>Gets colliders from its meshes, as the map's static props do.</summary>
        public bool Solid { get; init; }

        /// <summary>Which of the model's skins it wears.</summary>
        public int Skin { get; init; }

        public float Scale { get; init; } = 1f;
    }

    /// <summary>
    /// A sound the map plays from a place, such as a hum or dripping water, by its name in the game: a file below
    /// sound/ or a sound script entry.
    /// </summary>
    /// <param name="Radius">How far it carries, in metres.</param>
    /// <param name="Everywhere">Heard the same anywhere in the map.</param>
    public sealed record PlacedSound(string Sound, Float3 Position, float Volume, float Pitch, float Radius, bool Everywhere);

    /// <summary>Where a soundscape takes over: within <paramref name="Radius"/> metres, or anywhere if negative.</summary>
    public sealed record SoundscapeZone(string Name, Float3 Position, float Radius);

    /// <summary>Looping sounds the map places.</summary>
    public IReadOnlyList<PlacedSound> Sounds { get; init; } = [];

    /// <summary>Where each soundscape plays.</summary>
    public IReadOnlyList<SoundscapeZone> Soundscapes { get; init; } = [];

    /// <summary>Light probes for what moves in the map, so models are lit like the surfaces around them.</summary>
    public IReadOnlyList<Probe> Probes { get; init; } = [];

    /// <summary>
    /// The map's sun, which the game shines on models in real time and leaves out of <see cref="Probes"/>: the way its
    /// light travels in the map's space, and its colour in the lightmaps' units. Null when the map has none.
    /// </summary>
    public (Float3 Direction, Float3 Light)? Sun { get; init; }

    /// <summary>The map's 2D skybox as a sky material, or null when it has none.</summary>
    public Material Sky { get; init; }

    /// <summary>
    /// Nits per unit of the map's light: what its lightmaps and probes are scaled by, the light the game shows as a
    /// texture at full brightness, under the game's daylight exposure of EV100 14.
    /// </summary>
    public const float LightScale = 1.2f * 16384f;

    /// <summary>Finds the map's sounds and soundscapes, or null when it has none.</summary>
    public IMapAudio Audio { get; init; }

    public string Name { get; }
    public IReadOnlyList<Part> Parts { get; }

    /// <summary>RGBM lightmap pages. A part's UV2 is already in its page's space.</summary>
    public IReadOnlyList<Texture2D> LightmapPages { get; }

    public IReadOnlyList<SpawnPoint> SpawnPoints { get; }

    /// <summary>The NPCs, items and other models the map places.</summary>
    public IReadOnlyList<PlacedModel> Models { get; }

    public AABB Bounds { get; }

    /// <summary>Textures the map made itself, freed with it. Shared ones, such as WAD textures, are not listed.</summary>
    private readonly IReadOnlyList<Texture2D> _ownedTextures;
    private readonly IReadOnlyList<Material> _materials;

    public MountedMap(string name, IReadOnlyList<Part> parts, IReadOnlyList<Texture2D> lightmapPages,
        IReadOnlyList<SpawnPoint> spawnPoints, IReadOnlyList<PlacedModel> models, AABB bounds,
        IReadOnlyList<Material> materials, IReadOnlyList<Texture2D> ownedTextures)
    {
        Models = models;
        Name = name;
        Parts = parts;
        LightmapPages = lightmapPages;
        SpawnPoints = spawnPoints;
        Bounds = bounds;
        _materials = materials;
        _ownedTextures = ownedTextures;
    }

    /// <summary>
    /// A new object showing the map, a child per part, not in any scene. Its lightmaps are handed to the scene's
    /// baked lighting while it is in one (see <see cref="AddLighting"/>), which the standard shaders read through
    /// each part's second UV set.
    /// </summary>
    public GameObject Instantiate()
    {
        var root = new GameObject(Name);
        foreach (Part part in Parts)
        {
            var child = new GameObject(part.Name);
            child.SetParent(root, false);

            var renderer = child.AddComponent<MeshRenderer>();
            renderer.Mesh = part.Mesh;
            renderer.Materials = [.. part.Materials];

            if (part.Solid) child.AddComponent<MeshCollider>().Mesh = part.Mesh;
        }
        return root;
    }

    /// <summary>
    /// Adds the map's lightmap pages to <paramref name="section"/>, placed for the parts under <paramref name="root"/>,
    /// which must be an <see cref="Instantiate"/>d copy of this map.
    /// </summary>
    public void AddLighting(SceneSection section, GameObject root)
    {
        int firstPage = section.Lighting.Lightmaps.Count;
        // The section's pages share one scale. A section with none of its own takes the map's; one baked in the
        // editor keeps its own, which the editor's bake sets for the same daylight exposure.
        if (firstPage == 0) section.Lighting.LightmapScale = LightScale;
        else if (MathF.Abs(section.Lighting.LightmapScale - LightScale) > LightScale * 0.01f)
            Debug.LogWarning($"{Name}: the scene's lightmaps are scaled by {section.Lighting.LightmapScale}, not {LightScale}, so the map shows brighter or darker than in its game");
        section.Lighting.Lightmaps.AddRange(LightmapPages);

        int i = 0;
        foreach (GameObject child in root.Children)
        {
            if (i >= Parts.Count) break;
            Part part = Parts[i++];
            if (part.LightmapPage < 0) continue;
            section.Lighting.Placements[child.Identifier] = new LightmapPlacement
            {
                Index = firstPage + part.LightmapPage,
                ScaleOffset = new Float4(1f, 1f, 0f, 0f),
            };
        }
    }

    /// <summary>Directions spread evenly over the sphere, to project each probe onto spherical harmonics.</summary>
    private static readonly Float3[] s_sphere = Fibonacci(256);

    /// <summary>
    /// Adds this map's probes, placed and turned by <paramref name="space"/>, to <paramref name="positions"/> and
    /// <paramref name="sh"/>, fitted so a surface facing any way gets the light the probe's sides give it.
    /// </summary>
    /// <param name="seesSky">Whether nothing is between a world position and the sky the way given; the sun lights only those.</param>
    public void AppendProbes(Transform space, List<Float3> positions, List<SphericalHarmonicsL2> sh, Func<Float3, Float3, bool> seesSky = null)
    {
        (Float3 Direction, Float3 Light)? sun = Sun;
        Float3 toSunWorld = sun is { } s0 ? -Float3.Normalize(space.TransformDirection(s0.Direction)) : default;
        var lit = new Float3[6];
        // Real SH basis constants and the cosine lobe's band weights, as SphericalHarmonicsL2 uses.
        const float K0 = 0.2820947918f, K1 = 0.4886025119f, K2a = 1.0925484306f, K2b = 0.3153915652f, K2c = 0.5462742153f;
        const float A1 = 2f / 3f, A2 = 1f / 4f;
        // In nits, as the renderer takes probes, like the lightmaps beside them
        float weight = 4f * MathF.PI / s_sphere.Length * LightScale;

        foreach (Probe probe in Probes)
        {
            // The game shines its sun on a model when nothing of the map is between the model and the sky
            Float3[] sides = probe.Sides;
            Float3 at = space.TransformPoint(probe.Position);
            if (sun is { } s && seesSky != null && seesSky(at, toSunWorld))
            {
                Float3 toSun = -s.Direction;
                Float3[] axes = [Float3.UnitX, -Float3.UnitX, Float3.UnitY, -Float3.UnitY, Float3.UnitZ, -Float3.UnitZ];
                for (int i = 0; i < 6; i++) lit[i] = probe.Sides[i] + s.Light * MathF.Max(0f, Float3.Dot(toSun, axes[i]));
                sides = lit;
            }

            var c = new Float3[9];
            foreach (Float3 world in s_sphere)
            {
                Float3 d = space.InverseTransformDirection(world);
                Float3 light = d.X * d.X * (d.X >= 0f ? sides[0] : sides[1])
                    + d.Y * d.Y * (d.Y >= 0f ? sides[2] : sides[3])
                    + d.Z * d.Z * (d.Z >= 0f ? sides[4] : sides[5]);
                light *= weight;
                float x = world.X, y = world.Y, z = world.Z;
                c[0] += light * K0;
                c[1] += light * (K1 * y);
                c[2] += light * (K1 * z);
                c[3] += light * (K1 * x);
                c[4] += light * (K2a * x * y);
                c[5] += light * (K2a * y * z);
                c[6] += light * (K2b * (3f * z * z - 1f));
                c[7] += light * (K2a * x * z);
                c[8] += light * (K2c * (x * x - y * y));
            }

            // The sides are already the light a surface facing them gets, so undo the cosine lobe's blur.
            positions.Add(at);
            sh.Add(new SphericalHarmonicsL2
            {
                C0 = c[0],
                C1 = c[1] / A1, C2 = c[2] / A1, C3 = c[3] / A1,
                C4 = c[4] / A2, C5 = c[5] / A2, C6 = c[6] / A2, C7 = c[7] / A2, C8 = c[8] / A2,
            });
        }
    }

    private static Float3[] Fibonacci(int count)
    {
        var directions = new Float3[count];
        float golden = MathF.PI * (3f - MathF.Sqrt(5f));
        for (int i = 0; i < count; i++)
        {
            float y = 1f - 2f * (i + 0.5f) / count;
            float r = MathF.Sqrt(1f - y * y);
            directions[i] = new Float3(MathF.Cos(golden * i) * r, y, MathF.Sin(golden * i) * r);
        }
        return directions;
    }

    /// <summary>Takes this map's lightmaps for <paramref name="root"/> back out of <paramref name="section"/>.</summary>
    public void RemoveLighting(SceneSection section, GameObject root)
    {
        if (section == null) return;
        SceneLighting baked = section.Lighting;
        if (root.IsValid())
            foreach (GameObject child in root.Children)
                baked.Placements.Remove(child.Identifier);

        // Taking pages out moves the ones after them down, so every other map's placements follow their pages.
        var removed = new List<int>();
        foreach (Texture2D page in LightmapPages)
        {
            int index = baked.Lightmaps.IndexOf(page);
            if (index >= 0) removed.Add(index);
        }
        if (removed.Count == 0) return;
        removed.Sort();
        for (int i = removed.Count - 1; i >= 0; i--) baked.Lightmaps.RemoveAt(removed[i]);

        foreach (Guid id in new List<Guid>(baked.Placements.Keys))
        {
            LightmapPlacement placement = baked.Placements[id];
            int shift = 0;
            foreach (int index in removed)
                if (index < placement.Index) shift++;
            if (shift == 0) continue;
            placement.Index -= shift;
            baked.Placements[id] = placement;
        }
    }

    public void Dispose()
    {
        foreach (Part part in Parts)
            if (part.Mesh.IsValid()) part.Mesh.Dispose();
        foreach (Material material in _materials)
            if (material.IsValid()) material.Dispose();
        foreach (Texture2D texture in LightmapPages)
            if (texture.IsValid()) texture.Dispose();
        foreach (Texture2D texture in _ownedTextures)
            if (texture.IsValid()) texture.Dispose();
        (Audio as IDisposable)?.Dispose();
    }
}
