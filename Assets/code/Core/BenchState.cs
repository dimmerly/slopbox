#if PROWL_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Runtime.Networking;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using static Slopbox.Theme;
using TextAlignment = Prowl.PaperUI.TextAlignment;

namespace Slopbox;

/// <summary>
/// Testing only, editor only: SAVE, LOAD and CLEAR buttons, top right while the cursor is free. SAVE stores
/// every prop and the player's position and view in <c>Library/bench.json</c>; while it exists, play loads it
/// automatically, so each recompile measures the same scene from the same spot. CLEAR deletes it.
/// </summary>
public sealed class BenchState : Component
{
    private sealed class Data
    {
        public string Scene { get; set; } = "";
        public float[] Position { get; set; } = [0, 0, 0];
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public bool Noclip { get; set; }
        public Snapshot World { get; set; } = new();
    }

    private static string FilePath => Path.Combine(Prowl.Editor.Projects.Project.Current.RootPath, "Library", "bench.json");

    // A stored state loads on play, once the player exists.
    private bool _autoLoadPending;

    public override void Start() => _autoLoadPending = File.Exists(FilePath) && !Networking.IsClient;

    public override void Update()
    {
        // Mounted props can only be rebuilt once the installed games are mounted and listed.
        GameManager game = GameManager.Instance;
        if (!_autoLoadPending || game.LocalPlayer.IsNotValid() || !MountsReady(game)) return;
        _autoLoadPending = false;
        Load();
    }

    public override void OnGui(Paper paper)
    {
        GameManager game = GameManager.Instance;
        if (Theme.Font == null || !game.IsMenuOpen) return;

        paper.PushID(InstanceID);
        using (paper.Row("bench").PositionType(PositionType.SelfDirected)
            // Just under the pause menu's 72px header bar, lined up with its right padding.
            .AnchorRight(S6).AnchorTop(Px(72f) + S2)
            .Width(UnitValue.Auto).Height(UnitValue.Auto).Gap(Px(4f))
            .Opacity(0.35f).Hovered.Opacity(1f).End()
            .Enter())
        {
            bool stored = File.Exists(FilePath);
            Button(paper, "SAVE", Save, true);
            Button(paper, "LOAD", Load, stored);
            Button(paper, "CLEAR", Clear, stored);
        }
        paper.PopID();
    }

    private static void Button(Paper paper, string text, Action onClick, bool enabled)
    {
        paper.Box(text).Width(UnitValue.Auto).Height(Px(22f)).Padding(Px(8f), Px(8f), 0, 0)
            .BackgroundColor(Theme.Black(0.5f)).Rounded(Theme.Rounded)
            .Hovered.BackgroundColor(Theme.Black(enabled ? 0.8f : 0.5f)).End()
            .Cursor(enabled ? PaperCursor.Pointer : PaperCursor.Default)
            .Text(text, Theme.Font).FontSize(Px(11f)).LetterSpacing(Px(0.6f))
            .TextColor(Theme.White(enabled ? 0.85f : 0.3f))
            .Alignment(TextAlignment.MiddleCenter)
            .OnClick(_ => { if (enabled) onClick(); });
    }

    private static void Clear()
    {
        try
        {
            File.Delete(FilePath);
            Notify.Info("Bench cleared");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Notify.Error("Bench clear failed");
        }
    }

    private static void Save()
    {
        Player player = GameManager.Instance.LocalPlayer;
        if (player.IsNotValid()) return;

        var data = new Data
        {
            Scene = SceneInstance.Active.Name,
            Position = [player.Transform.Position.X, player.Transform.Position.Y, player.Transform.Position.Z],
            Yaw = player.Yaw,
            Pitch = player.Pitch,
            Noclip = player.Controller.Mode == player.Controller.Noclip,
            World = Snapshot.Capture(Prop.All, Float3.Zero, Quaternion.Identity),
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, GameJson.Options));
            Notify.Info($"Bench saved ({data.World.Count} props)");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Notify.Error("Bench save failed");
        }
    }

    private static bool MountsReady(GameManager game) => game.Catalog != null && !game.Catalog.IsMounting;

    private static void Load()
    {
        if (Networking.IsClient) { Notify.Error("Only the host can load a bench state"); return; }

        GameManager game = GameManager.Instance;
        if (!MountsReady(game)) { Notify.Error("Still mounting games, try again in a moment"); return; }
        Player player = game.LocalPlayer;
        if (player.IsNotValid() || !File.Exists(FilePath)) return;

        Data data;
        try { data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), GameJson.Options); }
        catch (Exception e)
        {
            Debug.LogException(e);
            Notify.Error("Bench file unreadable");
            return;
        }
        if (data == null) return;

        if (data.Scene != SceneInstance.Active.Name)
            Notify.Error($"Bench was saved in {data.Scene}");

        // Only what a save captures, so the map's own props stay.
        foreach (Prop prop in Prop.All.Where(Snapshot.CanCapture))
            prop.GameObject.Destroy();
        game.Undo.Clear();

        data.World.Spawn(Float3.Zero, Quaternion.Identity);

        player.Controller.Teleport(new Float3(data.Position[0], data.Position[1], data.Position[2]));
        if (data.Noclip) player.Controller.SetMode(player.Controller.Noclip);
        player.Yaw = data.Yaw;
        player.Pitch = data.Pitch;

        Notify.Info($"Bench loaded ({data.World.Count} props)");
    }
}
#endif
