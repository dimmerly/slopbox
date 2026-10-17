using System;
using System.Collections.Generic;
using System.Linq;
using Jitter2.Dynamics;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using static Slopbox.Theme;

namespace Slopbox;

/// <summary>
/// Mass places what the Q menu lists, in rows and columns, for looking over a lot of models at once. It takes
/// the open tab narrowed by its group and search, a batch at a time: each click places the next batch.
/// </summary>
[Tool("Placer", "Places a batch of what the Q menu lists in a grid, facing you. Pick a tab, group or search in the Q menu first; each click places the next batch.", Category = "Construction", Order = 10)]
public sealed class PlacerTool : BaseTool
{
    public enum LayoutMode
    {
        /// <summary>Every model on a fixed grid, <see cref="Spacing"/> apart centre to centre.</summary>
        Grid,

        /// <summary>Models side by side, <see cref="Spacing"/> apart edge to edge, so big and small never overlap.</summary>
        Packed,
    }

    [ToolSetting("Models per batch", Min = 1f, Max = 500f)] public int Count = 25;
    [ToolSetting("Columns", Min = 1f, Max = 50f)] public int Columns = 5;
    [ToolSetting("Layout")] public LayoutMode Layout = LayoutMode.Packed;
    [ToolSetting("Spacing", Min = 0f, Max = 20f, Format = "0.0 m")] public float Spacing = 1f;
    [ToolSetting("Freeze")] public bool Freeze = true;

    /// <summary>Models made per frame, so a large batch does not stall a frame building every model at once.</summary>
    private const int BuildsPerFrame = 6;

    private IReadOnlyList<Spawnable> _listedFrom, _props = [];
    private int _next;
    private string _source = "";
    private bool _placing;
    private int _placed, _total;

    public override string PrimaryHint => "Place next batch";
    public override string SecondaryHint => "Skip a batch";
    public override string ReloadHint => "Back to the first";

    private static SpawnMenu Menu => GameManager.Instance.GameObject.GetComponent<SpawnMenu>();

    private IReadOnlyList<Spawnable> Listed
    {
        get
        {
            SpawnMenu menu = Menu;
            if (menu.IsNotValid()) return [];

            // A different list starts from its first model again.
            if (menu.ListedSource != _source)
            {
                _source = menu.ListedSource;
                _next = 0;
            }
            if (menu.Listed != _listedFrom)
            {
                _listedFrom = menu.Listed;
                _props = menu.Listed.Where(s => s.IsProp).ToList();
            }
            return _props;
        }
    }

    public override string Status
    {
        get
        {
            if (_placing) return $"Placing {_placed} of {_total}...";
            IReadOnlyList<Spawnable> listed = Listed;
            if (listed.Count == 0) return "Pick something in the Q menu";
            (int first, int last) = NextRange(listed.Count);
            return $"Next: {first + 1}-{last} of {listed.Count} from {_source}";
        }
    }

    private (int First, int End) NextRange(int count)
    {
        if (_next >= count) _next = 0;
        return (_next, Math.Min(count, _next + Math.Max(1, Count)));
    }

    public override bool Primary(in ToolTrace tr)
    {
        if (_placing || !tr.Hit) return false;

        IReadOnlyList<Spawnable> listed = Listed;
        if (listed.Count == 0)
        {
            Notify.Error("Open the Q menu and pick a tab, group or search to place");
            return false;
        }

        (int first, int end) = NextRange(listed.Count);
        var batch = new List<Spawnable>(end - first);
        for (int i = first; i < end; i++) batch.Add(listed[i]);
        _next = end >= listed.Count ? 0 : end;

        // Rows run away from the player, columns across, every model turned to face back along the aim.
        float yaw = Owner.Yaw;
        Quaternion facing = Quaternion.FromEuler(0f, yaw + 180f, 0f);
        Float3 across = Quaternion.FromEuler(0f, yaw, 0f) * Float3.UnitX;
        Float3 away = Quaternion.FromEuler(0f, yaw, 0f) * Float3.UnitZ;

        PlaceAsync(batch, tr.Point, across, away, facing, $"{_source} {first + 1}-{end}");
        return true;
    }

    public override bool Secondary(in ToolTrace tr)
    {
        IReadOnlyList<Spawnable> listed = Listed;
        if (_placing || listed.Count == 0) return false;

        (_, int end) = NextRange(listed.Count);
        _next = end >= listed.Count ? 0 : end;
        return true;
    }

