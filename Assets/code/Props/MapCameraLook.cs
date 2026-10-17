using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Rendering;

namespace Slopbox;

/// <summary>
/// Draws a camera the way Source drew its maps while it is inside one: its tonemapper swapped for Source's plain
/// clamp and gamma encode (<see cref="SourceDisplay"/>) and bloom off, so lightmapped colours come out as the game
/// showed them. The camera's own effects come back once it leaves.
/// </summary>
[DisallowMultipleComponent]
public sealed class MapCameraLook : Component
{
    /// <summary>Seconds between looks at where the camera is.</summary>
    private const float Interval = 0.2f;

    [SerializeIgnore] private bool _inside;
    [SerializeIgnore] private TonemappingMode _saved;
    [SerializeIgnore] private float _savedBloom;
    [SerializeIgnore] private float _nextCheck;

    public override void Update()
    {
        if (Time.TimeSinceStartup < _nextCheck) return;
        _nextCheck = (float)Time.TimeSinceStartup + Interval;

        Camera camera = GetComponent<Camera>();
        if (camera.IsNotValid() || camera.Profile.IsNotValid() || !camera.Profile.TryGet(out Tonemapping tonemapping)) return;

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
            _saved = tonemapping.Mode.Value;
            tonemapping.Mode.Set(TonemappingMode.None);
            if (!camera.Profile.Has<SourceDisplay>()) camera.Profile.Add<SourceDisplay>();
            if (camera.Profile.TryGet(out Bloom bloom))
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
        if (camera.IsNotValid() || camera.Profile.IsNotValid()) return;
        camera.Profile.Remove<SourceDisplay>();
        if (camera.Profile.TryGet(out Tonemapping tonemapping)) tonemapping.Mode.Set(_saved);
        if (camera.Profile.TryGet(out Bloom bloom)) bloom.Intensity.Set(_savedBloom);
    }
}
