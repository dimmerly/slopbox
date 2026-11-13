using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// Runtime material helpers. Colours are linear, as everywhere in Prowl rendering.
/// The Lit shader uses OpenPBR property names (base_color, specular_roughness, emission_luminance, ...).
/// </summary>
public static class Materials
{
    public const string BaseColor = "base_color", BaseColorMap = "base_color_map";
    public const string Roughness = "specular_roughness", Metalness = "base_metalness";
    public const string Opacity = "geometry_opacity", OpacityMap = "geometry_opacity_map", AlphaCutoff = "alpha_cutoff";
    public const string EmissionColor = "emission_color", EmissionColorMap = "emission_color_map", EmissionLuminance = "emission_luminance";

    /// <summary>
    /// Emission is in nits now. Intensities in this project were tuned on the old relative scale, and this is
    /// the factor the engine's 1.0 migration used for them (the same exposure the old renderer assumed).
    /// </summary>
    public const float LegacyLuminance = 1.2f * 16384f;

    /// <summary>
    /// A Lit material. <paramref name="metallic"/> is accepted for intent but not applied: the old shader only
    /// used it through a surface texture, so it never showed, and applying it now would change how things look.
    /// </summary>
    public static Material Lit(Color color, float metallic = 0f, float roughness = 0.6f)
    {
        var material = new Material(Shader.LoadDefault(DefaultShader.Lit));
        material.SetColor(BaseColor, color);
        material.SetFloat(Roughness, roughness);
        return material;
    }

    /// <summary>Makes a Lit material glow. Intensity above 1 feeds bloom.</summary>
    public static Material Emissive(this Material material, Color color, float intensity)
    {
        material.SetColor(EmissionColor, color);
        material.SetFloat(EmissionLuminance, intensity * LegacyLuminance);
        return material;
    }

    /// <summary>Glow from a texture, as the old emission texture slot did.</summary>
    public static Material Emissive(this Material material, Texture2D texture, Color color, float intensity)
    {
        material.SetTexture(EmissionColorMap, texture);
        return material.Emissive(color, intensity);
    }

    /// <summary>
    /// For cutout and transparent materials: opacity comes from <paramref name="alpha"/> times the albedo
    /// texture's alpha channel. Call after setting the albedo texture.
    /// </summary>
    public static Material SeeThrough(this Material material, float alpha)
    {
        material.SetFloat(Opacity, alpha);
        Texture2D albedo = material.GetTexture(BaseColorMap);
        if (albedo.IsValid())
        {
            material.SetTexture(OpacityMap, albedo);
            material.SetTextureChannel(OpacityMap, 3);
        }
        return material;
    }

    public static Material Line() => new(Shader.LoadDefault(DefaultShader.Line));
}
