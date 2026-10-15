namespace Slopbox;

/// <summary>
/// Player preferences. Lives on the GameManager for now; persisting it to disk later only has to
/// serialize this one object.
/// </summary>
public sealed class Settings
{
    /// <summary>Source-style sensitivity: degrees turned per pixel are <c>0.022 * Sensitivity</c>.</summary>
    public float Sensitivity = 2.5f;
    public bool InvertY;

    /// <summary>Vertical field of view in degrees. 62 matches HL2's 75 horizontal at 4:3.</summary>
    public float FieldOfView = 62f;

    /// <summary>0 turns view bob off, 1 is the default amount.</summary>
    public float ViewBob = 1f;

    /// <summary>Degrees the view rolls while strafing. HL2 ships with this off.</summary>
    public float ViewRoll = 0f;

    /// <summary>Multiplies every UI size, on top of the automatic scaling with screen height.</summary>
    public float UiScale = 1f;

    public float MasterVolume = 0.85f;
    public float EffectsVolume = 0.85f;
    public float UiVolume = 0.7f;
    public float AmbienceVolume = 0.08f;

    public bool ShowHud = true;
    public bool ShowFps;
}
