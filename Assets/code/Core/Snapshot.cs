using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// A set of props and the constraints between them, stored relative to an origin so it can be rebuilt
/// anywhere. The duplicator captures a contraption against a point under it and pastes it at the aim.
/// </summary>
public sealed class Snapshot
{
    public sealed class PropData
    {
        /// <summary><see cref="Spawnable.Id"/>, or empty for a light or thruster.</summary>
        public string Spawn { get; set; } = "";
        public float[] Position { get; set; } = [0, 0, 0];
        public float[] Rotation { get; set; } = [0, 0, 0, 1];
        public float Scale { get; set; } = 1f;
        public float[] Tint { get; set; }
        public string Material { get; set; }
        public bool Frozen { get; set; }
        public bool Gravity { get; set; } = true;
        public float Mass { get; set; }
        public LightData Light { get; set; }
        public ThrusterData Thruster { get; set; }
    }

    public sealed class LightData
    {
        public float[] Color { get; set; }
        public float Brightness { get; set; }
        public float Range { get; set; }
    }

    public sealed class ThrusterData
    {
        public float Force { get; set; }
        public KeyCode Forward { get; set; }
        public KeyCode Back { get; set; }
        public bool Toggle { get; set; }
    }

    public sealed class ConstraintData
    {
        /// <summary>"weld", "ballsocket", "rope" or "hinge".</summary>
        public string Type { get; set; } = "";
        /// <summary>Index into <see cref="Props"/>. B is -1 for the world.</summary>
        public int A { get; set; }
        public int B { get; set; } = -1;
        /// <summary>Local to A. For a rope, AnchorB is local to B, or relative to the origin for the world.</summary>
        public float[] AnchorA { get; set; }
        public float[] AnchorB { get; set; }
        public bool NoCollide { get; set; }
        public float Length { get; set; }
        public float[] Color { get; set; }
        public float Width { get; set; }
        /// <summary>Hinge axis in A's local space.</summary>
        public float[] Axis { get; set; }
        public float MinAngle { get; set; } = -180f;
        public float MaxAngle { get; set; } = 180f;
        public bool Motor { get; set; }
        public float MotorSpeed { get; set; }
        public float MotorTorque { get; set; } = 100f;
    }

    public List<PropData> Props { get; set; } = new();
    public List<ConstraintData> Constraints { get; set; } = new();

    [JsonIgnore] public int Count => Props.Count;

