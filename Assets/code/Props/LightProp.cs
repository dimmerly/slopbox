using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// A placed light: a small physical bulb with a point light in it. Its colour, brightness and range are
/// saved fields, and <see cref="Apply"/> pushes them to the light and the bulb's glow. Made by the
/// <see cref="LightTool"/>.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Slopbox/Light Prop")]
public sealed class LightProp : Component
{
    public const float Radius = 0.12f;

    public Color Color = new(1f, 0.85f, 0.6f, 1f);
    public float Brightness = 4f;
    public float Range = 8f;

    [SerializeIgnore] private Material _material;

    /// <summary>Builds a light, detached from the scene, ready to be positioned and added.</summary>
    public static GameObject Create(Color color, float brightness, float range)
    {
        var go = new GameObject("Light");
        go.AddComponent<SphereCollider>().Radius = Radius;
        go.AddComponent<Rigidbody3D>().Mass = 1f;
        go.AddComponent<Prop>();

        var light = go.AddComponent<Light>();
        light.CastShadows = false;

        var bulb = new GameObject("Bulb");
        bulb.SetParent(go, false);
        bulb.Transform.LocalScale = new Float3(Radius * 2f);
        bulb.AddComponent<MeshRenderer>().Mesh = Shapes.Sphere;

        var prop = go.AddComponent<LightProp>();
        prop.Color = color;
        prop.Brightness = brightness;
        prop.Range = range;
        return go;
    }

    public override void OnEnable() => Apply();

    public override void OnValidate() => Apply();

    protected override void OnDispose()
    {
        if (_material.IsValid()) _material.Dispose();
        base.OnDispose();
    }

    /// <summary>Changes the light and its glow together.</summary>
    public void Set(Color color, float brightness, float range)
    {
        Color = color;
        Brightness = brightness;
        Range = range;
        Apply();
    }

    public void Apply()
    {
        if (TryGetComponent(out Light light))
        {
            light.Color = Color;
            light.Intensity = Brightness;
            light.Range = Range;
        }

        MeshRenderer bulb = GetComponentInChildren<MeshRenderer>();
        if (bulb.IsNotValid()) return;

        // Each light owns its material so lights can differ. The glow tracks brightness, but gently,
        // so a bright light does not blow out to plain white.
        if (_material.IsNotValid()) _material = Materials.Lit(new Color(0.05f, 0.05f, 0.05f, 1f), 0f, 0.3f);
        _material.Emissive(Color, 1.5f + Brightness * 0.25f);
        bulb.Material = _material;
    }
}
