// Ported from s&box's Sandbox.Mounting.Source (Copyright (c) 2025 Facepunch Studios Ltd, MIT licensed), with
// models, maps and sound scripts added.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Slopbox.Mounting.Source;

/// <summary>A game file: an entry in a VPK, or a loose file on disk.</summary>
internal readonly record struct SourceFile(VpkArchive Vpk, VpkEntry Entry, string LoosePath)
{
    public byte[] Read() => Vpk != null ? Vpk.Read(Entry) : File.Exists(LoosePath) ? File.ReadAllBytes(LoosePath) : null;
}

/// <summary>
/// Mounts a Source engine game from its Steam install: the VPKs and loose files of each of its game folders,
/// searched in order like the engine's search paths. Lists its models (.mdl), maps (.bsp), sounds, textures (.vtf)
/// and materials (.vmt). Folders in <see cref="BaseDirs"/> are searched for content but not listed, such as the
/// copy of Half-Life 2's files that Counter-Strike: Source ships with.
/// </summary>
public abstract class SourceMount : BaseGameMount, IContent
{
    public abstract long AppId { get; }

    public override long? SteamAppId => AppId;

    /// <summary>The folders whose files are listed, most important first, such as "cstrike".</summary>
    public abstract IReadOnlyList<string> GameDirs { get; }

    /// <summary>Folders searched for content after <see cref="GameDirs"/>, without being listed.</summary>
    public virtual IReadOnlyList<string> BaseDirs => [];

    /// <summary>Maps from elsewhere to list with this game, as (path below the mount, file on disk).</summary>
    protected virtual IEnumerable<(string Path, string File)> ExtraMaps => [];

    public string AppDirectory { get; private set; }

    private readonly List<VpkArchive> _vpks = new();
    private readonly Dictionary<string, SourceFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _textures = new();
    private readonly Dictionary<string, SourceMaterial> _materials = new();
    private readonly Dictionary<string, MountedModel> _characters = new(StringComparer.OrdinalIgnoreCase);
    private SoundScripts _sounds;

    protected override void Initialize(InitializeContext context)
    {
        if (!context.IsAppInstalled(AppId)) return;
        AppDirectory = context.GetAppDirectory(AppId);
        IsInstalled = AppDirectory != null;
    }

    protected override void Mount(MountContext context)
    {
        if (string.IsNullOrEmpty(AppDirectory)) return;

        var models = new List<(string Path, string Relative, SourceFile File)>();
        foreach (string dir in GameDirs) Index(context, dir, listed: true, models);
        foreach (string dir in BaseDirs) Index(context, dir, listed: false, models);

        // Only models with vertices are listed; the rest hold animations or textures for others.
        foreach ((string path, string relative, SourceFile file) in models)
            if (_files.ContainsKey(relative[..^4] + ".vvd")) context.Add(ResourceType.Model, path, new StudioModelLoader(relative, file));

        foreach ((string path, string file) in ExtraMaps)
            if (File.Exists(file)) context.Add(ResourceType.Scene, path, new SourceMapLoader(file));

        IsMounted = true;
    }

    protected override void Shutdown()
    {
        foreach (MountedModel character in _characters.Values) character?.Dispose();
        _characters.Clear();
        foreach (SourceMaterial material in _materials.Values) material?.Dispose();
        foreach (Texture2D texture in _textures.Values)
            if (texture.IsValid()) texture.Dispose();
        foreach (VpkArchive vpk in _vpks) vpk.Dispose();
        _materials.Clear();
        _textures.Clear();
        _vpks.Clear();
        _files.Clear();
        _sounds = null;
        _surfaceProps = null;
        SourceFootsteps.Forget(this);
    }

    private void Index(MountContext context, string dir, bool listed, List<(string, string, SourceFile)> models)
    {
        string root = Path.Combine(AppDirectory, dir);
        if (!Directory.Exists(root)) return;

        // Pack files first: the engine searches them before the loose files of the same folder.
        foreach (string vpkPath in Directory.GetFiles(root, "*_dir.vpk", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var vpk = new VpkArchive(vpkPath);
                _vpks.Add(vpk);
                foreach (VpkEntry entry in vpk.Entries)
                    Add(context, dir, entry.FullPath, new SourceFile(vpk, entry, null), listed, models);
            }
            catch (Exception e)
            {
                LogWarning($"Could not read {vpkPath}: {e.Message}");
            }
        }

        foreach (string sub in new[] { "materials", "models", "maps", "sound", "scripts" })
        {
            string folder = Path.Combine(root, sub);
            if (!Directory.Exists(folder)) continue;
            foreach (string fullPath in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
                Add(context, dir, relative, new SourceFile(null, null, fullPath), listed, models);
            }
        }
    }

