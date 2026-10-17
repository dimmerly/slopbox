using System;
using Prowl.PaperUI;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Scribe;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// Design tokens. The structure follows DXRP's <c>styles.scss</c> (near black #191919 / #111111 surfaces,
/// small radii, the 4px spacing scale and its type scale) with Claude orange as the accent.
/// <para>Every size is in "design pixels" at 1080p and goes through <see cref="Px"/>, which scales with the
/// screen height like s&amp;box UI does, times the player's UI scale setting.</para>
/// <para>Prowl ships one font, Geist Regular, so emphasis comes from size and colour rather than weight.</para>
/// UI colours are plain sRGB, unlike materials.
/// </summary>
public static class Theme
{
    // Colour
    public static readonly Color Primary = Hex(0x191919);
    public static readonly Color Secondary = Hex(0x111111);
    public static readonly Color Tertiary = Hex(0xF6F9FF);
    public static readonly Color Accent = Hex(0xD97757);       // Claude orange
    public static readonly Color AccentSoft = Hex(0xEBA68A);
    public static readonly Color OnAccent = Hex(0x111111);
    public static readonly Color Good = Hex(0x199C3B);
    public static readonly Color Error = Hex(0xF05252);
    public static readonly Color Warning = Hex(0xE0A526);
    public static readonly Color Gray400 = Hex(0x888B91);

    public static Color PrimaryA(float a) => WithAlpha(Primary, a);
    public static Color SecondaryA(float a) => WithAlpha(Secondary, a);
    public static Color AccentA(float a) => WithAlpha(Accent, a);
    public static Color White(float a) => new(1f, 1f, 1f, a);
    public static Color Black(float a) => new(0f, 0f, 0f, a);
    public static readonly Color Clear = new(0f, 0f, 0f, 0f);

    // ----------------------------------------------------------------
    //  Scale
    // ----------------------------------------------------------------

    /// <summary>Logical pixels per design pixel. Set once a frame by the HUD, before any other UI draws.</summary>
    public static float Scale { get; private set; } = 1f;

    /// <summary>A design pixel size, at 1080p and UI scale 1, in the current logical pixels.</summary>
    public static float Px(float designPixels) => designPixels * Scale;

    public static void UpdateScale(Paper paper, float userScale)
        => Scale = Math.Clamp(paper.Height / 1080f * userScale, 0.6f, 3f);

    // Type scale ($text-*)
    public static float TextXs => Px(12f);
    public static float TextSm => Px(14f);
    public static float TextBase => Px(16f);
    public static float TextLg => Px(18f);
    public static float TextXl => Px(22f);
    public static float Text2Xl => Px(30f);

    // Spacing scale ($space-*)
    public static float S1 => Px(4f);
    public static float S2 => Px(8f);
    public static float S3 => Px(12f);
    public static float S4 => Px(16f);
    public static float S6 => Px(24f);
    public static float S8 => Px(32f);

    // Radii: .rounded is 4px, build menu bodies are 5px.
    public static float Rounded => Px(4f);
    public static float BodyRadius => Px(5f);

    public static FontFile Font => FontAsset.LoadDefault().FontFile;

    /// <summary>Geist Black, for wordmarks and big titles. Falls back to <see cref="Font"/> if it is missing.</summary>
    public static FontFile TitleFont
    {
        get
        {
            GameManager game = GameManager.Instance;
            return game.IsValid() && game.TitleFont.IsValid() ? game.TitleFont.FontFile : Font;
        }
    }

    /// <summary>A build menu body: the panel under a row of tabs, square where the first tab joins it.</summary>
    public static ElementBuilder AsMenuBody(this ElementBuilder b)
        => b.BackgroundColor(PrimaryA(0.97f)).BackdropBlur(12f).Rounded(0f, BodyRadius, BodyRadius, BodyRadius);

    /// <summary>The HUD card: a dark translucent panel over a backdrop blur.</summary>
    public static ElementBuilder AsHudCard(this ElementBuilder b, float alpha = 0.95f)
        => b.BackgroundColor(PrimaryA(alpha)).BackdropBlur(8f).Rounded(Rounded);

    /// <summary>The lifted shadow on notifications and menu items.</summary>
    public static ElementBuilder AsLifted(this ElementBuilder b)
        => b.BoxShadow(0f, Px(4f), Px(12f), 0f, Black(0.6f));

    private static Color Hex(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    private static Color WithAlpha(Color c, float a) => new(c.R, c.G, c.B, a);
}
