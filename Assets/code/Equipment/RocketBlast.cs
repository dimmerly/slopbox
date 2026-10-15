using System;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>A short flash, expanding shockwave and sparks; all resources die with the effect.</summary>
public sealed class RocketBlast : Component
{
    private float _radius, _age;
    private Float3 _normal, _tangent, _bitangent;
    private GameObject _flash;
    private Light _light;
    private Material _flashMaterial, _lineMaterial;
    private LineRenderer _ring;
    private readonly LineRenderer[] _sparks = new LineRenderer[18];
    private readonly Float3[] _directions = new Float3[18];

    public static void Create(Float3 position, float radius, Float3 normal = default)
    {
        var go = new GameObject("Rocket Explosion");
        go.Transform.Position = position;
        var blast = go.AddComponent<RocketBlast>();
        blast._radius = radius;
        blast._normal = Float3.LengthSquared(normal) > 0.001f ? Float3.Normalize(normal) : Float3.UnitY;
        Scene.Current.Add(go);
    }

    public override void Start()
    {
        Float3 axis = MathF.Abs(_normal.Y) < 0.9f ? Float3.UnitY : Float3.UnitX;
        _tangent = Float3.Normalize(Float3.Cross(_normal, axis));
        _bitangent = Float3.Cross(_normal, _tangent);
        _flashMaterial = Materials.Lit(Color.Black).Emissive(new Color(1f, 0.25f, 0.02f, 1f), 8f);
        _lineMaterial = Materials.Line();
        _flash = new GameObject("Fireball");
        _flash.SetParent(GameObject, false);
        var mesh = _flash.AddComponent<MeshRenderer>();
        mesh.Mesh = Shapes.Sphere;
        mesh.Material = _flashMaterial;
        _light = GameObject.AddComponent<Light>();
        _light.Color = new Color(1f, 0.35f, 0.08f, 1f);
        _light.Range = _radius * 2f;
        _light.Intensity = 12f;
        _light.CastShadows = false;
        _ring = MakeLine("Shockwave");
        _ring.Loop = true;
        for (int i = 0; i < _sparks.Length; i++)
        {
            float y = 1f - 2f * (i + 0.5f) / _sparks.Length;
            float a = i * 2.399963f;
            float r = MathF.Sqrt(1f - y * y);
            _directions[i] = new Float3(MathF.Cos(a) * r, y, MathF.Sin(a) * r);
            _sparks[i] = MakeLine("Blast Spark");
        }
    }

    private LineRenderer MakeLine(string name)
    {
        var go = new GameObject(name);
        go.SetParent(GameObject, false);
        var line = go.AddComponent<LineRenderer>();
        line.Material = _lineMaterial;
        return line;
    }

    public override void Update()
    {
        _age += Time.DeltaTime;
        if (_age >= 0.65f)
        {
            GameObject.Destroy();
            return;
        }
        float t = _age / 0.65f;
        float fade = 1f - t;
        _flash.Enabled = _age < 0.18f;
        _flash.Transform.LocalScale = new Float3(0.4f + _age * 12f);
        _light.Intensity = 12f * fade * fade;
        _ring.StartColor = _ring.EndColor = new Color(2f * fade, 0.7f * fade, 0.1f * fade, fade);
        _ring.StartWidth = _ring.EndWidth = 0.12f * fade;
        _ring.Points.Clear();
        for (int i = 0; i < 48; i++)
        {
            float a = i * MathF.PI * 2f / 48f;
            // LineRenderer points are local to the effect; adding the impact again offsets it twice.
            _ring.Points.Add((_tangent * MathF.Cos(a) + _bitangent * MathF.Sin(a)) * (_radius * t) + _normal * 0.03f);
        }
        _ring.MarkDirty();
        for (int i = 0; i < _sparks.Length; i++)
        {
            LineRenderer spark = _sparks[i];
            Float3 end = _directions[i] * (_radius * t * 0.75f) - Float3.UnitY * (_age * _age * 3f);
            spark.StartColor = new Color(3f * fade, fade, 0.08f, fade);
            spark.EndColor = new Color(fade, 0.15f * fade, 0f, 0f);
            spark.StartWidth = 0.05f * fade;
            spark.EndWidth = 0.01f * fade;
            spark.Points.Clear();
            spark.Points.Add(end);
            spark.Points.Add(end - _directions[i] * 0.6f);
            spark.MarkDirty();
        }
    }

    protected override void OnDispose()
    {
        if (_flashMaterial.IsValid()) _flashMaterial.Dispose();
        if (_lineMaterial.IsValid()) _lineMaterial.Dispose();
        base.OnDispose();
    }
}
