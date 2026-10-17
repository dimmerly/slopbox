using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Vector3 = System.Numerics.Vector3;

namespace Slopbox.Mounting.Source.Bsp;

internal struct Plane
{
    public Vector3 Normal;
    public float Distance;
}

internal struct TexInfo
{
    public Vector3 S, T;
    public float OffsetS, OffsetT;
    public Vector3 LightS, LightT;
    public float LightOffsetS, LightOffsetT;
    public int Flags;
    public int TexData;

    public const int Light = 0x1, Sky2D = 0x2, Sky = 0x4, Warp = 0x8, Trans = 0x10, Trigger = 0x40, NoDraw = 0x80,
        Hint = 0x100, Skip = 0x200, NoLight = 0x400, BumpLight = 0x800;
}

internal struct TexData
{
    public int NameIndex;
    public int Width, Height;
}

internal struct Face
{
    public int Plane;
    public byte Side;
    public int FirstEdge;
    public int EdgeCount;
    public int TexInfo;
    public int DispInfo;
    public uint Styles;
    public int LightOffset;
    public int LightMinS, LightMinT;
    public int LightWidth, LightHeight;   // in luxels, one less than the samples across

    public readonly byte Style(int i) => (byte)(Styles >> (i * 8));
}

internal struct BrushModel
{
    public Vector3 Min, Max, Origin;
    public int FirstFace, FaceCount;
}

internal struct DispInfo
{
    public Vector3 StartPosition;
    public int VertStart;
    public int Power;
    public int Face;
}

internal struct DispVert
{
    public Vector3 Direction;
    public float Distance;
    public float Alpha;
}

internal struct Node
{
    public int Plane;
    public int Front, Back;   // a node index, or -(leaf + 1)
}

internal struct Leaf
{
    public short MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    public int FirstAmbient, AmbientCount;
}

/// <summary>One of a leaf's ambient light samples: a cube of six colours and where in the leaf it was taken.</summary>
internal struct AmbientSample
{
    public Vector3 Position;
    /// <summary>+X, -X, +Y, -Y, +Z, -Z, as ColorRGBExp32 offsets into the lighting lump.</summary>
    public int CubeOffset;
}

internal struct StaticProp
{
    public Vector3 Origin, Angles;
    public string Model;
    public byte Solid;
    public int Skin;
    public float Scale;
}

/// <summary>A map entity: its keys and values.</summary>
internal sealed class Entity
{
    public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);

    public string ClassName => Get("classname") ?? "";

    public string Get(string key) => Values.GetValueOrDefault(key);

    public float GetFloat(string key, float fallback = 0f)
        => float.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    public int GetInt(string key, int fallback = 0)
    {
        string value = Get(key);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i;
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? (int)f : fallback;
    }

    public Vector3 GetVector(string key)
    {
        string value = Get(key);
        if (value == null) return Vector3.Zero;
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var v = Vector3.Zero;
        if (parts.Length > 0) float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v.X);
        if (parts.Length > 1) float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v.Y);
        if (parts.Length > 2) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out v.Z);
        return v;
    }

    /// <summary>The brush model a "*N" model key names, or -1.</summary>
    public int BrushModel
    {
        get
        {
            string model = Get("model");
            return model is ['*', ..] && int.TryParse(model.AsSpan(1), out int index) ? index : -1;
        }
    }
}

/// <summary>
/// A Source engine map (.bsp, "VBSP", versions 19 to 21): the lumps needed to draw it, light it, place its props
/// and play its sounds. Uses the HDR lighting only where a map has no LDR lighting.
/// </summary>
internal sealed class BspFile
{
    private const int LumpEntities = 0, LumpPlanes = 1, LumpTexData = 2, LumpVertexes = 3, LumpTexInfo = 6, LumpFaces = 7,
        LumpLighting = 8, LumpEdges = 12, LumpSurfEdges = 13, LumpModels = 14, LumpDispInfo = 26, LumpDispVerts = 33,
        LumpGame = 35, LumpPak = 40, LumpTexDataStrings = 43, LumpTexDataTable = 44, LumpLightingHdr = 53, LumpFacesHdr = 58;

