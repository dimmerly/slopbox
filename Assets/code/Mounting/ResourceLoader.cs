// Ported from s&box's Sandbox.Mounting (Copyright (c) 2025 Facepunch Studios Ltd, MIT licensed).
// Loading is synchronous here: Prowl objects are made on the main thread, and scripts may not block on tasks.

using System;
using System.Collections.Generic;
using Prowl.Ember;
using Prowl.Runtime;

namespace Slopbox.Mounting;

/// <summary>Loads one resource of a mounted game, and caches the result.</summary>
public abstract class ResourceLoader
{
    internal BaseGameMount _mount;

    /// <summary>The type of resource this file gives.</summary>
    public ResourceType Type { get; private set; }

    /// <summary>The full mount path, such as "mount://hl1/valve/models/barney.mdl".</summary>
    public string Path { get; private set; } = "";

    /// <summary>The path below the mount, such as "valve/models/barney.mdl".</summary>
    public string RelativePath { get; private set; } = "";

    /// <summary>The file name without its extension.</summary>
    public string Name { get; private set; } = "";

    /// <summary>Anything that does not fit in the flags.</summary>
    public HashSet<string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ResourceFlags Flags { get; set; }

    /// <summary>The folder the resource sits in.</summary>
    public ResourceFolder Folder { get; internal set; }

    public BaseGameMount Mount => _mount;

    internal bool IsShutdown { get; private set; }

    // Not carried over a hot reload, so the resource is built again with the new code.
    [ReloadIgnore] private object _cached;
    [ReloadIgnore] private bool _failed;

    internal void InitializeInternal(ResourceType type, string path, BaseGameMount mount)
    {
        _mount = mount;
        IsShutdown = false;

        RelativePath = path.Replace('\\', '/').Trim('/');
        Path = $"mount://{mount.Ident}/{RelativePath}";
        Type = type;
        Name = System.IO.Path.GetFileNameWithoutExtension(RelativePath);

        mount.RegisterFileInternal(this);
    }

    /// <summary>The resource, loaded the first time it is asked for. Null if it could not be loaded.</summary>
    public object GetOrCreate()
    {
        if (IsShutdown || _failed) return null;
        if (_cached is EngineObject engineObject ? engineObject.IsValid() : _cached != null) return _cached;

        try
        {
            object result = Load();
            if (Type != ResourceType.GameResource) _cached = result;
            return result;
        }
        catch (Exception e)
        {
            _failed = true;
            Debug.LogWarning($"Exception when loading '{Path}': {e}");
            return null;
        }
    }

    public T GetOrCreate<T>() where T : class => GetOrCreate() as T;

    /// <summary>Makes the resource. Called once, on the main thread.</summary>
    protected abstract object Load();

    internal void ShutdownInternal()
    {
        IsShutdown = true;
        try
        {
            Shutdown();
        }
        finally
        {
            if (_cached is IDisposable disposable) disposable.Dispose();
            _cached = null;
        }
    }

    protected virtual void Shutdown() { }
}

/// <summary>A loader that knows the type of mount it belongs to.</summary>
public abstract class ResourceLoader<T> : ResourceLoader where T : BaseGameMount
{
    public T Host => _mount as T;
}