    private void Add(MountContext context, string dir, string relative, SourceFile file, bool listed, List<(string, string, SourceFile)> models)
    {
        relative = ContentPaths.Normalize(relative);
        bool first = _files.TryAdd(relative, file);
        if (!listed) return;

        string path = $"{dir}/{relative}";
        string ext = Path.GetExtension(relative);
        switch (ext)
        {
            case ".mdl":
                models.Add((path, relative, file));
                break;
            case ".bsp":
                if (relative.StartsWith("maps/")) context.Add(ResourceType.Scene, path, new SourceMapLoader(file));
                break;
            case ".wav":
            case ".mp3":
                context.Add(ResourceType.Sound, path, new SourceSoundLoader(file));
                break;
            case ".vtf":
                if (first) context.Add(ResourceType.Texture, path, new VtfTextureLoader(relative));
                break;
            case ".vmt":
                if (first) context.Add(ResourceType.Material, path, new VmtMaterialLoader(relative));
                break;
        }
    }

    /// <summary>
    /// A model by its path in the game, such as "models/humans/group01/male_07.mdl", built with its skeleton and
    /// bone weights to animate as a character. Made the first time it is asked for. Null if missing.
    /// </summary>
    public MountedModel Character(string path)
    {
        path = ContentPaths.Normalize(path);
        if (_characters.TryGetValue(path, out MountedModel cached)) return cached;

        MountedModel model = null;
        if (ReadFile(path) is { } mdl)
        {
            try
            {
                model = StudioModel.Build(Path.GetFileNameWithoutExtension(path), path, mdl, this, skinned: true);
            }
            catch (Exception e)
            {
                LogWarning($"Could not load character {path}: {e.Message}");
            }
        }
        _characters[path] = model;
        return model;
    }

    // ----------------------------------------------------------------
    //  IContent
    // ----------------------------------------------------------------

    internal bool TryGetFile(string path, out SourceFile file) => _files.TryGetValue(ContentPaths.Normalize(path), out file);

    byte[] IContent.Read(string path) => ReadFile(path);

    internal byte[] ReadFile(string path)
    {
        if (!TryGetFile(path, out SourceFile file)) return null;
        try
        {
            return file.Read();
        }
        catch (Exception e)
        {
            LogWarning($"Could not read {path}: {e.Message}");
            return null;
        }
    }

    public bool Exists(string path) => _files.ContainsKey(ContentPaths.Normalize(path));

    Texture2D IContent.Texture(string name) => Texture(name);

    /// <summary>A texture by its name below materials/, made the first time it is asked for.</summary>
    internal Texture2D Texture(string name)
    {
        name = ContentPaths.MaterialName(name);
        if (_textures.TryGetValue(name, out Texture2D cached) && (cached == null || cached.IsValid())) return cached;

        Texture2D texture = null;
        try
        {
            texture = SourceAssets.Texture(name, Vtf.Decode(ReadFile($"materials/{name}.vtf")));
        }
        catch (Exception e)
        {
            LogWarning($"Could not load texture {name}: {e.Message}");
        }
        _textures[name] = texture;
        return texture;
    }

    SourceMaterial IContent.Material(string name) => Material(name);

    /// <summary>A material by its name below materials/, made the first time it is asked for.</summary>
    internal SourceMaterial Material(string name)
    {
        name = ContentPaths.MaterialName(name);
        if (_materials.TryGetValue(name, out SourceMaterial cached)) return cached;

        SourceMaterial material = null;
        byte[] vmt = ReadFile($"materials/{name}.vmt");
        if (vmt != null)
        {
            try
            {
                material = SourceMaterials.Build(name, vmt, this);
            }
            catch (Exception e)
            {
                LogWarning($"Could not load material {name}: {e.Message}");
            }
        }
        _materials[name] = material;
        return material;
    }

    private Dictionary<string, KeyValues> _surfaceProps;

    /// <summary>
    /// The sound script entry a surface plays for a footstep, such as "Concrete.StepLeft" for "concrete", following
    /// each surface's "base" until one names it. Null if the game has no such surface.
    /// </summary>
    internal string StepSound(string surface, bool left)
    {
        _surfaceProps ??= LoadSurfaceProps();
        string key = left ? "stepleft" : "stepright";
        for (int depth = 0; depth < 8 && surface != null; depth++)
        {
            if (!_surfaceProps.TryGetValue(surface, out KeyValues props)) return null;
            if (props.Get(key) is { Length: > 0 } sound) return sound;
            surface = props.Get("base");
        }
        return null;
    }

