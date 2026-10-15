using System;
using System.Linq;
using System.Text;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

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

/// <summary>A model prop from a Resources folder. It gets a convex collider per mesh and a rigidbody.</summary>
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

        foreach (MeshRenderer renderer in go.GetComponentsInChildren<MeshRenderer>())
        {
            if (renderer.Mesh.IsNotValid()) continue;
            var collider = renderer.GameObject.AddComponent<MeshCollider>();
            collider.Convex = true;
        }

        AABB bounds = Spawner.LocalBounds(go);
        Float3 size = bounds.Size;
        var body = go.AddComponent<Rigidbody3D>();
        body.Mass = Math.Clamp(size.X * size.Y * size.Z * Density, 2f, 500f);

        go.AddComponent<Prop>().SpawnId = Id;
        return go;
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