    public int Version;
    public Plane[] Planes;
    public Vector3[] Vertices;
    public int[] Edges;   // pairs
    public int[] SurfEdges;
    public TexInfo[] TexInfos;
    public TexData[] TexDatas;
    public string[] TextureNames;
    public Face[] Faces;
    public BrushModel[] Models;
    public DispInfo[] DispInfos;
    public DispVert[] DispVerts;
    public byte[] Lighting;
    public byte[] Pak;
    public List<Entity> Entities;
    public List<StaticProp> StaticProps = new();
    public Node[] Nodes = [];
    public Leaf[] Leaves = [];
    public AmbientSample[] Ambient = [];
    /// <summary>The leaf ambient lighting lump, which <see cref="AmbientSample.CubeOffset"/> points into.</summary>
    public byte[] AmbientLighting = [];

    private readonly byte[] _data;

    public static bool IsMap(byte[] header) => header is { Length: >= 8 } && header[0] == 'V' && header[1] == 'B' && header[2] == 'S' && header[3] == 'P';

    public BspFile(byte[] data)
    {
        _data = data;
        if (!IsMap(data)) throw new InvalidOperationException("Not a Source map");
        Version = BitConverter.ToInt32(data, 4);
        if (Version is < 19 or > 21) throw new NotSupportedException($"Map version {Version} is not supported");

        Planes = Read(LumpPlanes, 20, at => new Plane { Normal = V3(at), Distance = F(at + 12) });
        Vertices = Read(LumpVertexes, 12, V3);
        Edges = Read(LumpEdges, 2, at => (int)BitConverter.ToUInt16(_data, at));
        SurfEdges = Read(LumpSurfEdges, 4, I);
        TexInfos = Read(LumpTexInfo, 72, at => new TexInfo
        {
            S = V3(at), OffsetS = F(at + 12), T = V3(at + 16), OffsetT = F(at + 28),
            LightS = V3(at + 32), LightOffsetS = F(at + 44), LightT = V3(at + 48), LightOffsetT = F(at + 60),
            Flags = I(at + 64), TexData = I(at + 68),
        });
        TexDatas = Read(LumpTexData, 32, at => new TexData { NameIndex = I(at + 12), Width = I(at + 16), Height = I(at + 20) });

        int[] table = Read(LumpTexDataTable, 4, I);
        (int stringsAt, int stringsLength) = Lump(LumpTexDataStrings);
        TextureNames = new string[TexDatas.Length];
        for (int i = 0; i < TexDatas.Length; i++)
        {
            int index = TexDatas[i].NameIndex;
            TextureNames[i] = index >= 0 && index < table.Length ? CString(stringsAt + table[index], stringsAt + stringsLength) : "";
        }

        bool ldr = Lump(LumpLighting).Length > 0;
        int facesLump = !ldr && Lump(LumpFacesHdr).Length > 0 ? LumpFacesHdr : LumpFaces;
        Faces = Read(facesLump, 56, at => new Face
        {
            Plane = BitConverter.ToUInt16(_data, at),
            Side = _data[at + 2],
            FirstEdge = I(at + 4),
            EdgeCount = BitConverter.ToInt16(_data, at + 8),
            TexInfo = BitConverter.ToInt16(_data, at + 10),
            DispInfo = BitConverter.ToInt16(_data, at + 12),
            Styles = BitConverter.ToUInt32(_data, at + 16),
            LightOffset = I(at + 20),
            LightMinS = I(at + 28), LightMinT = I(at + 32),
            LightWidth = I(at + 36), LightHeight = I(at + 40),
        });
        Lighting = Bytes(ldr ? LumpLighting : LumpLightingHdr);

        Models = Read(LumpModels, 48, at => new BrushModel { Min = V3(at), Max = V3(at + 12), Origin = V3(at + 24), FirstFace = I(at + 40), FaceCount = I(at + 44) });
        DispInfos = Read(LumpDispInfo, 176, at => new DispInfo { StartPosition = V3(at), VertStart = I(at + 12), Power = I(at + 20), Face = BitConverter.ToUInt16(_data, at + 36) });
        DispVerts = Read(LumpDispVerts, 20, at => new DispVert { Direction = V3(at), Distance = F(at + 12), Alpha = F(at + 16) });
        Pak = Bytes(LumpPak);

        (int entAt, int entLength) = Lump(LumpEntities);
        Entities = ParseEntities(Encoding.Latin1.GetString(_data, entAt, Math.Max(0, entLength)));

        ReadStaticProps();
        ReadAmbient(ldr);
    }

