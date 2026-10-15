using Prowl.Runtime;
using Prowl.Vector;

namespace Slopbox;

[Tool("Joint", "Creates a rotating hinge at the first click. Click a second prop or the world to attach it. Use WorldY for a door hinge, or SurfaceNormal for a wheel axle.", Category = "Constraints", Order = 3)]
public sealed class JointTool : TwoPointTool
{
    public enum JointAxis { SurfaceNormal, WorldX, WorldY, WorldZ }

    [ToolSetting("Axis")] public JointAxis Axis = JointAxis.SurfaceNormal;
    [ToolSetting("No collide")] public bool NoCollide = true;
    [ToolSetting("Limit rotation")] public bool LimitRotation;
    [ToolSetting("Minimum angle", Min = -180f, Max = 180f, Format = "0°")] public float MinAngle = -90f;
    [ToolSetting("Maximum angle", Min = -180f, Max = 180f, Format = "0°")] public float MaxAngle = 90f;
    [ToolSetting("Motor")] public bool Motor;
    [ToolSetting("Motor speed", Min = -720f, Max = 720f, Format = "0°/s")] public float MotorSpeed = 90f;
    [ToolSetting("Motor torque", Min = 0f, Max = 5000f, Format = "0 Nm")] public float MotorTorque = 100f;

    private Float3 _localNormal;

    public override string ReloadHint => "Remove joints";

    public override bool Primary(in ToolTrace tr)
    {
        if (!HasFirst && tr.HitBody)
        {
            Float3 normal = Float3.Length(tr.Normal) > 0.001f ? Float3.Normalize(tr.Normal) : Float3.UnitY;
            _localNormal = tr.Body.Transform.InverseTransformDirection(normal);
        }
        return base.Primary(tr);
    }

    private Float3 WorldAxis => Axis switch
    {
        JointAxis.WorldX => Float3.UnitX,
        JointAxis.WorldY => Float3.UnitY,
        JointAxis.WorldZ => Float3.UnitZ,
        _ => FirstBody.Transform.TransformDirection(_localNormal),
    };

    public override void Think()
    {
        base.Think();
        if (HasFirst)
            Debug.DrawLine(FirstWorldPoint - WorldAxis * 0.3f, FirstWorldPoint + WorldAxis * 0.3f, Theme.Accent);
    }

    protected override bool Connect(Rigidbody3D a, Float3 pointA, Rigidbody3D b, Float3 pointB)
    {
        float min = LimitRotation ? System.MathF.Min(MinAngle, MaxAngle) : -180f;
        float max = LimitRotation ? System.MathF.Max(MinAngle, MaxAngle) : 180f;
        HingeJoint joint = Constraints.Hinge(a, pointA, b, WorldAxis, NoCollide,
            min, max, Motor, MotorSpeed * Maths.Deg2Rad, MotorTorque);
        Undo.Add("Joint", joint);
        return true;
    }

    public override bool Reload(in ToolTrace tr)
    {
        OnDeselect();
        if (!tr.HitBody) return false;
        int count = Constraints.RemoveAll<HingeJoint>(tr.Body);
        if (count > 0) Notify.Info($"Removed {count} joints");
        return count > 0;
    }
}
