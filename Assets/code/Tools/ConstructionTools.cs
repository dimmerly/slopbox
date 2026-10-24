using System;
using System.Collections.Generic;
using Prowl.Runtime;
using Prowl.Vector;

namespace Slopbox;

[Tool("Remover", "Removes props.", Category = "Construction", Order = 0)]
public sealed class RemoverTool : BaseTool
{
    public override string PrimaryHint => "Remove";
    public override string SecondaryHint => "Remove constraints";

    public override bool Primary(in ToolTrace tr)
    {
        // Only things with a body: the remover never deletes the map.
        if (!tr.HitBody) return false;
        tr.Body.GameObject.Destroy();
        return true;
    }

    public override bool Secondary(in ToolTrace tr)
    {
        if (!tr.HitBody) return false;
        int removed = Constraints.RemoveAll<PhysicsConstraint>(tr.Body);
        if (removed > 0) Notify.Info($"Removed {removed} constraints");
        return removed > 0;
    }
}

[Tool("Physical Properties", "Changes gravity and mass.", Category = "Construction", Order = 1)]
public sealed class PhysPropTool : BaseTool
{
    [ToolSetting("Gravity")] public bool Gravity = true;
    [ToolSetting("Mass", Min = 1f, Max = 1000f, Format = "0 kg")] public float Mass = 50f;
    [ToolSetting("Bounciness", Min = 0f, Max = 1f)] public float Restitution = 0f;
    [ToolSetting("Friction", Min = 0f, Max = 2f)] public float Friction = 0.5f;

    public override string PrimaryHint => "Apply";
    public override string SecondaryHint => "Copy from object";

    public override bool Primary(in ToolTrace tr)
    {
        if (!tr.HitBody) return false;
        Rigidbody3D body = tr.Body;

        (bool gravity, float mass, float restitution, float friction) old = (body.AffectedByGravity, body.Mass, body.Restitution, body.Friction);
        Apply(body, (Gravity, Mass, Restitution, Friction));
        Undo.Add(new ActionUndo("Physical Properties", () => Apply(body, old), () => body.IsValid()));
        return true;
    }

    public override bool Secondary(in ToolTrace tr)
    {
        if (!tr.HitBody) return false;
        Gravity = tr.Body.AffectedByGravity;
        Mass = tr.Body.Mass;
        Restitution = tr.Body.Restitution;
        Friction = tr.Body.Friction;
        return true;
    }

    private static void Apply(Rigidbody3D body, (bool Gravity, float Mass, float Restitution, float Friction) p)
    {
        body.AffectedByGravity = p.Gravity;
        body.Mass = p.Mass;
        body.Restitution = p.Restitution;
        body.Friction = p.Friction;
        body.SetActive(true);
    }
}

[Tool("Duplicator", "Copies a prop, or a whole contraption with its welds and ropes, and pastes it anywhere.", Category = "Construction", Order = 2)]
public sealed partial class DuplicatorTool : BaseTool
{
    [ToolSetting("Copy whole contraption")] public bool Contraption = true;
    [ToolSetting("Paste frozen")] public bool PasteFrozen;

    private Snapshot _copied;
    internal string NetworkCopy => _copied?.ToJson() ?? "";
    internal void LoadNetworkCopy(string json)
    {
        _copied = Snapshot.FromJson(json);
        _name = $"{_copied.Count} props";
    }
    private string _name = "";

    public override string PrimaryHint => _copied != null ? $"Paste {_name}" : "";
    public override string SecondaryHint => "Copy";
    public override string Status => _copied != null ? $"Holding {_name}" : "Nothing copied";

    public override bool Secondary(in ToolTrace tr)
    {
        if (!tr.HitBody) return false;
        Prop prop = tr.Prop;
        if (!Snapshot.CanCapture(prop))
        {
            Notify.Error("That object was not spawned, it cannot be copied");
            return false;
        }

        List<Prop> props = Contraption ? Snapshot.Contraption(prop) : [prop];

        // Copied about the point under the middle of it, facing the way the player faces, so a paste
        // sits on the surface aimed at and turns with the player.
        Float3 centre = Float3.Zero;
        foreach (Prop p in props) centre += p.Transform.Position;
        centre /= props.Count;
        var origin = new Float3(centre.X, Snapshot.Bottom(props), centre.Z);

        _copied = Snapshot.Capture(props, origin, Quaternion.FromEuler(0f, Owner.Yaw, 0f));
        _name = _copied.Count == 1 ? NameOf(prop) : $"{_copied.Count} props";
        Notify.Info($"Copied {_name}");
        return true;
    }

    public override bool Primary(in ToolTrace tr)
    {
        if (_copied == null || !tr.Hit) return false;

        List<GameObject> made = _copied.Spawn(tr.Point + tr.Normal * 0.01f, Quaternion.FromEuler(0f, Owner.Yaw, 0f));
        if (made.Count == 0) return false;
        if (PasteFrozen)
            foreach (GameObject go in made)
                if (go.TryGetComponent(out Prop p)) p.SetFrozen(true);

        Undo.Add(_name, made.ToArray());
        return true;
    }

    private static string NameOf(Prop prop)
    {
        if (prop.GameObject.TryGetComponent(out LightProp _)) return "Light";
        if (prop.GameObject.TryGetComponent(out ThrusterProp _)) return "Thruster";
        return GameManager.Instance.Catalog.Find(prop.SpawnId)?.Name ?? prop.GameObject.Name;
    }
}
