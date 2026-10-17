// The GoldSrc map format (BSP version 30). Laid out like Quake's BSP 29, which s&box's Quake mount reads
// (Copyright (c) 2025 Facepunch Studios Ltd, MIT licensed), with RGB lightmaps and textures that carry their
// own palettes or live in the WADs the map names.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Slopbox.Mounting.GoldSrc.Bsp;

public readonly record struct Plane(Vector3 Normal, float Distance, int Type);

/// <summary>How a texture is projected onto a face: texel u = dot(p, S) + OffsetS, likewise v.</summary>
public readonly record struct TexInfo(Vector3 S, float OffsetS, Vector3 T, float OffsetT, int MipTex, int Flags)
{
    /// <summary>Sky, liquids and other faces without a lightmap.</summary>
    public const int Special = 1;
}

public readonly record struct Face(int Plane, int PlaneSide, int FirstEdge, int EdgeCount, int TexInfo,
    byte Style0, byte Style1, byte Style2, byte Style3, int LightmapOffset)
{
    public byte Style(int i) => i switch { 0 => Style0, 1 => Style1, 2 => Style2, _ => Style3 };
}

public readonly record struct Edge(ushort Vertex0, ushort Vertex1);

public readonly record struct BrushModel(Vector3 Mins, Vector3 Maxs, Vector3 Origin, int FirstFace, int FaceCount);

/// <summary>A texture the map uses. <see cref="Rgba"/> is null when it lives in a WAD rather than the map.</summary>
public sealed record MipTexture(string Name, int Width, int Height, byte[] Rgba);

/// <summary>An entity from the map's entity lump: its keys and values.</summary>
public sealed class Entity
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ClassName => Get("classname") ?? "";

    public string Get(string key) => Values.TryGetValue(key, out string value) ? value : null;

    public float GetFloat(string key, float fallback = 0f)
        => float.TryParse(Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;

    public int GetInt(string key, int fallback = 0)
        => int.TryParse(Get(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : fallback;

    public Vector3 GetVector(string key, Vector3 fallback = default)
    {
        string value = Get(key);
        if (value == null) return fallback;
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return fallback;
        var c = System.Globalization.CultureInfo.InvariantCulture;
        return float.TryParse(parts[0], System.Globalization.NumberStyles.Float, c, out float x)
            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, c, out float y)
            && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, c, out float z)
            ? new Vector3(x, y, z) : fallback;
    }

    /// <summary>The brush model index of "*12", or -1.</summary>
    public int BrushModel
    {
        get
        {
            string model = Get("model");
            return model != null && model.StartsWith('*') && int.TryParse(model.AsSpan(1), out int index) ? index : -1;
        }
    }
}

/// <summary>A parsed GoldSrc map.</summary>
public sealed class BspFile
{
    public const int Version = 30;

    private enum Lump { Entities, Planes, Textures, Vertices, Visibility, Nodes, TexInfo, Faces, Lighting, ClipNodes, Leafs, MarkSurfaces, Edges, SurfEdges, Models, Count }

    public Plane[] Planes { get; }
    public Vector3[] Vertices { get; }
    public TexInfo[] TexInfos { get; }
    public Face[] Faces { get; }
    public Edge[] Edges { get; }
    public int[] SurfEdges { get; }
    public BrushModel[] Models { get; }
    public MipTexture[] Textures { get; }

    /// <summary>RGB luxels, three bytes each. Face lightmap offsets index it in bytes.</summary>
    public byte[] Lighting { get; }

    public List<Entity> Entities { get; } = [];

    /// <summary>True if the file at <paramref name="path"/> is a GoldSrc map.</summary>
    public static bool IsMap(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        if (stream.Length < 4 + (int)Lump.Count * 8) return false;
        using var reader = new BinaryReader(stream);
        return reader.ReadInt32() == Version;
    }

