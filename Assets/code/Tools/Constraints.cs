using System.Collections.Generic;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>Creating and removing the joints the constraint tools make.</summary>
public static class Constraints
{
    /// <summary>
    /// Rigidly joins <paramref name="a"/> to <paramref name="b"/>, or to the world when b is null. A weld is
    /// a ball socket at a's origin, holding position, plus a fixed angle, holding rotation.
    /// </summary>
    public static PhysicsConstraint[] Weld(Rigidbody3D a, Rigidbody3D b, bool noCollide)
    {
        var socket = a.GameObject.AddComponent<BallSocketConstraint>();
        socket.Anchor = Float3.Zero;
        socket.ConnectedBody = b;
        socket.CollideConnected = !noCollide;

        var angle = a.GameObject.AddComponent<FixedAngleConstraint>();
        angle.ConnectedBody = b;
        angle.CollideConnected = !noCollide;

        Rebuild(socket);
        Rebuild(angle);
        return [socket, angle];
    }

    public static BallSocketConstraint BallSocket(Rigidbody3D a, Float3 worldPoint, Rigidbody3D b, bool noCollide)
    {
        var socket = a.GameObject.AddComponent<BallSocketConstraint>();
        socket.Anchor = a.Transform.InverseTransformPoint(worldPoint);
        socket.ConnectedBody = b;
        socket.CollideConnected = !noCollide;
        Rebuild(socket);
        return socket;
    }

    /// <summary>A hinge at the first clicked point, rotating about a world-space axis.</summary>
    public static HingeJoint Hinge(Rigidbody3D a, Float3 worldPoint, Rigidbody3D b, Float3 worldAxis,
        bool noCollide, float minAngle = -180f, float maxAngle = 180f,
        bool motor = false, float motorSpeed = 0f, float motorTorque = 100f)
    {
        var joint = a.GameObject.AddComponent<HingeJoint>();
        joint.Enabled = false;
        joint.Anchor = a.Transform.InverseTransformPoint(worldPoint);
        joint.Axis = a.Transform.InverseTransformDirection(worldAxis);
        joint.ConnectedBody = b;
        joint.CollideConnected = !noCollide;
        joint.MinAngle = minAngle;
        joint.MaxAngle = maxAngle;
        joint.HasMotor = motor;
        joint.MotorTargetVelocity = motorSpeed;
        joint.MotorMaxForce = motorTorque;
        joint.Enabled = true;
        return joint;
    }

    /// <summary>A rope that stops the two points getting further apart than <paramref name="length"/>, but lets them come closer.</summary>
    public static DistanceLimitConstraint Rope(Rigidbody3D a, Float3 worldA, Rigidbody3D b, Float3 worldB, float length)
    {
        var rope = a.GameObject.AddComponent<DistanceLimitConstraint>();
        rope.Anchor = a.Transform.InverseTransformPoint(worldA);
        // The connected anchor is local to b, or a world position when there is no b.
        rope.ConnectedAnchor = b.IsValid() ? b.Transform.InverseTransformPoint(worldB) : worldB;
        rope.ConnectedBody = b;
        rope.TargetDistance = length;
        rope.MinDistance = 0f;
        rope.MaxDistance = length;
        rope.CollideConnected = true;
        Rebuild(rope);
        return rope;
    }

    /// <summary>
    /// Constraints are built the moment they are added, before their fields are set, and some setters
    /// only move one end of a live joint. Toggling rebuilds it from the final values.
    /// </summary>
    private static void Rebuild(PhysicsConstraint c)
    {
        c.Enabled = false;
        c.Enabled = true;
    }

    /// <summary>Every constraint touching <paramref name="body"/>, from either end.</summary>
    public static List<PhysicsConstraint> On(Rigidbody3D body)
    {
        var found = new List<PhysicsConstraint>();
        foreach (PhysicsConstraint c in Scene.Current.FindObjectsOfType<PhysicsConstraint>())
        {
            if (c.IsNotValid()) continue;
            if (c.GameObject == body.GameObject || c.ConnectedBody == body) found.Add(c);
        }
        return found;
    }

    /// <summary>Removes constraints of type <typeparamref name="T"/> touching <paramref name="body"/>. Returns how many.</summary>
    public static int RemoveAll<T>(Rigidbody3D body) where T : PhysicsConstraint
    {
        int count = 0;
        foreach (PhysicsConstraint c in On(body))
        {
            if (c is not T) continue;
            c.GameObject.RemoveComponent(c);
            count++;
        }
        return count;
    }
}
