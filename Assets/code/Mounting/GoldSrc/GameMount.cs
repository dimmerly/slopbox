// Ported from s&box's Sandbox.Mounting.GoldSrc (Copyright (c) 2025 Facepunch Studios Ltd, MIT licensed).

using System;
using System.Collections.Generic;
using System.IO;

namespace Slopbox.Mounting.GoldSrc;

/// <summary>
/// Mounts a GoldSrc game from its Steam install: its models, maps, WAD textures (each also as a material) and
/// sounds. The HD folder comes first where the game has one, as Steam's "high definition models" option.
/// </summary>
public abstract class GameMount : BaseGameMount
{
    public abstract long AppId { get; }

    public override long? SteamAppId => AppId;

    /// <summary>The mod folders to mount, such as "valve_hd" and "valve".</summary>
    public abstract IReadOnlyList<string> GameDirs { get; }

    /// <summary>The folder the game is installed in.</summary>
    public string AppDirectory { get; private set; }

    protected override void Initialize(InitializeContext context)
    {
        if (!context.IsAppInstalled(AppId)) return;

        AppDirectory = context.GetAppDirectory(AppId);
        IsInstalled = AppDirectory != null;
    }

    protected override void Mount(MountContext context)
    {
        if (string.IsNullOrEmpty(AppDirectory) || GameDirs is null || GameDirs.Count == 0) return;

        foreach (string dir in GameDirs)
        {
            string root = Path.Combine(AppDirectory, dir);
            if (!Directory.Exists(root)) continue;

            foreach (string fullPath in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                if (ext.Length == 0) continue;

                string path = Path.GetRelativePath(AppDirectory, fullPath).Replace('\\', '/');

                try
                {
                    switch (ext)
                    {
                        case ".wad":
                            AddWad(context, dir, fullPath);
                            break;
                        case ".mdl":
                            if (Mdl.MdlFile.IsModelWithGeometry(fullPath))
                                context.Add(ResourceType.Model, path, new ModelLoader(fullPath));
                            break;
                        case ".bsp":
                            if (Bsp.BspFile.IsMap(fullPath))
                                context.Add(ResourceType.Scene, path, new MapLoader(fullPath));
                            break;
                        case ".wav":
                            context.Add(ResourceType.Sound, path, new WavSoundLoader(fullPath));
                            break;
                    }
                }
                catch (Exception e)
                {
                    LogWarning($"Skipped {path}: {e.Message}");
                }
            }
        }

        IsMounted = true;
    }

    private readonly Dictionary<string, WadTextureLoader> _wadTextures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The WAD texture maps refer to by <paramref name="name"/>, from whichever WAD has it first.</summary>
    internal WadTextureLoader FindWadTexture(string name) => _wadTextures.GetValueOrDefault(name);

    protected override void Shutdown() => _wadTextures.Clear();

    private void AddWad(MountContext context, string dir, string fullPath)
    {
        var wad = new Wad();
        wad.LoadWadFile(fullPath);
        string wadName = Path.GetFileNameWithoutExtension(fullPath);

        foreach (WadLump lump in wad.Lumps)
        {
            if (lump.Type != Wad.MipTexture) continue;

            var texture = new WadTextureLoader(wad, lump.Name);
            context.Add(ResourceType.Texture, $"{dir}/textures/{wadName}/{lump.Name}", texture);
            context.Add(ResourceType.Material, $"{dir}/materials/{wadName}/{lump.Name}", new WadMaterialLoader(texture));
            _wadTextures.TryAdd(lump.Name, texture);
        }
    }
}

public sealed class HalfLifeMount : GameMount
{
    public override string Ident => "hl1";
    public override string Title => "Half-Life";
    public override long AppId => 70;
    public override IReadOnlyList<string> GameDirs => ["valve_hd", "valve"];
}

public sealed class OpposingForceMount : GameMount
{
    public override string Ident => "opfor";
    public override string Title => "Half-Life: Opposing Force";
    public override long AppId => 50;
    public override IReadOnlyList<string> GameDirs => ["gearbox_hd", "gearbox"];
}

public sealed class BlueShiftMount : GameMount
{
    public override string Ident => "bshift";
    public override string Title => "Half-Life: Blue Shift";
    public override long AppId => 130;
    public override IReadOnlyList<string> GameDirs => ["bshift_hd", "bshift"];
}

public sealed class CounterStrikeMount : GameMount
{
    public override string Ident => "cstrike";
    public override string Title => "Counter-Strike";
    public override long AppId => 10;
    public override IReadOnlyList<string> GameDirs => ["cstrike_hd", "cstrike"];
}

public sealed class ConditionZeroMount : GameMount
{
    public override string Ident => "czero";
    public override string Title => "Counter-Strike: Condition Zero";
    public override long AppId => 80;
    public override IReadOnlyList<string> GameDirs => ["czero_hd", "czero"];
}

public sealed class RicochetMount : GameMount
{
    public override string Ident => "ricochet";
    public override string Title => "Ricochet";
    public override long AppId => 60;
    public override IReadOnlyList<string> GameDirs => ["ricochet_hd", "ricochet"];
}

public sealed class TeamFortressClassicMount : GameMount
{
    public override string Ident => "tfc";
    public override string Title => "Team Fortress Classic";
    public override long AppId => 20;
    public override IReadOnlyList<string> GameDirs => ["tfc_hd", "tfc"];
}

public sealed class DeathmatchClassicMount : GameMount
{
    public override string Ident => "dmc";
    public override string Title => "Deathmatch Classic";
    public override long AppId => 40;
    public override IReadOnlyList<string> GameDirs => ["dmc_hd", "dmc"];
}

public sealed class DayOfDefeatMount : GameMount
{
    public override string Ident => "dod";
    public override string Title => "Day of Defeat";
    public override long AppId => 30;
    public override IReadOnlyList<string> GameDirs => ["dod_hd", "dod"];
}

public sealed class SvenCoopMount : GameMount
{
    public override string Ident => "svencoop";
    public override string Title => "Sven Co-op";
    public override long AppId => 225840;
    public override IReadOnlyList<string> GameDirs => ["svencoop"];
}

public sealed class CryOfFearMount : GameMount
{
    public override string Ident => "cof";
    public override string Title => "Cry of Fear";
    public override long AppId => 223710;
    public override IReadOnlyList<string> GameDirs => ["cryoffear"];
}