    private static readonly JsonSerializerOptions s_json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, s_json);
    public static Snapshot FromJson(string json) => JsonSerializer.Deserialize<Snapshot>(json, s_json);

    /// <summary>Whether <paramref name="prop"/> can be rebuilt: spawned from the catalog, a light or a thruster.</summary>
    public static bool CanCapture(Prop prop)
        => prop.IsValid() && !prop.GameObject.IsDestroyQueued && prop.Body.IsValid()
           && (prop.GameObject.TryGetComponent(out LightProp _) || prop.GameObject.TryGetComponent(out ThrusterProp _)
               || GameManager.Instance.Catalog.Find(prop.SpawnId) != null);

    /// <summary><paramref name="prop"/> and everything joined to it through constraints, directly or not.</summary>
    public static List<Prop> Contraption(Prop prop)
    {
        var found = new List<Prop>();
        var seen = new HashSet<Rigidbody3D>();
        var queue = new Queue<Rigidbody3D>();
        queue.Enqueue(prop.Body);
        seen.Add(prop.Body);

        while (queue.Count > 0)
        {
            Rigidbody3D body = queue.Dequeue();
            if (body.GameObject.TryGetComponent(out Prop p) && CanCapture(p)) found.Add(p);
            foreach (PhysicsConstraint c in Slopbox.Constraints.On(body))
            {
                Rigidbody3D other = c.GameObject == body.GameObject ? c.ConnectedBody : c.GameObject.GetComponent<Rigidbody3D>();
                if (other.IsValid() && seen.Add(other)) queue.Enqueue(other);
            }
        }
        return found;
    }

    /// <summary>The lowest world height of any of the props' meshes.</summary>
    public static float Bottom(IEnumerable<Prop> props)
    {
        float bottom = float.MaxValue;
        foreach (Prop prop in props)
            foreach (Float3 corner in Spawner.LocalBounds(prop.GameObject).GetCorners())
                bottom = MathF.Min(bottom, prop.Transform.TransformPoint(corner).Y);
        return bottom == float.MaxValue ? 0f : bottom;
    }

    // ----------------------------------------------------------------
    //  Capture
    // ----------------------------------------------------------------

    public static Snapshot Capture(IEnumerable<Prop> props, Float3 origin, Quaternion rotation)
    {
        var snap = new Snapshot();
        Quaternion inverse = Quaternion.Inverse(rotation);
        var index = new Dictionary<Rigidbody3D, int>();

        foreach (Prop prop in props.Where(CanCapture).Distinct())
        {
            Rigidbody3D body = prop.Body;
            PropData data = CaptureProp(prop, origin, inverse);
            index[body] = snap.Props.Count;
            snap.Props.Add(data);
        }

        CaptureConstraints(snap, index, origin, inverse);
        return snap;
    }

    internal static PropData CaptureProp(Prop prop, Float3 origin, Quaternion inverse)
    {
        Rigidbody3D body = prop.Body;
        if (body.IsNotValid()) return new PropData { Spawn = prop.SpawnId };
        var data = new PropData
        {
            Spawn = prop.SpawnId,
            Position = Vec(inverse * (body.Transform.Position - origin)),
            Rotation = Quat(inverse * body.Transform.Rotation),
            Scale = prop.Scale,
            Tint = prop.Tint != Color.White ? Col(prop.Tint) : null,
            Material = prop.MaterialOverride.IsValid() ? prop.MaterialOverride.AssetID.ToString() : null,
            Frozen = prop.IsFrozen,
            Gravity = body.AffectedByGravity,
            Mass = body.Mass,
        };
        if (prop.GameObject.TryGetComponent(out LightProp light))
        {
            data.Spawn = "";
            data.Light = new LightData { Color = Col(light.Color), Brightness = light.Brightness, Range = light.Range };
        }
        if (prop.GameObject.TryGetComponent(out ThrusterProp thruster))
        {
            data.Spawn = "";
            data.Thruster = new ThrusterData { Force = thruster.Force, Forward = thruster.Forward, Back = thruster.Back, Toggle = thruster.Toggle };
        }
        return data;
    }

    private static void CaptureConstraints(Snapshot snap, Dictionary<Rigidbody3D, int> index, Float3 origin, Quaternion inverse)
    {
        var ropes = Prowl.Runtime.Resources.Scene.Current.FindObjectsOfType<RopeVisual>()
            .Where(r => r.Constraint.IsValid())
            .ToDictionary(r => r.Constraint);
        var welded = new HashSet<(Rigidbody3D, Rigidbody3D)>();

        int B(Rigidbody3D body) => body.IsValid() && index.TryGetValue(body, out int i) ? i : -1;

        foreach ((Rigidbody3D body, int a) in index)
        {
            foreach (HingeJoint hinge in body.GameObject.GetComponents<HingeJoint>())
            {
                if (!Included(hinge.ConnectedBody)) continue;
                snap.Constraints.Add(new ConstraintData
                {
                    Type = "hinge", A = a, B = B(hinge.ConnectedBody),
                    AnchorA = Vec(hinge.Anchor), Axis = Vec(hinge.Axis), NoCollide = !hinge.CollideConnected,
                    MinAngle = hinge.MinAngle, MaxAngle = hinge.MaxAngle, Motor = hinge.HasMotor,
                    MotorSpeed = hinge.MotorTargetVelocity, MotorTorque = hinge.MotorMaxForce,
                });
            }

            // A weld is a fixed angle plus a ball socket at the origin; the fixed angle stands for both.
            foreach (FixedAngleConstraint angle in body.GameObject.GetComponents<FixedAngleConstraint>())
            {
                if (!Included(angle.ConnectedBody)) continue;
                welded.Add((body, angle.ConnectedBody));
                snap.Constraints.Add(new ConstraintData { Type = "weld", A = a, B = B(angle.ConnectedBody), NoCollide = !angle.CollideConnected });
            }

            foreach (BallSocketConstraint socket in body.GameObject.GetComponents<BallSocketConstraint>())
            {
                if (!Included(socket.ConnectedBody)) continue;
                if (socket.Anchor == Float3.Zero && welded.Contains((body, socket.ConnectedBody))) continue;
                snap.Constraints.Add(new ConstraintData
                {
                    Type = "ballsocket", A = a, B = B(socket.ConnectedBody),
                    AnchorA = Vec(socket.Anchor), NoCollide = !socket.CollideConnected,
                });
            }

            foreach (DistanceLimitConstraint rope in body.GameObject.GetComponents<DistanceLimitConstraint>())
            {
                if (!Included(rope.ConnectedBody)) continue;
                ropes.TryGetValue(rope, out RopeVisual visual);
                Float3 anchorB = rope.ConnectedBody.IsValid() ? rope.ConnectedAnchor : inverse * (rope.ConnectedAnchor - origin);
                snap.Constraints.Add(new ConstraintData
                {
                    Type = "rope", A = a, B = B(rope.ConnectedBody),
                    AnchorA = Vec(rope.Anchor), AnchorB = Vec(anchorB), Length = rope.MaxDistance,
                    Color = visual.IsValid() ? Col(visual.Color) : null, Width = visual.IsValid() ? visual.Width : 0.03f,
                });
            }
        }

        // To the world, or to another captured prop. Joints to props left out are dropped.
        bool Included(Rigidbody3D other) => other.IsNotValid() || index.ContainsKey(other);
    }

    // ----------------------------------------------------------------
    //  Spawn
    // ----------------------------------------------------------------

    /// <summary>Builds every prop and constraint at <paramref name="origin"/>. Returns what was made, for undo.</summary>
    public List<GameObject> Spawn(Float3 origin, Quaternion rotation)
    {
        GameManager game = GameManager.Instance;
        var made = new List<GameObject>();
        var bodies = new Rigidbody3D[Props.Count];

        for (int i = 0; i < Props.Count; i++)
        {
            PropData data = Props[i];
            GameObject go = Build(game, data);
            if (go.IsNotValid()) continue;

            go.Transform.Position = origin + rotation * V(data.Position);
            go.Transform.Rotation = rotation * Q(data.Rotation);
            go.Transform.LocalScale = new Float3(data.Scale);
            Prowl.Runtime.Resources.Scene.Current.Add(go);

            Prop prop = go.GetComponent<Prop>();
            Rigidbody3D body = go.GetComponent<Rigidbody3D>();
            if (data.Material != null && Guid.TryParse(data.Material, out Guid guid))
            {
                Material material = AssetDatabase.Get<Material>(guid);
                if (material.IsValid()) prop.SetMaterial(material);
            }
            if (data.Tint != null) prop.SetTint(C(data.Tint));
            if (data.Mass > 0f) body.Mass = data.Mass;
            body.AffectedByGravity = data.Gravity;
            prop.SetFrozen(data.Frozen);

            bodies[i] = body;
            made.Add(go);
        }

        foreach (ConstraintData c in Constraints)
        {
            Rigidbody3D a = c.A >= 0 && c.A < bodies.Length ? bodies[c.A] : null;
            Rigidbody3D b = c.B >= 0 && c.B < bodies.Length ? bodies[c.B] : null;
            if (a.IsNotValid() || (c.B >= 0 && b.IsNotValid())) continue;

            switch (c.Type)
            {
                case "hinge":
                    Float3 axis = c.Axis == null ? Float3.UnitY : V(c.Axis);
                    if (Float3.Length(axis) < 0.001f) axis = Float3.UnitY;
                    Slopbox.Constraints.Hinge(a, a.Transform.TransformPoint(V(c.AnchorA)), b,
                        a.Transform.TransformDirection(axis), c.NoCollide, c.MinAngle, c.MaxAngle,
                        c.Motor, c.MotorSpeed, c.MotorTorque);
                    break;
                case "weld":
                    Slopbox.Constraints.Weld(a, b, c.NoCollide);
                    break;
                case "ballsocket":
                    Slopbox.Constraints.BallSocket(a, a.Transform.TransformPoint(V(c.AnchorA)), b, c.NoCollide);
                    break;
                case "rope":
                    Float3 worldB = b.IsValid() ? b.Transform.TransformPoint(V(c.AnchorB)) : origin + rotation * V(c.AnchorB);
                    DistanceLimitConstraint rope = Slopbox.Constraints.Rope(a, a.Transform.TransformPoint(V(c.AnchorA)), b, worldB, c.Length);
                    var visual = new GameObject("Rope");
                    RopeVisual draw = visual.AddComponent<RopeVisual>();
                    draw.Constraint = rope;
                    if (c.Color != null) draw.Color = C(c.Color);
                    draw.Width = c.Width;
                    Prowl.Runtime.Resources.Scene.Current.Add(visual);
                    made.Add(visual);
                    break;
            }
        }

        return made;
    }

    internal static GameObject Build(GameManager game, PropData data)
    {
        if (data.Light != null)
            return LightProp.Create(C(data.Light.Color), data.Light.Brightness, data.Light.Range);
        if (data.Thruster != null)
            return ThrusterProp.Create(data.Thruster.Force, data.Thruster.Forward, data.Thruster.Back, data.Thruster.Toggle);

        Spawnable source = game.Catalog.Find(data.Spawn);
        if (source == null) return null;
        try
        {
            return source.Create();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            return null;
        }
    }

    // ----------------------------------------------------------------

    private static float[] Vec(Float3 v) => [v.X, v.Y, v.Z];
    private static float[] Quat(Quaternion q) => [q.X, q.Y, q.Z, q.W];
    private static float[] Col(Color c) => [c.R, c.G, c.B, c.A];
    private static Float3 V(float[] a) => a is { Length: >= 3 } ? new Float3(a[0], a[1], a[2]) : Float3.Zero;
    private static Quaternion Q(float[] a) => a is { Length: >= 4 } ? new Quaternion(a[0], a[1], a[2], a[3]) : Quaternion.Identity;
    private static Color C(float[] a) => a is { Length: >= 4 } ? new Color(a[0], a[1], a[2], a[3]) : Color.White;
}
