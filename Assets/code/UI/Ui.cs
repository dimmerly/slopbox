using System;
using Prowl.PaperUI;
using Prowl.PaperUI.Events;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Scribe;
using Prowl.Vector;
using static Slopbox.Theme;
using KeyCode = Prowl.Runtime.KeyCode;
using TextAlignment = Prowl.PaperUI.TextAlignment;

namespace Slopbox;

/// <summary>Scroll position for a <see cref="Ui.Scroll"/> area. Keep one per list in a field.</summary>
public sealed class ScrollState
{
    public float Offset;
    public float ContentHeight;
    public float ViewHeight;

    public float Max => MathF.Max(0f, ContentHeight - ViewHeight);
    public void Clamp() => Offset = Math.Clamp(Offset, 0f, Max);
}

/// <summary>
/// The widget kit, styled after DXRP's build menu and tool inspector. Paper has no widgets of its own,
/// so these are boxes. All sizes go through <see cref="Theme.Px"/>. Each takes an id so it can be used
/// several times under one parent: Paper ids include the caller's line, which is shared inside a helper.
/// </summary>
public static class Ui
{
    private static FontFile Font => Theme.Font;

    // ----------------------------------------------------------------
    //  Build menu chrome
    // ----------------------------------------------------------------

    public static float TabHeight => Px(32f);

