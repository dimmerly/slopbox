using System;
using Prowl.Runtime;

namespace Slopbox;

[Tool("Scale", "Resizes props. Mass follows the volume.", Category = "Construction", Order = 3)]
public sealed class ScaleTool : BaseTool
{
    [ToolSetting("Scale", Min = 0.1f, Max = 5f, Format = "0.00x")] public float Scale = 2f;

    public override string PrimaryHint => "Apply scale";
    public override string SecondaryHint => "Copy scale";
    public override string ReloadHint => "Reset scale";

    public override bool Primary(in ToolTrace tr) => Resize(tr, Scale);

    public override bool Secondary(in ToolTrace tr)
    {
        if (!tr.HitBody) return false;
        Scale = tr.Prop.Scale;
        return true;
    }

    public override bool Reload(in ToolTrace tr) => Resize(tr, 1f);

    private static bool Resize(in ToolTrace tr, float scale)
    {
        if (!tr.HitBody) return false;
        Prop prop = tr.Prop;
        Rigidbody3D body = tr.Body;
        float oldScale = prop.Scale, oldMass = body.Mass;
        if (MathF.Abs(oldScale - scale) < 0.001f) return false;

        float ratio = scale / oldScale;
        prop.SetScale(scale);
        body.Mass = Math.Clamp(oldMass * ratio * ratio * ratio, 0.5f, 50000f);

        Undo.Add(new ActionUndo("Scale", () =>
        {
            prop.SetScale(oldScale);
            body.Mass = oldMass;
        }, () => prop.IsValid() && body.IsValid()));
        return true;
    }
}
