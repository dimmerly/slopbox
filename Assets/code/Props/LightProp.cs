using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Networking;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// A placed light: a small physical bulb with a point light in it. A networked prop from
/// <see cref="GameManager.LightPrefab"/>: its colour, brightness and range are synced, and every peer pushes them
/// to its light and the bulb's glow, a bulb each builds for itself. Made by the <see cref="LightTool"/>.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Slopbox/Light Prop")]
public sealed class LightProp : Component
{
    public const float Radius = 0.12f;

    [Sync] public Color Color { get; set; } = new(1f, 0.85f, 0.6f, 1f);
    [Sync] public float Brightness { get; set; } = 4f;
    [Sync] public float Range { get; set; } = 8f;

    [SerializeIgnore] private Material _material;

    private (Color, float, float) _applied;

    /// <summary>Host: spawns a light at <paramref name="position"/>. Outside a session it spawns once this peer hosts.</summary>
    public static LightProp Spawn(Float3 position, Color color, float brightness, float range)
    {
        GameObject go = Networking.SpawnPrefab(GameManager.Instance.LightPrefab, position, Quaternion.Identity);
        var light = go.GetComponent<LightProp>();
        light.Set(color, brightness, range);
        return light;
    }

    public override void OnEnable()
    {
        // The bulb is built here rather than stored in the prefab, as its mesh is made in code.
        if (GetComponentInChildren<MeshRenderer>().IsNotValid())
        {
            var bulb = new GameObject("Bulb");
            bulb.SetParent(GameObject, false);
            bulb.Transform.LocalScale = new Float3(Radius * 2f);
            bulb.AddComponent<MeshRenderer>().Mesh = Shapes.Sphere;
        }
        Apply();
    }

    // A proxy's values arrive with the host's updates.
    public override void Update()
    {
        if (_applied != (Color, Brightness, Range)) Apply();
    }

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
        _applied = (Color, Brightness, Range);
        if (TryGetComponent(out PointLight light))
        {
            light.Color = Color;
            light.Intensity = Brightness * LegacyLight.Scale;
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
