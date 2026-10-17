using System;
using System.Collections.Generic;
using System.Linq;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Slopbox.Mounting;

namespace Slopbox;

/// <summary>
/// Everything the spawn menu lists, by category. Model props are found, without loading them, in the
/// Resources folder named by <see cref="GameManager.PropsFolder"/>: drop a model into
/// <c>Assets/Resources/props/</c> (sub folders become groups) and it appears in the menu. Models from
/// installed games, such as Half-Life, are listed under "Mounts".
/// </summary>
public sealed class SpawnCatalog
{
    private readonly List<Spawnable> _all = new();
    private readonly Dictionary<string, Spawnable> _byId = new();

    public const string MountsCategory = "Mounts";

    public IReadOnlyList<Spawnable> All => _all;
    public IReadOnlyList<string> Categories { get; private set; } = [];

    public SpawnCatalog(string propsFolder)
    {
        AddModels(propsFolder);
        AddShapes();
        AddMounts();

        // Mounts always has its tab, so it can say when no game is installed.
        Categories = _all.Select(s => s.Category).Append(MountsCategory).Distinct().ToList();
    }

    public void Add(Spawnable spawnable)
    {
        if (!_byId.TryAdd(spawnable.Id, spawnable)) return;
        _all.Add(spawnable);
    }

    public Spawnable Find(string id) => _byId.GetValueOrDefault(id);

    public IEnumerable<Spawnable> InCategory(string category) => _all.Where(s => s.Category == category);

    // ----------------------------------------------------------------

    private void AddModels(string folder)
    {
        string prefix = folder.Trim('/') + "/";
        var found = new List<ResourceEntry>();

        foreach (AssetBackend backend in AssetDatabase.Mounts.Append(AssetDatabase.Backend))
        {
            if (backend == null) continue;
            foreach (ResourceEntry entry in backend.Resources)
            {
                if (entry.LoadPath.Contains('#')) continue;   // meshes and materials inside a model
                if (!entry.LoadPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsPrefab(entry)) continue;
                found.Add(entry);
            }
        }

        foreach (ResourceEntry entry in found.OrderBy(e => e.LoadPath, StringComparer.OrdinalIgnoreCase))
            Add(new ModelSpawnable(entry, "Props"));

        if (found.Count == 0)
            Debug.LogWarning($"No props found. Put models in a folder named Resources/{folder} to list them in the spawn menu.");
    }

    /// <summary>True while installed games are being mounted in the background.</summary>
    public bool IsMounting { get; private set; }

    /// <summary>
    /// Mounts every installed game on a worker thread, since listing a game's files takes seconds, then lists
    /// its models, one group per game and kind. Games mounted earlier in the session are listed straight away.
    /// </summary>
    private async void AddMounts()
    {
        IsMounting = true;
        try
        {
            await GameTask.WorkerThread();
            MountDirectory.MountInstalled();
            await GameTask.MainThread();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
        finally
        {
            IsMounting = false;
        }

        foreach (BaseGameMount mount in MountDirectory.All.Where(m => m.IsMounted).OrderBy(m => m.Title))
        {
            var models = mount.GetAll(ResourceType.Model)
                .Where(m => (m.Flags & ResourceFlags.DeveloperOnly) == 0)
                .Select(m => new MountSpawnable(m))
                .OrderBy(s => s.Group.Contains("/HD "))
                .ThenBy(s => s.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase);
            foreach (MountSpawnable spawnable in models)
                Add(spawnable);

            foreach (ResourceLoader map in mount.GetAll(ResourceType.Scene).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                Add(new MapSpawnable(map));
        }
    }

    private static bool IsPrefab(ResourceEntry entry)
    {
        // The editor writes assembly qualified names, players plain full names, and either may be blank
        // for an asset not imported yet, so ask the database as a fallback.
        if (entry.TypeName.Contains(nameof(PrefabAsset))) return true;
        Type type = AssetDatabase.GetAssetType(entry.Guid);
        return type != null && typeof(PrefabAsset).IsAssignableFrom(type);
    }

    private void AddShapes()
    {
        // Light, mid and dark greys like PHX, plus a couple of colours. Linear values.
        Color light = new(0.6f, 0.6f, 0.62f, 1f);
        Color mid = new(0.3f, 0.31f, 0.33f, 1f);
        Color dark = new(0.08f, 0.085f, 0.09f, 1f);
        Color orange = new(0.87f, 0.09f, 0f, 1f);
        Color blue = new(0.05f, 0.25f, 0.8f, 1f);

        const string Blocks = "Blocks", Plates = "Plates", Round = "Round";
        Add(new ShapeSpawnable("cube_025", "Cube 0.25", Blocks, ShapeSpawnable.Shape.Box, new Float3(0.25f), light, 5f));
        Add(new ShapeSpawnable("cube_05", "Cube 0.5", Blocks, ShapeSpawnable.Shape.Box, new Float3(0.5f), light, 20f));
        Add(new ShapeSpawnable("cube_1", "Cube 1", Blocks, ShapeSpawnable.Shape.Box, new Float3(1f), mid, 60f));
        Add(new ShapeSpawnable("cube_2", "Cube 2", Blocks, ShapeSpawnable.Shape.Box, new Float3(2f), dark, 200f));
        Add(new ShapeSpawnable("beam_2", "Beam 2", Blocks, ShapeSpawnable.Shape.Box, new Float3(0.25f, 0.25f, 2f), mid, 20f));
        Add(new ShapeSpawnable("beam_4", "Beam 4", Blocks, ShapeSpawnable.Shape.Box, new Float3(0.25f, 0.25f, 4f), mid, 40f));

        Add(new ShapeSpawnable("plate_1", "Plate 1x1", Plates, ShapeSpawnable.Shape.Box, new Float3(1f, 0.05f, 1f), light, 10f));
        Add(new ShapeSpawnable("plate_2", "Plate 2x2", Plates, ShapeSpawnable.Shape.Box, new Float3(2f, 0.05f, 2f), light, 30f));
        Add(new ShapeSpawnable("plate_4", "Plate 4x4", Plates, ShapeSpawnable.Shape.Box, new Float3(4f, 0.05f, 4f), mid, 90f));
        Add(new ShapeSpawnable("plate_2x4", "Plate 2x4", Plates, ShapeSpawnable.Shape.Box, new Float3(2f, 0.05f, 4f), mid, 60f));

        Add(new ShapeSpawnable("ball_05", "Ball 0.5", Round, ShapeSpawnable.Shape.Sphere, new Float3(0.5f), orange, 8f));
        Add(new ShapeSpawnable("ball_1", "Ball 1", Round, ShapeSpawnable.Shape.Sphere, new Float3(1f), blue, 40f));
        Add(new ShapeSpawnable("cylinder_05", "Cylinder 0.5", Round, ShapeSpawnable.Shape.Cylinder, new Float3(0.5f, 0.5f, 0.5f), light, 10f));
        Add(new ShapeSpawnable("cylinder_1", "Cylinder 1", Round, ShapeSpawnable.Shape.Cylinder, new Float3(1f, 1f, 1f), mid, 40f));
        Add(new ShapeSpawnable("wheel", "Wheel", Round, ShapeSpawnable.Shape.Cylinder, new Float3(1f, 0.2f, 1f), dark, 15f));
    }
}