    private const int LumpNodes = 5, LumpLeafs = 10, LumpLeafAmbientIndexHdr = 51, LumpLeafAmbientIndex = 52,
        LumpLeafAmbientLightingHdr = 55, LumpLeafAmbientLighting = 56;

    /// <summary>
    /// The BSP tree and each leaf's ambient light samples (version 20 maps), which light models: from the LDR
    /// lumps where the map has LDR lighting, else the HDR ones.
    /// </summary>
    private void ReadAmbient(bool ldr)
    {
        if (Version < 20) return;
        int indexLump = ldr ? LumpLeafAmbientIndex : LumpLeafAmbientIndexHdr;
        int lightingLump = ldr ? LumpLeafAmbientLighting : LumpLeafAmbientLightingHdr;
        if (Lump(indexLump).Length == 0 || Lump(lightingLump).Length == 0) return;

        Nodes = Read(LumpNodes, 32, at => new Node { Plane = I(at), Front = I(at + 4), Back = I(at + 8) });
        int[] index = Read(indexLump, 4, at => I(at));
        Leaves = Read(LumpLeafs, 32, at => new Leaf
        {
            MinX = BitConverter.ToInt16(_data, at + 8), MinY = BitConverter.ToInt16(_data, at + 10), MinZ = BitConverter.ToInt16(_data, at + 12),
            MaxX = BitConverter.ToInt16(_data, at + 14), MaxY = BitConverter.ToInt16(_data, at + 16), MaxZ = BitConverter.ToInt16(_data, at + 18),
        });
        for (int i = 0; i < Leaves.Length && i < index.Length; i++)
        {
            Leaves[i].AmbientCount = index[i] & 0xFFFF;
            Leaves[i].FirstAmbient = (index[i] >> 16) & 0xFFFF;
        }

        AmbientLighting = Bytes(lightingLump);
        var samples = new AmbientSample[AmbientLighting.Length / 28];
        for (int s = 0; s < samples.Length; s++)
        {
            int at = s * 28;
            samples[s] = new AmbientSample
            {
                CubeOffset = at,
                // Fractions of the leaf's box, 0 to 255; turned into positions per leaf below.
                Position = new Vector3(AmbientLighting[at + 24], AmbientLighting[at + 25], AmbientLighting[at + 26]),
            };
        }
        foreach (Leaf leaf in Leaves)
        {
            for (int s = leaf.FirstAmbient; s < leaf.FirstAmbient + leaf.AmbientCount && s < samples.Length; s++)
            {
                Vector3 f = samples[s].Position / 255f;
                samples[s].Position = new Vector3(
                    leaf.MinX + (leaf.MaxX - leaf.MinX) * f.X,
                    leaf.MinY + (leaf.MaxY - leaf.MinY) * f.Y,
                    leaf.MinZ + (leaf.MaxZ - leaf.MinZ) * f.Z);
            }
        }
        Ambient = samples;
    }

    /// <summary>The leaf a point is in, walking the world's BSP tree from its head node. -1 if there is no tree.</summary>
    public int LeafAt(Vector3 point)
    {
        if (Nodes.Length == 0 || Models.Length == 0) return -1;
        int node = 0;
        for (int depth = 0; depth < 4096 && node >= 0 && node < Nodes.Length; depth++)
        {
            Plane plane = Planes[Nodes[node].Plane];
            node = Vector3.Dot(plane.Normal, point) - plane.Distance >= 0f ? Nodes[node].Front : Nodes[node].Back;
        }
        return node < 0 ? -node - 1 : -1;
    }

