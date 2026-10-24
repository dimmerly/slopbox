using System;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Runtime.Networking;
using Prowl.Runtime.Resources;
using Prowl.Scribe;
using Prowl.Vector;
using static Slopbox.Theme;
using TextAlignment = Prowl.PaperUI.TextAlignment;

namespace Slopbox;

/// <summary>
/// The in-game overlay, laid out like DXRP's HUD:
/// <list type="bullet">
/// <item>Tool card, top left, while the tool gun is out.</item>
/// <item>Input hints, bottom right, for other equipment.</item>
/// <item>Loadout, top centre, briefly after switching.</item>
/// <item>Notifications, bottom right, and the noclip readout.</item>
/// </list>
/// Everything but notifications hides while a menu is open. The HUD draws first each frame, so it also
/// updates <see cref="Theme.Scale"/> for every other screen.
/// </summary>
public sealed class Hud : Component
{
    private const float LoadoutSeconds = 1.6f;

    private float _fps;

    public override void Update()
    {
        if (Controls.Pressed(Controls.ToggleHud))
        {
            Settings s = GameManager.Instance.Settings;
            s.ShowHud = !s.ShowHud;
        }
        Notify.Expire();

        float dt = Time.UnscaledDeltaTime;
        if (dt > 0f) _fps += (1f / dt - _fps) * MathF.Min(1f, dt * 4f);
    }

    public override void OnGui(Paper paper)
    {
        GameManager game = GameManager.Instance;
        game.Paper = paper;
        Theme.UpdateScale(paper, game.Settings.UiScale);

        FontFile font = Theme.Font;
        Player player = game.LocalPlayer;
        if (font == null || player.IsNotValid()) return;

        // Ask for every equipment icon up front so they are rendered before the first switch.
        if (game.Thumbnails.IsValid())
            foreach (Equipment e in player.Equipment)
                game.Thumbnails.Get(e);

        Settings settings = game.Settings;
        paper.PushID(InstanceID);
        using (paper.Box("hud root").PositionType(PositionType.SelfDirected)
            .AnchorLeft(0).AnchorRight(0).AnchorTop(0).AnchorBottom(0)
            .IsNotInteractable()
            .Enter())
        {
            if (settings.ShowHud && !game.IsMenuOpen)
            {
                // Free look turns the camera off the aim, so the crosshair would point at nothing.
                float crosshair = 1f - player.FreeLookAmount;
                if (crosshair > 0.01f) Crosshair(paper, crosshair);
                ToolCard(paper, font, player);
                InputHelper(paper, font, player);
                Loadout(paper, font, player);
                NoclipOverlay(paper, font, player);
            }

            Notifications(paper, font);
            if (settings.ShowFps) Fps(paper, font, _fps);
        }
        paper.PopID();
    }

    private static void Crosshair(Paper paper, float alpha)
    {
        float size = Px(40f);
        using (paper.Box("crosshair").PositionType(PositionType.SelfDirected)
            .Width(size).Height(size).Margin(UnitValue.Stretch())
            .IsNotInteractable()
            .Enter())
        {
            float gap = Px(5f), len = Px(9f), w = MathF.Max(2f, Px(2f)), o = MathF.Max(1f, Px(1f));
            paper.Draw((canvas, rect) =>
            {
                float cx = rect.Min.X + rect.Size.X * 0.5f, cy = rect.Min.Y + rect.Size.Y * 0.5f;
                foreach ((Color c, float d) in new[] { (Theme.Black(alpha), o), (Theme.White(0.8f * alpha), 0f) })
                {
                    canvas.RectFilled(cx - w / 2 + d, cy - gap - len + d, w, len, c);
                    canvas.RectFilled(cx - w / 2 + d, cy + gap + d, w, len, c);
                    canvas.RectFilled(cx - gap - len + d, cy - w / 2 + d, len, w, c);
                    canvas.RectFilled(cx + gap + d, cy - w / 2 + d, len, w, c);
                    canvas.RectFilled(cx - w / 2 + d, cy - w / 2 + d, w, w, c);
                }
            });
        }
    }

    /// <summary>Top left while a tool is out: accent title, description, then its controls in a row.</summary>
    private static void ToolCard(Paper paper, FontFile font, Player player)
    {
        if (player.ActiveEquipment is not ToolGun gun || gun.Tool == null) return;
        BaseTool tool = gun.Tool;

        using (paper.Column("tool card").PositionType(PositionType.SelfDirected)
            .AnchorLeft(S4).AnchorTop(S4)
            .Width(UnitValue.Auto).MaxWidth(Px(640f)).Height(UnitValue.Auto)
            .AsHudCard()
            .Padding(Px(18f), Px(18f), Px(14f), Px(16f)).Gap(Px(2f))
            .IsNotInteractable()
            .Enter())
        {
            paper.Box("name").Width(UnitValue.Auto).Height(Px(38f))
                .Text(tool.Name, font).FontSize(Text2Xl).TextColor(Theme.Accent)
                .Alignment(TextAlignment.MiddleLeft);

            paper.Box("description").Width(UnitValue.Auto).Height(Px(24f))
                .Text(tool.Description, font).FontSize(TextBase).TextColor(Theme.White(0.85f))
                .Alignment(TextAlignment.MiddleLeft);

            string status = Networking.IsClient ? player.ToolStatus : tool.Status;
            if (status.Length > 0)
                paper.Box("status").Width(UnitValue.Auto).Height(Px(22f))
                    .Text(status, font).FontSize(TextSm).TextColor(Theme.AccentSoft)
                    .Alignment(TextAlignment.MiddleLeft);

            int i = 0;
            using (paper.Row("controls").Width(UnitValue.Auto).Height(UnitValue.Auto).Gap(Px(14f)).Margin(0, 0, S2, 0).Enter())
                foreach (InputHint hint in gun.Hints)
                    Ui.KeyHint(paper, hint.Key, hint.Action, i++);
        }
    }