    /// <summary>A folder tab above a menu body.</summary>
    public static void MenuTab(Paper paper, Icon icon, string text, bool active, Action onClick, int id = 0)
    {
        float iconSize = Px(18f);
        using (paper.Row("ui menu tab", id).Width(UnitValue.Auto).Height(TabHeight)
            .Padding(Px(12f), Px(14f), 0, 0).Gap(Px(6f))
            .Rounded(Theme.Rounded, Theme.Rounded, 0f, 0f)
            .BackgroundColor(active ? Theme.Accent : Theme.Primary)
            .Hovered.BackgroundColor(active ? Theme.Accent : Lighten(Theme.Primary, 0.05f)).End()
            .Cursor(PaperCursor.Pointer)
            .OnEnter(_ => GameAudio.Hover())
            .OnClick(_ => { GameAudio.Click(); onClick(); })
            .Enter())
        {
            using (paper.Box("icon wrap").Width(iconSize).Height(iconSize).Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch()).IsNotInteractable().Enter())
                Icons.Box(paper, icon, iconSize, Theme.White(0.95f));
            paper.Box("label").Width(UnitValue.Auto)
                .Text(text, Font).FontSize(Px(15f)).TextColor(Theme.White(0.95f))
                .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
        }
    }

    /// <summary>A small uppercase caption above a group of controls.</summary>
    public static void Caption(Paper paper, string text, int id = 0)
    {
        paper.Box("ui caption", id).Height(UnitValue.Auto).Padding(Theme.S2, Theme.S2, Px(12f), Theme.S1)
            .Text(text.ToUpperInvariant(), Font).FontSize(Px(12f)).LetterSpacing(Px(0.6f)).TextColor(Theme.White(0.55f))
            .Alignment(TextAlignment.MiddleLeft)
            .IsNotInteractable();
    }

    /// <summary>A dark bar heading a group of list rows.</summary>
    public static void GroupTitle(Paper paper, string text, int id = 0)
    {
        paper.Box("ui group title", id).Height(Px(26f)).Padding(Px(10f), Px(10f), 0, 0)
            .BackgroundColor(Theme.SecondaryA(0.7f)).Rounded(Px(3f))
            .Text(text, Font).FontSize(Px(13f)).TextColor(Theme.White(0.95f))
            .Alignment(TextAlignment.MiddleLeft)
            .IsNotInteractable();
    }

    /// <summary>A tool in the tool list. Active is a soft accent tint.</summary>
    public static void ToolItem(Paper paper, string text, bool active, Action onClick, int id = 0)
    {
        paper.Box("ui tool item", id).Height(Px(30f)).Padding(Px(10f), Px(10f), 0, 0)
            .Rounded(Px(3f))
            .BackgroundColor(active ? Theme.AccentA(0.25f) : Theme.Clear)
            .Hovered.BackgroundColor(active ? Theme.AccentA(0.32f) : Theme.White(0.06f)).End()
            .Text(text, Font).FontSize(Theme.TextSm).TextColor(active ? Theme.White(1f) : Theme.White(0.85f))
            .Alignment(TextAlignment.MiddleLeft)
            .TextTruncate()
            .Cursor(PaperCursor.Pointer)
            .OnEnter(_ => GameAudio.Hover())
            .OnClick(_ => onClick());
    }

    /// <summary>A folder row in the spawn nav, with a count on the right.</summary>
    public static void CategoryRow(Paper paper, string text, int count, bool active, Action onClick, int id = 0)
    {
        using (paper.Row("ui category", id).Height(Px(34f)).Padding(Px(10f), Px(10f), 0, 0).Gap(Theme.S1)
            .Rounded(Px(3f))
            .BackgroundColor(active ? Theme.AccentA(0.28f) : Theme.Clear)
            .Hovered.BackgroundColor(active ? Theme.AccentA(0.28f) : Theme.White(0.06f)).End()
            .Cursor(PaperCursor.Pointer)
            .OnEnter(_ => GameAudio.Hover())
            .OnClick(_ => { GameAudio.Click(); onClick(); })
            .Enter())
        {
            paper.Box("name").Width(UnitValue.Stretch())
                .Text(text, Font).FontSize(Px(15f)).TextColor(active ? Theme.White(1f) : Theme.White(0.65f))
                .Alignment(TextAlignment.MiddleLeft).TextTruncate().IsNotInteractable();
            paper.Box("count").Width(UnitValue.Auto)
                .Text(count.ToString(), Font).FontSize(Px(12f)).TextColor(active ? Theme.White(0.75f) : Theme.White(0.4f))
                .Alignment(TextAlignment.MiddleRight).IsNotInteractable();
        }
    }

    /// <summary>
    /// A search field with a magnifier, dark with an accent edge while focused. Enter or Escape leave the
    /// field, which lets a hold-to-open menu close again.
    /// </summary>
    public static void SearchField(Paper paper, string id, string value, Action<string> onChange, string placeholder)
    {
        float icon = Px(16f);
        using (paper.Box(id).Height(Px(36f)).Padding(Px(34f), Px(12f), 0, 0)
            .Rounded(Theme.Rounded)
            .BackgroundColor(Theme.SecondaryA(0.95f))
            .BorderWidth(1).BorderColor(Theme.White(0.08f))
            .Focused.BorderColor(Theme.Accent).End()
            .FontSize(Theme.TextSm)
            .Alignment(TextAlignment.MiddleLeft)
            .TextField(value, Font, onChange, Theme.White(0.9f), placeholder, Theme.White(0.4f))
            .OnKeyPressed(e =>
            {
                if (e.Key is PaperKey.Enter or PaperKey.Escape) paper.ClearFocus();
            })
            .Enter())
        {
            paper.Draw((canvas, rect) => Icons.Draw(canvas, Icon.Search, rect.Min.X + Px(17f), rect.Min.Y + rect.Size.Y * 0.5f, icon, Theme.White(0.45f)));
        }
    }

    // ----------------------------------------------------------------
    //  Text
    // ----------------------------------------------------------------

    public static void InspectorTitle(Paper paper, string text, int id = 0)
    {
        paper.Box("ui inspector title", id).Height(Px(32f)).Padding(Theme.S2, Theme.S2, Px(6f), 0)
            .Text(text, Font).FontSize(Px(20f)).TextColor(Theme.White(0.97f))
            .Alignment(TextAlignment.MiddleLeft)
            .IsNotInteractable();
    }

    public static void Description(Paper paper, string text, int id = 0, Color? color = null)
    {
        paper.Box("ui description", id).Height(UnitValue.Auto)
            .Text(text, Font).FontSize(Theme.TextSm).LineHeight(1.45f).TextColor(color ?? Theme.White(0.7f))
            .Wrap(TextWrapMode.Wrap)
            .Alignment(TextAlignment.Left)
            .IsNotInteractable();
    }

    // ----------------------------------------------------------------
    //  Controls
    // ----------------------------------------------------------------

    public static void Button(Paper paper, string text, Action onClick, int id = 0, bool fit = false, Icon? icon = null)
    {
        float iconSize = Px(18f);
        using (paper.Row("ui button", id).Width(fit ? UnitValue.Auto : UnitValue.Stretch()).Height(Px(40f))
            .Padding(Px(12f), Px(12f), 0, 0).Gap(Px(10f))
            .Rounded(Theme.Rounded)
            .BackgroundColor(Theme.Secondary)
            .Hovered.BackgroundColor(Lighten(Theme.Secondary, 0.05f)).End()
            .Scale(1f).Active.Scale(0.98f).End()
            .Transition(GuiProp.BackgroundColor, 0.1f)
            .Cursor(PaperCursor.Pointer)
            .OnEnter(_ => GameAudio.Hover())
            .OnClick(_ => { GameAudio.Click(); onClick(); })
            .Enter())
        {
            if (icon.HasValue)
                using (paper.Box("icon wrap").Width(iconSize).Height(iconSize).Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch()).IsNotInteractable().Enter())
                    Icons.Box(paper, icon.Value, iconSize, Theme.Accent);

            paper.Box("label").Width(fit ? UnitValue.Auto : UnitValue.Stretch())
                .Text(text, Font).FontSize(Px(15f)).TextColor(Theme.White(1f))
                .Alignment(icon.HasValue ? TextAlignment.MiddleLeft : TextAlignment.MiddleCenter)
                .IsNotInteractable();
        }
    }

    public static void Checkbox(Paper paper, string text, bool value, Action<bool> onChange, int id = 0)
    {
        float box = Px(20f);
        using (paper.Row("ui checkbox", id).Height(Px(32f)).Gap(Theme.S3)
            .Cursor(PaperCursor.Pointer)
            .OnEnter(_ => GameAudio.Hover())
            .OnClick(_ => { GameAudio.Click(); onChange(!value); })
            .Enter())
        {
            using (paper.Box("box").Width(box).Height(box).Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch()).Rounded(Px(3f))
                .BackgroundColor(value ? Theme.Accent : Theme.Secondary)
                .BorderWidth(1).BorderColor(value ? Theme.Accent : Theme.White(0.14f))
                .Transition(GuiProp.BackgroundColor, 0.1f)
                .IsNotInteractable()
                .Enter())
            {
                if (value) Icons.Box(paper, Icon.Check, box, Theme.OnAccent);
            }

            paper.Box("text").Width(UnitValue.Stretch())
                .Text(text, Font).FontSize(Px(15f)).TextColor(Theme.White(0.9f))
                .Alignment(TextAlignment.MiddleLeft)
                .IsNotInteractable();
        }
    }

    /// <summary>A labelled slider with its value in a box on the right.</summary>
    public static void Slider(Paper paper, string text, float value, float min, float max, Action<float> onChange, string format = "0.00", int id = 0)
    {
        float t = Math.Clamp((value - min) / MathF.Max(max - min, 1e-6f), 0f, 1f);
        float row = Px(32f), thumb = Px(16f), rail = Px(4f);

        using (paper.Column("ui slider", id).Height(UnitValue.Auto).Gap(Px(2f)).Enter())
        {
            paper.Box("label").Height(Px(22f))
                .Text(text, Font).FontSize(Px(15f)).TextColor(Theme.White(0.9f))
                .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

            using (paper.Row("input row").Height(row).Gap(Theme.S2).Enter())
            {
                void Set(ElementEvent e)
                {
                    onChange(min + Math.Clamp(e.NormalizedPosition.X, 0f, 1f) * (max - min));
                    GameAudio.Play("hover", 0.08f, ui: true, cooldown: 0.12f);
                }

                using (paper.Box("track").Width(UnitValue.Stretch())
                    .Cursor(PaperCursor.Pointer)
                    .OnPress(e => Set(e))
                    .OnDragging(e => Set(e))
                    .Enter())
                using (paper.Box("inner").Margin(thumb / 2f, thumb / 2f, 0, 0).IsNotInteractable().Enter())
                {
                    float railTop = (row - rail) / 2f;
                    paper.Box("rail").Height(rail).Margin(0, 0, railTop, railTop).Rounded(rail / 2f).BackgroundColor(Theme.Secondary).IsNotInteractable();
                    paper.Box("fill").Width(UnitValue.Percentage(t * 100f)).Height(rail)
                        .PositionType(PositionType.SelfDirected).Position(0, railTop)
                        .Rounded(rail / 2f).BackgroundColor(Theme.Accent).IsNotInteractable();
                    paper.Box("thumb").Width(thumb).Height(thumb).Rounded(thumb / 2f)
                        .PositionType(PositionType.SelfDirected)
                        .AnchorLeft(UnitValue.Percentage(t * 100f, -thumb / 2f)).AnchorTop((row - thumb) / 2f)
                        .BackgroundColor(Theme.Tertiary)
                        .BorderWidth(Px(2f)).BorderColor(Theme.Accent)
                        .IsNotInteractable();
                }

                paper.Box("value").Width(Px(64f)).Height(row)
                    .Rounded(Theme.Rounded).BackgroundColor(Theme.Secondary)
                    .Text(value.ToString(format), Font).FontSize(Theme.TextSm).TextColor(Theme.White(0.85f))
                    .Alignment(TextAlignment.MiddleCenter).IsNotInteractable();
            }
        }
    }

    public static void EnumPicker(Paper paper, string text, Enum value, Action<object> onChange, int id = 0)
    {
        Array values = Enum.GetValues(value.GetType());
        int index = Array.IndexOf(values, value);
        object next = values.GetValue((index + 1) % values.Length);

        using (paper.Column("ui enum", id).Height(UnitValue.Auto).Gap(Theme.S1).Enter())
        {
            paper.Box("label").Height(Px(22f))
                .Text(text, Font).FontSize(Px(15f)).TextColor(Theme.White(0.9f))
                .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            Button(paper, value.ToString(), () => onChange(next));
        }
    }

    /// <summary>
    /// A key binding: shows the bound key, and clicking it calls <paramref name="onListen"/> so the caller can
    /// wait for the next key press. While <paramref name="listening"/> it is outlined in the accent.
    /// </summary>
    public static void KeyBind(Paper paper, string text, KeyCode value, bool listening, Action onListen, int id = 0)
    {
        string shown = listening ? "Press a key... (Esc cancel, Backspace clear)" : KeyName(value);

        using (paper.Column("ui keybind", id).Height(UnitValue.Auto).Gap(Theme.S1).Enter())
        {
            paper.Box("label").Height(Px(22f))
                .Text(text, Font).FontSize(Px(15f)).TextColor(Theme.White(0.9f))
                .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

            using (paper.Row("key").Height(Px(40f)).Padding(Px(12f), Px(12f), 0, 0)
                .Rounded(Theme.Rounded)
                .BackgroundColor(Theme.Secondary)
                .BorderWidth(Px(2f)).BorderColor(listening ? Theme.Accent : Theme.Clear)
                .Hovered.BackgroundColor(Lighten(Theme.Secondary, 0.05f)).End()
                .Transition(GuiProp.BackgroundColor, 0.1f)
                .Cursor(PaperCursor.Pointer)
                .OnClick(_ => { GameAudio.Click(); onListen(); })
                .Enter())
            {
                paper.Box("name").Width(UnitValue.Stretch())
                    .Text(shown, Font).FontSize(Px(15f)).TextColor(listening ? Theme.Accent : Theme.White(1f))
                    .Alignment(TextAlignment.MiddleCenter).TextTruncate()
                    .IsNotInteractable();
            }
        }
    }

    /// <summary>A key's name for display. <see cref="KeyCode.Unknown"/> means unbound.</summary>
    public static string KeyName(KeyCode key) => key switch
    {
        KeyCode.Unknown => "None",
        >= KeyCode.Number0 and <= KeyCode.Number9 => ((int)(key - KeyCode.Number0)).ToString(),
        >= KeyCode.Keypad0 and <= KeyCode.Keypad9 => $"Keypad {(int)(key - KeyCode.Keypad0)}",
        _ => key.ToString(),
    };

    private static readonly Color[] Palette =
    [
        new(1f, 1f, 1f, 1f), new(0.6f, 0.6f, 0.6f, 1f), new(0.25f, 0.25f, 0.25f, 1f), new(0.05f, 0.05f, 0.05f, 1f),
        new(0.94f, 0.33f, 0f, 1f), new(1f, 0.1f, 0.1f, 1f), new(1f, 0.8f, 0.1f, 1f), new(0.2f, 0.8f, 0.3f, 1f),
        new(0.1f, 0.8f, 0.85f, 1f), new(0.15f, 0.4f, 1f, 1f), new(0.44f, 0.44f, 0.9f, 1f), new(1f, 0.35f, 0.7f, 1f),
    ];

    public static void ColorPicker(Paper paper, string text, Color value, Action<Color> onChange, int id = 0)
    {
        float swatch = Px(28f);
        using (paper.Column("ui color", id).Height(UnitValue.Auto).Gap(Theme.S2).Enter())
        {
            using (paper.Row("title").Height(Px(22f)).Gap(Theme.S2).Enter())
            {
                paper.Box("name").Width(UnitValue.Stretch())
                    .Text(text, Font).FontSize(Px(15f)).TextColor(Theme.White(0.9f))
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                paper.Box("preview").Width(Px(44f)).Height(Px(18f)).Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch()).Rounded(Px(3f))
                    .BackgroundColor(value).BorderWidth(1).BorderColor(Theme.White(0.14f)).IsNotInteractable();
            }

            using (paper.Row("swatches").Height(UnitValue.Auto).WrapContent().Gap(Px(6f)).LineGap(Px(6f)).Enter())
            {
                for (int i = 0; i < Palette.Length; i++)
                {
                    Color c = Palette[i];
                    bool selected = MathF.Abs(c.R - value.R) + MathF.Abs(c.G - value.G) + MathF.Abs(c.B - value.B) < 0.01f;
                    paper.Box("swatch", i).Width(swatch).Height(swatch).Rounded(Px(3f))
                        .BackgroundColor(c)
                        .BorderWidth(Px(2f)).BorderColor(selected ? Theme.Tertiary : Theme.Clear)
                        .Hovered.BorderColor(selected ? Theme.Tertiary : Theme.White(0.4f)).End()
                        .Cursor(PaperCursor.Pointer)
                        .OnClick(_ => { GameAudio.Click(); onChange(new Color(c.R, c.G, c.B, value.A)); });
                }
            }

            Slider(paper, "Red", value.R, 0f, 1f, v => onChange(new Color(v, value.G, value.B, value.A)), "0.00", 0);
            Slider(paper, "Green", value.G, 0f, 1f, v => onChange(new Color(value.R, v, value.B, value.A)), "0.00", 1);
            Slider(paper, "Blue", value.B, 0f, 1f, v => onChange(new Color(value.R, value.G, v, value.A)), "0.00", 2);
        }
    }

    // ----------------------------------------------------------------
    //  Input glyphs
    // ----------------------------------------------------------------

    /// <summary>A light key cap, standing in for s&amp;box's input glyphs.</summary>
    public static void KeyCap(Paper paper, string key, int id = 0)
    {
        float size = Px(28f);
        paper.Box("ui key cap", id).Width(UnitValue.Auto).MinWidth(size).Height(size).Padding(Px(7f), Px(7f), 0, 0)
            .Rounded(Theme.Rounded)
            .BackgroundColor(Theme.Tertiary)
            .BoxShadow(0f, Px(2f), 0f, 0f, Theme.Black(0.45f))
            .Text(key, Font).FontSize(Px(13f)).TextColor(Theme.Primary)
            .Alignment(TextAlignment.MiddleCenter)
            .IsNotInteractable();
    }

    /// <summary>A key cap and what it does.</summary>
    public static void KeyHint(Paper paper, string key, string action, int id = 0)
    {
        float cap = Px(28f);
        using (paper.Row("ui key hint", id).Width(UnitValue.Auto).Height(Px(34f)).Gap(Px(10f)).IsNotInteractable().Enter())
        {
            using (paper.Box("cap wrap").Width(UnitValue.Auto).Height(cap).Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch()).Enter())
                KeyCap(paper, key);
            paper.Box("action").Width(UnitValue.Auto)
                .Text(action, Font).FontSize(Px(15f)).TextColor(Theme.Tertiary)
                .Alignment(TextAlignment.MiddleLeft);
        }
    }

    // ----------------------------------------------------------------
    //  Scrolling
    // ----------------------------------------------------------------

    /// <summary>
    /// A clipped area whose content scrolls with the wheel. Declare the content inside the returned scope;
    /// it is laid out as a column at full width, starting <paramref name="topInset"/> down.
    /// </summary>
    public static ScrollScope Scroll(Paper paper, string id, ScrollState state, float gap = 2f, int intId = 0, float topInset = 0f)
    {
        state.Clamp();
        bool overflowing = state.Max > 0f;

        IDisposable outer = paper.Box(id, intId)
            .Clip()
            .OnPostLayout((_, rect) => state.ViewHeight = rect.Size.Y)
            .OnScroll(e =>
            {
                state.Offset -= e.Delta * Px(48f);
                state.Clamp();
                e.StopPropagation();
            })
            .Enter();

        if (overflowing)
        {
            float visible = state.ViewHeight / MathF.Max(state.ContentHeight, 1f);
            float top = state.Offset / state.ContentHeight * 100f;
            paper.Box("scrollbar", intId).Width(Px(4f)).Height(UnitValue.Percentage(visible * 100f))
                .PositionType(PositionType.SelfDirected)
                .AnchorRight(0).AnchorTop(UnitValue.Percentage(top))
                .Rounded(Px(2f)).BackgroundColor(Theme.White(0.15f))
                .IsNotInteractable();
        }

        IDisposable inner = paper.Column("content", intId)
            .PositionType(PositionType.SelfDirected)
            .AnchorLeft(0).AnchorRight(overflowing ? Px(10f) : 0)
            .Top(topInset - state.Offset)
            .Height(UnitValue.Auto)
            .Gap(gap)
            .OnPostLayout((_, rect) => state.ContentHeight = rect.Size.Y + topInset)
            .Enter();

        return new ScrollScope(outer, inner);
    }

    public readonly struct ScrollScope(IDisposable outer, IDisposable inner) : IDisposable
    {
        public void Dispose()
        {
            inner.Dispose();
            outer.Dispose();
        }
    }

    /// <summary>
    /// Draws a Prowl texture filling <paramref name="rect"/>. Paper samples Prowl textures bottom row
    /// first, so the image is mirrored vertically about its centre to come out upright.
    /// </summary>
    public static void DrawTexture(Prowl.Quill.Canvas canvas, Rect rect, Prowl.Runtime.Resources.Texture2D texture)
    {
        float centerY = rect.Min.Y + rect.Size.Y * 0.5f;
        canvas.SaveState();
        canvas.TransformBy(new Prowl.Vector.Spatial.Transform2D(1f, 0f, 0f, -1f, 0f, centerY * 2f));
        canvas.DrawImage(texture, rect.Min.X, rect.Min.Y, rect.Size.X, rect.Size.Y);
        canvas.RestoreState();
    }

    /// <summary>SCSS's lighten(): moves each channel toward white by <paramref name="amount"/>.</summary>
    public static Color Lighten(Color c, float amount)
        => new(MathF.Min(1f, c.R + amount), MathF.Min(1f, c.G + amount), MathF.Min(1f, c.B + amount), c.A);
}