    public Vector3 FaceVertex(in Face face, int i)
    {
        int surfEdge = SurfEdges[face.FirstEdge + i];
        int vertex = surfEdge >= 0 ? Edges[surfEdge * 2] : Edges[-surfEdge * 2 + 1];
        return Vertices[vertex];
    }

    private (int Offset, int Length) Lump(int index)
    {
        int at = 8 + index * 16;
        int offset = I(at), length = I(at + 4);
        if (I(at + 12) != 0) throw new NotSupportedException("Compressed map lumps are not supported");
        if (offset < 0 || length < 0 || offset + length > _data.Length) return (0, 0);
        return (offset, length);
    }

    private byte[] Bytes(int index)
    {
        (int offset, int length) = Lump(index);
        return _data.AsSpan(offset, length).ToArray();
    }

    private T[] Read<T>(int lump, int size, Func<int, T> read)
    {
        (int offset, int length) = Lump(lump);
        var result = new T[length / size];
        for (int i = 0; i < result.Length; i++) result[i] = read(offset + i * size);
        return result;
    }

    private int I(int at) => BitConverter.ToInt32(_data, at);
    private float F(int at) => BitConverter.ToSingle(_data, at);
    private Vector3 V3(int at) => new(F(at), F(at + 4), F(at + 8));

    private string CString(int at, int end)
    {
        if (at < 0 || at >= _data.Length) return "";
        int stop = at;
        while (stop < end && stop < _data.Length && _data[stop] != 0) stop++;
        return Encoding.ASCII.GetString(_data, at, stop - at);
    }

    /// <summary>The static props of the "sprp" game lump, in any version from 4 to 11.</summary>
    private void ReadStaticProps()
    {
        (int offset, int length) = Lump(LumpGame);
        if (length < 4) return;

        int count = I(offset);
        for (int i = 0; i < count; i++)
        {
            int header = offset + 4 + i * 16;
            if (header + 16 > offset + length) return;
            int id = I(header);
            int flags = BitConverter.ToUInt16(_data, header + 4);
            int version = BitConverter.ToUInt16(_data, header + 6);
            int at = I(header + 8), size = I(header + 12);
            if (id != 0x73707270 /* "sprp" */ || (flags & 1) != 0 || at <= 0 || at + size > _data.Length) continue;

            int end = at + size;
            int dictionary = I(at);
            at += 4;
            var names = new string[dictionary];
            for (int n = 0; n < dictionary; n++, at += 128) names[n] = CString(at, at + 128);

            int leaves = I(at);
            at += 4 + leaves * 2;

            int props = I(at);
            at += 4;
            if (props <= 0) return;
            int stride = (end - at) / props;
            if (stride < 56) return;

            for (int p = 0; p < props; p++, at += stride)
            {
                int type = BitConverter.ToUInt16(_data, at + 24);
                StaticProps.Add(new StaticProp
                {
                    Origin = V3(at),
                    Angles = V3(at + 12),
                    Model = type < names.Length ? names[type] : null,
                    Solid = _data[at + 30],
                    Skin = I(at + 32),
                    Scale = version >= 11 && stride >= 80 ? F(at + 76) : 1f,
                });
            }
            return;
        }
    }

    private static List<Entity> ParseEntities(string text)
    {
        var entities = new List<Entity>();
        Entity current = null;
        int pos = 0;
        while (pos < text.Length)
        {
            char c = text[pos];
            if (c == '{')
            {
                current = new Entity();
                pos++;
            }
            else if (c == '}')
            {
                if (current != null) entities.Add(current);
                current = null;
                pos++;
            }
            else if (c == '"')
            {
                string key = Quoted(text, ref pos);
                while (pos < text.Length && text[pos] != '"' && text[pos] != '}') pos++;
                if (pos >= text.Length || text[pos] != '"') continue;
                string value = Quoted(text, ref pos);
                current?.Values.TryAdd(key, value);
            }
            else
            {
                pos++;
            }
        }
        return entities;
    }

    private static string Quoted(string text, ref int pos)
    {
        int start = ++pos;
        while (pos < text.Length && text[pos] != '"') pos++;
        string result = text[start..Math.Min(pos, text.Length)];
        pos++;
        return result;
    }
}
