using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Slopbox.Mounting.Source;

/// <summary>
/// Somewhere game files can be found, by their path in the game ("materials/brick/brickwall001a.vmt"), the way
/// the engine's search paths work. Textures and materials made from them are cached by whoever holds the files.
/// </summary>
internal interface IContent
{
    /// <summary>The file's bytes, or null if it is not here.</summary>
    byte[] Read(string path);

    bool Exists(string path);

    /// <summary>A texture by its name below materials/, such as "brick/brickwall001a". Null if missing.</summary>
    Texture2D Texture(string name);

    /// <summary>A material by its name below materials/. Null if missing.</summary>
    SourceMaterial Material(string name);
}

internal static class ContentPaths
{
    /// <summary>"Materials\Brick\Wall.vmt" and "brick/wall" both become "brick/wall".</summary>
    public static string MaterialName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        name = name.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
        while (name.Contains("//")) name = name.Replace("//", "/");
        if (name.StartsWith("materials/")) name = name[10..];
        if (name.EndsWith(".vmt") || name.EndsWith(".vtf")) name = name[..^4];
        return name;
    }

    /// <summary>A file path as a key: forward slashes, lower case, no leading slash.</summary>
    public static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
}

/// <summary>Several places to look, in order: the first that has a file gives it.</summary>
internal sealed class ContentChain(IReadOnlyList<IContent> layers) : IContent
{
    public IReadOnlyList<IContent> Layers => layers;

    public byte[] Read(string path)
    {
        foreach (IContent layer in layers)
            if (layer.Read(path) is { } data) return data;
        return null;
    }

    public bool Exists(string path)
    {
        foreach (IContent layer in layers)
            if (layer.Exists(path)) return true;
        return false;
    }

    public Texture2D Texture(string name)
    {
        foreach (IContent layer in layers)
            if (layer.Texture(name) is { } texture && texture.IsValid()) return texture;
        return null;
    }

    public SourceMaterial Material(string name)
    {
        foreach (IContent layer in layers)
            if (layer.Material(name) is { } material) return material;
        return null;
    }
}

/// <summary>
/// The files a map carries inside itself, in its pak lump: usually its cubemap-patched materials and any custom
/// textures. What it makes from them belongs to the map. Textures and includes it does not have come from
/// <see cref="Fallback"/>.
/// </summary>
internal sealed class PakContent : IContent
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _textures = new();
    private readonly Dictionary<string, SourceMaterial> _materials = new();

    /// <summary>Where everything else comes from, and what materials here read their textures through.</summary>
    public IContent Fallback { get; set; }

    /// <summary>Everything made here, for the map to free.</summary>
    public List<Texture2D> OwnedTextures { get; } = new();
    public List<SourceMaterial> OwnedMaterials { get; } = new();

    public PakContent(byte[] zip)
    {
        if (zip == null || zip.Length < 22) return;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.Length == 0) continue;
                using Stream stream = entry.Open();
                var bytes = new byte[entry.Length];
                stream.ReadExactly(bytes);
                _files[ContentPaths.Normalize(entry.FullName)] = bytes;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Could not read a map's pak lump: {e.Message}");
        }
    }

    public int Count => _files.Count;

    public byte[] Read(string path) => _files.GetValueOrDefault(ContentPaths.Normalize(path));

    public bool Exists(string path) => _files.ContainsKey(ContentPaths.Normalize(path));

    public Texture2D Texture(string name)
    {
        name = ContentPaths.MaterialName(name);
        if (_textures.TryGetValue(name, out Texture2D cached)) return cached;

        Texture2D texture = null;
        if (_files.TryGetValue($"materials/{name}.vtf", out byte[] data))
        {
            texture = SourceAssets.Texture(name, Vtf.Decode(data));
            if (texture != null) OwnedTextures.Add(texture);
        }
        _textures[name] = texture;
        return texture;
    }

    public SourceMaterial Material(string name)
    {
        name = ContentPaths.MaterialName(name);
        if (_materials.TryGetValue(name, out SourceMaterial cached)) return cached;

        SourceMaterial material = null;
        if (_files.TryGetValue($"materials/{name}.vmt", out byte[] data))
        {
            material = SourceMaterials.Build(name, data, Fallback == null ? this : new ContentChain([this, Fallback]));
            if (material != null) OwnedMaterials.Add(material);
        }
        _materials[name] = material;
        return material;
    }
}
