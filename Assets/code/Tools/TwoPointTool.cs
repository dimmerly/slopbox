using Prowl.Runtime;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// Base for tools that join two things with two clicks (weld, rope, ball socket). The first click picks
/// a body and a point on it; the second calls <see cref="Connect"/>. Right click cancels.
/// </summary>
public abstract class TwoPointTool : BaseTool
{
    protected Rigidbody3D FirstBody;
    protected Float3 FirstLocalPoint;
    protected bool HasFirst => FirstBody.IsValid();

    /// <summary>Whether the second point may be the static world.</summary>
    protected virtual bool AllowWorld => true;

    public override string PrimaryHint => HasFirst ? "Select the second object" : "Select the first object";
    public override string SecondaryHint => HasFirst ? "Cancel" : "";
    public override string Status => HasFirst ? $"Attaching {FirstBody.GameObject.Name}..." : "";

    protected Float3 FirstWorldPoint => FirstBody.Transform.TransformPoint(FirstLocalPoint);

    public override bool Primary(in ToolTrace tr)
    {
        if (!tr.Hit) return false;

        if (!HasFirst)
        {
            if (!tr.HitBody) return false;
            FirstBody = tr.Body;
            FirstLocalPoint = FirstBody.Transform.InverseTransformPoint(tr.Point);
            return true;
        }

        if (tr.Body == FirstBody) return false;
        if (!tr.HitBody && !AllowWorld) return false;

        bool done = Connect(FirstBody, FirstWorldPoint, tr.HitBody ? tr.Body : null, tr.Point);
        FirstBody = null;
        return done;
    }

    public override bool Secondary(in ToolTrace tr)
    {
        if (!HasFirst) return false;
        FirstBody = null;
        return false;
    }

    public override void OnDeselect() => FirstBody = null;

    public override void Think()
    {
        if (HasFirst) Debug.DrawWireSphere(FirstWorldPoint, 0.05f, Theme.Accent);
    }

    /// <summary>Joins <paramref name="a"/> at <paramref name="pointA"/> to <paramref name="b"/> (null for the world) at <paramref name="pointB"/>.</summary>
    protected abstract bool Connect(Rigidbody3D a, Float3 pointA, Rigidbody3D b, Float3 pointB);
}