    /// <summary>Bottom right for everything that is not the tool gun: a row of key and label pills.</summary>
    private static void InputHelper(Paper paper, FontFile font, Player player)
    {
        Equipment equipment = player.ActiveEquipment;
        if (equipment == null || equipment is ToolGun) return;

        using (paper.Row("input helper").PositionType(PositionType.SelfDirected)
            .AnchorRight(UnitValue.Percentage(1f)).AnchorBottom(UnitValue.Percentage(3f))
            .Width(UnitValue.Auto).Height(UnitValue.Auto).Gap(S3)
            .IsNotInteractable()
            .Enter())
        {
            int i = 0;
            foreach (InputHint hint in equipment.Hints)
            {
                using (paper.Row("pill", i).Width(UnitValue.Auto).Height(Px(38f))
                    .Padding(Px(5f), Px(12f), 0, 0)
                    .BackgroundColor(Theme.PrimaryA(0.8f)).Rounded(Theme.Rounded)
                    .Enter())
                {
                    Ui.KeyHint(paper, hint.Key, hint.Action, 0);
                }
                i++;
            }
        }
    }

    /// <summary>
    /// Top centre after switching: one column per slot with its key, its rendered icon and its name. The
    /// current one is lit and lifted, the rest are dimmed.
    /// </summary>
    private static void Loadout(Paper paper, FontFile font, Player player)
    {
        float since = Time.TimeSinceStartup - player.SwitchedAt;
        if (since > LoadoutSeconds) return;
        float shown = Math.Clamp((LoadoutSeconds - since) / 0.3f, 0f, 1f);

        Thumbnails thumbnails = GameManager.Instance.Thumbnails;
        float slotWidth = Px(160f), iconHeight = Px(80f);

        using (paper.Row("loadout").PositionType(PositionType.SelfDirected)
            .Width(UnitValue.Auto).Height(UnitValue.Auto)
            .Margin(UnitValue.Stretch(), UnitValue.Stretch(), S3, 0)
            .Gap(S4)
            .Opacity(shown).TranslateY(-Px(30f) * (1f - shown))
            .IsNotInteractable()
            .Enter())
        {
            for (int i = 0; i < player.Equipment.Count; i++)
            {
                Equipment equipment = player.Equipment[i];
                bool active = i == player.ActiveIndex;
                Texture2D icon = thumbnails.IsValid() ? thumbnails.Get(equipment) : null;

                // Icons float on the HUD; the current slot is lit and named in the accent, the rest dimmed.
                using (paper.Column("slot", i).Width(slotWidth).Height(UnitValue.Auto)
                    .Padding(S2, S2, S2, S2).Gap(Px(2f))
                    .Opacity(active ? 1f : 0.4f)
                    .Enter())
                {
                    using (paper.Box("cap holder").Width(UnitValue.Auto).Height(Px(28f)).Margin(UnitValue.Stretch(), UnitValue.Stretch(), 0, 0).Enter())
                        Ui.KeyCap(paper, (i + 1).ToString());

                    using (paper.Box("icon").Height(iconHeight).Enter())
                    {
                        if (icon.IsValid())
                            paper.Draw((canvas, rect) => Ui.DrawTexture(canvas, rect, icon));
                    }

                    paper.Box("name").Height(Px(24f))
                        .Text(equipment.Name, font).FontSize(Px(16f))
                        .TextColor(active ? Theme.Accent : Theme.White(1f))
                        .Alignment(TextAlignment.MiddleCenter);
                }
            }

            // Slot 0: empty hands, last as on the keyboard.
            bool holstered = player.ActiveIndex == Player.Nothing;
            using (paper.Column("slot nothing").Width(slotWidth).Height(UnitValue.Auto)
                .Padding(S2, S2, S2, S2).Gap(Px(2f))
                .Opacity(holstered ? 1f : 0.4f)
                .Enter())
            {
                using (paper.Box("cap holder").Width(UnitValue.Auto).Height(Px(28f)).Margin(UnitValue.Stretch(), UnitValue.Stretch(), 0, 0).Enter())
                    Ui.KeyCap(paper, "0");

                using (paper.Box("icon").Height(iconHeight).Enter())
                    paper.Draw((canvas, rect) => Icons.Draw(canvas, Icon.Close,
                        rect.Min.X + rect.Size.X * 0.5f, rect.Min.Y + rect.Size.Y * 0.5f, Px(36f), Theme.White(0.8f)));

                paper.Box("name").Height(Px(24f))
                    .Text("Nothing", font).FontSize(Px(16f))
                    .TextColor(holstered ? Theme.Accent : Theme.White(1f))
                    .Alignment(TextAlignment.MiddleCenter);
            }
        }
    }

