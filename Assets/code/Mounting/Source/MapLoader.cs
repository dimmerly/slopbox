using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Slopbox.Mounting.GoldSrc;
using Slopbox.Mounting.Source.Bsp;
using Vector3 = System.Numerics.Vector3;

namespace Slopbox.Mounting.Source;

/// <summary>A Source map (.bsp) of a mounted game, or one dropped into the custom maps folder.</summary>
internal sealed class SourceMapLoader : ResourceLoader<SourceMount>
{
    private readonly SourceFile _file;

    public SourceMapLoader(SourceFile file) => _file = file;

    public SourceMapLoader(string fullPath) => _file = new SourceFile(null, null, fullPath);

    protected override object Load() => SourceMapBuilder.Build(Name, _file.Read(), Host);
}

/// <summary>
/// Builds a <see cref="MountedMap"/> from a Source map: the world, its displacements and every visible brush
/// entity, lit by the map's own lightmaps packed into RGBM pages; the static props and prop entities as placed
/// models; and its ambient sounds and soundscapes. Content is looked for in the map's pak lump first, then its
/// game, then every other mounted Source game, as Garry's Mod does.
/// </summary>
internal static class SourceMapBuilder
{
    private const int PageSize = 2048;

    /// <summary>
    /// The most light a surface can show, as a multiple of its texture. Source stores lightmaps as
    /// (light ^ (1 / 2.2)) / 2 in a byte and doubles them in the shader, in gamma space; drawn linearly that is the
    /// texture times the stored light, up to 2 ^ 2.2.
    /// </summary>
    private static readonly float s_maxLight = MathF.Pow(2f, 2.2f);

    /// <summary>Brush entities that are drawn but never block.</summary>
    private static readonly HashSet<string> s_nonSolid = new(StringComparer.OrdinalIgnoreCase)
    {
        "func_illusionary", "func_water_analog", "func_dustmotes", "func_dustcloud", "func_smokevolume", "func_precipitation",
    };

    /// <summary>Brush entities that are never drawn.</summary>
    private static readonly string[] s_hiddenPrefixes =
    [
        "trigger_", "func_areaportal", "func_occluder", "func_viscluster", "func_clip", "func_vehicleclip", "func_playerclip",
        "func_npcclip", "func_ladder", "func_useableladder", "func_buyzone", "func_bomb_target", "func_hostage_rescue",
        "func_nobuild", "func_respawnroom", "func_regenerate", "func_precipitation", "func_dustmotes", "env_", "point_",
    ];

