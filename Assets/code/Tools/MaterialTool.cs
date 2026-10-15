using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Slopbox;

[Tool("Material", "Paints props with any material from the library.", Category = "Render", Order = 1)]
public sealed class MaterialTool : BaseTool
{
    [ToolSetting("Material")] public Material Material;

    public override string PrimaryHint => "Apply material";
    public override string SecondaryHint => "Copy material";
    public override string ReloadHint => "Reset material";
    public override string Status => Material.IsValid() ? "" : "Pick a material in the spawn menu";

    /// <summary>A prop the tool may paint. Lights and thrusters draw their own glow, so they are left alone.</summary>
    private static Prop Target(in ToolTrace tr)
    {
        if (!tr.HitBody || tr.Body.GameObject.TryGetComponent(out LightProp _)
            || tr.Body.GameObject.TryGetComponent(out ThrusterProp _)) return null;
        return tr.Prop;
    }

    public override bool Primary(in ToolTrace tr) => Paint(Target(tr), Material);

    public override bool Secondary(in ToolTrace tr)
    {
        Prop prop = Target(tr);
        if (prop.IsNotValid() || prop.MaterialOverride.IsNotValid()) return false;
        Material = prop.MaterialOverride;
        return true;
    }

    public override bool Reload(in ToolTrace tr) => Paint(Target(tr), null);

    private static bool Paint(Prop prop, Material material)
    {
        if (prop.IsNotValid()) return false;
        if (material.IsNotValid() && prop.MaterialOverride.IsNotValid()) return false;

        Material old = prop.MaterialOverride;
        prop.SetMaterial(material);
        Undo.Add(new ActionUndo("Material", () => prop.SetMaterial(old), () => prop.IsValid()));
        return true;
    }
}
