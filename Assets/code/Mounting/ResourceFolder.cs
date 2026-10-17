// Ported from s&box's Sandbox.Mounting (Copyright (c) 2025 Facepunch Studios Ltd, MIT licensed).

using System;
using System.Collections.Generic;
using System.Linq;

namespace Slopbox.Mounting;

/// <summary>A directory in a mount's resource tree, with its child directories and resources.</summary>
public sealed class ResourceFolder
{
    public string Name { get; }

    /// <summary>The path below the mount, such as "valve/models".</summary>
    public string Path { get; }

    /// <summary>Null for the root.</summary>
    public ResourceFolder Parent { get; }

    public List<ResourceFolder> Folders { get; } = new();
    public List<ResourceLoader> Files { get; } = new();

    public bool IsRoot => Parent == null;

    public int Depth
    {
        get
        {
            int depth = 0;
            for (ResourceFolder f = Parent; f != null; f = f.Parent) depth++;
            return depth;
        }
    }

    private ResourceFolder(string name, string path, ResourceFolder parent)
    {
        Name = name;
        Path = path;
        Parent = parent;
    }

    internal static ResourceFolder CreateRoot() => new("", "", null);

    private ResourceFolder GetOrCreateChild(string name)
    {
        ResourceFolder existing = Folders.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        var child = new ResourceFolder(name, IsRoot ? name : $"{Path}/{name}", this);
        Folders.Add(child);
        return child;
    }

    /// <summary>Files a resource under its relative path, making the folders on the way.</summary>
    internal void AddResource(ResourceLoader resource)
    {
        string[] parts = resource.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        ResourceFolder folder = this;
        for (int i = 0; i < parts.Length - 1; i++)
            folder = folder.GetOrCreateChild(parts[i]);

        folder.Files.Add(resource);
        resource.Folder = folder;
    }

    public IEnumerable<ResourceLoader> GetAllFilesRecursive()
    {
        foreach (ResourceLoader file in Files)
            yield return file;
        foreach (ResourceFolder folder in Folders)
            foreach (ResourceLoader file in folder.GetAllFilesRecursive())
                yield return file;
    }

    /// <summary>True if this folder or one below it holds a resource of <paramref name="type"/>.</summary>
    public bool ContainsType(ResourceType type) => Files.Any(f => f.Type == type) || Folders.Any(d => d.ContainsType(type));
}
