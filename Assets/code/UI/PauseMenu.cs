using System;
using Prowl.Echo;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Scribe;
using Prowl.Vector;
using static Slopbox.Theme;
using TextAlignment = Prowl.PaperUI.TextAlignment;

namespace Slopbox;

/// <summary>
/// Escape: pauses the game. Laid out like DXRP's pause menu: a blurred backdrop, a header bar with the
/// Slopbox wordmark and a few stats, large menu items down the left, and the settings panel on the right.
/// </summary>
public sealed class PauseMenu : Component
{
    private bool _open;
    private bool _settingsOpen;

    public override void OnDisable()
    {
        if (_open) SetOpen(false);
    }

    public override void Update()
    {
        if (!Controls.Pressed(Controls.Pause)) return;

        // Escape belongs to a focused text field or an open spawn menu first.
        GameManager game = GameManager.Instance;
        bool typing = game.Paper != null && game.Paper.WantsCaptureKeyboard;
        bool otherMenu = game.IsMenuOpen && !_open;
        if (!typing && !otherMenu) SetOpen(!_open);
    }

    private void SetOpen(bool open)
    {
        _open = open;
        if (!open) _settingsOpen = false;
        GameManager game = GameManager.Instance;
        if (game.IsValid())
        {
            game.SetMenuOpen(this, open);
            game.IsPaused = open;
        }
        Time.TimeScale = open ? 0f : 1f;
    }

    public override void OnGui(Paper paper)
    {
        GameManager game = GameManager.Instance;
        FontFile font = Theme.Font;
        if (font == null) return;

        paper.PushID(InstanceID);
        using (paper.Box("pause root").PositionType(PositionType.SelfDirected)
            .AnchorLeft(0).AnchorRight(0).AnchorTop(0).AnchorBottom(0)
            .IsNotInteractable()
            .Enter())
        {
            float t = paper.AnimateBool(_open, 0.2f, Easing.CubicOut);
            if (t > 0.001f) Screen(paper, font, game, t);
        }
        paper.PopID();
    }

    private void Screen(Paper paper, FontFile font, GameManager game, float t)
    {
        using (paper.Column("screen").PositionType(PositionType.SelfDirected)
            .AnchorLeft(0).AnchorRight(0).AnchorTop(0).AnchorBottom(0)
            .BackgroundColor(Theme.Black(0.5f)).BackdropBlur(8f * t)
            .Opacity(t)
            .Enter())
        {
            Header(paper, font, game);

            using (paper.Row("main").Padding(0, S8, S8, S8).Enter())
            {
                using (paper.Column("items").Width(UnitValue.Auto).Gap(S6)
                    .TranslateX(-Px(24f) * (1f - t))
                    .Enter())
                {
                    MenuItem(paper, font, Icon.Play, Theme.Good, "Resume", () => SetOpen(false), 0);
                    MenuItem(paper, font, Icon.Settings, Theme.Tertiary, "Settings", () => _settingsOpen = !_settingsOpen, 1);
                    MenuItem(paper, font, Icon.Refresh, Theme.AccentSoft, "Respawn", () =>
                    {
                        game.LocalPlayer.Respawn();
                        SetOpen(false);
                    }, 2);
                    MenuItem(paper, font, Icon.Delete, Theme.Warning, "Clean up props", () => game.Cleanup(), 3);

                    paper.Box("spacer").Height(UnitValue.Stretch()).IsNotInteractable();
                    MenuItem(paper, font, Icon.Logout, Theme.Error, "Quit", Application.Quit, 4);
                }

                paper.Box("middle").Width(UnitValue.Stretch()).IsNotInteractable();

                if (_settingsOpen) SettingsPanel(paper, game);
            }
        }
    }

    /// <summary>The bar across the top: the wordmark on the left, a few stats in the middle.</summary>
    private void Header(Paper paper, FontFile font, GameManager game)
    {
        using (paper.Row("header").Height(Px(72f)).Padding(S6, S6, 0, 0)
            .BackgroundColor(Theme.Secondary).AsLifted()
            .Enter())
        {
            using (paper.Row("brand").Width(UnitValue.Stretch()).Enter())
            {
                paper.Box("wordmark").Width(UnitValue.Auto)
                    .Text("SLOPBOX", font).FontSize(Text2Xl).TextColor(Theme.Accent)
                    .Alignment(TextAlignment.MiddleLeft);
            }

            using (paper.Row("stats").Width(UnitValue.Auto).Gap(S8).Enter())
            {
                Stat(paper, font, "Time", DateTime.Now.ToString("hh:mm tt"), 0);
                Stat(paper, font, "Props", Prop.All.Length.ToString(), 1);
                Stat(paper, font, "Undo", game.Undo.Count.ToString(), 2);
            }

            paper.Box("balance").Width(UnitValue.Stretch()).IsNotInteractable();
        }
    }

