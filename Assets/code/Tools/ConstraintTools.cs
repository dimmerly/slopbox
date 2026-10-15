using Prowl.Runtime;
using Prowl.Vector;

namespace Slopbox;

[Tool("Weld", "Rigidly joins two objects, or an object to the world.", Category = "Constraints", Order = 0)]
public sealed class WeldTool : TwoPointTool
{
    [ToolSetting("No collide")] public bool NoCollide = true;

    public override string ReloadHint => "Remove welds";

    protected override bool Connect(Rigidbody3D a, Float3 pointA, Rigidbody3D b, Float3 pointB)
    {
        PhysicsConstraint[] weld = Constraints.Weld(a, b, NoCollide);
        Undo.Add("Weld", weld);
        return true;
    }

    public override bool Reload(in ToolTrace tr)
    {
        if (!tr.HitBody) return false;
        int removed = Constraints.RemoveAll<FixedAngleConstraint>(tr.Body) + Constraints.RemoveAll<BallSocketConstraint>(tr.Body);
        if (removed > 0) Notify.Info("Removed welds");
        return removed > 0;
    }
}

[Tool("Ball Socket", "Joins two objects at a point they can swivel around.", Category = "Constraints", Order = 1)]
public sealed class BallSocketTool : TwoPointTool
{
    [ToolSetting("No collide")] public bool NoCollide = true;

    public override string ReloadHint => "Remove ball sockets";

    protected override bool Connect(Rigidbody3D a, Float3 pointA, Rigidbody3D b, Float3 pointB)
    {
        // The pivot is the first point, where the player clicked on the first object.
        Undo.Add("Ball Socket", Constraints.BallSocket(a, pointA, b, NoCollide));
        return true;
    }

    public override bool Reload(in ToolTrace tr)
    {
        if (!tr.HitBody) return false;
        return Constraints.RemoveAll<BallSocketConstraint>(tr.Body) > 0;
    }
}

[Tool("Rope", "Ties two points together with a rope.", Category = "Constraints", Order = 2)]
public sealed class RopeTool : TwoPointTool
{
    [ToolSetting("Extra length", Min = 0f, Max = 5f, Format = "0.0 m")] public float Slack = 0.5f;
    [ToolSetting("Width", Min = 0.005f, Max = 0.1f, Format = "0.000 m")] public float Width = 0.03f;
    [ToolSetting("Colour")] public Color Color = new(0.15f, 0.1f, 0.06f, 1f);

    public override string ReloadHint => "Remove ropes";

    protected override bool Connect(Rigidbody3D a, Float3 pointA, Rigidbody3D b, Float3 pointB)
    {
        float length = Float3.Distance(pointA, pointB) + Slack;
        DistanceLimitConstraint rope = Constraints.Rope(a, pointA, b, pointB, length);

        var visual = new GameObject("Rope");
        RopeVisual draw = visual.AddComponent<RopeVisual>();
        draw.Constraint = rope;
        draw.Color = Color;
        draw.Width = Width;
        Prowl.Runtime.Resources.Scene.Current.Add(visual);

        Undo.Add("Rope", rope, visual);
        return true;
    }

    public override bool Reload(in ToolTrace tr)
    {
        if (!tr.HitBody) return false;
        return Constraints.RemoveAll<DistanceLimitConstraint>(tr.Body) > 0;
    }
}
