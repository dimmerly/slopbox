using System.Collections.Generic;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Scribe;
using static Slopbox.Theme;
using TextAlignment = Prowl.PaperUI.TextAlignment;

namespace Slopbox;

/// <summary>The duplicator's saved dupes: save what is held to a .dupe file, or load one to paste.</summary>
public sealed partial class DuplicatorTool
{
    private string _saveName = "";
    private string _confirmDelete = "";

    public override void Inspector(Paper paper)
    {
        FontFile font = Theme.Font;
        IReadOnlyList<Dupes.Entry> dupes = Dupes.List();

        Ui.Caption(paper, "Saved dupes", 2);
        using (paper.Column("dupes").Height(UnitValue.Auto).Padding(S2).Gap(S2).Enter())
        {
            if (_copied != null)
            {
                using (paper.Row("save").Height(Px(40f)).Gap(S2).Enter())
                {
                    using (paper.Box("name holder").Width(UnitValue.Stretch()).Padding(0, 0, Px(2f), 0).Enter())
                        Ui.SearchField(paper, "dupe name", _saveName, v => _saveName = v, $"Name {_name}...");
                    string label = _saveName.Trim().Length > 0 && Dupes.Exists(_saveName) ? "Overwrite" : "Save";
                    using (paper.Box("save holder").Width(Px(120f)).Enter())
                        Ui.Button(paper, label, () =>
                        {
                            if (Dupes.Save(_saveName, _copied)) _saveName = "";
                        }, 0, icon: Icon.Check);
                }
            }
            else
            {
                Ui.Description(paper, "Copy something with right click to save it here.", 0, Theme.White(0.5f));
            }

            if (dupes.Count == 0)
                Ui.Description(paper, $"No saved dupes yet. They are kept in {Dupes.Folder}.", 1, Theme.White(0.4f));

            for (int i = 0; i < dupes.Count; i++)
                Row(paper, font, dupes[i], i);
        }
    }

    private void Row(Paper paper, FontFile font, Dupes.Entry dupe, int id)
    {
        bool confirming = _confirmDelete == dupe.Name;
        string detail = dupe.Props < 0 ? "Unreadable" : $"{dupe.Props} props  ·  {dupe.Modified:d MMM, h:mm tt}";

        using (paper.Row("dupe row", id).Height(Px(52f)).Padding(S3, S2, 0, 0).Gap(S2)
            .Rounded(Theme.Rounded).BackgroundColor(Theme.Secondary)
            .Enter())
        {
            using (paper.Column("text").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                .Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch()).IsNotInteractable().Enter())
            {
                paper.Box("name").Height(Px(20f))
                    .Text(dupe.Name, font).FontSize(Px(15f)).TextColor(Theme.White(0.95f))
                    .Alignment(TextAlignment.MiddleLeft).TextTruncate();
                paper.Box("detail").Height(Px(16f))
                    .Text(detail, font).FontSize(TextXs).TextColor(Theme.Gray400)
                    .Alignment(TextAlignment.MiddleLeft).TextTruncate();
            }

            using (paper.Row("buttons").Width(UnitValue.Auto).Height(Px(40f)).Gap(S2)
                .Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch()).Enter())
            {
                if (confirming)
                {
                    Ui.Button(paper, "Delete", () =>
                    {
                        Dupes.Delete(dupe.Name);
                        _confirmDelete = "";
                    }, 0, fit: true, icon: Icon.Delete);
                    Ui.Button(paper, "Keep", () => _confirmDelete = "", 1, fit: true);
                }
                else
                {
                    Ui.Button(paper, "Load", () => Hold(dupe.Name), 2, fit: true, icon: Icon.Play);
                    Ui.Button(paper, "", () => _confirmDelete = dupe.Name, 3, fit: true, icon: Icon.Delete);
                }
            }
        }
    }

    /// <summary>Reads a saved dupe into the tool, ready to paste.</summary>
    public bool Hold(string name)
    {
        Snapshot dupe = Dupes.Load(name);
        if (dupe == null) return false;
        _copied = dupe;
        _name = name;
        Notify.Info($"Holding {name} ({dupe.Count} props)");
        return true;
    }
}
