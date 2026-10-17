using System;
using System.Collections.Generic;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox.Mounting;

/// <summary>The light a map's surfaces give the models standing in it.</summary>
public interface IMapLight
{
    /// <summary>
    /// The linear light of the floor under <paramref name="position"/>, relative to the map, as the game lights a
    /// model standing there. False when there is no floor below.
    /// </summary>
    bool TrySample(Float3 position, out Float3 light);
}

/// <summary>
/// A map light that can also say where in its lightmap pages a point's floor light sits, so models standing there
/// can read it the way the map's own surfaces do.
/// </summary>
public interface IMapLightmapLight : IMapLight
{
    /// <summary>
    /// The lightmap page, an index into <see cref="MountedMap.LightmapPages"/>, and the UV in it of the floor luxel
    /// under <paramref name="position"/>, relative to the map. False when there is no floor below.
    /// </summary>
    bool TryFindLuxel(Float3 position, out int page, out Float2 uv);
}

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

    /// <summary>
    /// Drawn the way its game drew it: no tonemapping, colours clamped, so a camera inside the map switches its
    /// tonemapper off (see MapCameraLook).
    /// </summary>
    public bool SourceLook { get; init; }

    /// <summary>The way the map's sunlight travels, relative to the map, or null when it has no sun.</summary>
    public Float3? SunDirection { get; init; }

    /// <summary>
    /// The sun's light on a surface facing it, in the lightmaps' linear units, or null when it has no sun. The
    /// lightmaps hold it already; models, which the game lights at runtime, take it from a light.
    /// </summary>
    public Float3? SunLight { get; init; }

    /// <summary>How the shadows of moving things fall on the map, as the game projects them.</summary>
    public readonly record struct ShadowSettings(Float3 Direction, float Darkness, bool Enabled);

    /// <summary>The map's own shadow settings, or null to use its sun, else straight down.</summary>
    public ShadowSettings? Shadows { get; init; }

    /// <summary>Finds the map's sounds and soundscapes, or null when it has none.</summary>
    public IMapAudio Audio { get; init; }

    public string Name { get; }
    public IReadOnlyList<Part> Parts { get; }

    /// <summary>RGBM lightmap pages. A part's UV2 is already in its page's space.</summary>
    public IReadOnlyList<Texture2D> LightmapPages { get; }

    public IReadOnlyList<SpawnPoint> SpawnPoints { get; }

    /// <summary>The NPCs, items and other models the map places.</summary>
    public IReadOnlyList<PlacedModel> Models { get; }

    /// <summary>Lights models standing in the map, or null when the map has no light to give.</summary>
    public IMapLight Light { get; init; }
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
            // Its shadows are in its lightmaps; realtime ones come only from what moves in it.
            renderer.ShadowCasting = ShadowCasting.Off;

            if (part.Solid) child.AddComponent<MeshCollider>().Mesh = part.Mesh;
        }
        return root;
    }

    /// <summary>
    /// Adds the map's lightmap pages to <paramref name="scene"/>, placed for the parts under <paramref name="root"/>,
    /// which must be an <see cref="Instantiate"/>d copy of this map.
    /// </summary>
    public void AddLighting(Scene scene, GameObject root)
    {
        int firstPage = scene.BakedLighting.Lightmaps.Count;
        scene.BakedLighting.Lightmaps.AddRange(LightmapPages);

        int i = 0;
        foreach (GameObject child in root.Children)
        {
            if (i >= Parts.Count) break;
            Part part = Parts[i++];
            if (part.LightmapPage < 0) continue;
            scene.BakedLighting.Placements[child.Identifier] = new Scene.LightmapPlacement
            {
                Index = firstPage + part.LightmapPage,
                ScaleOffset = new Float4(1f, 1f, 0f, 0f),
            };
        }
    }

    /// <summary>Takes this map's lightmaps for <paramref name="root"/> back out of <paramref name="scene"/>.</summary>
    public void RemoveLighting(Scene scene, GameObject root)
    {
        if (scene.IsNotValid()) return;
        Scene.BakedLightingData baked = scene.BakedLighting;
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
            Scene.LightmapPlacement placement = baked.Placements[id];
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
