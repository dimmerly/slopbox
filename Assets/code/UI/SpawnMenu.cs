using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Quill;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Scribe;
using Prowl.Vector;
using Prowl.Vector.Spatial;
using static Slopbox.Theme;
using TextAlignment = Prowl.PaperUI.TextAlignment;

namespace Slopbox;

/// <summary>
/// The Q menu, in the style of DXRP's build menu. Hold Q to open it.
/// <list type="bullet">
/// <item>Left: one tab per spawn category over a body with a folder nav (groups with counts) and the
/// thumbnail grid, with the search box floating at the top of the grid. Click a cell to spawn at the
/// crosshair.</item>
/// <item>Right: Tools and Utilities tabs. Tools lists every tool by category beside an inspector generated
/// from the selected tool's <see cref="ToolSettingAttribute"/> fields.</item>
/// </list>
/// </summary>
public sealed class SpawnMenu : Component
{
    private enum RightTab { Tools, Utilities }

    private bool _open;
    private string _category = "";
    private string _group = "";
    private string _search = "";
    private string _toolSearch = "";
    private string _materialSearch = "";
    private string _hoveredMaterial = "";
    private RightTab _rightTab = RightTab.Tools;

    /// <summary>The key setting waiting for a key press, or null.</summary>
    private (BaseTool Tool, FieldInfo Field)? _binding;

    private static readonly KeyCode[] s_keys = Enum.GetValues<KeyCode>().Where(k => k != KeyCode.Unknown).Distinct().ToArray();

    private readonly ScrollState _navScroll = new(), _gridScroll = new(), _toolScroll = new(), _inspectorScroll = new();
    private readonly Dictionary<Type, List<(FieldInfo Field, ToolSettingAttribute Setting)>> _settingFields = new();

    private static float Cell => Px(124f);

    public bool IsOpen => _open;

    /// <summary>True while a key setting is waiting for a press, so that press does not also act in the game.</summary>
    public static bool IsBindingKey => s_listening;

    /// <summary>The key just bound, ignored by the game until it is released. Unknown when there is none.</summary>
    public static KeyCode SwallowedKey { get; private set; } = KeyCode.Unknown;

    private static bool s_listening;

    public override void OnDisable() => SetOpen(false);

    public override void Update()
    {
        GameManager game = GameManager.Instance;
        bool typing = game.Paper != null && game.Paper.WantsCaptureKeyboard;

        if (SwallowedKey != KeyCode.Unknown && !Input.GetKey(SwallowedKey)) SwallowedKey = KeyCode.Unknown;
        if (_binding != null) ListenForKey();

        // Hold to open, like gmod and DXRP. Stays open while typing in a search box or binding a key.
        bool want = !game.IsPaused && (Controls.Down(Controls.SpawnMenu) || (_open && (typing || _binding != null)));
        if (want != _open) SetOpen(want);
    }

    /// <summary>Binds the next key pressed. Escape cancels and Backspace unbinds.</summary>
    private void ListenForKey()
    {
        if (!Input.AnyKeyDown) return;
        foreach (KeyCode key in s_keys)
        {
            if (!Input.GetKeyDown(key)) continue;
            (BaseTool tool, FieldInfo field) = _binding.Value;
            if (key != KeyCode.Escape) field.SetValue(tool, key == KeyCode.Backspace ? KeyCode.Unknown : key);
            SwallowedKey = key;
            StopBinding();
            return;
        }
    }

    private void StopBinding()
    {
        _binding = null;
        s_listening = false;
    }

    private void SetOpen(bool open)
    {
        _open = open;
        if (!open) StopBinding();
        GameManager game = GameManager.Instance;
        if (game.IsNotValid()) return;
        game.SetMenuOpen(this, open);
        if (!open && game.Paper != null) game.Paper.ClearFocus();
    }

    public override void OnGui(Paper paper)
    {
        GameManager game = GameManager.Instance;
        FontFile font = Theme.Font;
        if (font == null || game.LocalPlayer.IsNotValid() || game.Catalog == null) return;

        paper.PushID(InstanceID);
        using (paper.Box("spawn menu root").PositionType(PositionType.SelfDirected)
            .AnchorLeft(0).AnchorRight(0).AnchorTop(0).AnchorBottom(0)
            .IsNotInteractable()
            .Enter())
        {
            float t = paper.AnimateBool(_open, 0.18f, Easing.CubicOut);
            if (t > 0.001f)
            {
                float rightWidth = Math.Clamp(paper.Width * 0.32f, Px(460f), Px(680f));
                using (paper.Row("menu").PositionType(PositionType.SelfDirected)
                    .AnchorLeft(UnitValue.Percentage(2.5f)).AnchorRight(UnitValue.Percentage(2.5f))
                    .AnchorTop(UnitValue.Percentage(2.5f)).AnchorBottom(UnitValue.Percentage(2.5f))
                    .Gap(S2)
                    .Opacity(t).Scale(0.985f + 0.015f * t)
                    .IsNotInteractable()
                    .Enter())
                {
                    SpawnSide(paper, font, game);
                    using (paper.Column("right").Width(rightWidth).Enter())
                        RightSide(paper, font, game);
                }
            }
        }
        paper.PopID();
    }

