namespace Slopbox;

/// <summary>
/// Prowl lights now use physical units (lux for directional lights, candela for point and spot lights).
/// The brightness numbers in this project were tuned on the old relative scale, so code-made lights
/// multiply them by the same factor the engine's 1.0 project migration applied to saved lights.
/// </summary>
public static class LegacyLight
{
    public const float Scale = 157286.4f;
}
