using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using G = Prowl.Graphite.Rendering;

namespace Slopbox;

/// <summary>
/// The display transform Source's LDR renderer has: colour clamped to 0 to 1 and gamma encoded, nothing else. Prowl's
/// Tonemapping component offers only curved operators, and with its mode at None skips the pass that gamma encodes,
/// which shows linear colour as is and makes everything look dark and harsh. This selects Graphite's plain clamp
/// operator, with contrast and saturation left alone.
/// </summary>
[VolumeComponentMenu("Image/Source Display")]
public sealed class SourceDisplay : VolumeComponent<G.Tonemapper>
{
    public override bool IsActive => true;

    protected override G.Tonemapper CreateFeature(G.Renderer renderer) => new(renderer);

    protected override void Apply(G.Tonemapper feature, Camera camera)
    {
        // Any value past the named operators compiles Graphite's clamp-only tonemap.
        feature.Operator = (G.TonemapOperator)(-1);
        feature.Contrast = 1f;
        feature.Saturation = 1f;
    }
}
