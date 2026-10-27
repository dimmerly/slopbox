using System;
using System.Collections.Generic;
using System.Globalization;
using Prowl.Ember;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Slopbox.Mounting.GoldSrc;

namespace Slopbox.Mounting.Source;

/// <summary>A Valve material (.vmt) made into a Prowl material, with what the map builder needs to know about it.</summary>
internal sealed class SourceMaterial : IDisposable
{
    public string Name;

    /// <summary>The shader the material asked for, lower case, such as "lightmappedgeneric".</summary>
    public string Shader;

    /// <summary>Null for materials that are never drawn.</summary>
    public Material Material;

    /// <summary>Tool textures and the like, which are never drawn.</summary>
    public bool NoDraw;

    /// <summary>The sky: not drawn, the scene's sky shows through.</summary>
    public bool Sky;

    /// <summary>A water surface: drawn see through, and never solid.</summary>
    public bool Water;

    /// <summary>Textures made only for this material, freed with it.</summary>
    public List<Texture2D> OwnedTextures = new();

    public void Dispose()
    {
        if (Material.IsValid()) Material.Dispose();
        foreach (Texture2D texture in OwnedTextures)
            if (texture.IsValid()) texture.Dispose();
    }
}

internal static class SourceAssets
{
    /// <summary>A texture from a decoded VTF, or null.</summary>
    public static Texture2D Texture(string name, VtfImage image)
    {
        if (image == null) return null;
        bool mipmaps = (image.Flags & VtfImage.FlagNoMip) == 0 && (image.Width > 1 || image.Height > 1);
        return GoldSrcAssets.Texture(name, image.Width, image.Height, image.Rgba, mipmaps);
    }
}

/// <summary>Turns Valve materials into Prowl ones on the Lit shader: their base texture, tint and blending.</summary>
public static class SourceMaterials
{
    // $flags bits, for materials written with the old flags field.
    private const int FlagAdditive = 0x80, FlagAlphaTest = 0x100, FlagNoCull = 0x2000, FlagSelfIllum = 0x40, FlagTranslucent = 0x200000;

