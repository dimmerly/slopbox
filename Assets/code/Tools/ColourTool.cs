using Prowl.Runtime;
using Prowl.Vector;

namespace Slopbox;

[Tool("Colour", "Tints props.", Category = "Render", Order = 0)]
public sealed class ColourTool : BaseTool
{
    [ToolSetting("Colour")] public Color Color = new(0.94f, 0.33f, 0f, 1f);

    public override string PrimaryHint => "Apply colour";
    public override string SecondaryHint => "Copy colour";
    public override string ReloadHint => "Reset colour";

    public override bool Primary(in ToolTrace tr) => Paint(tr, Color);

    public override bool Secondary(in ToolTrace tr)
    {
        if (!tr.HitBody) return false;
        Color = tr.Prop.Tint;
        return true;
    }

    public override bool Reload(in ToolTrace tr) => Paint(tr, Color.White);

    private bool Paint(in ToolTrace tr, Color color)
    {
        if (!tr.HitBody) return false;
        Prop prop = tr.Prop;
        Color old = prop.Tint;
        if (old == color) return false;

        prop.SetTint(color);
        Undo.Add(new ActionUndo("Colour", () => prop.SetTint(old), () => prop.IsValid()));
        return true;
    }
}
