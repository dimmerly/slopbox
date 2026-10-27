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
            if (missing == null)
            {
                missing = Materials.Lit(new Color(0.5f, 0.5f, 0.5f, 1f), 0f, 1f);
                owned.Add(missing);
            }
            materials[i] = new SourceMaterial { Name = bsp.TextureNames[i], Material = missing };
        }

        (Lightmap[] lightmaps, List<Texture2D> pages) = PackLightmaps(bsp);

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
            BuildBrushModel(bsp, bsp.Models[modelIndex], transform, materials, lightmaps, $"{className} {e}", solid, parts, ref min, ref max);
        }

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
        Lightmap[] lightmaps, string name, bool solid, List<MountedMap.Part> parts, ref Float3 min, ref Float3 max)
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
            if (!byMaterial.TryGetValue(material.Material, out Geometry geometry)) byMaterial[material.Material] = geometry = new Geometry();

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

    private static (Lightmap[] Lightmaps, List<Texture2D> Pages) PackLightmaps(BspFile bsp)
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
                page++;
                x = y = shelf = 0;
            }

            lightmaps[f].Page = page;
            lightmaps[f].X = x;
            lightmaps[f].Y = y;
            WriteLuxels(bsp, bsp.Faces[f], lightmaps[f], pageData[page]);

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
        return (lightmaps, pages);
    }

    /// <summary>
    /// The map's leaf ambient samples as probes: the light the game gives a model there from each side, which vrad
    /// computed for exactly that. Maps compiled without them give none.
    /// </summary>
    private static List<MountedMap.Probe> Probes(BspFile bsp)
    {
        var probes = new List<MountedMap.Probe>(bsp.Ambient.Length);
        byte[] light = bsp.AmbientLighting;
        float brightest = 0f;
        foreach (AmbientSample sample in bsp.Ambient)
        {
            // Stored +X, -X, +Y, -Y, +Z, -Z in Source's axes; Prowl's +X is Source's -Y, +Y its +Z and +Z its +X.
            var source = new Float3[6];
            for (int side = 0; side < 6; side++)
            {
                int src = sample.CubeOffset + side * 4;
                float scale = MathF.Pow(2f, (sbyte)light[src + 3]) / 255f;
                source[side] = new Float3(Curve(light[src] * scale), Curve(light[src + 1] * scale), Curve(light[src + 2] * scale));
                brightest = MathF.Max(brightest, MathF.Max(source[side].X, MathF.Max(source[side].Y, source[side].Z)));
            }
            probes.Add(new MountedMap.Probe(GoldSrcAssets.Position(sample.Position),
                [source[3], source[2], source[4], source[5], source[0], source[1]]));
        }

        // Decompiled and rebuilt maps keep the lump but leave it black.
        return brightest < 0.01f ? [] : probes;
    }

    /// <summary>
    /// A luxel's light, every style added so switchable lights show on. Faces lit for bump mapping keep four
    /// lightmaps per style, the first being the plain one.
    /// </summary>
    private static Float3 Luxel(BspFile bsp, in Face face, int luxel, int luxels)
    {
        byte[] light = bsp.Lighting;
        bool bumped = (bsp.TexInfos[face.TexInfo].Flags & TexInfo.BumpLight) != 0;
        int perStyle = bumped ? 4 : 1;
        Float3 sum = Float3.Zero;
        for (int s = 0; s < 4; s++)
        {
            if (face.Style(s) == 255) break;
            int src = face.LightOffset + (s * perStyle * luxels + luxel) * 4;
            if (src + 3 >= light.Length) break;
            float scale = MathF.Pow(2f, (sbyte)light[src + 3]) / 255f;
            sum += new Float3(light[src] * scale, light[src + 1] * scale, light[src + 2] * scale);
        }
        return new Float3(Curve(sum.X), Curve(sum.Y), Curve(sum.Z));
    }

    private static float Curve(float light) => Math.Clamp(light, 0f, s_maxLight);

    private static void WriteLuxels(BspFile bsp, in Face face, in Lightmap lightmap, byte[] page)
    {
        int luxels = lightmap.Width * lightmap.Height;
        for (int ly = -1; ly <= lightmap.Height; ly++)
        {
            for (int lx = -1; lx <= lightmap.Width; lx++)
            {
                // The border repeats the nearest edge luxel, so filtering never reaches a neighbour.
                int sx = Math.Clamp(lx, 0, lightmap.Width - 1), sy = Math.Clamp(ly, 0, lightmap.Height - 1);
                Float3 c = Luxel(bsp, face, sy * lightmap.Width + sx, luxels);

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
