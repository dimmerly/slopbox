// Ported from s&box's Sandbox.Mounting (Copyright (c) 2025 Facepunch Studios Ltd, MIT licensed).

using System;
using System.Collections.Generic;
using Prowl.Runtime;

namespace Slopbox.Mounting;

/// <summary>
/// A game whose files can be mounted, such as Half-Life. Subclasses say whether the game is installed in
/// <see cref="Initialize"/> and register its files in <see cref="Mount"/>. Every non abstract subclass in the
/// game assembly is found by <see cref="MountDirectory"/>.
/// </summary>
public abstract class BaseGameMount
{
    /// <summary>True if the game is on this computer and can be mounted.</summary>
    public bool IsInstalled { get; protected set; }

    /// <summary>True while mounted.</summary>
    public bool IsMounted { get; protected set; }

    /// <summary>A short lowercase id, such as "hl1".</summary>
    public abstract string Ident { get; }

    /// <summary>The game's name, such as "Half-Life".</summary>
    public abstract string Title { get; }

    /// <summary>The Steam app the game belongs to, if any.</summary>
    public virtual long? SteamAppId => null;

    /// <summary>Wide capsule art from Steam's CDN, 460x215. Null without a Steam app.</summary>
    public virtual string CapsuleUrl => SteamImageUrl("header.jpg");

    /// <summary>Portrait library art from Steam's CDN, 600x900. Null without a Steam app.</summary>
    public virtual string PortraitUrl => SteamImageUrl("library_600x900.jpg");

    private string SteamImageUrl(string name)
        => SteamAppId is long appId ? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{name}" : null;

    internal MountHost _host;

    internal void InitializeInternal(MountHost host)
    {
        _host = host;
        Initialize(new InitializeContext(host));
    }

    /// <summary>Called on startup. Checks whether the game is on disk and sets <see cref="IsInstalled"/>.</summary>
    protected abstract void Initialize(InitializeContext context);

    internal void MountInternal()
    {
        RootFolder = ResourceFolder.CreateRoot();
        if (!IsInstalled) return;

        Mount(new MountContext(this));

        foreach (ResourceLoader entry in _entries.Values)
            RootFolder.AddResource(entry);
    }

    /// <summary>Registers the game's files with <see cref="MountContext.Add"/>. Sets <see cref="IsMounted"/> on success.</summary>
    protected abstract void Mount(MountContext context);

    internal void ShutdownInternal()
    {
        IsMounted = false;
        Shutdown();

        foreach (ResourceLoader entry in _entries.Values)
            entry?.ShutdownInternal();

        _entries.Clear();
        _entriesByType.Clear();
        RootFolder = null;
    }

    /// <summary>Called when unmounted. Close any open files here.</summary>
    protected virtual void Shutdown() { }

    private readonly Dictionary<string, ResourceLoader> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ResourceType, List<ResourceLoader>> _entriesByType = new();

    /// <summary>Every resource in the game.</summary>
    public IReadOnlyCollection<ResourceLoader> Resources => _entries.Values;

    /// <summary>The loader for a full mount path, or null.</summary>
    public ResourceLoader GetByPath(string path) => _entries.GetValueOrDefault(path);

    /// <summary>Every loader of one type.</summary>
    public IReadOnlyList<ResourceLoader> GetAll(ResourceType type) => _entriesByType.TryGetValue(type, out var list) ? list : [];

    public ResourceFolder RootFolder { get; private set; }

    internal void RegisterFileInternal(ResourceLoader entry)
    {
        _entries[entry.Path] = entry;
        if (!_entriesByType.TryGetValue(entry.Type, out var list)) _entriesByType[entry.Type] = list = new List<ResourceLoader>();
        list.Add(entry);
    }

    /// <summary>Unmounts and mounts again, picking up changed files or a fresh install.</summary>
    public void Refresh()
    {
        bool wasMounted = IsMounted;
        ShutdownInternal();
        IsInstalled = false;
        InitializeInternal(_host);
        if (wasMounted) MountInternal();
    }

    protected void Log(string message) => Debug.Log($"[{Ident}] {message}");
    protected void LogWarning(string message) => Debug.LogWarning($"[{Ident}] {message}");
}

/// <summary>Handed to <see cref="BaseGameMount"/> while it checks for its game.</summary>
public readonly ref struct InitializeContext
{
    private readonly MountHost _host;

    internal InitializeContext(MountHost host) => _host = host;

    /// <summary>True if the Steam app is installed.</summary>
    public bool IsAppInstalled(long appId) => _host.Steam?.IsAppInstalled(appId) ?? false;

    /// <summary>True if the Steam DLC is installed.</summary>
    public bool IsDlcInstalled(long appId) => _host.Steam?.IsDlcInstalled(appId) ?? false;

    /// <summary>The folder the Steam app is installed in, or null.</summary>
    public string GetAppDirectory(long appId) => IsAppInstalled(appId) ? _host.Steam?.GetAppDirectory(appId) : null;
}

/// <summary>Handed to <see cref="BaseGameMount"/> while it registers its files.</summary>
public readonly ref struct MountContext
{
    private readonly BaseGameMount _mount;

    internal MountContext(BaseGameMount mount) => _mount = mount;

    /// <summary>Registers a file. <paramref name="path"/> is relative to the mount, such as "valve/models/barney.mdl".</summary>
    public void Add(ResourceType type, string path, ResourceLoader entry) => entry.InitializeInternal(type, path, _mount);
}
