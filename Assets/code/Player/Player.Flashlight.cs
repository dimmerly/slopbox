using Prowl.Runtime;
using Prowl.Vector;

namespace Slopbox;

/// <summary>The flashlight: a spot light riding on the camera, toggled with <see cref="Controls.Flashlight"/>.</summary>
public sealed partial class Player
{
    private Light _flashlight;

    public bool FlashlightOn => _flashlight.IsValid() && _flashlight.Enabled;

    private void SetupFlashlight()
    {
        var go = new GameObject("Flashlight");
        go.SetParent(Camera.GameObject, false);

        // Prowl lights have no culling mask, so the light starts just past the view model's muzzle
        // instead: the weapon sits behind the cone and is never lit by it.
        go.Transform.LocalPosition = new Float3(0f, 0f, 0.75f);

        _flashlight = go.AddComponent<Light>();
        _flashlight.Type = LightType.Spot;
        _flashlight.Color = new Color(1f, 0.93f, 0.8f, 1f);
        _flashlight.Intensity = 6f;
        _flashlight.Range = 30f;
        _flashlight.OuterAngle = 50f;
        _flashlight.InnerAngle = 25f;
        _flashlight.CastShadows = false;
        _flashlight.Enabled = false;
    }

    private void UpdateFlashlight()
    {
        if (_flashlight.IsNotValid()) return;
        GameManager game = GameManager.Instance;
        if (game.IsMenuOpen || !Controls.Pressed(Controls.Flashlight)) return;

        _flashlight.Enabled = !_flashlight.Enabled;
        GameAudio.Play("flashlight", 0.45f, _flashlight.Enabled ? 1.05f : 0.9f);
    }
}
