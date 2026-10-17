using System;
using System.Linq;
using System.Text;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Slopbox.Mounting;

namespace Slopbox;

/// <summary>The little glyph the spawn menu draws for an entry until real thumbnails exist.</summary>
public enum SpawnIcon { Model, Cube, Sphere, Cylinder, Plate, Light }

/// <summary>
/// One entry in the spawn menu. <see cref="Create"/> builds the object detached from the scene, fully
/// set up with its physics; the <see cref="Spawner"/> places it and adds it.
/// </summary>
public abstract class Spawnable
{
    /// <summary>Stable id, used by the duplicator and for saving.</summary>
    public string Id { get; protected init; } = "";
    public string Name { get; protected init; } = "";

    /// <summary>The tab it is listed under, such as "Props" or "Shapes".</summary>
    public string Category { get; protected init; } = "";

    /// <summary>A group inside the tab, such as a props sub folder. Empty for none.</summary>
    public string Group { get; protected init; } = "";

    public virtual SpawnIcon Icon => SpawnIcon.Model;
    public virtual Color IconColor => new(0.8f, 0.8f, 0.85f, 1f);

    /// <summary>How strongly the spawn menu thumbnail is lit. Textured models tend to be dark and want more.</summary>
    public virtual float ThumbnailLighting => 0.6f;

    /// <summary>Builds the object, not yet in any scene. Return null if it could not be made.</summary>
    public abstract GameObject Create();

    /// <summary>False for entries too big to preview or place in batches, such as maps. They get no thumbnail and the placer skips them.</summary>
    public virtual bool IsProp => true;

    /// <summary>What clicking the entry in the spawn menu does: spawn it at the crosshair.</summary>
    public virtual void Activate(Player player) => Spawner.SpawnAtCrosshair(player, this);

    /// <summary>
    /// A display name from a file name: "wooden_crate_1" becomes "Wooden Crate". Number tokens and the
    /// short codes asset packs append ("toilet_mp_1_1", "metal_barrel_hr_1") are dropped.
    /// </summary>
    public static string Prettify(string raw)
    {
        var words = new System.Collections.Generic.List<string>();
        foreach (string token in raw.Split('_', '-', '.', ' '))
        {
            if (token.Length == 0 || token.All(char.IsDigit)) continue;
            words.Add(token);
        }

        // Trailing one or two letter codes, unless that would leave nothing.
        while (words.Count > 1 && words[^1].Length <= 2) words.RemoveAt(words.Count - 1);
        if (words.Count == 0) words.Add(raw);

        var sb = new StringBuilder(raw.Length);
        foreach (string word in words)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToUpperInvariant(word[0])).Append(word, 1, word.Length - 1);
        }
        return sb.ToString();
    }
}

/// <summary>A model prop from a Resources folder. It gets a box collider around its bounds and a rigidbody.</summary>
public sealed class ModelSpawnable : Spawnable
{
    /// <summary>Kilograms per cubic metre of the model's bounding box, used to pick a mass.</summary>
    public const float Density = 150f;

    public Guid AssetId { get; }

    public override float ThumbnailLighting => 1.25f;

    // Held once spawned, so the model and its meshes stay loaded for the next spawn.
    private PrefabAsset _prefab;

    public ModelSpawnable(ResourceEntry entry, string category)
    {
        AssetId = entry.Guid;
        Id = "model:" + entry.Guid;
        Category = category;

        string path = entry.LoadPath;
        int slash = path.LastIndexOf('/');
        Name = Prettify(slash >= 0 ? path[(slash + 1)..] : path);

        // "props/basic/crate" lists under group "Basic".
        string[] parts = path.Split('/');
        Group = parts.Length > 2 ? Prettify(parts[^2]).Replace(" And ", " & ") : "";
    }

    public override GameObject Create()
    {
        if (_prefab.IsNotValid()) _prefab = AssetDatabase.Get<PrefabAsset>(AssetId);
        PrefabAsset prefab = _prefab;
        if (prefab.IsNotValid()) return null;

        // Load the meshes and materials too, not only the prefab, so reading them below does not
        // reload each one on demand.
        using (AssetGroup group = AssetDatabase.LoadGroup([prefab]))
            group.Wait();

        GameObject go = GameObject.InstantiateDetached(prefab);
        if (go.IsNotValid()) return null;

        go.Transform.Position = Float3.Zero;
        go.Transform.Rotation = Quaternion.Identity;

        AddBoxPhysics(go, Id);
        return go;
    }

    /// <summary>
    /// Makes a model a prop: one box around the whole model, a rigidbody weighed by the box's volume, and a
    /// <see cref="Prop"/>. Flat models get a little thickness so they cannot tunnel.
    /// </summary>
    public static void AddBoxPhysics(GameObject go, string spawnId)
    {
        AABB bounds = Spawner.LocalBounds(go);
        Float3 size = Maths.Max(bounds.Size, new Float3(0.02f));
        var collider = go.AddComponent<BoxCollider>();
        collider.Size = size;
        collider.Center = bounds.Center;

        var body = go.AddComponent<Rigidbody3D>();
        body.Mass = Math.Clamp(size.X * size.Y * size.Z * Density, 2f, 500f);

        go.AddComponent<Prop>().SpawnId = spawnId;
    }
}

/// <summary>
/// A model from a mounted game, such as a Half-Life .mdl. It is built the first time it is spawned or shown,
/// then every spawn shares its meshes and materials. Listed under "Mounts", grouped as "Game/Kind".
/// </summary>
public sealed class MountSpawnable : Spawnable
{
    public ResourceLoader Loader { get; }

