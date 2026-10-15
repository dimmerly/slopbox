using System;
using Prowl.Runtime;
using Prowl.Vector;

namespace Slopbox;

/// <summary>Draws a rope between the two anchors of a <see cref="DistanceLimitConstraint"/>, sagging when slack. Removes itself with the constraint.</summary>
public sealed class RopeVisual : Component
{
    public DistanceLimitConstraint Constraint;
    public Color Color = new(0.15f, 0.1f, 0.06f, 1f);
    public float Width = 0.03f;

    private LineRenderer _line;

    public override void OnEnable()
    {
        _line = GetComponent<LineRenderer>();
        if (_line.IsNotValid()) _line = AddComponent<LineRenderer>();
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
        if (c.IsNotValid() || c.GameObject.IsDestroyQueued || !c.EnabledInHierarchy)
        {
            GameObject.Destroy();
            return;
        }

        Float3 a = c.Transform.TransformPoint(c.Anchor);
        Float3 b = c.ConnectedBody.IsValid() ? c.ConnectedBody.Transform.TransformPoint(c.ConnectedAnchor) : c.ConnectedAnchor;

        // A parabola whose depth matches the slack, roughly where a real rope would hang.
        float distance = Float3.Distance(a, b);
        float slack = MathF.Max(0f, c.MaxDistance - distance);
        float sag = MathF.Sqrt(slack * MathF.Max(c.MaxDistance, 0.01f)) * 0.5f;

        const int Segments = 16;
        _line.StartWidth = _line.EndWidth = Width;
        _line.StartColor = _line.EndColor = Color;
        _line.Points.Clear();
        for (int i = 0; i <= Segments; i++)
        {
            float t = i / (float)Segments;
            _line.Points.Add(Maths.Lerp(a, b, t) - Float3.UnitY * (sag * 4f * t * (1f - t)));
        }
        _line.MarkDirty();
    }
}