    // ----------------------------------------------------------------
    //  Spawn side
    // ----------------------------------------------------------------

    private static Icon CategoryIcon(string category) => category switch
    {
        "Shapes" => Icon.Shapes,
        _ => Icon.Cube,
    };

    private void SpawnSide(Paper paper, FontFile font, GameManager game)
    {
        SpawnCatalog catalog = game.Catalog;
        if (_category.Length == 0 && catalog.Categories.Count > 0) _category = catalog.Categories[0];
        string query = _search.Trim();
        bool searching = query.Length > 0;

        using (paper.Column("left").Width(UnitValue.Stretch()).Enter())
        {
            using (paper.Row("tabs").Height(Ui.TabHeight).Gap(S1).Enter())
            {
                for (int i = 0; i < catalog.Categories.Count; i++)
                {
                    string category = catalog.Categories[i];
                    Ui.MenuTab(paper, CategoryIcon(category), category, category == _category, () =>
                    {
                        _category = category;
                        _group = "";
                        _gridScroll.Offset = 0f;
                    }, i);
                }
            }

            List<Spawnable> inCategory = catalog.InCategory(_category).ToList();
            List<string> groups = inCategory.Select(s => s.Group).Where(g => g.Length > 0).Distinct().ToList();
            if (_group.Length > 0 && !groups.Contains(_group)) _group = "";

            // The search covers the open tab, like DXRP's prop search.
            List<Spawnable> items = inCategory
                .Where(s => _group.Length == 0 || s.Group == _group)
                .Where(s => !searching || s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            using (paper.Row("body").AsMenuBody().Padding(S2).Gap(S2).Enter())
            {
                using (paper.Column("nav").Width(Px(210f))
                    .BackgroundColor(Theme.Black(0.4f)).Rounded(Theme.Rounded).Padding(S1)
                    .Enter())
                using (Ui.Scroll(paper, "nav scroll", _navScroll, Px(2f)))
                {
                    Ui.CategoryRow(paper, "All", inCategory.Count, _group.Length == 0, () => SelectGroup(""), -1);
                    for (int i = 0; i < groups.Count; i++)
                    {
                        string group = groups[i];
                        int count = inCategory.Count(s => s.Group == group);
                        Ui.CategoryRow(paper, group, count, group == _group, () => SelectGroup(group), i);
                    }
                }

                // The grid scrolls under a search box that floats at the top of the content area.
                float searchBand = Px(56f);
                using (paper.Box("canvas").Width(UnitValue.Stretch()).Enter())
                {
                    using (Ui.Scroll(paper, "grid scroll", _gridScroll, 0f, 0, searchBand))
                    {
                        if (items.Count == 0)
                        {
                            EmptyState(paper, font, searching ? $"Nothing matches \"{query}\"" : "Nothing here yet");
                        }
                        else
                        {
                            Thumbnails thumbnails = game.Thumbnails;
                            using (paper.Row("grid").Height(UnitValue.Auto).WrapContent().Gap(Px(6f)).LineGap(Px(6f)).Padding(S1).Enter())
                                for (int i = 0; i < items.Count; i++)
                                    SpawnCell(paper, font, game, thumbnails, items[i], i);
                        }
                    }

                    using (paper.Box("search band").PositionType(PositionType.SelfDirected)
                        .AnchorLeft(0).AnchorRight(0).AnchorTop(0).Height(searchBand)
                        .IsNotInteractable()
                        .Enter())
                    using (paper.Box("search holder").Width(UnitValue.Percentage(60f)).MaxWidth(Px(440f)).Height(Px(36f))
                        .Margin(UnitValue.Stretch(), UnitValue.Stretch(), Px(10f), 0)
                        .Enter())
                    {
                        Ui.SearchField(paper, "search", _search, v => { _search = v; _gridScroll.Offset = 0f; }, $"Search {_category.ToLowerInvariant()}...");
                    }
                }
            }
        }
    }

    private void SelectGroup(string group)
    {
        _group = group;
        _gridScroll.Offset = 0f;
    }

    /// <summary>A thumbnail cell with its name on a dark bar along the bottom, like DXRP's package icons.</summary>
    private static void SpawnCell(Paper paper, FontFile font, GameManager game, Thumbnails thumbnails, Spawnable item, int index)
    {
        Texture2D thumb = thumbnails.IsValid() ? thumbnails.Get(item) : null;
        float cell = Cell;

        using (paper.Box("cell", index).Width(cell).Height(cell)
            .Rounded(Theme.Rounded).Clip()
            .BorderWidth(Px(2f)).BorderColor(Theme.Clear)
            .Hovered.BorderColor(Theme.Accent).BackgroundColor(Theme.White(0.04f)).End()
            .Scale(1f).Hovered.Scale(1.03f).End().Active.Scale(0.97f).End()
            .Transition(GuiProp.BorderColor, 0.08f)
            .Transition(GuiProp.ScaleX, 0.08f).Transition(GuiProp.ScaleY, 0.08f)
            .Cursor(PaperCursor.Pointer)
            .OnEnter(_ => GameAudio.Hover())
            .OnClick(_ => Spawner.SpawnAtCrosshair(game.LocalPlayer, item))
            .Enter())
        {
            paper.Draw((canvas, rect) =>
            {
                if (thumb.IsValid()) Ui.DrawTexture(canvas, rect, thumb);
                else Icons.Draw(canvas, CategoryIcon(item.Category), rect.Min.X + rect.Size.X * 0.5f, rect.Min.Y + rect.Size.Y * 0.42f, Px(28f), Theme.White(0.2f));
            });

            paper.Box("title").PositionType(PositionType.SelfDirected)
                .AnchorLeft(0).AnchorRight(0).AnchorBottom(0).Height(Px(26f))
                .Padding(Px(6f), Px(6f), 0, 0)
                .BackgroundColor(Theme.Black(0.6f))
                .Text(item.Name, font).FontSize(Px(13f)).TextColor(Theme.White(0.92f))
                .Alignment(TextAlignment.MiddleCenter).TextTruncate()
                .IsNotInteractable();
        }
    }

    private static void EmptyState(Paper paper, FontFile font, string message)
    {
        float icon = Px(44f);
        using (paper.Column("empty").Height(Px(240f)).Gap(S3).Enter())
        {
            paper.Box("top").Height(UnitValue.Stretch());
            using (paper.Row("icon row").Height(icon).Enter())
            {
                paper.Box("l").Width(UnitValue.Stretch());
                Icons.Box(paper, Icon.Search, icon, Theme.AccentSoft);
                paper.Box("r").Width(UnitValue.Stretch());
            }
            paper.Box("message").Height(Px(24f))
                .Text(message, font).FontSize(TextBase).TextColor(Theme.White(0.92f))
                .Alignment(TextAlignment.MiddleCenter);
            paper.Box("bottom").Height(UnitValue.Stretch());
        }
    }

    // ----------------------------------------------------------------
    //  Right side: tools and utilities
    // ----------------------------------------------------------------

    private void RightSide(Paper paper, FontFile font, GameManager game)
    {
        using (paper.Row("tabs").Height(Ui.TabHeight).Gap(S1).Enter())
        {
            Ui.MenuTab(paper, Icon.Construction, "Tools", _rightTab == RightTab.Tools, () => _rightTab = RightTab.Tools, 0);
            Ui.MenuTab(paper, Icon.Bolt, "Utilities", _rightTab == RightTab.Utilities, () => _rightTab = RightTab.Utilities, 1);
        }

        using (paper.Row("body").AsMenuBody().Padding(S2).Gap(S2).Enter())
        {
            if (_rightTab == RightTab.Tools) Tools(paper, game);
            else Utilities(paper, game);
        }
    }

    private void Tools(Paper paper, GameManager game)
    {
        Player player = game.LocalPlayer;
        ToolGun gun = player.Get<ToolGun>();
        BaseTool current = gun?.Tool;
        bool gunOut = player.ActiveEquipment == gun;
        string query = _toolSearch.Trim();

        using (paper.Column("list").Width(UnitValue.Percentage(36f)).Gap(S2).Enter())
        {
            Ui.SearchField(paper, "tool search", _toolSearch, v => _toolSearch = v, "Search tools...");

            using (Ui.Scroll(paper, "tool scroll", _toolScroll, S1))
            {
                int id = 0;
                for (int c = 0; c < game.Tools.Categories.Count; c++)
                {
                    string category = game.Tools.Categories[c];
                    List<BaseTool> tools = game.Tools.InCategory(category)
                        .Where(tool => query.Length == 0 || tool.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (tools.Count == 0) continue;

                    Ui.GroupTitle(paper, category, c);
                    using (paper.Column("group", c).Height(UnitValue.Auto).Gap(Px(1f)).Padding(0, 0, Px(2f), Px(4f)).Enter())
                    {
                        foreach (BaseTool tool in tools)
                        {
                            BaseTool pick = tool;
                            Ui.ToolItem(paper, tool.Name, gunOut && tool == current, () =>
                            {
                                gun.Select(pick);
                                player.Switch(gun);
                            }, id++);
                        }
                    }
                }
            }
        }

        paper.Box("divider").Width(1).BackgroundColor(Theme.White(0.06f)).IsNotInteractable();
        using (paper.Column("inspector").Width(UnitValue.Stretch()).Enter())
        using (Ui.Scroll(paper, "inspector scroll", _inspectorScroll, S1))
        {
            if (current != null) Inspector(paper, current);
        }
    }

    private void Inspector(Paper paper, BaseTool tool)
    {
        Ui.InspectorTitle(paper, tool.Name);
        using (paper.Column("description").Height(UnitValue.Auto).Padding(S2, S2, 0, Px(6f)).Enter())
            Ui.Description(paper, tool.Description);

        Ui.Caption(paper, "Controls", 0);
        using (paper.Column("controls").Height(UnitValue.Auto).Padding(S2, S2, 0, 0).Gap(S1).Enter())
        {
            if (tool.PrimaryHint.Length > 0) Ui.KeyHint(paper, "LMB", tool.PrimaryHint, 0);
            if (tool.SecondaryHint.Length > 0) Ui.KeyHint(paper, "RMB", tool.SecondaryHint, 1);
            if (tool.ReloadHint.Length > 0) Ui.KeyHint(paper, "R", tool.ReloadHint, 2);
        }

        List<(FieldInfo Field, ToolSettingAttribute Setting)> fields = SettingFields(tool.GetType());
        if (fields.Count > 0) Settings(paper, tool, fields);
        tool.Inspector(paper);
    }

    private void Settings(Paper paper, BaseTool tool, List<(FieldInfo Field, ToolSettingAttribute Setting)> fields)
    {
        Ui.Caption(paper, "Settings", 1);
        using (paper.Column("settings").Height(UnitValue.Auto).Padding(S2).Gap(S2).Enter())
        {
            for (int i = 0; i < fields.Count; i++)
            {
                (FieldInfo field, ToolSettingAttribute setting) = fields[i];
                object value = field.GetValue(tool);
                void Set(object v) => field.SetValue(tool, v);

                // Checked by type, since an unset material is null and matches no pattern below.
                if (field.FieldType == typeof(Material))
                {
                    MaterialPicker(paper, setting.Label, value as Material, v => Set(v), i);
                    continue;
                }

                switch (value)
                {
                    case KeyCode key:
                        bool listening = _binding is { } b0 && b0.Tool == tool && b0.Field == field;
                        Ui.KeyBind(paper, setting.Label, key, listening, () =>
                        {
                            _binding = listening ? null : (tool, field);
                            s_listening = _binding != null;
                        }, i);
                        break;
                    case bool b:
                        Ui.Checkbox(paper, setting.Label, b, v => Set(v), i);
                        break;
                    case float f:
                        Ui.Slider(paper, setting.Label, f, setting.Min, setting.Max, v => Set(v), setting.Format, i);
                        break;
                    case int n:
                        Ui.Slider(paper, setting.Label, n, setting.Min, setting.Max, v => Set((int)MathF.Round(v)), "0", i);
                        break;
                    case Color color:
                        Ui.ColorPicker(paper, setting.Label, color, v => Set(v), i);
                        break;
                    case Enum e:
                        Ui.EnumPicker(paper, setting.Label, e, v => Set(v), i);
                        break;
                }
            }

            paper.Box("gap").Height(S1).IsNotInteractable();
            Ui.Button(paper, "Reset to defaults", () => ResetSettings(tool), 0, icon: Icon.Refresh);
        }
    }

    private static void Utilities(Paper paper, GameManager game)
    {
        using (paper.Column("utilities").Gap(S1).Enter())
        {
            Ui.InspectorTitle(paper, "Utilities");
            using (paper.Column("description").Height(UnitValue.Auto).Padding(S2, S2, 0, Px(6f)).Enter())
                Ui.Description(paper, "Actions for your props and your player.");

            Ui.Caption(paper, "World", 0);
            using (paper.Column("world").Height(UnitValue.Auto).Padding(S2, S2, 0, 0).Gap(S2).Enter())
            {
                Ui.Button(paper, "Undo last action", () => game.Undo.UndoLast(), 0, icon: Icon.Undo);
                Ui.Button(paper, "Clean up all props", () => game.Cleanup(), 1, icon: Icon.Delete);
            }

            Ui.Caption(paper, "Player", 1);
            using (paper.Column("player").Height(UnitValue.Auto).Padding(S2, S2, 0, 0).Gap(S2).Enter())
            {
                Ui.Button(paper, "Respawn", () => game.LocalPlayer.Respawn(), 0, icon: Icon.Refresh);
                Ui.Button(paper, "Toggle noclip", () => game.LocalPlayer.Controller.ToggleNoclip(), 1, icon: Icon.Speed);
            }
        }
    }

    /// <summary>
    /// A searchable grid of every material in the library, shown by its albedo texture. The selected one
    /// is outlined in the accent; hovering one shows its name above the grid.
    /// </summary>
    private void MaterialPicker(Paper paper, string label, Material current, Action<Material> onChange, int id)
    {
        MaterialCatalog library = GameManager.Instance.MaterialLibrary;
        if (library == null) return;

        MaterialCatalog.Entry selected = library.Find(current);
        string query = _materialSearch.Trim();
        string shownName = _hoveredMaterial.Length > 0 ? _hoveredMaterial : selected?.Name ?? "None";
        float swatch = Px(60f), inset = Px(2f);

        using (paper.Column("material picker", id).Height(UnitValue.Auto).Gap(S2).Enter())
        {
            using (paper.Row("title").Height(Px(22f)).Gap(S2).Enter())
            {
                paper.Box("label").Width(UnitValue.Auto)
                    .Text(label, Theme.Font).FontSize(Px(15f)).TextColor(Theme.White(0.9f))
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                paper.Box("name").Width(UnitValue.Stretch())
                    .Text(shownName, Theme.Font).FontSize(TextSm).TextColor(Theme.Accent)
                    .Alignment(TextAlignment.MiddleRight).TextTruncate().IsNotInteractable();
            }

            Ui.SearchField(paper, "material search", _materialSearch, v => _materialSearch = v, $"Search {library.All.Count} materials...");

            _hoveredMaterial = "";
            using (paper.Row("swatches").Height(UnitValue.Auto).WrapContent().Gap(Px(4f)).LineGap(Px(4f)).Enter())
            {
                for (int i = 0; i < library.All.Count; i++)
                {
                    MaterialCatalog.Entry entry = library.All[i];
                    if (query.Length > 0 && !entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                    Material material = library.Get(entry);
                    Texture2D albedo = material.IsValid() ? material._properties.GetTexture("_MainTex") : null;
                    bool isSelected = entry == selected;

                    using (paper.Box("swatch", i).Width(swatch).Height(swatch)
                        .Rounded(Px(3f))
                        .BackgroundColor(Theme.White(0.05f))
                        .BorderWidth(inset).BorderColor(isSelected ? Theme.Accent : Theme.Clear)
                        .Hovered.BorderColor(isSelected ? Theme.Accent : Theme.White(0.5f)).End()
                        .Cursor(PaperCursor.Pointer)
                        .OnHover(_ => _hoveredMaterial = entry.Name)
                        .OnEnter(_ => GameAudio.Hover())
                        .OnClick(_ =>
                        {
                            Material picked = library.Get(entry);
                            if (picked.IsValid()) onChange(picked);
                        })
                        .Enter())
                    {
                        if (albedo.IsValid())
                        {
                            Texture2D tex = albedo;
                            paper.Draw((canvas, rect) => Ui.DrawTexture(canvas,
                                new Rect(rect.Min.X + inset, rect.Min.Y + inset, rect.Max.X - inset, rect.Max.Y - inset), tex));
                        }
                    }
                }
            }
        }
    }

    private List<(FieldInfo, ToolSettingAttribute)> SettingFields(Type type)
    {
        if (_settingFields.TryGetValue(type, out var cached)) return cached;

        var list = new List<(FieldInfo, ToolSettingAttribute)>();
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            ToolSettingAttribute setting = field.GetCustomAttribute<ToolSettingAttribute>();
            if (setting != null) list.Add((field, setting));
        }
        _settingFields[type] = list;
        return list;
    }

    private void ResetSettings(BaseTool tool)
    {
        object defaults = Activator.CreateInstance(tool.GetType());
        foreach ((FieldInfo field, ToolSettingAttribute _) in SettingFields(tool.GetType()))
            field.SetValue(tool, field.GetValue(defaults));
    }
}
