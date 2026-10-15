using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

[Tool("Light", "Places lights on any surface, welded to whatever they touch.", Category = "Render", Order = 2)]
public sealed class LightTool : BaseTool
{
    [ToolSetting("Colour")] public Color Color = new(1f, 0.85f, 0.6f, 1f);
    [ToolSetting("Brightness", Min = 0.5f, Max = 20f, Format = "0.0")] public float Brightness = 4f;
    [ToolSetting("Range", Min = 1f, Max = 30f, Format = "0.0 m")] public float Range = 8f;
    [ToolSetting("Weld to surface")] public bool Weld = true;

    public override string PrimaryHint => "Place light";
    public override string SecondaryHint => "Update light";
    public override string ReloadHint => "Copy settings";

    private static LightProp LightOf(in ToolTrace tr)
        => tr.HitBody && tr.Body.GameObject.TryGetComponent(out LightProp light) ? light : null;

    public override bool Primary(in ToolTrace tr)
    {
        if (!tr.Hit) return false;

        // Clicking an existing light updates it rather than stacking another on top.
        if (LightOf(tr).IsValid()) return Secondary(tr);

        GameObject go = LightProp.Create(Color, Brightness, Range);
        go.Transform.Position = tr.Point + tr.Normal * (LightProp.Radius + 0.01f);
        Scene.Current.Add(go);

        if (Weld)
        {
            // Welded to a body it rides along with it; on the world it is simply frozen in place.
            if (tr.HitBody) Constraints.Weld(go.GetComponent<Rigidbody3D>(), tr.Body, noCollide: true);
            else go.GetComponent<Prop>().SetFrozen(true);
        }

        Undo.Add("Light", go);
        return true;
    }

    public override bool Secondary(in ToolTrace tr)
    {
        LightProp light = LightOf(tr);
        if (light.IsNotValid()) return false;

        (Color color, float brightness, float range) old = (light.Color, light.Brightness, light.Range);
        light.Set(Color, Brightness, Range);
        Undo.Add(new ActionUndo("Light settings", () => light.Set(old.color, old.brightness, old.range), () => light.IsValid()));
        return true;
    }

    public override bool Reload(in ToolTrace tr)
    {
        LightProp light = LightOf(tr);
        if (light.IsNotValid()) return false;

        Color = light.Color;
        Brightness = light.Brightness;
        Range = light.Range;
        Notify.Info("Copied light settings");
        return true;
    }
}