    public override bool Reload(in ToolTrace tr)
    {
        if (_placing || _next == 0) return false;
        _next = 0;
        Notify.Info("Back to the first model");
        return true;
    }

    public override void Inspector(Paper paper)
    {
        Ui.Caption(paper, "Source", 2);
        using (paper.Column("placer source").Height(UnitValue.Auto).Padding(S2, S2, 0, 0).Gap(S1).Enter())
        {
            IReadOnlyList<Spawnable> listed = Listed;
            if (listed.Count == 0)
            {
                Ui.Description(paper, "Nothing listed. The placer takes what the left side of this menu shows.", 0, Theme.White(0.5f));
                return;
            }

            (int first, int end) = NextRange(listed.Count);
            int batches = (listed.Count + Math.Max(1, Count) - 1) / Math.Max(1, Count);
            Ui.Description(paper, $"{_source}: {listed.Count} models in {batches} batches. Next is {first + 1}-{end}.", 0);
        }
    }

    /// <summary>
    /// Builds the batch a few models a frame, a row at a time, and lays each row out once all of it is built,
    /// since a packed row is centred on its total width.
    /// </summary>
    private async void PlaceAsync(List<Spawnable> batch, Float3 origin, Float3 across, Float3 away, Quaternion facing, string label)
    {
        _placing = true;
        _placed = 0;
        _total = batch.Count;
        var placed = new List<GameObject>(batch.Count);
        int columns = Math.Max(1, Columns);
        float depth = 0f, spacing = MathF.Max(0f, Spacing);

        try
        {
            int built = 0;
            for (int rowStart = 0; rowStart < batch.Count; rowStart += columns)
            {
                var row = new List<(GameObject Go, AABB Bounds)>(columns);
                for (int i = rowStart; i < Math.Min(batch.Count, rowStart + columns); i++)
                {
                    GameObject go = Build(batch[i]);
                    if (go.IsValid()) row.Add((go, Spawner.LocalBounds(go)));
                    if (++built % BuildsPerFrame == 0) await GameTask.NextFrame(GameTask.SessionToken);
                }
                if (row.Count == 0) continue;

                // A grid cell is Spacing across; a packed model is as wide as its bounds plus the gap.
                float Width((GameObject, AABB Bounds) m) => Layout == LayoutMode.Grid ? spacing : m.Bounds.Size.X + spacing;
                float Depth((GameObject, AABB Bounds) m) => Layout == LayoutMode.Grid ? spacing : m.Bounds.Size.Z + spacing;

                float rowWidth = 0f, rowDepth = 0f;
                foreach (var m in row)
                {
                    rowWidth += Width(m);
                    rowDepth = MathF.Max(rowDepth, Depth(m));
                }
                if (Layout == LayoutMode.Grid) rowWidth = spacing * Math.Min(columns, batch.Count);

                float x = -rowWidth * 0.5f;
                foreach (var m in row)
                {
                    float w = Width(m);
                    Float3 cell = origin + across * (x + w * 0.5f) + away * (depth + rowDepth * 0.5f);
                    Place(m.Go, m.Bounds, cell, facing);
                    placed.Add(m.Go);
                    _placed++;
                    x += w;
                }
                depth += rowDepth;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _placing = false;
            if (placed.Count > 0) Undo.Add($"{placed.Count} placed models", placed.ToArray());
        }

        if (placed.Count > 0) GameAudio.Play("spawn", 0.4f);
        if (placed.Count < batch.Count) Notify.Error($"Placed {placed.Count} of {batch.Count}; the rest could not be made");
    }

    private static GameObject Build(Spawnable spawnable)
    {
        try
        {
            return spawnable.Create();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            return null;
        }
    }

    /// <summary>Stands a model on whatever is under its cell, its footprint centred on the cell.</summary>
    private void Place(GameObject go, AABB bounds, Float3 cell, Quaternion facing)
    {
        float ground = cell.Y;
        if (Scene.Current.Physics.Raycast(cell + Float3.UnitY * 20f, -Float3.UnitY, out RaycastHit hit, 60f, QueryFilter.Default))
            ground = hit.Point.Y;

        Float3 center = facing * bounds.Center;
        go.Transform.Rotation = facing;
        go.Transform.Position = new Float3(cell.X - center.X, ground - bounds.Min.Y + 0.01f, cell.Z - center.Z);

        // Frozen before it is in the world, so a whole batch never tumbles into itself.
        if (Freeze && go.TryGetComponent(out Rigidbody3D body)) body.MotionType = MotionType.Kinematic;
        Scene.Current.Add(go);
    }
}
