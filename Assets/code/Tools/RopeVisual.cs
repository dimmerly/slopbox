using System;
using Prowl.Runtime;
using Prowl.Runtime.Networking;
using Prowl.Vector;

namespace Slopbox;

/// <summary>Draws a rope between the two anchors of a <see cref="DistanceLimitConstraint"/>, sagging when slack. Removes itself with the constraint.</summary>
public sealed class RopeVisual : Component
{
    public DistanceLimitConstraint Constraint;
    public Color Color = new(0.15f, 0.1f, 0.06f, 1f);
    public float Width = 0.03f;

    [Sync] public Float3 EndpointA { get; set; }
    [Sync] public Float3 EndpointB { get; set; }
    [Sync] public float RopeLength { get; set; }
    [Sync] public Color RopeColor { get; set; }
    [Sync] public float RopeWidth { get; set; }
    private LineRenderer _line;

    public override void OnEnable()
    {
        var visual = new GameObject("Rope Visual");
        visual.Network.Mode = NetworkMode.Never;
        visual.SetParent(GameObject, false);
        _line = visual.AddComponent<LineRenderer>();
        _line.Material = Materials.Line();
    }

    protected override void OnDispose()
    {
        if (_line.IsValid() && _line.Material.IsValid()) _line.Material.Dispose();
        base.OnDispose();
    }

    public override void LateUpdate()
    {
        DistanceLimitConstraint c = Constraint;
        if (!IsProxy && (c.IsNotValid() || c.GameObject.IsDestroyQueued || !c.EnabledInHierarchy))
        {
            GameObject.Destroy();
            return;
        }

        if (!IsProxy)
        {
            EndpointA = c.Transform.TransformPoint(c.Anchor);
            EndpointB = c.ConnectedBody.IsValid() ? c.ConnectedBody.Transform.TransformPoint(c.ConnectedAnchor) : c.ConnectedAnchor;
            RopeLength = c.MaxDistance; RopeColor = Color; RopeWidth = Width;
            if (Networking.IsHost && !Network.Active) Network.Spawn();
        }
        Float3 a = EndpointA, b = EndpointB;

        // A parabola whose depth matches the slack, roughly where a real rope would hang.
        float distance = Float3.Distance(a, b);
        float slack = MathF.Max(0f, RopeLength - distance);
        float sag = MathF.Sqrt(slack * MathF.Max(RopeLength, 0.01f)) * 0.5f;

        const int Segments = 16;
        _line.StartWidth = _line.EndWidth = RopeWidth;
        _line.StartColor = _line.EndColor = RopeColor;
        _line.Points.Clear();
        for (int i = 0; i <= Segments; i++)
        {
            float t = i / (float)Segments;
            _line.Points.Add(Maths.Lerp(a, b, t) - Float3.UnitY * (sag * 4f * t * (1f - t)));
        }
        _line.MarkDirty();
    }
}
