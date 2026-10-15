using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>Runtime material helpers. Colours are linear, as everywhere in Prowl rendering.</summary>
public static class Materials
{
    /// <summary>A Lit material. With no surface texture, the roughness and metallic passed here are the final ones.</summary>
    public static Material Lit(Color color, float metallic = 0f, float roughness = 0.6f)
    {
        var material = new Material(Shader.LoadDefault(DefaultShader.Lit));
        material.SetColor("_MainColor", color);
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Roughness", roughness);
        return material;
    }

    /// <summary>Makes a Lit material glow. Intensity above 1 feeds bloom.</summary>
    public static Material Emissive(this Material material, Color color, float intensity)
    {
        material.SetInt("_Emission", 1);
        material.SetColor("_EmissiveColor", color);
        material.SetFloat("_EmissionIntensity", intensity);
        return material;
    }

    public static Material Line() => new(Shader.LoadDefault(DefaultShader.Line));
}
