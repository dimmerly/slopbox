using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Rendering;

namespace Slopbox;

/// <summary>
/// Draws a camera the way Source drew its maps while it is inside one: its tonemapper swapped for Source's plain
/// clamp and gamma encode and bloom off, so lightmapped colours come out as the game showed them. The camera's own
/// effects come back once it leaves.
/// </summary>
[DisallowMultipleComponent]
public sealed class MapCameraLook : Component
{
    /// <summary>Seconds between looks at where the camera is.</summary>
    private const float Interval = 0.2f;

    /// <summary>
    /// Any mode past the named ones compiles Graphite's clamp-only tonemap: colour clamped to 0 to 1 and gamma encoded,
    /// nothing else, which is the display transform Source's LDR renderer has.
    /// </summary>
    private const TonemappingMode SourceDisplay = (TonemappingMode)(-1);

    [SerializeIgnore] private bool _inside;
    [SerializeIgnore] private TonemappingMode _saved;
    [SerializeIgnore] private float _savedBloom;
    [SerializeIgnore] private float _nextCheck;

    public override void Update()
    {
        if (Time.TimeSinceStartup < _nextCheck) return;
        _nextCheck = (float)Time.TimeSinceStartup + Interval;

        Camera camera = GetComponent<Camera>();
        if (camera.IsNotValid()) return;

        bool inside = false;
        foreach (MapProp map in MapProp.All)
        {
            if (map.IsValid() && map.SourceLook && map.TrySampleLight(Transform.Position, out _))
            {
                inside = true;
                break;
            }
        }

        if (inside == _inside) return;
        _inside = inside;
        if (inside)
        {
            _saved = camera.Tonemapping;
            camera.Tonemapping = SourceDisplay;
            if (camera.Profile.IsValid() && camera.Profile.TryGet(out Bloom bloom))
            {
                _savedBloom = bloom.Intensity.Value;
                bloom.Intensity.Set(0f);
            }
        }
        else
        {
            Apply(camera);
        }
    }

    public override void OnDisable() => Restore();

    protected override void OnDispose()
    {
        Restore();
        base.OnDispose();
    }

    private void Restore()
    {
        if (!_inside) return;
        _inside = false;
        Apply(GetComponent<Camera>());
    }

    /// <summary>Gives the camera back the effects it had before it went in.</summary>
    private void Apply(Camera camera)
    {
        if (camera.IsNotValid()) return;
        camera.Tonemapping = _saved;
        if (camera.Profile.IsValid() && camera.Profile.TryGet(out Bloom bloom)) bloom.Intensity.Set(_savedBloom);
    }
}
