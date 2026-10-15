using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

[Tool("Thruster", "Places thrusters that push whatever they are welded to while a key is held.", Category = "Construction", Order = 4)]
public sealed class ThrusterTool : BaseTool
{
    [ToolSetting("Force", Min = 10f, Max = 10000f, Format = "0 N")] public float Force = 1000f;
    [ToolSetting("Forward key")] public KeyCode Forward = KeyCode.Keypad8;
    [ToolSetting("Reverse key")] public KeyCode Back = KeyCode.Keypad2;
    [ToolSetting("Toggle")] public bool Toggle;
    [ToolSetting("Weld to surface")] public bool Weld = true;

    public override string PrimaryHint => "Place thruster";
    public override string SecondaryHint => "Update thruster";
    public override string ReloadHint => "Copy settings";

    private static ThrusterProp ThrusterOf(in ToolTrace tr)
        => tr.HitBody && tr.Body.GameObject.TryGetComponent(out ThrusterProp thruster) ? thruster : null;

    public override bool Primary(in ToolTrace tr)
    {
        if (!tr.Hit) return false;

        // Clicking an existing thruster updates it rather than stacking another on top.
        if (ThrusterOf(tr).IsValid()) return Secondary(tr);

        // The exhaust faces out of the surface, so firing pushes the prop the other way.
        GameObject go = ThrusterProp.Create(Force, Forward, Back, Toggle);
        go.Transform.Position = tr.Point + tr.Normal * (ThrusterProp.Height * 0.5f + 0.01f);
        go.Transform.Rotation = Quaternion.FromToRotation(Float3.UnitY, tr.Normal);
        Scene.Current.Add(go);

        if (Weld)
        {
            if (tr.HitBody) Constraints.Weld(go.GetComponent<Rigidbody3D>(), tr.Body, noCollide: true);
            else go.GetComponent<Prop>().SetFrozen(true);
        }

        Undo.Add("Thruster", go);
        return true;
    }

    public override bool Secondary(in ToolTrace tr)
    {
        ThrusterProp thruster = ThrusterOf(tr);
        if (thruster.IsNotValid()) return false;

        (float force, KeyCode forward, KeyCode back, bool toggle) old = (thruster.Force, thruster.Forward, thruster.Back, thruster.Toggle);
        thruster.Set(Force, Forward, Back, Toggle);
        Undo.Add(new ActionUndo("Thruster settings", () => thruster.Set(old.force, old.forward, old.back, old.toggle), () => thruster.IsValid()));
        return true;
    }

    public override bool Reload(in ToolTrace tr)
    {
        ThrusterProp thruster = ThrusterOf(tr);
        if (thruster.IsNotValid()) return false;

        Force = thruster.Force;
        Forward = thruster.Forward;
        Back = thruster.Back;
        Toggle = thruster.Toggle;
        Notify.Info("Copied thruster settings");
        return true;
    }
}