    private static readonly HashSet<string> s_spawnClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "info_player_start", "info_player_deathmatch", "info_player_terrorist", "info_player_counterterrorist",
        "info_player_combine", "info_player_rebel", "info_player_teamspawn",
    };

    private struct Lightmap
    {
        public int Width, Height;   // samples
        public int Page, X, Y;
        public bool Has;
    }

    public static MountedMap Build(string name, byte[] data, SourceMount host)
    {
        if (data == null) return null;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var bsp = new BspFile(data);

        var pak = new PakContent(bsp.Pak);
        IContent games = host?.WithOtherGames() ?? (IContent)new ContentChain([]);
        pak.Fallback = games;
        IContent content = new ContentChain([pak, games]);

        // A material per texture name.
        var owned = new List<Material>();
        Material missing = null;
        var missingNames = new List<string>();
        var materials = new SourceMaterial[bsp.TextureNames.Length];
        for (int i = 0; i < materials.Length; i++)
        {
            materials[i] = content.Material(bsp.TextureNames[i]);
            if (materials[i] != null) continue;
            missingNames.Add(bsp.TextureNames[i]);
            if (missing.IsNotValid())
            {
                missing = Materials.Lit(new Color(0.5f, 0.5f, 0.5f, 1f), 0f, 1f);
                owned.Add(missing);
            }
            materials[i] = new SourceMaterial { Name = bsp.TextureNames[i], Material = missing };
        }

        (Lightmap[] lightmaps, List<Texture2D> pages, List<Texture2D>[] bumpPages) = PackLightmaps(bsp);
        var ownedTexturesExtra = new List<Texture2D>(bumpPages.SelectMany(b => b));

        // A bumped material on a page reads that page's directional lightmaps: one copy of it per page
        var bumpVariants = new Dictionary<(Material, int), Material>();
        Material BumpVariant(SourceMaterial material, int page)
        {
            if (!material.Bumped || page < 0) return material.Material;
            if (bumpVariants.TryGetValue((material.Material, page), out Material variant)) return variant;
            variant = material.Material.Clone();
            variant.Name = material.Material.Name + $" (page {page})";
            variant.SetTexture("_Basis1", bumpPages[0][page]);
            variant.SetTexture("_Basis2", bumpPages[1][page]);
            variant.SetTexture("_Basis3", bumpPages[2][page]);
            variant.SetFloat("_HasBasis", 1f);
            owned.Add(variant);
            return bumpVariants[(material.Material, page)] = variant;
        }

        var parts = new List<MountedMap.Part>();
        var spawns = new List<MountedMap.SpawnPoint>();
        var models = new List<MountedMap.PlacedModel>();
        var sounds = new List<MountedMap.PlacedSound>();
        var soundscapes = new List<MountedMap.SoundscapeZone>();
        Float3 min = new(float.MaxValue), max = new(float.MinValue);

        for (int e = 0; e < bsp.Entities.Count; e++)
        {
            Entity entity = bsp.Entities[e];
            string className = entity.ClassName;

            if (s_spawnClasses.Contains(className))
            {
                spawns.Add(new MountedMap.SpawnPoint(GoldSrcAssets.Position(entity.GetVector("origin")), -entity.GetVector("angles").Y));
                continue;
            }

            if (className.Equals("ambient_generic", StringComparison.OrdinalIgnoreCase))
            {
                AddSound(entity, sounds);
                continue;
            }

            if (className.StartsWith("env_soundscape", StringComparison.OrdinalIgnoreCase))
            {
                string soundscape = entity.Get("soundscape");
                if (!string.IsNullOrWhiteSpace(soundscape) && entity.GetInt("StartDisabled") == 0)
                {
                    float radius = entity.GetFloat("radius", -1f);
                    soundscapes.Add(new MountedMap.SoundscapeZone(soundscape, GoldSrcAssets.Position(entity.GetVector("origin")),
                        radius < 0f ? -1f : radius * GoldSrcAssets.Scale));
                }
                continue;
            }

            string model = entity.Get("model");
            if (model != null && model.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            {
                if (entity.GetInt("StartDisabled") != 0 || entity.GetInt("rendermode") == 10) continue;
                models.Add(new MountedMap.PlacedModel(className, ContentPaths.Normalize(model),
                    GoldSrcAssets.Position(entity.GetVector("origin")), Rotation(entity.GetVector("angles")))
                {
                    Exact = true,
                    Solid = true,
                    Skin = entity.GetInt("skin"),
                    Scale = entity.GetFloat("modelscale", 1f),
                });
                continue;
            }

            int modelIndex = className.Equals("worldspawn", StringComparison.OrdinalIgnoreCase) ? 0 : entity.BrushModel;
            if (modelIndex < 0 || modelIndex >= bsp.Models.Length) continue;
            if (modelIndex > 0 && s_hiddenPrefixes.Any(p => className.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (entity.GetInt("rendermode") == 10 || (entity.Get("renderamt") != null && entity.GetInt("rendermode") != 0 && entity.GetInt("renderamt", 255) == 0)) continue;

            bool solid = !s_nonSolid.Contains(className);
            var transform = new EntityTransform(entity.GetVector("origin"), modelIndex == 0 ? Vector3.Zero : entity.GetVector("angles"));
            BuildBrushModel(bsp, bsp.Models[modelIndex], transform, materials, lightmaps, BumpVariant, $"{className} {e}", solid, parts, ref min, ref max);
        }

        BuildOverlays(bsp, materials, lightmaps, parts);
        Material skyMaterial = SkyMaterial(bsp, content, owned);

        foreach (StaticProp prop in bsp.StaticProps)
        {
            if (string.IsNullOrWhiteSpace(prop.Model)) continue;
            models.Add(new MountedMap.PlacedModel("prop_static", ContentPaths.Normalize(prop.Model),
                GoldSrcAssets.Position(prop.Origin), Rotation(prop.Angles))
            {
                Exact = true,
                Solid = prop.Solid != 0,
                Skin = prop.Skin,
                Scale = prop.Scale <= 0f ? 1f : prop.Scale,
            });
        }

        if (spawns.Count == 0) spawns.Add(new MountedMap.SpawnPoint(Float3.Zero, 0f));
        var bounds = min.X <= max.X ? new AABB(min, max) : new AABB(new Float3(-1f), new Float3(1f));

        var scripts = new List<SoundScripts>();
        if (host != null) scripts.Add(host.Sounds);
        if (host != null) scripts.AddRange(host.OtherGames().Select(m => m.Sounds));
        var audio = new SourceMapAudio(content, scripts, pak.Read($"scripts/soundscapes_{name}.txt"));

        var ownedTextures = new List<Texture2D>(pak.OwnedTextures);
        ownedTextures.AddRange(ownedTexturesExtra);
        foreach (SourceMaterial material in pak.OwnedMaterials)
        {
            if (material.Material.IsValid()) owned.Add(material.Material);
            ownedTextures.AddRange(material.OwnedTextures);
        }

        if (missingNames.Count > 0)
            Debug.LogWarning($"{name}: {missingNames.Count} materials not found, such as {string.Join(", ", missingNames.Take(8))}");
        Debug.Log($"Built {name}: {parts.Count} parts, {pages.Count} lightmap pages, {models.Count} models, {sounds.Count} sounds, "
            + $"{soundscapes.Count} soundscapes, {pak.Count} packed files in {timer.ElapsedMilliseconds} ms");

        return new MountedMap(name, parts, pages, spawns, models, bounds, owned, ownedTextures)
        {
            Probes = Probes(bsp),
            Sun = Sun(bsp),
            Sky = skyMaterial,
            Sounds = sounds,
            Soundscapes = soundscapes,
            Audio = audio,
        };
    }

    /// <summary>
    /// Source angles (pitch, yaw, roll in degrees) as a rotation in Prowl's space: yaw about up, then pitch about
    /// the side axis, then roll about forward.
    /// </summary>
    public static Quaternion Rotation(Vector3 angles)
    {
        const float ToRadians = MathF.PI / 180f;
        var q = System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angles.Y * ToRadians)
            * System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitY, angles.X * ToRadians)
            * System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitX, angles.Z * ToRadians);
        return GoldSrcAssets.Rotation(q);
    }

    /// <summary>An ambient_generic that loops on its own from the start; ones fired by the map's logic are skipped.</summary>
    private static void AddSound(Entity entity, List<MountedMap.PlacedSound> sounds)
    {
        int flags = entity.GetInt("spawnflags");
        const int Everywhere = 1, StartSilent = 16, NotLooped = 32;
        if ((flags & (StartSilent | NotLooped)) != 0) return;

        string sound = entity.Get("message");
        if (string.IsNullOrWhiteSpace(sound)) return;

        float volume = Math.Clamp(entity.GetFloat("health", 10f) / 10f, 0f, 1f);
        if (volume <= 0f) return;
        float pitch = Math.Clamp(entity.GetFloat("pitch", 100f), 1f, 255f) / 100f;
        float radius = entity.GetFloat("radius", 1250f);
        sounds.Add(new MountedMap.PlacedSound(sound, GoldSrcAssets.Position(entity.GetVector("origin")), volume, pitch,
            Math.Max(1f, radius * GoldSrcAssets.Scale), (flags & Everywhere) != 0));
    }

    /// <summary>Moves a brush entity's faces by its origin and turns them by its angles.</summary>
    private readonly struct EntityTransform(Vector3 origin, Vector3 angles)
    {
        private readonly System.Numerics.Matrix4x4 _rotation = angles == Vector3.Zero
            ? System.Numerics.Matrix4x4.Identity
            : System.Numerics.Matrix4x4.CreateFromQuaternion(
                System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angles.Y * MathF.PI / 180f)
                * System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitY, angles.X * MathF.PI / 180f)
                * System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitX, angles.Z * MathF.PI / 180f));

        public Vector3 Point(Vector3 p) => Vector3.Transform(p, _rotation) + origin;
        public Vector3 Normal(Vector3 n) => Vector3.TransformNormal(n, _rotation);
    }

    private sealed class Geometry
    {
        public readonly List<Float3> Positions = new();
        public readonly List<Float3> Normals = new();
        public readonly List<Float2> UV = new();
        public readonly List<Float2> UV2 = new();
        public readonly List<uint> Indices = new();
    }

    private static void BuildBrushModel(BspFile bsp, BrushModel model, EntityTransform transform, SourceMaterial[] materials,
        Lightmap[] lightmaps, Func<SourceMaterial, int, Material> variant, string name, bool solid, List<MountedMap.Part> parts, ref Float3 min, ref Float3 max)
    {
        var groups = new Dictionary<(int Page, bool Solid), Dictionary<Material, Geometry>>();
        Float3 low = min, high = max;   // ref parameters cannot be captured below

        for (int f = model.FirstFace; f < model.FirstFace + model.FaceCount && f < bsp.Faces.Length; f++)
        {
            Face face = bsp.Faces[f];
            if (face.EdgeCount < 3 || face.TexInfo < 0 || face.TexInfo >= bsp.TexInfos.Length) continue;

            TexInfo texInfo = bsp.TexInfos[face.TexInfo];
            if ((texInfo.Flags & (TexInfo.NoDraw | TexInfo.Sky | TexInfo.Sky2D | TexInfo.Trigger | TexInfo.Hint | TexInfo.Skip)) != 0) continue;
            if (texInfo.TexData < 0 || texInfo.TexData >= bsp.TexDatas.Length) continue;

            SourceMaterial material = materials[texInfo.TexData];
            if (material.NoDraw || material.Material.IsNotValid()) continue;

            Lightmap lightmap = lightmaps[f];
            int page = lightmap.Has ? lightmap.Page : -1;
            bool faceSolid = solid && !material.Water;

            if (!groups.TryGetValue((page, faceSolid), out var byMaterial)) groups[(page, faceSolid)] = byMaterial = new();
            bool bumpLit = (texInfo.Flags & TexInfo.BumpLight) != 0;
            Material drawn = bumpLit ? variant(material, page) : material.Material;
            if (!byMaterial.TryGetValue(drawn, out Geometry geometry)) byMaterial[drawn] = geometry = new Geometry();

            TexData texData = bsp.TexDatas[texInfo.TexData];
            float texWidth = Math.Max(1, texData.Width), texHeight = Math.Max(1, texData.Height);

            Float2 TextureUV(Vector3 p) => new(
                (Vector3.Dot(p, texInfo.S) + texInfo.OffsetS) / texWidth,
                (Vector3.Dot(p, texInfo.T) + texInfo.OffsetT) / texHeight);

            Float2 LightUV(Vector3 p)
            {
                if (!lightmap.Has) return Float2.Zero;
                float s = Vector3.Dot(p, texInfo.LightS) + texInfo.LightOffsetS - face.LightMinS;
                float t = Vector3.Dot(p, texInfo.LightT) + texInfo.LightOffsetT - face.LightMinT;
                s = Math.Clamp(s, 0f, lightmap.Width - 1);
                t = Math.Clamp(t, 0f, lightmap.Height - 1);
                return new Float2((lightmap.X + 1 + s + 0.5f) / PageSize, (lightmap.Y + 1 + t + 0.5f) / PageSize);
            }

            void AddVertex(Vector3 p, Vector3 basePosition, Vector3 normal)
            {
                Float3 position = GoldSrcAssets.Position(transform.Point(p));
                geometry.Positions.Add(position);
                geometry.Normals.Add(GoldSrcAssets.Direction(transform.Normal(normal)));
                geometry.UV.Add(TextureUV(basePosition));
                geometry.UV2.Add(LightUV(basePosition));
                low = Maths.Min(low, position);
                high = Maths.Max(high, position);
            }

            // A face's plane already faces the way the face does; its side flag is about the BSP node, not the face.
            Vector3 faceNormal = bsp.Planes[face.Plane].Normal;

            if (face.DispInfo >= 0 && face.DispInfo < bsp.DispInfos.Length && face.EdgeCount == 4)
            {
                BuildDisplacement(bsp, face, bsp.DispInfos[face.DispInfo], faceNormal, geometry, AddVertex);
                continue;
            }

            int first = geometry.Positions.Count;
            for (int i = 0; i < face.EdgeCount; i++)
            {
                Vector3 p = bsp.FaceVertex(face, i);
                AddVertex(p, p, faceNormal);
            }

            // A convex polygon as a fan. Source winds faces clockwise from the front; mirroring into Prowl's space
            // makes that the way round Prowl draws as the front.
            for (int i = 1; i < face.EdgeCount - 1; i++)
            {
                geometry.Indices.Add((uint)first);
                geometry.Indices.Add((uint)(first + i));
                geometry.Indices.Add((uint)(first + i + 1));
            }
        }

        min = low;
        max = high;

        foreach (((int page, bool faceSolid), Dictionary<Material, Geometry> byMaterial) in groups)
        {
            var positions = new List<Float3>();
            var normals = new List<Float3>();
            var uvs = new List<Float2>();
            var uv2s = new List<Float2>();
            var indices = new List<uint>();
            var submeshes = new List<SubMeshDescriptor>();
            var partMaterials = new List<Material>();

            foreach ((Material material, Geometry geometry) in byMaterial)
            {
                if (geometry.Indices.Count == 0) continue;
                int baseVertex = positions.Count, start = indices.Count;
                positions.AddRange(geometry.Positions);
                normals.AddRange(geometry.Normals);
                uvs.AddRange(geometry.UV);
                uv2s.AddRange(geometry.UV2);
                foreach (uint index in geometry.Indices) indices.Add((uint)(index + baseVertex));
                submeshes.Add(new SubMeshDescriptor(start, indices.Count - start));
                partMaterials.Add(material);
            }

            if (indices.Count == 0) continue;

            var mesh = new Mesh
            {
                Name = name,
                IndexFormat = positions.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16,
                Vertices = [.. positions],
                Normals = [.. normals],
                UV = [.. uvs],
                UV2 = [.. uv2s],
                Indices = [.. indices],
            };
            mesh.SetSubMeshCount(submeshes.Count);
            for (int i = 0; i < submeshes.Count; i++) mesh.SetSubMesh(i, submeshes[i]);
            mesh.RecalculateBounds();

            parts.Add(new MountedMap.Part(name, mesh, [.. partMaterials], page, faceSolid));
        }
    }

    /// <summary>
    /// The map's overlays (info_overlay: road markings, signs, grime) as quads lifted just off the faces they lie on,
    /// lit by the first such face's lightmap. Not clipped to the faces, which is close enough for flat ones.
    /// </summary>
    private static void BuildOverlays(BspFile bsp, SourceMaterial[] materials, Lightmap[] lightmaps, List<MountedMap.Part> parts)
    {
        var groups = new Dictionary<(int Page, Material Material), Geometry>();
        foreach (Overlay overlay in bsp.Overlays)
        {
            if (overlay.TexInfo < 0 || overlay.TexInfo >= bsp.TexInfos.Length || overlay.Faces.Length == 0) continue;
            int texData = bsp.TexInfos[overlay.TexInfo].TexData;
            if (texData < 0 || texData >= materials.Length) continue;
            SourceMaterial material = materials[texData];
            if (material == null || material.NoDraw || material.Material.IsNotValid()) continue;

            int f = overlay.Faces[0];
            if (f < 0 || f >= bsp.Faces.Length) continue;
            Face face = bsp.Faces[f];
            Lightmap lightmap = lightmaps[f];
            TexInfo faceInfo = bsp.TexInfos[face.TexInfo];

            Vector3 normal = Vector3.Normalize(overlay.BasisNormal);
            Vector3 u = Vector3.Normalize(overlay.BasisU);
            Vector3 v = Vector3.Cross(normal, u);
            int page = lightmap.Has ? lightmap.Page : -1;
            if (!groups.TryGetValue((page, material.Material), out Geometry geometry)) groups[(page, material.Material)] = geometry = new Geometry();

            Float2[] uvs = [new(overlay.U0, overlay.V0), new(overlay.U0, overlay.V1), new(overlay.U1, overlay.V1), new(overlay.U1, overlay.V0)];
            int first = geometry.Positions.Count;
            for (int i = 0; i < 4; i++)
            {
                Vector3 p = overlay.Origin + u * overlay.Points[i].X + v * overlay.Points[i].Y + normal * 0.25f;
                geometry.Positions.Add(GoldSrcAssets.Position(p));
                geometry.Normals.Add(GoldSrcAssets.Direction(normal));
                geometry.UV.Add(uvs[i]);
                if (lightmap.Has)
                {
                    float s = Math.Clamp(Vector3.Dot(p, faceInfo.LightS) + faceInfo.LightOffsetS - face.LightMinS, 0f, lightmap.Width - 1);
                    float t = Math.Clamp(Vector3.Dot(p, faceInfo.LightT) + faceInfo.LightOffsetT - face.LightMinT, 0f, lightmap.Height - 1);
                    geometry.UV2.Add(new Float2((lightmap.X + 1 + s + 0.5f) / PageSize, (lightmap.Y + 1 + t + 0.5f) / PageSize));
                }
                else geometry.UV2.Add(Float2.Zero);
            }
            geometry.Indices.AddRange([(uint)first, (uint)first + 1, (uint)first + 2, (uint)first, (uint)first + 2, (uint)first + 3]);
        }

        foreach (((int page, Material material), Geometry geometry) in groups)
        {
            var mesh = new Mesh
            {
                Name = "overlays",
                IndexFormat = geometry.Positions.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16,
                Vertices = [.. geometry.Positions],
                Normals = [.. geometry.Normals],
                UV = [.. geometry.UV],
                UV2 = [.. geometry.UV2],
                Indices = [.. geometry.Indices],
            };
            mesh.RecalculateBounds();
            parts.Add(new MountedMap.Part("overlays", mesh, [material], page, false));
        }
    }

    [Prowl.Ember.ReloadIgnore] private static Shader s_skyShader;

    /// <summary>
    /// A Source 2D skybox: the faces Quake laid out and Source kept, rt +X, lf -X, bk +Y, ft -Y, up and dn, each
    /// facing in with its own s and t, in Source's axes (Prowl's direction (x, y, z) is Source's (z, -x, y)).
    /// </summary>
    private const string SkyShaderSource = """
        Shader "Slopbox/SourceSky"
        {
            Properties
            {
                _rt("rt", Texture2D) = "white" {}
                _lf("lf", Texture2D) = "white" {}
                _bk("bk", Texture2D) = "white" {}
                _ft("ft", Texture2D) = "white" {}
                _up("up", Texture2D) = "white" {}
                _dn("dn", Texture2D) = "white" {}
                intensity("Intensity", Float) = 19660.8
            }
            Surface "Sky"
            {
                GLSLPROGRAM
                uniform sampler2D _rt;
                uniform sampler2D _lf;
                uniform sampler2D _bk;
                uniform sampler2D _ft;
                uniform sampler2D _up;
                uniform sampler2D _dn;
                uniform float intensity;

                vec3 Face(sampler2D face, float s, float t)
                {
                    vec2 uv = clamp(vec2(s + 1.0, 1.0 - t) * 0.5, vec2(0.002), vec2(0.998));
                    return texture(face, uv).rgb;
                }

                vec3 Sky(vec3 direction)
                {
                    vec3 d = vec3(direction.z, -direction.x, direction.y);
                    vec3 a = abs(d);
                    vec3 c;
                    if (a.x >= a.y && a.x >= a.z)
                        c = d.x > 0.0 ? Face(_rt, -d.y / a.x, d.z / a.x) : Face(_lf, d.y / a.x, d.z / a.x);
                    else if (a.y >= a.z)
                        c = d.y > 0.0 ? Face(_bk, d.x / a.y, d.z / a.y) : Face(_ft, -d.x / a.y, d.z / a.y);
                    else
                        c = d.z > 0.0 ? Face(_up, -d.y / a.z, -d.x / a.z) : Face(_dn, -d.y / a.z, d.x / a.z);
                    return c * intensity;
                }
                ENDGLSL
            }
        }
        """;

    /// <summary>The map's 2D skybox (worldspawn's skyname), at the brightness its game shows it.</summary>
    private static Material SkyMaterial(BspFile bsp, IContent content, List<Material> owned)
    {
        string sky = bsp.Entities.FirstOrDefault(e => e.ClassName.Equals("worldspawn", StringComparison.OrdinalIgnoreCase))?.Get("skyname");
        if (string.IsNullOrWhiteSpace(sky)) return null;
        Texture2D Face(string side) => content.Texture($"skybox/{sky}{side}");
        if (s_skyShader.IsNotValid()) s_skyShader = Shader.FromSource(SkyShaderSource, "Slopbox/SourceSky.shader");
        var material = new Material(s_skyShader) { Name = $"sky {sky}" };
        int found = 0;
        foreach (string side in new[] { "rt", "lf", "bk", "ft", "up", "dn" })
        {
            Texture2D texture = Face(side);
            if (texture.IsValid()) { material.SetTexture("_" + side, texture); found++; }
        }
        if (found == 0) return null;
        material.SetFloat("intensity", MountedMap.LightScale);
        owned.Add(material);
        return material;
    }

    /// <summary>
    /// A displacement: a grid of (2^power + 1)² points spread over its four sided base face, starting from the
    /// corner nearest its start position, each pushed out along its own direction. Texture and lightmap
    /// coordinates come from the flat base, as the game does.
    /// </summary>
    private static void BuildDisplacement(BspFile bsp, in Face face, in DispInfo disp, Vector3 faceNormal, Geometry geometry,
        Action<Vector3, Vector3, Vector3> addVertex)
    {
        Span<Vector3> corners = stackalloc Vector3[4];
        int start = 0;
        float best = float.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            corners[i] = bsp.FaceVertex(face, i);
            float d = Vector3.DistanceSquared(corners[i], disp.StartPosition);
            if (d < best)
            {
                best = d;
                start = i;
            }
        }
        Span<Vector3> c = stackalloc Vector3[4];
        for (int i = 0; i < 4; i++) c[i] = corners[(start + i) % 4];

        int n = (1 << Math.Clamp(disp.Power, 0, 4)) + 1;
        if (disp.VertStart < 0 || disp.VertStart + n * n > bsp.DispVerts.Length) return;

        var positions = new Vector3[n * n];
        var bases = new Vector3[n * n];
        for (int y = 0; y < n; y++)
        {
            float ty = y / (float)(n - 1);
            Vector3 a = Vector3.Lerp(c[0], c[1], ty);
            Vector3 b = Vector3.Lerp(c[3], c[2], ty);
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                Vector3 basePosition = Vector3.Lerp(a, b, x / (float)(n - 1));
                DispVert vert = bsp.DispVerts[disp.VertStart + i];
                bases[i] = basePosition;
                positions[i] = basePosition + vert.Direction * vert.Distance;
            }
        }

        // Which way round the grid's triangles face, from the flat base against the face's normal.
        Vector3 gridNormal = Vector3.Cross(bases[n] - bases[0], bases[1] - bases[0]);
        bool flip = Vector3.Dot(gridNormal, faceNormal) > 0f;

        // Smooth normals from the neighbouring points.
        var normals = new Vector3[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                Vector3 dx = positions[y * n + Math.Min(x + 1, n - 1)] - positions[y * n + Math.Max(x - 1, 0)];
                Vector3 dy = positions[Math.Min(y + 1, n - 1) * n + x] - positions[Math.Max(y - 1, 0) * n + x];
                Vector3 normal = Vector3.Cross(dy, dx);
                if (Vector3.Dot(normal, faceNormal) < 0f) normal = -normal;
                normals[y * n + x] = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : faceNormal;
            }
        }

        int first = geometry.Positions.Count;
        for (int i = 0; i < n * n; i++) addVertex(positions[i], bases[i], normals[i]);

        void Triangle(int a, int b, int d)
        {
            geometry.Indices.Add((uint)(first + a));
            if (flip)
            {
                geometry.Indices.Add((uint)(first + d));
                geometry.Indices.Add((uint)(first + b));
            }
            else
            {
                geometry.Indices.Add((uint)(first + b));
                geometry.Indices.Add((uint)(first + d));
            }
        }

        for (int y = 0; y < n - 1; y++)
        {
            for (int x = 0; x < n - 1; x++)
            {
                int i00 = y * n + x, i10 = y * n + x + 1, i01 = (y + 1) * n + x, i11 = (y + 1) * n + x + 1;
                // The diagonal alternates, as the game's tessellation does.
                if (((x + y) & 1) == 0)
                {
                    Triangle(i00, i01, i11);
                    Triangle(i00, i11, i10);
                }
                else
                {
                    Triangle(i00, i01, i10);
                    Triangle(i10, i01, i11);
                }
            }
        }
    }

    // ----------------------------------------------------------------
    //  Lightmaps
    // ----------------------------------------------------------------

    private static bool HasLightmap(BspFile bsp, in Face face)
    {
        if (face.LightOffset < 0 || face.Style(0) == 255 || face.TexInfo < 0 || face.TexInfo >= bsp.TexInfos.Length) return false;
        TexInfo texInfo = bsp.TexInfos[face.TexInfo];
        return (texInfo.Flags & (TexInfo.NoLight | TexInfo.Sky | TexInfo.Sky2D | TexInfo.NoDraw)) == 0;
    }

    /// <summary>
    /// The faces' lightmaps packed into pages, and for bumped faces their three directional lightmaps in pages of
    /// their own laid out the same, so one lightmap UV reads all four.
    /// </summary>
    private static (Lightmap[] Lightmaps, List<Texture2D> Pages, List<Texture2D>[] BumpPages) PackLightmaps(BspFile bsp)
    {
        var lightmaps = new Lightmap[bsp.Faces.Length];
        for (int f = 0; f < bsp.Faces.Length; f++)
        {
            Face face = bsp.Faces[f];
            if (!HasLightmap(bsp, face)) continue;
            int width = face.LightWidth + 1, height = face.LightHeight + 1;
            if (width <= 0 || height <= 0 || width + 2 > PageSize || height + 2 > PageSize) continue;
            lightmaps[f] = new Lightmap { Width = width, Height = height, Has = true };
        }

        var order = Enumerable.Range(0, lightmaps.Length).Where(f => lightmaps[f].Has)
            .OrderByDescending(f => lightmaps[f].Height).ThenByDescending(f => lightmaps[f].Width).ToList();

        var pageData = new List<byte[]>();
        var bumpData = new[] { new List<byte[]>(), new List<byte[]>(), new List<byte[]>() };
        int page = -1, x = 0, y = 0, shelf = 0;
        foreach (int f in order)
        {
            int w = lightmaps[f].Width + 2, h = lightmaps[f].Height + 2;
            if (page < 0 || x + w > PageSize)
            {
                x = 0;
                y += shelf;
                shelf = 0;
            }
            if (page < 0 || y + h > PageSize)
            {
                pageData.Add(new byte[PageSize * PageSize * 4]);
                foreach (List<byte[]> basis in bumpData) basis.Add(new byte[PageSize * PageSize * 4]);
                page++;
                x = y = shelf = 0;
            }

            lightmaps[f].Page = page;
            lightmaps[f].X = x;
            lightmaps[f].Y = y;
            WriteLuxels(bsp, bsp.Faces[f], lightmaps[f], pageData[page]);
            for (int b = 0; b < 3; b++) WriteLuxels(bsp, bsp.Faces[f], lightmaps[f], bumpData[b][page], b + 1);

            x += w;
            shelf = Math.Max(shelf, h);
        }

        var pages = new List<Texture2D>(pageData.Count);
        for (int p = 0; p < pageData.Count; p++)
        {
            var texture = new Texture2D(PageSize, PageSize, TextureFormat.RGBA8, mipmaps: false, srgb: false) { Name = $"Lightmap {p}" };
            texture.SetData(new Memory<byte>(pageData[p]));
            pages.Add(texture);
        }
        var bumpPages = new List<Texture2D>[3];
        for (int b = 0; b < 3; b++)
        {
            bumpPages[b] = new List<Texture2D>(pageData.Count);
            for (int p = 0; p < pageData.Count; p++)
            {
                var texture = new Texture2D(PageSize, PageSize, TextureFormat.RGBA8, mipmaps: false, srgb: false) { Name = $"Bumped Lightmap {p}.{b + 1}" };
                texture.SetData(new Memory<byte>(bumpData[b][p]));
                bumpPages[b].Add(texture);
            }
        }
        return (lightmaps, pages, bumpPages);
    }

    /// <summary>
    /// The map's leaf ambient samples as probes, with the map's point, spot and surface lights added: the light the
    /// game gives a model there from each side. The game lights models from the ambient cube under them plus the
    /// world lights their leaf can see, worked out as they move; here those lights are added once per probe, which is
    /// what the game does with all but the few strongest. Maps compiled without samples give none. The 3D skybox's are
    /// left out: it is drawn around the map from far away, and nothing moves in it.
    /// </summary>
    /// <summary>The most probes a map gets.</summary>
    private const int ProbeBudget = 1000;

    private static List<MountedMap.Probe> Probes(BspFile bsp)
    {
        var positions = new List<Vector3>(bsp.Ambient.Length);
        var lights = new List<Vector3>(bsp.Ambient.Length * 6);
        byte[] light = bsp.AmbientLighting;
        float brightest = 0f;
        int skyArea = SkyboxArea(bsp);
        var lightsSeen = new Dictionary<int, int[]>();
        Span<Vector3> sides = stackalloc Vector3[6];
        foreach (AmbientSample sample in bsp.Ambient)
        {
            if (sample.Leaf < 0) continue;
            Leaf leaf = bsp.Leaves[sample.Leaf];
            if ((leaf.Contents & Leaf.Solid) != 0 || leaf.Area == skyArea) continue;

            // Stored +X, -X, +Y, -Y, +Z, -Z in Source's axes. Unlike luxels, the cube is not stored over 255.
            for (int side = 0; side < 6; side++)
            {
                int src = sample.CubeOffset + side * 4;
                float scale = MathF.Pow(2f, (sbyte)light[src + 3]);
                sides[side] = new Vector3(light[src], light[src + 1], light[src + 2]) * scale;
                brightest = MathF.Max(brightest, MathF.Max(sides[side].X, MathF.Max(sides[side].Y, sides[side].Z)));
            }

            if (!lightsSeen.TryGetValue(leaf.Cluster, out int[] seen))
                lightsSeen[leaf.Cluster] = seen = LightsSeenFrom(bsp, leaf.Cluster);
            foreach (int l in seen)
                AddWorldLight(bsp.WorldLights[l], sample.Position, sides);

            positions.Add(sample.Position);
            for (int i = 0; i < 6; i++)
                lights.Add(new Vector3(Curve(sides[i].X), Curve(sides[i].Y), Curve(sides[i].Z)));
        }

        // Decompiled and rebuilt maps keep the lump but leave it black.
        if (brightest < 0.01f || positions.Count == 0) return [];

        var probes = new List<MountedMap.Probe>(ProbeBudget);
        ReadOnlySpan<int> order = [3, 2, 4, 5, 0, 1];
        foreach (int[] group in SplitByLight(positions, lights, ProbeBudget))
        {
            Vector3 position = Vector3.Zero;
            var summed = new Vector3[6];
            foreach (int i in group)
            {
                position += positions[i];
                for (int side = 0; side < 6; side++) summed[side] += lights[i * 6 + side];
            }
            // Prowl's +X is Source's -Y, +Y its +Z and +Z its +X.
            var prowl = new Float3[6];
            for (int side = 0; side < 6; side++)
            {
                Vector3 c = summed[order[side]] / group.Length;
                prowl[side] = new Float3(c.X, c.Y, c.Z);
            }
            probes.Add(new MountedMap.Probe(GoldSrcAssets.Position(position / group.Length), prowl));
        }
        return probes;
    }

    /// <summary>
    /// Groups the samples into at most <paramref name="budget"/> boxes, each standing for one probe: the box whose
    /// samples' light strays furthest from their average is halved along its longest side, again and again. Evenly
    /// lit space ends up in a few big boxes; doorways, shade edges and the lit sides of walls keep their detail.
    /// </summary>
    private static List<int[]> SplitByLight(List<Vector3> positions, List<Vector3> lights, int budget)
    {
        float Error(int[] group)
        {
            Span<Vector3> mean = stackalloc Vector3[6];
            foreach (int i in group)
                for (int s = 0; s < 6; s++) mean[s] += lights[i * 6 + s];
            for (int s = 0; s < 6; s++) mean[s] /= group.Length;
            float error = 0f;
            foreach (int i in group)
                for (int s = 0; s < 6; s++) error += (lights[i * 6 + s] - mean[s]).LengthSquared();
            return error;
        }

        var queue = new PriorityQueue<int[], float>();
        int[] all = Enumerable.Range(0, positions.Count).ToArray();
        queue.Enqueue(all, -Error(all));
        var done = new List<int[]>();
        while (queue.Count > 0 && queue.Count + done.Count < budget)
        {
            int[] group = queue.Dequeue();
            if (group.Length < 2) { done.Add(group); continue; }

            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (int i in group) { min = Vector3.Min(min, positions[i]); max = Vector3.Max(max, positions[i]); }
            Vector3 size = max - min;
            int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            float Along(int i) => axis == 0 ? positions[i].X : axis == 1 ? positions[i].Y : positions[i].Z;
            float middle = axis == 0 ? (min.X + max.X) / 2 : axis == 1 ? (min.Y + max.Y) / 2 : (min.Z + max.Z) / 2;

            int[] low = group.Where(i => Along(i) < middle).ToArray();
            int[] high = group.Where(i => Along(i) >= middle).ToArray();
            if (low.Length == 0 || high.Length == 0) { done.Add(group); continue; }
            queue.Enqueue(low, -Error(low));
            queue.Enqueue(high, -Error(high));
        }
        while (queue.Count > 0) done.Add(queue.Dequeue());
        return done;
    }

    /// <summary>The point, spot and surface lights in clusters that <paramref name="cluster"/> can see, as the game picks them.</summary>
    private static int[] LightsSeenFrom(BspFile bsp, int cluster)
    {
        byte[] visible = bsp.Visible(cluster);
        var seen = new List<int>();
        for (int i = 0; i < bsp.WorldLights.Length; i++)
        {
            WorldLight wl = bsp.WorldLights[i];
            if (wl.Type is not (WorldLight.Point or WorldLight.Spot or WorldLight.Surface)) continue;
            if (visible != null && wl.Cluster >= 0 && (wl.Cluster >> 3 >= visible.Length || (visible[wl.Cluster >> 3] & (1 << (wl.Cluster & 7))) == 0)) continue;
            seen.Add(i);
        }
        return [.. seen];
    }

    private static readonly Vector3[] s_axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

    /// <summary>
    /// A world light's light at a point, onto the sides facing it, falling off as the game's does: by its constant,
    /// linear and quadratic terms to its radius, a spot's between its cones, and a surface light's by distance squared
    /// and the angle it is seen at.
    /// </summary>
    private static void AddWorldLight(in WorldLight wl, Vector3 point, Span<Vector3> sides)
    {
        Vector3 toLight = wl.Origin - point;
        float distance = toLight.Length();
        if (distance < 1f) distance = 1f;
        if (wl.Radius > 0f && distance > wl.Radius) return;
        Vector3 direction = toLight / distance;

        float ratio;
        if (wl.Type == WorldLight.Surface)
        {
            float facing = -Vector3.Dot(direction, wl.Normal);
            if (facing <= 0f) return;
            ratio = facing / (distance * distance);
        }
        else
        {
            float falloff = wl.Constant + wl.Linear * distance + wl.Quadratic * distance * distance;
            if (falloff <= 0f) return;
            ratio = 1f / falloff;
            if (wl.Type == WorldLight.Spot)
            {
                float dot = -Vector3.Dot(direction, wl.Normal);
                if (dot <= wl.StopDot2) return;
                if (dot <= wl.StopDot) ratio *= (dot - wl.StopDot2) / (wl.StopDot - wl.StopDot2);
                if (wl.Exponent > 0f && wl.Exponent != 1f) ratio *= MathF.Pow(dot, wl.Exponent);
            }
        }

        Vector3 light = wl.Intensity * ratio;
        if (MathF.Max(light.X, MathF.Max(light.Y, light.Z)) < 1e-4f) return;
        for (int i = 0; i < 6; i++)
        {
            float d = Vector3.Dot(direction, s_axes[i]);
            if (d > 0f) sides[i] += light * d;
        }
    }

    /// <summary>The map's sun, from its sky light: the way its light travels in the map's space and its colour, or null.</summary>
    private static (Float3 Direction, Float3 Light)? Sun(BspFile bsp)
    {
        foreach (WorldLight wl in bsp.WorldLights)
        {
            if (wl.Type != WorldLight.Sky) continue;
            return (GoldSrcAssets.Direction(wl.Normal), new Float3(wl.Intensity.X, wl.Intensity.Y, wl.Intensity.Z));
        }
        return null;
    }

    /// <summary>The area the sky_camera is in, which is the 3D skybox's, or -1 if the map has none.</summary>
    private static int SkyboxArea(BspFile bsp)
    {
        foreach (Entity entity in bsp.Entities)
        {
            if (!entity.ClassName.Equals("sky_camera", StringComparison.OrdinalIgnoreCase)) continue;
            int leaf = bsp.LeafAt(entity.GetVector("origin"));
            return leaf >= 0 && leaf < bsp.Leaves.Length ? bsp.Leaves[leaf].Area : -1;
        }
        return -1;
    }

    /// <summary>
    /// A luxel's light, every style added so switchable lights show on. Faces lit for bump mapping keep four
    /// lightmaps per style, the first being the plain one.
    /// </summary>
    /// <param name="basis">0 for the plain lightmap, 1 to 3 for a bumped face's directional ones (the plain one otherwise).</param>
    private static Float3 Luxel(BspFile bsp, in Face face, int luxel, int luxels, int basis = 0)
    {
        byte[] light = bsp.Lighting;
        bool bumped = (bsp.TexInfos[face.TexInfo].Flags & TexInfo.BumpLight) != 0;
        int perStyle = bumped ? 4 : 1;
        Float3 sum = Float3.Zero;
        for (int s = 0; s < 4; s++)
        {
            if (face.Style(s) == 255) break;
            int src = face.LightOffset + ((s * perStyle + (bumped ? basis : 0)) * luxels + luxel) * 4;
            if (src + 3 >= light.Length) break;
            float scale = MathF.Pow(2f, (sbyte)light[src + 3]) / 255f;
            sum += new Float3(light[src] * scale, light[src + 1] * scale, light[src + 2] * scale);
        }
        return new Float3(Curve(sum.X), Curve(sum.Y), Curve(sum.Z));
    }

    private static float Curve(float light) => Math.Clamp(light, 0f, s_maxLight);

    private static void WriteLuxels(BspFile bsp, in Face face, in Lightmap lightmap, byte[] page, int basis = 0)
    {
        int luxels = lightmap.Width * lightmap.Height;
        for (int ly = -1; ly <= lightmap.Height; ly++)
        {
            for (int lx = -1; lx <= lightmap.Width; lx++)
            {
                // The border repeats the nearest edge luxel, so filtering never reaches a neighbour.
                int sx = Math.Clamp(lx, 0, lightmap.Width - 1), sy = Math.Clamp(ly, 0, lightmap.Height - 1);
                Float3 c = Luxel(bsp, face, sy * lightmap.Width + sx, luxels, basis);

                // RGBM: colour times alpha times 8.
                float m = Math.Clamp(MathF.Max(c.X, MathF.Max(c.Y, c.Z)) / 8f, 1f / 255f, 1f);
                m = MathF.Ceiling(m * 255f) / 255f;
                float scale = 1f / (m * 8f);

                int dst = ((lightmap.Y + 1 + ly) * PageSize + (lightmap.X + 1 + lx)) * 4;
                page[dst] = ToByte(c.X * scale);
                page[dst + 1] = ToByte(c.Y * scale);
                page[dst + 2] = ToByte(c.Z * scale);
                page[dst + 3] = (byte)(m * 255f);
            }
        }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