    /// <summary>Bottom right, newest last: dark pills with an icon, lifted by a shadow.</summary>
    private static void Notifications(Paper paper, FontFile font)
    {
        double now = Time.TimeSinceStartup;
        float icon = Px(20f);
        using (paper.Column("notifications").PositionType(PositionType.SelfDirected)
            .AnchorRight(UnitValue.Percentage(1f)).AnchorBottom(UnitValue.Percentage(10f))
            .Width(UnitValue.Auto).Height(UnitValue.Auto)
            .Gap(S2)
            .IsNotInteractable()
            .Enter())
        {
            foreach (Notify.Entry entry in Notify.Entries)
            {
                float age = (float)(now - entry.PostedAt);
                float fadeIn = Math.Clamp(age / 0.15f, 0f, 1f);
                float fadeOut = Math.Clamp((Notify.Lifetime - age) / 0.4f, 0f, 1f);
                (Icon kind, Color color) = entry.Kind switch
                {
                    NotifyKind.Error => (Icon.Close, Theme.Error),
                    NotifyKind.Undo => (Icon.Undo, Theme.AccentSoft),
                    _ => (Icon.Info, Theme.AccentSoft),
                };

                using (paper.Row("note", entry.Id).Width(UnitValue.Auto).Height(Px(42f))
                    .Padding(Px(12f), Px(16f), 0, 0).Gap(Px(10f))
                    .BackgroundColor(Theme.PrimaryA(0.8f)).Rounded(Theme.Rounded).AsLifted()
                    .Opacity(fadeIn * fadeOut).TranslateX((1f - fadeIn) * Px(24f))
                    .Enter())
                {
                    using (paper.Box("icon holder").Width(icon).Height(icon).Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch()).Enter())
                        Icons.Box(paper, kind, icon, color);
                    paper.Box("text").Width(UnitValue.Auto)
                        .Text(entry.Text, font).FontSize(TextBase).TextColor(Theme.White(1f))
                        .Alignment(TextAlignment.MiddleLeft);
                }
            }
        }
    }

    /// <summary>Above the input hints while noclipping: icon, uppercase caption and an accent readout.</summary>
    private static void NoclipOverlay(Paper paper, FontFile font, Player player)
    {
        PlayerController c = player.Controller;
        if (c.Mode != c.Noclip) return;

        float icon = Px(22f);
        using (paper.Row("noclip").PositionType(PositionType.SelfDirected)
            .AnchorRight(UnitValue.Percentage(1f)).AnchorBottom(UnitValue.Percentage(3f, Px(52f)))
            .Width(UnitValue.Auto).Height(UnitValue.Auto)
            .Padding(S3, Px(16f), S2, S2).Gap(S3)
            .BackgroundColor(Theme.Black(0.6f)).BackdropBlur(4f).Rounded(Theme.Rounded)
            .IsNotInteractable()
            .Enter())
        {
            using (paper.Box("icon holder").Width(icon).Height(icon).Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch()).Enter())
                Icons.Box(paper, Icon.Speed, icon, Theme.AccentA(0.9f));

            using (paper.Column("text").Width(UnitValue.Auto).Height(UnitValue.Auto).Enter())
            {
                paper.Box("label").Width(UnitValue.Auto).Height(Px(16f))
                    .Text("NOCLIP", font).FontSize(Px(12f)).LetterSpacing(Px(0.6f)).TextColor(Theme.White(0.5f))
                    .Alignment(TextAlignment.MiddleLeft);
                paper.Box("value").Width(UnitValue.Auto).Height(Px(26f))
                    .Text($"{Float3.Length(c.Velocity):0.0} m/s", font).FontSize(Px(20f)).TextColor(Theme.Accent)
                    .Alignment(TextAlignment.MiddleLeft);
            }
        }
    }

    private static void Fps(Paper paper, FontFile font, float fps)
    {
        paper.Box("fps").PositionType(PositionType.SelfDirected)
            .AnchorRight(S4).AnchorTop(S4)
            .Width(UnitValue.Auto).Height(Px(28f)).Padding(Px(10f), Px(10f), 0, 0)
            .BackgroundColor(Theme.PrimaryA(0.8f)).Rounded(Theme.Rounded)
            .Text($"{fps:0} FPS", font).FontSize(TextSm).TextColor(fps < 30f ? Theme.Error : Theme.White(0.75f))
            .Alignment(TextAlignment.MiddleCenter)
            .IsNotInteractable();
    }
}
