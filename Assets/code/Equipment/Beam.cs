using System;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>A world space line drawn as a curve, for the physgun beam, tool gun tracers and ropes.</summary>
public sealed class Beam
{
    private readonly GameObject _go;
    private readonly LineRenderer _line;

    public Beam(string name, Color color, float width)
    {
        _go = new GameObject(name);
        _line = _go.AddComponent<LineRenderer>();
        _line.Material = Materials.Line();
        _line.StartWidth = width;
        _line.EndWidth = width;
        SetColor(color);
        Scene.Current.Add(_go);
    }

    public bool IsValid => _go.IsValid() && !_go.IsDestroyQueued;

    public void SetColor(Color color) => SetColors(color, color);

    public void SetColors(Color start, Color end)
    {
        _line.StartColor = start;
        _line.EndColor = end;
    }

    public void SetWidth(float start, float end)
    {
        _line.StartWidth = start;
        _line.EndWidth = end;
    }

    public void Hide()
    {
        if (_line.Points.Count == 0) return;
        _line.Points.Clear();
        _line.MarkDirty();
    }

    /// <summary>A quadratic Bezier from <paramref name="start"/> through the pull of <paramref name="control"/> to <paramref name="end"/>.</summary>
    public void Curve(Float3 start, Float3 control, Float3 end, int segments = 20)
    {
        _line.Points.Clear();
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments, u = 1f - t;
            _line.Points.Add(start * (u * u) + control * (2f * u * t) + end * (t * t));
        }
        _line.MarkDirty();
    }

    public void Line(Float3 start, Float3 end) => Curve(start, (start + end) * 0.5f, end, 1);

    public void Destroy()
    {
        if (_go.IsValid()) _go.Destroy();
    }
}