    private static void Stat(Paper paper, FontFile font, string label, string value, int id)
    {
        using (paper.Column("stat", id).Width(UnitValue.Auto).Height(UnitValue.Auto).Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch()).Enter())
        {
            paper.Box("label").Width(UnitValue.Auto).Height(Px(18f))
                .Text(label.ToUpperInvariant(), font).FontSize(TextXs).LetterSpacing(Px(0.6f)).TextColor(Theme.Gray400)
                .Alignment(TextAlignment.MiddleCenter);
            paper.Box("value").Width(UnitValue.Auto).Height(Px(26f))
                .Text(value, font).FontSize(Px(20f)).TextColor(Theme.White(1f))
                .Alignment(TextAlignment.MiddleCenter);
        }
    }

    /// <summary>A large menu entry: coloured icon and title on a dark block that grows a little on hover.</summary>
    private static void MenuItem(Paper paper, FontFile font, Icon icon, Color iconColor, string text, Action onClick, int id)
    {
        float iconSize = Px(40f);
        using (paper.Row("menu item", id).Width(Px(340f)).Height(Px(76f))
            .Padding(S6, S4, 0, 0).Gap(Px(18f))
            .BackgroundColor(Theme.Secondary).AsLifted()
            .Hovered.BackgroundColor(Ui.Lighten(Theme.Secondary, 0.04f)).End()
            .Scale(1f).Hovered.Scale(1.04f).End().Active.Scale(0.98f).End()
            .TransformOrigin(0f, 0.5f)
            .Transition(GuiProp.ScaleX, 0.12f, Easing.CubicOut).Transition(GuiProp.ScaleY, 0.12f, Easing.CubicOut)
            .Transition(GuiProp.BackgroundColor, 0.12f)
            .Cursor(PaperCursor.Pointer)
            .OnEnter(_ => GameAudio.Hover())
            .OnClick(_ => { GameAudio.Click(); onClick(); })
            .Enter())
        {
            using (paper.Box("icon holder").Width(iconSize).Height(iconSize).Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch()).IsNotInteractable().Enter())
                Icons.Box(paper, icon, iconSize, iconColor);
            paper.Box("label").Width(UnitValue.Stretch())
                .Text(text, font).FontSize(Px(24f)).TextColor(Theme.White(1f))
                .Alignment(TextAlignment.MiddleLeft)
                .IsNotInteractable();
        }
    }

    private static void SettingsPanel(Paper paper, GameManager game)
    {
        Settings s = game.Settings;

        using (paper.Column("settings").Width(Px(460f)).Height(UnitValue.Auto)
            .BackgroundColor(Theme.PrimaryA(0.85f)).Rounded(BodyRadius).AsLifted()
            .Padding(S4).Gap(S1)
            .Enter())
        {
            Ui.InspectorTitle(paper, "Settings");

            Ui.Caption(paper, "Mouse", 0);
            using (paper.Column("mouse").Height(UnitValue.Auto).Padding(S2, S2, 0, 0).Gap(S2).Enter())
            {
                Ui.Slider(paper, "Sensitivity", s.Sensitivity, 0.2f, 10f, v => s.Sensitivity = v, "0.0", 0);
                Ui.Checkbox(paper, "Invert vertical look", s.InvertY, v => s.InvertY = v, 0);
            }

            Ui.Caption(paper, "View", 1);
            using (paper.Column("view").Height(UnitValue.Auto).Padding(S2, S2, 0, 0).Gap(S2).Enter())
            {
                Ui.Slider(paper, "Field of view", s.FieldOfView, 50f, 110f, v => s.FieldOfView = v, "0", 0);
                Ui.Slider(paper, "View bob", s.ViewBob, 0f, 2f, v => s.ViewBob = v, "0.0", 1);
                Ui.Slider(paper, "Strafe roll", s.ViewRoll, 0f, 5f, v => s.ViewRoll = v, "0.0", 2);
            }

            Ui.Caption(paper, "Audio", 3);
            using (paper.Column("audio").Height(UnitValue.Auto).Padding(S2, S2, 0, 0).Gap(S2).Enter())
            {
                Ui.Slider(paper, "Master volume", s.MasterVolume, 0f, 1f, v => s.MasterVolume = v, "0.00", 0);
                Ui.Slider(paper, "Sound effects", s.EffectsVolume, 0f, 1f, v => s.EffectsVolume = v, "0.00", 1);
                Ui.Slider(paper, "Interface sounds", s.UiVolume, 0f, 1f, v => s.UiVolume = v, "0.00", 2);
                Ui.Slider(paper, "Ambience", s.AmbienceVolume, 0f, 1f, v => s.AmbienceVolume = v, "0.00", 3);
            }

            Ui.Caption(paper, "Interface", 2);
            using (paper.Column("interface").Height(UnitValue.Auto).Padding(S2, S2, 0, 0).Gap(S2).Enter())
            {
                Ui.Slider(paper, "UI scale", s.UiScale, 0.75f, 2f, v => s.UiScale = MathF.Round(v * 20f) / 20f, "0.00x", 0);
                Ui.Checkbox(paper, "Show HUD (F1)", s.ShowHud, v => s.ShowHud = v, 0);
                Ui.Checkbox(paper, "Show FPS", s.ShowFps, v => s.ShowFps = v, 1);
            }
        }
    }
}
