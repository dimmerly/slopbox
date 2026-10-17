using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Slopbox.Mounting.GoldSrc.Bsp;
using Slopbox.Mounting.GoldSrc.Mdl;
using Vector3 = System.Numerics.Vector3;
using Plane = Slopbox.Mounting.GoldSrc.Bsp.Plane;

namespace Slopbox.Mounting.GoldSrc;

/// <summary>
/// A GoldSrc map (.bsp) as a <see cref="MountedMap"/>: the world and every visible brush entity, with the map's
/// own lightmaps packed into RGBM pages for Prowl's baked lighting. Textures come from the map or the mount's WADs.
/// </summary>
internal sealed class MapLoader(string fullPath) : ResourceLoader<GameMount>
{
    /// <summary>Lightmap luxels per side of a page.</summary>
    private const int PageSize = 2048;

    /// <summary>World units per lightmap luxel.</summary>
    private const float LuxelSize = 16f;

    /// <summary>
    /// How a lightmap byte becomes linear light: (byte / 255) ^ <see cref="LightCurve"/> * <see cref="Brightness"/>.
    /// The stored light is dim; the game lifted it with its light gamma (2.5) and drew it overbright, doubled.
    /// Lifting by 1 / 2.5 in display space is about 0.75 once made linear.
    /// </summary>
    private const float LightCurve = 0.75f, Brightness = 2f;