    public override float ThumbnailLighting => 1.25f;

    public MountSpawnable(ResourceLoader loader)
    {
        Loader = loader;
        Id = "mount:" + loader.Path;
        Category = "Mounts";
        Name = Prettify(loader.Name);
        Group = loader.Mount.Title + "/" + KindOf(loader.RelativePath, loader.Mount is Slopbox.Mounting.Source.SourceMount);
    }

    public override GameObject Create()
    {
        var model = Loader.GetOrCreate<MountedModel>();
        if (model == null || model.Parts.Count == 0) return null;

        GameObject go = model.Instantiate();
        go.Name = Name;
        ModelSpawnable.AddBoxPhysics(go, Id);
        go.AddComponent<MapLit>();
        return go;
    }

    /// <summary>
    /// What a GoldSrc model is from its path and name: "valve/models/w_crowbar.mdl" is a weapon,
    /// "valve_hd/models/barney.mdl" an HD model, "valve/models/player/gman/gman.mdl" a player model.
    /// </summary>
    private static string KindOf(string relativePath, bool source)
    {
        string[] parts = relativePath.ToLowerInvariant().Split('/');

        // Source games sort their models into folders, such as "hl2/models/props_c17/oildrum001.mdl".
        if (source)
            return parts.Length > 3 && parts[1] == "models" ? parts[2] : "models";

        string file = parts[^1];
        string kind =
            parts.Contains("player") ? "Players" :
            file.StartsWith("w_") ? "Weapons" :
            file.StartsWith("v_") || file.StartsWith("p_") ? "Viewmodels" :
            file.Contains("gib") ? "Gibs" :
            "Models";
        return parts[0].EndsWith("_hd") ? "HD " + kind : kind;
    }
}

/// <summary>A built in physics shape: boxes, plates, balls and cylinders, like gmod's PHX parts.</summary>
public sealed class ShapeSpawnable : Spawnable
{
    public enum Shape { Box, Sphere, Cylinder }

    private readonly Shape _shape;
    private readonly Float3 _size;
    private readonly Color _color;
    private readonly float _mass;
    private Mesh _mesh;
    private Material _material;

    public override SpawnIcon Icon => _shape switch
    {
        Shape.Sphere => SpawnIcon.Sphere,
        Shape.Cylinder => SpawnIcon.Cylinder,
        _ => _size.Y < _size.X * 0.3f ? SpawnIcon.Plate : SpawnIcon.Cube,
    };

    public override Color IconColor => _color;

    /// <param name="size">Box extents, or (diameter, height, diameter) for cylinders and diameter in X for spheres.</param>
    public ShapeSpawnable(string id, string name, string group, Shape shape, Float3 size, Color color, float mass)
    {
        Id = "shape:" + id;
        Name = name;
        Category = "Shapes";
        Group = group;
        _shape = shape;
        _size = size;
        _color = color;
        _mass = mass;
    }

    public override GameObject Create()
    {
        // Every spawn of this entry shares one mesh and material. Tinting clones the material per prop.
        if (_mesh.IsNotValid())
        {
            _mesh = _shape switch
            {
                Shape.Sphere => Mesh.CreateSphere(_size.X * 0.5f, 16, 24),
                Shape.Cylinder => Mesh.CreateCylinder(_size.X * 0.5f, _size.Y, 24),
                _ => Mesh.CreateCube(_size),
            };
        }
        if (_material.IsNotValid()) _material = Materials.Lit(_color, 0f, 0.55f);

        var go = new GameObject(Name);
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.Mesh = _mesh;
        renderer.Material = _material;

        switch (_shape)
        {
            case Shape.Sphere:
                go.AddComponent<SphereCollider>().Radius = _size.X * 0.5f;
                break;
            case Shape.Cylinder:
                var cylinder = go.AddComponent<CylinderCollider>();
                cylinder.Radius = _size.X * 0.5f;
                cylinder.Height = _size.Y;
                break;
            default:
                go.AddComponent<BoxCollider>().Size = _size;
                break;
        }

        go.AddComponent<Rigidbody3D>().Mass = _mass;
        go.AddComponent<Prop>().SpawnId = Id;
        return go;
    }
}

/// <summary>
/// A map from a mounted game, such as Half-Life's c1a0, placed as a static object: it collides but has no
/// rigidbody, so nothing moves it. Undo, cleanup and the remover take it away like any prop.
/// </summary>
public sealed class MapSpawnable : Spawnable
{
    public ResourceLoader Loader { get; }

    /// <summary>Too big to preview or to place in batches.</summary>
    public override bool IsProp => false;

    public MapSpawnable(ResourceLoader loader)
    {
        Loader = loader;
        Id = "map:" + loader.Path;
        Category = "Mounts";
        Name = loader.Name;
        Group = loader.Mount.Title + "/Maps";
    }

    public override GameObject Create()
    {
        var map = Loader.GetOrCreate<MountedMap>();
        if (map == null || map.Parts.Count == 0) return null;

        // The map's geometry sits under the prop, moved so its footprint is centred on the prop's origin.
        var go = new GameObject(Name);
        GameObject geometry = map.Instantiate();
        geometry.SetParent(go, false);
        Float3 center = map.Bounds.Center;
        geometry.Transform.LocalPosition = new Float3(-center.X, 0f, -center.Z);

        go.AddComponent<MapProp>().Bind(this, map, geometry);
        if (map.Audio != null) go.AddComponent<MapAmbience>().Bind(map, geometry);
        go.AddComponent<Prop>().SpawnId = Id;
        return go;
    }
}
