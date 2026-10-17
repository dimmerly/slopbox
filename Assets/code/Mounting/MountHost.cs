// Ported from s&box's Sandbox.Mounting and Sandbox.Engine's Mount/Directory.cs
// (Copyright (c) 2025 Facepunch Studios Ltd, MIT licensed).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Prowl.Ember;
using Prowl.Runtime;
using Debug = Prowl.Runtime.Debug;

namespace Slopbox.Mounting;

/// <summary>Holds every mount and is the single way in to them.</summary>
internal sealed class MountHost : IDisposable
{
    private readonly Dictionary<string, BaseGameMount> _sources = new();

    public ISteamIntegration Steam { get; init; }

    public IReadOnlyCollection<BaseGameMount> All => _sources.Values;

    public void Dispose()
    {
        foreach (BaseGameMount source in All.ToArray())
            source?.ShutdownInternal();
        _sources.Clear();
    }

    private void Initialize(Type type)
    {
        try
        {
            var source = (BaseGameMount)Activator.CreateInstance(type);
            if (_sources.ContainsKey(source.Ident)) throw new Exception($"A mount named '{source.Ident}' already exists");

            source.InitializeInternal(this);
            _sources[source.Ident] = source;
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to initialize mount {type.Name}: {e}");
        }
    }

    public BaseGameMount GetSource(string ident) => _sources.GetValueOrDefault(ident);

    public void Mount(string ident)
    {
        BaseGameMount source = GetSource(ident);
        if (source is null || source.IsMounted) return;

        var timer = Stopwatch.StartNew();
        try
        {
            source.MountInternal();
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to mount {source.Title}: {e}");
            return;
        }
        if (source.IsMounted)
            Debug.Log($"Mounted {source.Title}: {source.Resources.Count} files in {timer.ElapsedMilliseconds} ms");
    }

    public void Unmount(string ident)
    {
        BaseGameMount source = GetSource(ident);
        if (source is null || !source.IsMounted) return;
        source.ShutdownInternal();
    }

    /// <summary>Creates and initializes every mount type in <paramref name="assembly"/>.</summary>
    public void RegisterTypes(Assembly assembly)
    {
        foreach (Type type in assembly.GetTypes().Where(t => typeof(BaseGameMount).IsAssignableFrom(t) && !t.IsAbstract))
            Initialize(type);
    }
}

/// <summary>
/// Every game mount the game knows. Mounts are the non abstract <see cref="BaseGameMount"/> types in the game
/// assembly, rather than s&amp;box's per mount DLLs. Set up on first use; script reloads start over.
/// </summary>
public static class MountDirectory
{
    // Not carried over a hot reload, so the next use mounts again with the new code.
    [ReloadIgnore] private static MountHost s_host;

    private static MountHost Host
    {
        get
        {
            if (s_host != null) return s_host;
            s_host = new MountHost { Steam = new SteamIntegration() };
            s_host.RegisterTypes(typeof(MountDirectory).Assembly);
            return s_host;
        }
    }

    /// <summary>Every mount, installed or not.</summary>
    public static IReadOnlyCollection<BaseGameMount> All => Host.All;

    /// <summary>The mount with this ident, or null.</summary>
    public static BaseGameMount Get(string ident) => Host.GetSource(ident);

    /// <summary>Mounts a game, if it is installed and not mounted yet. Blocks while its files are listed.</summary>
    public static void Mount(string ident) => Host.Mount(ident);

    public static void Unmount(string ident) => Host.Unmount(ident);

    /// <summary>Mounts every installed game.</summary>
    public static void MountInstalled()
    {
        foreach (BaseGameMount mount in All)
            if (mount.IsInstalled) Mount(mount.Ident);
    }

    /// <summary>True if <paramref name="path"/> is a mount:// path.</summary>
    public static bool IsMountPath(string path) => !string.IsNullOrWhiteSpace(path) && path.StartsWith("mount://", StringComparison.Ordinal);

    /// <summary>The mount ident in a mount:// path.</summary>
    public static bool TryParse(string path, out string ident)
    {
        ident = null;
        if (!IsMountPath(path)) return false;

        string rest = path[8..];
        int slash = rest.IndexOf('/');
        if (slash < 0) return false;

        ident = rest[..slash];
        return true;
    }

    /// <summary>The loader behind a mount:// path, or null.</summary>
    public static ResourceLoader FindLoader(string path)
        => TryParse(path, out string ident) ? Get(ident)?.GetByPath(path) : null;
}