    private Dictionary<string, KeyValues> LoadSurfaceProps()
    {
        var surfaces = new Dictionary<string, KeyValues>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        KeyValues manifest = KeyValues.Parse(ReadFile("scripts/surfaceproperties_manifest.txt"));
        if (manifest != null)
            foreach (KeyValues entry in manifest.All("file"))
                if (entry.Value != null) files.Add(entry.Value);
        if (files.Count == 0) files.Add("scripts/surfaceproperties.txt");

        foreach (string file in files)
        {
            List<KeyValues> all = KeyValues.ParseAll(ReadFile(file));
            if (all == null) continue;
            // Later files refine earlier ones, as the game reads them.
            foreach (KeyValues surface in all)
                if (surface.IsBlock) surfaces[surface.Key] = surface;
        }
        return surfaces;
    }

    /// <summary>The game's sound scripts and soundscapes, read the first time they are needed.</summary>
    internal SoundScripts Sounds => _sounds ??= new SoundScripts(this);

    /// <summary>
    /// This game's files, then every other mounted Source game's, the way Garry's Mod mounts games: what maps
    /// made for one game with another's content look through.
    /// </summary>
    internal IContent WithOtherGames()
    {
        var layers = new List<IContent> { this };
        layers.AddRange(OtherGames());
        return new ContentChain(layers);
    }

    /// <summary>
    /// The other mounted Source games, from the same set of mounts as this one, so a loader kept over a script
    /// reload still finds the games it was mounted with.
    /// </summary>
    internal IEnumerable<SourceMount> OtherGames()
        => (_host?.All ?? MountDirectory.All).OfType<SourceMount>().Where(m => m != this && m.IsMounted).OrderBy(m => m.Title);
}

public sealed class HalfLife2Mount : SourceMount
{
    public override string Ident => "hl2";
    public override string Title => "Half-Life 2";
    public override long AppId => 220;
    public override IReadOnlyList<string> GameDirs => ["hl2", "episodic", "ep2", "lostcoast", "hl2_complete"];

    /// <summary>
    /// Maps dropped into the "maps" folder, in the project while in the editor or in %AppData%/Slopbox in a built
    /// game. They are listed with Half-Life 2 and use its content, plus that of every other mounted Source game.
    /// </summary>
    protected override IEnumerable<(string Path, string File)> ExtraMaps
    {
        get
        {
            string folder = CustomMapsFolder;
            if (folder == null || !Directory.Exists(folder)) yield break;
            foreach (string file in Directory.EnumerateFiles(folder, "*.bsp"))
                yield return ($"custom/maps/{Path.GetFileName(file)}", file);
        }
    }

    /// <summary>Where custom maps are read from.</summary>
#if PROWL_EDITOR
    public static string CustomMapsFolder => Path.Combine(Prowl.Editor.Projects.Project.Current.RootPath, "maps");
#else
    public static string CustomMapsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Slopbox", "maps");
#endif
}

public sealed class CounterStrikeSourceMount : SourceMount
{
    public override string Ident => "cstrikesource";
    public override string Title => "Counter-Strike: Source";
    public override long AppId => 240;
    public override IReadOnlyList<string> GameDirs => ["cstrike"];
    public override IReadOnlyList<string> BaseDirs => ["hl2"];
}

public sealed class HalfLife2DeathmatchMount : SourceMount
{
    public override string Ident => "hl2mp";
    public override string Title => "Half-Life 2: Deathmatch";
    public override long AppId => 320;
    public override IReadOnlyList<string> GameDirs => ["hl2mp"];
    public override IReadOnlyList<string> BaseDirs => ["hl2"];
}

public sealed class GarrysModMount : SourceMount
{
    public override string Ident => "garrysmod";
    public override string Title => "Garry's Mod";
    public override long AppId => 4000;
    public override IReadOnlyList<string> GameDirs => ["garrysmod"];
    public override IReadOnlyList<string> BaseDirs => ["sourceengine"];
}

public sealed class PortalMount : SourceMount
{
    public override string Ident => "portal";
    public override string Title => "Portal";
    public override long AppId => 400;
    public override IReadOnlyList<string> GameDirs => ["portal"];
    public override IReadOnlyList<string> BaseDirs => ["hl2"];
}

public sealed class TeamFortress2Mount : SourceMount
{
    public override string Ident => "tf2";
    public override string Title => "Team Fortress 2";
    public override long AppId => 440;
    public override IReadOnlyList<string> GameDirs => ["tf"];
    public override IReadOnlyList<string> BaseDirs => ["hl2"];
}

// ----------------------------------------------------------------
//  Loaders
// ----------------------------------------------------------------

/// <summary>A VTF texture of a Source game, shared with its materials.</summary>
internal sealed class VtfTextureLoader(string gamePath) : ResourceLoader<SourceMount>
{
    protected override object Load() => Host.Texture(gamePath);
}

/// <summary>A VMT material of a Source game.</summary>
internal sealed class VmtMaterialLoader(string gamePath) : ResourceLoader<SourceMount>
{
    protected override object Load() => Host.Material(gamePath)?.Material;
}