    public BspFile(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var br = new BinaryReader(stream);

        if (br.ReadInt32() != Version) throw new InvalidDataException("Not a GoldSrc (version 30) map.");

        var lumps = new (int Offset, int Length)[(int)Lump.Count];
        for (int i = 0; i < lumps.Length; i++) lumps[i] = (br.ReadInt32(), br.ReadInt32());

        BinaryReader At(Lump lump)
        {
            stream.Seek(lumps[(int)lump].Offset, SeekOrigin.Begin);
            return br;
        }
        int CountOf(Lump lump, int size) => lumps[(int)lump].Length / size;

        Planes = new Plane[CountOf(Lump.Planes, 20)];
        At(Lump.Planes);
        for (int i = 0; i < Planes.Length; i++) Planes[i] = new Plane(ReadVector(br), br.ReadSingle(), br.ReadInt32());

        Vertices = new Vector3[CountOf(Lump.Vertices, 12)];
        At(Lump.Vertices);
        for (int i = 0; i < Vertices.Length; i++) Vertices[i] = ReadVector(br);

        TexInfos = new TexInfo[CountOf(Lump.TexInfo, 40)];
        At(Lump.TexInfo);
        for (int i = 0; i < TexInfos.Length; i++)
            TexInfos[i] = new TexInfo(ReadVector(br), br.ReadSingle(), ReadVector(br), br.ReadSingle(), br.ReadInt32(), br.ReadInt32());

        Faces = new Face[CountOf(Lump.Faces, 20)];
        At(Lump.Faces);
        for (int i = 0; i < Faces.Length; i++)
            Faces[i] = new Face(br.ReadUInt16(), br.ReadUInt16(), br.ReadInt32(), br.ReadUInt16(), br.ReadUInt16(),
                br.ReadByte(), br.ReadByte(), br.ReadByte(), br.ReadByte(), br.ReadInt32());

        Edges = new Edge[CountOf(Lump.Edges, 4)];
        At(Lump.Edges);
        for (int i = 0; i < Edges.Length; i++) Edges[i] = new Edge(br.ReadUInt16(), br.ReadUInt16());

        SurfEdges = new int[CountOf(Lump.SurfEdges, 4)];
        At(Lump.SurfEdges);
        for (int i = 0; i < SurfEdges.Length; i++) SurfEdges[i] = br.ReadInt32();

        Models = new BrushModel[CountOf(Lump.Models, 64)];
        At(Lump.Models);
        for (int i = 0; i < Models.Length; i++)
        {
            Vector3 mins = ReadVector(br), maxs = ReadVector(br), origin = ReadVector(br);
            for (int h = 0; h < 4; h++) br.ReadInt32();   // head nodes
            br.ReadInt32();                                 // vis leafs
            Models[i] = new BrushModel(mins, maxs, origin, br.ReadInt32(), br.ReadInt32());
        }

        Lighting = new byte[lumps[(int)Lump.Lighting].Length];
        At(Lump.Lighting);
        stream.ReadExactly(Lighting);

        Textures = ReadTextures(br, lumps[(int)Lump.Textures].Offset);

        At(Lump.Entities);
        ParseEntities(Encoding.ASCII.GetString(br.ReadBytes(lumps[(int)Lump.Entities].Length)));
    }

    /// <summary>The position of a face's i-th corner.</summary>
    public Vector3 FaceVertex(in Face face, int i)
    {
        int surfEdge = SurfEdges[face.FirstEdge + i];
        return Vertices[surfEdge >= 0 ? Edges[surfEdge].Vertex0 : Edges[-surfEdge].Vertex1];
    }

    private static Vector3 ReadVector(BinaryReader br) => new(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

    private static MipTexture[] ReadTextures(BinaryReader br, int lumpOffset)
    {
        Stream stream = br.BaseStream;
        stream.Seek(lumpOffset, SeekOrigin.Begin);
        int count = br.ReadInt32();
        var offsets = new int[count];
        for (int i = 0; i < count; i++) offsets[i] = br.ReadInt32();

        var textures = new MipTexture[count];
        for (int i = 0; i < count; i++)
        {
            if (offsets[i] < 0)
            {
                textures[i] = new MipTexture("", 16, 16, null);
                continue;
            }

            int start = lumpOffset + offsets[i];
            stream.Seek(start, SeekOrigin.Begin);
            string name = Mdl.MdlFile.GetNullTerminatedString(br.ReadBytes(16));
            int width = br.ReadInt32(), height = br.ReadInt32();
            var mips = new int[4];
            for (int m = 0; m < 4; m++) mips[m] = br.ReadInt32();

            // No mip offsets: the texture is in one of the map's WADs.
            if (mips[0] == 0 || width <= 0 || height <= 0)
            {
                textures[i] = new MipTexture(name, Math.Max(1, width), Math.Max(1, height), null);
                continue;
            }

            stream.Seek(start + mips[0], SeekOrigin.Begin);
            byte[] indices = br.ReadBytes(width * height);

            // The palette follows the smallest mip, after a two byte count.
            stream.Seek(start + mips[3] + (width / 8) * (height / 8) + 2, SeekOrigin.Begin);
            byte[] palette = br.ReadBytes(768);

            textures[i] = new MipTexture(name, width, height, Expand(name, indices, palette));
        }
        return textures;
    }

    /// <summary>Palette indices to RGBA. "{" textures are cut out where they use the last palette entry.</summary>
    public static byte[] Expand(string name, byte[] indices, byte[] palette)
    {
        bool masked = name.StartsWith('{');
        var rgba = new byte[indices.Length * 4];
        for (int i = 0, o = 0; i < indices.Length; i++)
        {
            int index = indices[i], p = index * 3;
            if (p + 2 >= palette.Length) p = 0;
            rgba[o++] = palette[p];
            rgba[o++] = palette[p + 1];
            rgba[o++] = palette[p + 2];
            rgba[o++] = masked && index == 255 ? (byte)0 : (byte)255;
        }
        return rgba;
    }

    // Entities are { "key" "value" ... } blocks; values may hold spaces but never quotes.
    private void ParseEntities(string text)
    {
        Entity current = null;
        int i = 0;
        string key = null;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '{')
            {
                current = new Entity();
                key = null;
                i++;
            }
            else if (c == '}')
            {
                if (current != null) Entities.Add(current);
                current = null;
                i++;
            }
            else if (c == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0) break;
                string token = text.Substring(i + 1, end - i - 1);
                i = end + 1;

                if (current == null) continue;
                if (key == null) key = token;
                else
                {
                    current.Values[key] = token;
                    key = null;
                }
            }
            else i++;
        }
    }
}