    /// <summary>Shaders for brushes, which are drawn from both sides since a map's faces only face inwards.</summary>
    private static readonly HashSet<string> s_worldShaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "lightmappedgeneric", "worldvertextransition", "lightmappedreflective", "lightmapped_4wayblend", "worldtwotextureblend",
    };

    internal static SourceMaterial Build(string name, byte[] vmt, IContent content)
    {
        KeyValues root = Resolve(KeyValues.Parse(vmt), content, 0);
        if (root == null) return null;

        var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValues child in root.Children)
            if (!child.IsBlock) p.TryAdd(child.Key.TrimStart('$'), child.Value);

        string shader = root.Key.ToLowerInvariant();
        var result = new SourceMaterial { Name = name, Shader = shader };

        result.Sky = shader == "sky" || Flag(p, "%compilesky") || Flag(p, "%compile2dsky") || name.StartsWith("tools/toolsskybox");
        bool toolTexture = name.StartsWith("tools/") && !name.StartsWith("tools/toolsblack");
        result.NoDraw = result.Sky || toolTexture || Flag(p, "%compilenodraw") || Flag(p, "%compileclip")
            || Flag(p, "%compiletrigger") || Flag(p, "%compileskip") || Flag(p, "%compilehint") || Flag(p, "%compileinvisible");
        if (result.NoDraw) return result;

        if (shader is "water" || Flag(p, "%compilewater"))
        {
            result.Water = true;
            result.Material = WaterMaterial(name, p);
            MaterialSurfaces.Set(result.Material, p.GetValueOrDefault("surfaceprop") ?? "water");
            return result;
        }

        int flags = p.TryGetValue("flags", out string flagText) && int.TryParse(flagText, out int f) ? f : 0;
        bool translucent = Flag(p, "translucent") || (flags & FlagTranslucent) != 0;
        bool alphaTest = Flag(p, "alphatest") || (flags & FlagAlphaTest) != 0;
        bool additive = Flag(p, "additive") || (flags & FlagAdditive) != 0;
        bool noCull = Flag(p, "nocull") || (flags & FlagNoCull) != 0 || s_worldShaders.Contains(shader);
        bool selfIllum = Flag(p, "selfillum") || (flags & FlagSelfIllum) != 0;
        bool unlit = shader is "unlitgeneric" or "unlittwotexture" or "sprite" or "spritecard" or "modulate" or "decalmodulate";
        if (shader is "modulate" or "decalmodulate") return Hidden(result);   // multiplies what is behind: no Lit equivalent

        string baseName = p.GetValueOrDefault("basetexture");
        Texture2D texture = string.IsNullOrWhiteSpace(baseName) ? null : content.Texture(baseName);
        if (texture.IsNotValid() && shader is "refract" or "eyerefract") return Hidden(result);

        var material = new Material(Shader.LoadDefault(DefaultShader.Lit)) { Name = name };
        if (texture.IsValid()) material.SetTexture("_MainTex", texture);

        Color color = ParseColor(p.GetValueOrDefault("color")) ?? ParseColor(p.GetValueOrDefault("color2")) ?? Color.White;
        float alpha = float.TryParse(p.GetValueOrDefault("alpha"), NumberStyles.Float, CultureInfo.InvariantCulture, out float a) ? a : 1f;
        if (alpha < 1f) translucent = true;
        material.SetColor("_MainColor", new Color(color.R, color.G, color.B, alpha));
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Roughness", p.ContainsKey("envmap") ? 0.5f : Flag(p, "phong") ? 0.6f : 0.9f);

        SurfaceCull cull = noCull ? SurfaceCull.Off : SurfaceCull.Back;
        if (additive)
        {
            material.Surface = new SurfaceSettings(SurfaceType.Transparent, SurfaceBlend.Additive, cull);
        }
        else if (alphaTest)
        {
            material.Surface = new SurfaceSettings(SurfaceType.Cutout, SurfaceBlend.Alpha, cull);
            float reference = float.TryParse(p.GetValueOrDefault("alphatestreference"), NumberStyles.Float, CultureInfo.InvariantCulture, out float r) && r > 0f ? r : 0.5f;
            material.SetFloat("_AlphaCutoff", reference);
        }
        else if (translucent)
        {
            material.Surface = new SurfaceSettings(SurfaceType.Transparent, SurfaceBlend.Alpha, cull);
        }
        else if (noCull)
        {
            material.Surface = new SurfaceSettings(SurfaceType.Opaque, SurfaceBlend.Alpha, cull);
        }

        // Unlit materials and self lit ones glow. Self illumination is masked by the base texture's alpha.
        if (texture.IsValid() && (unlit || selfIllum))
        {
            Texture2D emission = texture;
            if (selfIllum && !unlit)
            {
                emission = MaskedEmission(name, baseName, content);
                if (emission != null) result.OwnedTextures.Add(emission);
            }
            if (emission.IsValid())
            {
                material.SetInt("_Emission", 1);
                material.SetTexture("_EmissionTex", emission);
                material.SetColor("_EmissiveColor", unlit ? new Color(color.R, color.G, color.B, 1f) : Color.White);
                material.SetFloat("_EmissionIntensity", 1f);
                if (unlit) material.SetColor("_MainColor", new Color(0f, 0f, 0f, alpha));
            }
        }

        result.Material = material;
        MaterialSurfaces.Set(material, p.GetValueOrDefault("surfaceprop"));
        return result;
    }

    private static SourceMaterial Hidden(SourceMaterial material)
    {
        material.NoDraw = true;
        return material;
    }

    /// <summary>Follows "patch" materials to the one they include, applying their inserts and replacements.</summary>
    private static KeyValues Resolve(KeyValues root, IContent content, int depth)
    {
        if (root == null || depth > 8) return root;
        if (!root.Key.Equals("patch", StringComparison.OrdinalIgnoreCase)) return root;

        string include = root.Get("include");
        if (string.IsNullOrWhiteSpace(include)) return null;
        KeyValues baseRoot = Resolve(KeyValues.Parse(content.Read(ContentPaths.Normalize(include))), content, depth + 1);
        if (baseRoot == null) return null;

        var merged = new KeyValues(baseRoot.Key);
        merged.Children.AddRange(baseRoot.Children);
        foreach (string section in new[] { "insert", "replace" })
        {
            KeyValues block = root[section];
            if (block == null) continue;
            foreach (KeyValues change in block.Children)
            {
                merged.Children.RemoveAll(c => c.Key.Equals(change.Key, StringComparison.OrdinalIgnoreCase));
                merged.Children.Add(change);
            }
        }
        return merged;
    }

    private static Material WaterMaterial(string name, Dictionary<string, string> p)
    {
        Color fog = ParseColor(p.GetValueOrDefault("fogcolor")) ?? new Color(0.1f, 0.15f, 0.15f, 1f);
        var material = new Material(Shader.LoadDefault(DefaultShader.Lit)) { Name = name };
        material.SetColor("_MainColor", new Color(fog.R, fog.G, fog.B, 0.75f));
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Roughness", 0.1f);
        material.Surface = new SurfaceSettings(SurfaceType.Transparent, SurfaceBlend.Alpha, SurfaceCull.Off);
        return material;
    }

    /// <summary>The base texture's colour where its alpha says it glows, black elsewhere.</summary>
    private static Texture2D MaskedEmission(string name, string baseName, IContent content)
    {
        VtfImage image = Vtf.Decode(content.Read($"materials/{ContentPaths.MaterialName(baseName)}.vtf"));
        if (image == null) return null;
        byte[] rgba = image.Rgba;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int m = rgba[i + 3];
            rgba[i] = (byte)(rgba[i] * m / 255);
            rgba[i + 1] = (byte)(rgba[i + 1] * m / 255);
            rgba[i + 2] = (byte)(rgba[i + 2] * m / 255);
            rgba[i + 3] = 255;
        }
        return SourceAssets.Texture(name + " emission", image);
    }

    private static bool Flag(Dictionary<string, string> p, string key)
        => p.TryGetValue(key, out string value) && value.Trim() is "1" or "true";

    /// <summary>"[1 0.5 0.5]" in 0 to 1, or "{255 128 128}" in bytes, as a linear colour.</summary>
    public static Color? ParseColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        bool bytes = value.StartsWith('{');
        string[] parts = value.Trim('[', ']', '{', '}', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;

        var c = new float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out c[i])) return null;
            if (bytes) c[i] /= 255f;
            c[i] = MathF.Pow(Math.Clamp(c[i], 0f, 4f), 2.2f);
        }
        return new Color(c[0], c[1], c[2], 1f);
    }
}