    /// <summary>Tool textures that are never drawn.</summary>
    private static readonly HashSet<string> s_hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "sky", "aaatrigger", "clip", "origin", "null", "hint", "skip", "bevel", "black_hidden", "nodraw",
    };

    /// <summary>Brush entities that are drawn but never block.</summary>
    private static readonly HashSet<string> s_nonSolid = new(StringComparer.OrdinalIgnoreCase)
    {
        "func_illusionary", "func_water", "func_ladder", "func_monsterclip", "func_mortar_field",
    };

    // A face's lightmap rectangle: where its luxels start in texture space, its size in luxels, and where it was packed.
    private struct Lightmap
    {
        public float UMin, VMin;
        public int Width, Height;
        public int Page, X, Y;
        public bool Has;
    }

    protected override object Load()
    {
        var bsp = new BspFile(File.ReadAllBytes(fullPath));

        var owned = new List<Texture2D>();
        Texture2D[] textures = LoadTextures(bsp, owned);
        (Lightmap[] lightmaps, List<Texture2D> pages) = PackLightmaps(bsp);

        var materials = new Dictionary<(int Tex, int Mode, int Amount), Material>();
        var parts = new List<MountedMap.Part>();
        var spawns = new List<MountedMap.SpawnPoint>();
        var models = new List<MountedMap.PlacedModel>();
        Float3 min = new(float.MaxValue), max = new(float.MinValue);

        Material MaterialFor(int tex, int mode, int amount)
        {
            if (materials.TryGetValue((tex, mode, amount), out Material material)) return material;

            string name = bsp.Textures[tex].Name;
            LightingFlags flags = name.StartsWith('{') ? LightingFlags.Masked : 0;
            if (mode == 5) flags |= LightingFlags.Additive;
            material = GoldSrcAssets.Material(name, textures[tex], flags);
            material.SetFloat("_Roughness", 1f);

            // The compiler keeps only faces seen from inside, so from outside a map would be nothing at all. Drawn
            // from both sides, it shows as a shell of its rooms.
            material.Surface ??= new SurfaceSettings(SurfaceType.Opaque, SurfaceBlend.Alpha, SurfaceCull.Off);

            // Water and see through brushes blend with what is behind.
            float alpha = mode is 1 or 2 ? amount / 255f : IsLiquid(name) ? 0.6f : 1f;
            if (alpha < 1f && mode != 5)
            {
                material.Surface = new SurfaceSettings(SurfaceType.Transparent, SurfaceBlend.Alpha, SurfaceCull.Off);
                material.SetColor("_MainColor", new Color(1f, 1f, 1f, alpha));
            }

            materials[(tex, mode, amount)] = material;
            return material;
        }

        for (int e = 0; e < bsp.Entities.Count; e++)
        {
            Entity entity = bsp.Entities[e];
            string className = entity.ClassName;

            if (className.StartsWith("info_player_start", StringComparison.OrdinalIgnoreCase)
                || className.Equals("info_player_deathmatch", StringComparison.OrdinalIgnoreCase))
            {
                // The origin is the middle of the player's 72 unit hull; Prowl's player stands on its position.
                Vector3 origin = entity.GetVector("origin") - new Vector3(0, 0, 36);
                float yaw = entity.Get("angles") != null ? entity.GetVector("angles").Y : entity.GetFloat("angle");
                spawns.Add(new MountedMap.SpawnPoint(GoldSrcAssets.Position(origin), -yaw));
                continue;
            }

            if (MapEntities.Place(entity) is { } placed)
            {
                models.Add(placed);
                continue;
            }

            int modelIndex = className.Equals("worldspawn", StringComparison.OrdinalIgnoreCase) ? 0 : entity.BrushModel;
            if (modelIndex < 0 || modelIndex >= bsp.Models.Length) continue;
            if (className.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase)) continue;

            int mode = entity.GetInt("rendermode");
            int amount = entity.GetInt("renderamt", 255);
            if (mode is 1 or 2 or 5 && amount == 0) continue;   // fully faded out
            bool solid = !s_nonSolid.Contains(className);
            Vector3 offset = entity.GetVector("origin");

            BuildEntity(bsp, bsp.Models[modelIndex], offset, lightmaps, $"{className} {e}", solid, MaterialFor, mode, amount, parts, ref min, ref max);
        }

        if (spawns.Count == 0) spawns.Add(new MountedMap.SpawnPoint(Float3.Zero, 0f));

        var bounds = min.X <= max.X ? new AABB(min, max) : new AABB(new Float3(-1f), new Float3(1f));
        return new MountedMap(Name, parts, pages, spawns, models, bounds, [.. materials.Values], owned)
        {
            Light = new LightSampler(bsp, lightmaps),
        };
    }

    private static bool IsLiquid(string name) => name.StartsWith('!') || name.StartsWith("water", StringComparison.OrdinalIgnoreCase);

    private static bool IsHidden(string name)
        => s_hidden.Contains(name) || name.StartsWith("sky", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Makes the meshes of one brush model: a part per lightmap page and solidity, with a submesh per material.
    /// Liquids never collide.
    /// </summary>
    private static void BuildEntity(BspFile bsp, BrushModel model, Vector3 offset, Lightmap[] lightmaps, string name, bool solid,
        Func<int, int, int, Material> materialFor, int mode, int amount, List<MountedMap.Part> parts, ref Float3 min, ref Float3 max)
    {
        // (page, solid) -> material -> geometry
        var groups = new Dictionary<(int Page, bool Solid), Dictionary<Material, Geometry>>();

        for (int f = model.FirstFace; f < model.FirstFace + model.FaceCount && f < bsp.Faces.Length; f++)
        {
            Face face = bsp.Faces[f];
            if (face.EdgeCount < 3) continue;

            TexInfo texInfo = bsp.TexInfos[face.TexInfo];
            int tex = Math.Clamp(texInfo.MipTex, 0, bsp.Textures.Length - 1);
            MipTexture mip = bsp.Textures[tex];
            if (IsHidden(mip.Name)) continue;

            Lightmap lightmap = lightmaps[f];
            int page = lightmap.Has ? lightmap.Page : -1;
            bool faceSolid = solid && !IsLiquid(mip.Name);
            Material material = materialFor(tex, mode, amount);

            if (!groups.TryGetValue((page, faceSolid), out var byMaterial)) groups[(page, faceSolid)] = byMaterial = new();
            if (!byMaterial.TryGetValue(material, out Geometry geometry)) byMaterial[material] = geometry = new Geometry();

            Plane plane = bsp.Planes[face.Plane];
            Float3 normal = GoldSrcAssets.Direction(face.PlaneSide == 0 ? plane.Normal : -plane.Normal);

            int first = geometry.Positions.Count;
            for (int i = 0; i < face.EdgeCount; i++)
            {
                Vector3 p = bsp.FaceVertex(face, i);
                float u = Vector3.Dot(p, texInfo.S) + texInfo.OffsetS;
                float v = Vector3.Dot(p, texInfo.T) + texInfo.OffsetT;

                Float3 position = GoldSrcAssets.Position(p + offset);
                geometry.Positions.Add(position);
                geometry.Normals.Add(normal);
                geometry.UV.Add(new Float2(u / mip.Width, v / mip.Height));
                geometry.UV2.Add(lightmap.Has
                    ? new Float2(
                        (lightmap.X + 1 + (u - lightmap.UMin) / LuxelSize + 0.5f) / PageSize,
                        (lightmap.Y + 1 + (v - lightmap.VMin) / LuxelSize + 0.5f) / PageSize)
                    : Float2.Zero);

                min = Maths.Min(min, position);
                max = Maths.Max(max, position);
            }

            // A convex polygon as a fan. GoldSrc winds faces clockwise from the front; mirroring into Prowl's space
            // makes that the other way round, which is what Prowl draws as the front.
            for (int i = 1; i < face.EdgeCount - 1; i++)
            {
                geometry.Indices.Add((uint)first);
                geometry.Indices.Add((uint)(first + i));
                geometry.Indices.Add((uint)(first + i + 1));
            }
        }

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

            var mesh = new Prowl.Runtime.Resources.Mesh
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

    private sealed class Geometry
    {
        public readonly List<Float3> Positions = new();
        public readonly List<Float3> Normals = new();
        public readonly List<Float2> UV = new();
        public readonly List<Float2> UV2 = new();
        public readonly List<uint> Indices = new();
    }

    // ----------------------------------------------------------------
    //  Textures
    // ----------------------------------------------------------------

    /// <summary>Each of the map's textures: its own, or the WAD texture of the same name, or a grey stand in.</summary>
    private Texture2D[] LoadTextures(BspFile bsp, List<Texture2D> owned)
    {
        var textures = new Texture2D[bsp.Textures.Length];
        Texture2D missing = null;

        for (int i = 0; i < textures.Length; i++)
        {
            MipTexture mip = bsp.Textures[i];
            if (mip.Rgba != null)
            {
                textures[i] = GoldSrcAssets.Texture(mip.Name, mip.Width, mip.Height, mip.Rgba);
                owned.Add(textures[i]);
                continue;
            }

            textures[i] = Host?.FindWadTexture(mip.Name)?.GetOrCreate<Texture2D>();
            if (textures[i].IsValid()) continue;

            if (missing == null)
            {
                missing = GoldSrcAssets.Texture("missing", 1, 1, [128, 128, 128, 255], mipmaps: false);
                owned.Add(missing);
            }
            textures[i] = missing;
        }

        return textures;
    }

    // ----------------------------------------------------------------
    //  Lightmaps
    // ----------------------------------------------------------------

    /// <summary>
    /// Packs every face's lightmap into RGBM pages, tallest first along shelves, each with a one luxel border copied
    /// from its edge so filtering never reaches a neighbour. Every light style the face has is added together, so
    /// switchable lights show switched on.
    /// </summary>
    private static (Lightmap[] Lightmaps, List<Texture2D> Pages) PackLightmaps(BspFile bsp)
    {
        var lightmaps = new Lightmap[bsp.Faces.Length];

        for (int f = 0; f < bsp.Faces.Length; f++)
        {
            Face face = bsp.Faces[f];
            TexInfo texInfo = bsp.TexInfos[face.TexInfo];
            if (face.LightmapOffset < 0 || (texInfo.Flags & TexInfo.Special) != 0 || face.EdgeCount < 3) continue;

            float umin = float.MaxValue, vmin = float.MaxValue, umax = float.MinValue, vmax = float.MinValue;
            for (int i = 0; i < face.EdgeCount; i++)
            {
                Vector3 p = bsp.FaceVertex(face, i);
                float u = Vector3.Dot(p, texInfo.S) + texInfo.OffsetS;
                float v = Vector3.Dot(p, texInfo.T) + texInfo.OffsetT;
                umin = MathF.Min(umin, u); umax = MathF.Max(umax, u);
                vmin = MathF.Min(vmin, v); vmax = MathF.Max(vmax, v);
            }

            float minU = MathF.Floor(umin / LuxelSize), minV = MathF.Floor(vmin / LuxelSize);
            int width = (int)(MathF.Ceiling(umax / LuxelSize) - minU) + 1;
            int height = (int)(MathF.Ceiling(vmax / LuxelSize) - minV) + 1;
            if (width + 2 > PageSize || height + 2 > PageSize) continue;

            lightmaps[f] = new Lightmap { UMin = minU * LuxelSize, VMin = minV * LuxelSize, Width = width, Height = height, Has = true };
        }

        // Shelf packing, tallest first.
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

    private static readonly float[] s_linear = BuildLinearTable();

    private static float[] BuildLinearTable()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++) table[i] = MathF.Pow(i / 255f, LightCurve) * Brightness;
        return table;
    }

    private static void WriteLuxels(BspFile bsp, in Face face, in Lightmap lightmap, byte[] page)
    {
        int luxels = lightmap.Width * lightmap.Height;
        byte[] light = bsp.Lighting;

        for (int ly = -1; ly <= lightmap.Height; ly++)
        {
            for (int lx = -1; lx <= lightmap.Width; lx++)
            {
                // The border repeats the nearest edge luxel.
                int sx = Math.Clamp(lx, 0, lightmap.Width - 1), sy = Math.Clamp(ly, 0, lightmap.Height - 1);
                int luxel = sy * lightmap.Width + sx;

                float r = 0f, g = 0f, b = 0f;
                for (int s = 0, block = 0; s < 4; s++)
                {
                    if (face.Style(s) == 255) break;
                    int src = face.LightmapOffset + (block * luxels + luxel) * 3;
                    block++;
                    if (src + 2 >= light.Length) continue;
                    r += s_linear[light[src]];
                    g += s_linear[light[src + 1]];
                    b += s_linear[light[src + 2]];
                }

                // RGBM: colour times alpha times 8.
                float m = Math.Clamp(MathF.Max(r, MathF.Max(g, b)) / 8f, 1f / 255f, 1f);
                m = MathF.Ceiling(m * 255f) / 255f;
                float scale = 1f / (m * 8f);

                int dst = ((lightmap.Y + 1 + ly) * PageSize + (lightmap.X + 1 + lx)) * 4;
                page[dst] = ToByte(r * scale);
                page[dst + 1] = ToByte(g * scale);
                page[dst + 2] = ToByte(b * scale);
                page[dst + 3] = (byte)(m * 255f);
            }
        }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    /// <summary>
    /// The light of the floor under a point, as the game lights models (R_LightPoint): straight down to the nearest
    /// upward facing lit face of the world, then that face's luxel there, every light style added.
    /// </summary>
    private sealed class LightSampler : IMapLight
    {
        /// <summary>How far down a floor is looked for, in game units.</summary>
        private const float Reach = 2048f;

        private readonly BspFile _bsp;
        private readonly Lightmap[] _lightmaps;
        private readonly List<Floor> _floors = new();

        // An upward facing face of the world, flattened for the downward test.
        private sealed class Floor
        {
            public int Face;
            public Vector3 Normal;
            public float Distance;
            public float MinX, MinY, MaxX, MaxY;
            public System.Numerics.Vector2[] Corners;
        }

        public LightSampler(BspFile bsp, Lightmap[] lightmaps)
        {
            _bsp = bsp;
            _lightmaps = lightmaps;

            BrushModel world = bsp.Models.Length > 0 ? bsp.Models[0] : default;
            for (int f = world.FirstFace; f < world.FirstFace + world.FaceCount && f < bsp.Faces.Length; f++)
            {
                Face face = bsp.Faces[f];
                if (!lightmaps[f].Has || face.EdgeCount < 3) continue;

                Plane plane = bsp.Planes[face.Plane];
                Vector3 normal = face.PlaneSide == 0 ? plane.Normal : -plane.Normal;
                if (normal.Z < 0.1f) continue;

                var floor = new Floor
                {
                    Face = f,
                    Normal = normal,
                    Distance = face.PlaneSide == 0 ? plane.Distance : -plane.Distance,
                    Corners = new System.Numerics.Vector2[face.EdgeCount],
                    MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue,
                };
                for (int i = 0; i < face.EdgeCount; i++)
                {
                    Vector3 p = bsp.FaceVertex(face, i);
                    floor.Corners[i] = new System.Numerics.Vector2(p.X, p.Y);
                    floor.MinX = MathF.Min(floor.MinX, p.X); floor.MaxX = MathF.Max(floor.MaxX, p.X);
                    floor.MinY = MathF.Min(floor.MinY, p.Y); floor.MaxY = MathF.Max(floor.MaxY, p.Y);
                }
                _floors.Add(floor);
            }
        }

        public bool TrySample(Float3 position, out Float3 light)
        {
            light = default;
            var p = new Vector3(position.Z, -position.X, position.Y) / GoldSrcAssets.Scale;

            Floor best = null;
            float bestDrop = Reach;
            foreach (Floor floor in _floors)
            {
                if (p.X < floor.MinX || p.X > floor.MaxX || p.Y < floor.MinY || p.Y > floor.MaxY) continue;

                float z = (floor.Distance - floor.Normal.X * p.X - floor.Normal.Y * p.Y) / floor.Normal.Z;
                float drop = p.Z - z;
                if (drop < -4f || drop >= bestDrop || !Inside(floor.Corners, p.X, p.Y)) continue;
                best = floor;
                bestDrop = drop;
            }
            if (best == null) return false;

            Face face = _bsp.Faces[best.Face];
            TexInfo texInfo = _bsp.TexInfos[face.TexInfo];
            Lightmap lightmap = _lightmaps[best.Face];
            var hit = new Vector3(p.X, p.Y, p.Z - bestDrop);
            int s = Math.Clamp((int)MathF.Round((Vector3.Dot(hit, texInfo.S) + texInfo.OffsetS - lightmap.UMin) / LuxelSize), 0, lightmap.Width - 1);
            int t = Math.Clamp((int)MathF.Round((Vector3.Dot(hit, texInfo.T) + texInfo.OffsetT - lightmap.VMin) / LuxelSize), 0, lightmap.Height - 1);

            int luxels = lightmap.Width * lightmap.Height, luxel = t * lightmap.Width + s;
            byte[] data = _bsp.Lighting;
            for (int style = 0; style < 4 && face.Style(style) != 255; style++)
            {
                int src = face.LightmapOffset + (style * luxels + luxel) * 3;
                if (src + 2 >= data.Length) break;
                light += new Float3(s_linear[data[src]], s_linear[data[src + 1]], s_linear[data[src + 2]]);
            }
            return true;
        }

        // Convex polygon, either winding: the point is on the same side of every edge.
        private static bool Inside(System.Numerics.Vector2[] corners, float x, float y)
        {
            bool? sign = null;
            for (int i = 0; i < corners.Length; i++)
            {
                System.Numerics.Vector2 a = corners[i], b = corners[(i + 1) % corners.Length];
                float cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
                if (MathF.Abs(cross) < 1e-3f) continue;
                if (sign == null) sign = cross > 0;
                else if (sign != cross > 0) return false;
            }
            return true;
        }
    }
}
