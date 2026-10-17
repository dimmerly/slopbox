// Ported from s&box's Sandbox.Mounting.GoldSrc (Copyright (c) 2025 Facepunch Studios Ltd, MIT licensed).
// Vectors are System.Numerics in GoldSrc's own space: inches, X forward, Y left, Z up.

using System;
using System.IO;
using System.Numerics;

namespace Slopbox.Mounting.GoldSrc.Mdl;

public readonly struct Header(BinaryReader reader)
{
    public readonly int Id = reader.ReadInt32();
    public readonly int Version = reader.ReadInt32();
    private readonly byte[] NameBytes = reader.ReadBytes(64);
    public string Name => MdlFile.GetNullTerminatedString(NameBytes);
    public readonly int Length = reader.ReadInt32();
    public readonly Vector3 EyePosition = reader.ReadVector3();
    public readonly Vector3 Min = reader.ReadVector3();
    public readonly Vector3 Max = reader.ReadVector3();
    public readonly Vector3 BoundsMin = reader.ReadVector3();
    public readonly Vector3 BoundsMax = reader.ReadVector3();
    public readonly int Flags = reader.ReadInt32();
    public readonly int NumBones = reader.ReadInt32();
    public readonly int BoneIndex = reader.ReadInt32();
    public readonly int NumBoneControllers = reader.ReadInt32();
    public readonly int BoneControllerIndex = reader.ReadInt32();
    public readonly int NumHitBoxes = reader.ReadInt32();
    public readonly int HitBoxIndex = reader.ReadInt32();
    public readonly int NumSeq = reader.ReadInt32();
    public readonly int SeqIndex = reader.ReadInt32();
    public readonly int NumSeqGroups = reader.ReadInt32();
    public readonly int SeqGroupIndex = reader.ReadInt32();
    public readonly int NumTextures = reader.ReadInt32();
    public readonly int TextureIndex = reader.ReadInt32();
    public readonly int TextureDataIndex = reader.ReadInt32();
    public readonly int NumSkinRef = reader.ReadInt32();
    public readonly int NumSkinFamilies = reader.ReadInt32();
    public readonly int SkinIndex = reader.ReadInt32();
    public readonly int NumBodyParts = reader.ReadInt32();
    public readonly int BodyPartIndex = reader.ReadInt32();
    public readonly int NumAttachments = reader.ReadInt32();
    public readonly int AttachmentIndex = reader.ReadInt32();
    public readonly int SoundTable = reader.ReadInt32();
    public readonly int SoundIndex = reader.ReadInt32();
    public readonly int SoundGroups = reader.ReadInt32();
    public readonly int SoundGroupIndex = reader.ReadInt32();
    public readonly int NumTransitions = reader.ReadInt32();
    public readonly int TransitionIndex = reader.ReadInt32();

    /// <summary>"IDST", the magic of a studio model.</summary>
    public const int Magic = 0x54534449;

    /// <summary>GoldSrc's studio model version.</summary>
    public const int GoldSrcVersion = 10;

    /// <summary>Where <see cref="NumBodyParts"/> sits, to tell models from their texture and sequence files.</summary>
    public const int NumBodyPartsOffset = 204;
}

public readonly struct Model(BinaryReader reader)
{
    private readonly byte[] NameBytes = reader.ReadBytes(64);
    public string Name => MdlFile.GetNullTerminatedString(NameBytes);
    public readonly int Type = reader.ReadInt32();
    public readonly float BoundingRadius = reader.ReadSingle();
    public readonly int NumMesh = reader.ReadInt32();
    public readonly int MeshIndex = reader.ReadInt32();
    public readonly int NumVerts = reader.ReadInt32();
    public readonly int VertInfoIndex = reader.ReadInt32();
    public readonly int VertIndex = reader.ReadInt32();
    public readonly int NumNorms = reader.ReadInt32();
    public readonly int NormInfoIndex = reader.ReadInt32();
    public readonly int NormIndex = reader.ReadInt32();
    public readonly int NumGroups = reader.ReadInt32();
    public readonly int GroupIndex = reader.ReadInt32();
}

public readonly struct Mesh(BinaryReader reader)
{
    public readonly int NumTris = reader.ReadInt32();
    public readonly int TriIndex = reader.ReadInt32();
    public readonly int SkinRef = reader.ReadInt32();
    public readonly int NumNorms = reader.ReadInt32();
    public readonly int NormIndex = reader.ReadInt32();
}

public readonly record struct TriVert(short VertIndex, short NormIndex, short S, short T)
{
    public TriVert(BinaryReader reader) : this(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16()) { }
}

public readonly struct BodyParts(BinaryReader reader)
{
    private readonly byte[] NameBytes = reader.ReadBytes(64);
    public string Name => MdlFile.GetNullTerminatedString(NameBytes);
    public readonly int NumModels = reader.ReadInt32();
    public readonly int BaseIndex = reader.ReadInt32();
    public readonly int ModelIndex = reader.ReadInt32();
}

public readonly struct TextureData(BinaryReader reader)
{
    private readonly byte[] NameBytes = reader.ReadBytes(64);
    public string Name => MdlFile.GetNullTerminatedString(NameBytes);
    public readonly LightingFlags Flags = (LightingFlags)reader.ReadInt32();
    public readonly int Width = reader.ReadInt32();
    public readonly int Height = reader.ReadInt32();
    public readonly int Index = reader.ReadInt32();
}

public readonly struct Attachment(BinaryReader reader)
{
    private readonly byte[] NameBytes = reader.ReadBytes(32);
    public string Name => MdlFile.GetNullTerminatedString(NameBytes);
    public readonly int Type = reader.ReadInt32();
    public readonly int Bone = reader.ReadInt32();
    public readonly Vector3 Origin = reader.ReadVector3();
    public readonly Vector3[] Vectors = [reader.ReadVector3(), reader.ReadVector3(), reader.ReadVector3()];
}

public readonly struct Bone(BinaryReader reader)
{
    private readonly byte[] NameBytes = reader.ReadBytes(32);
    public string Name => MdlFile.GetNullTerminatedString(NameBytes);
    public readonly int Parent = reader.ReadInt32();
    public readonly int Flags = reader.ReadInt32();
    public readonly int[] BoneController = [reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()];
    public readonly float[] Value = [reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()];
    public readonly float[] Scale = [reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()];
    public Vector3 Position => new(Value[0], Value[1], Value[2]);
    public Vector3 PositionScale => new(Scale[0], Scale[1], Scale[2]);
    public Vector3 Rotation => new(Value[3], Value[4], Value[5]);
    public Vector3 RotationScale => new(Scale[3], Scale[4], Scale[5]);
}

public readonly struct BoundingBox(BinaryReader reader)
{
    public readonly int Bone = reader.ReadInt32();
    public readonly int Group = reader.ReadInt32();
    public readonly Vector3 Min = reader.ReadVector3();
    public readonly Vector3 Max = reader.ReadVector3();
}

public readonly struct SequenceGroup(BinaryReader reader)
{
    private readonly byte[] LabelBytes = reader.ReadBytes(32);
    public string Label => MdlFile.GetNullTerminatedString(LabelBytes);
    private readonly byte[] NameBytes = reader.ReadBytes(64);
    public string Name => MdlFile.GetNullTerminatedString(NameBytes);
    public readonly int Unused1 = reader.ReadInt32();
    public readonly int Unused2 = reader.ReadInt32();
}

public readonly struct SequenceDesc(BinaryReader reader)
{
    private readonly byte[] LabelBytes = reader.ReadBytes(32);
    public string Label => MdlFile.GetNullTerminatedString(LabelBytes);
    public readonly float Fps = reader.ReadSingle();
    public readonly int Flags = reader.ReadInt32();
    public readonly int Activity = reader.ReadInt32();
    public readonly int ActWeight = reader.ReadInt32();
    public readonly int NumEvents = reader.ReadInt32();
    public readonly int EventIndex = reader.ReadInt32();
    public readonly int NumFrames = reader.ReadInt32();
    public readonly int NumPivots = reader.ReadInt32();
    public readonly int PivotIndex = reader.ReadInt32();
    public readonly MotionFlags MotionType = (MotionFlags)reader.ReadInt32();
    public readonly int MotionBone = reader.ReadInt32();
    public readonly Vector3 LinearMovement = reader.ReadVector3();
    public readonly int AutoMovePosIndex = reader.ReadInt32();
    public readonly int AutoMoveAngleIndex = reader.ReadInt32();
    public readonly Vector3 Min = reader.ReadVector3();
    public readonly Vector3 Max = reader.ReadVector3();
    public readonly int NumBlends = reader.ReadInt32();
    public readonly int AnimIndex = reader.ReadInt32();
    public readonly int[] BlendType = [reader.ReadInt32(), reader.ReadInt32()];
    public readonly float[] BlendStart = [reader.ReadSingle(), reader.ReadSingle()];
    public readonly float[] BlendEnd = [reader.ReadSingle(), reader.ReadSingle()];
    public readonly int BlendParent = reader.ReadInt32();
    public readonly int SeqGroup = reader.ReadInt32();
    public readonly int EntryNode = reader.ReadInt32();
    public readonly int ExitNode = reader.ReadInt32();
    public readonly int NodeFlags = reader.ReadInt32();
    public readonly int NextSeq = reader.ReadInt32();
}

public readonly struct Anim(BinaryReader reader)
{
    public readonly ushort[] PositionOffsets = [reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16()];
    public readonly ushort[] RotationOffsets = [reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16()];

    /// <summary>The size of one bone's offsets.</summary>
    public const int Size = 12;
}

[Flags]
public enum LightingFlags : int
{
    FlatShade = 0x0001,
    Chrome = 0x0002,
    FullBright = 0x0004,
    NoMips = 0x0008,
    Alpha = 0x0010,
    Additive = 0x0020,
    Masked = 0x0040,
    RenderFlags = Chrome | Additive | Masked | FullBright,
}

[Flags]
public enum MotionFlags : int
{
    X = 0x0001,
    Y = 0x0002,
    Z = 0x0004,
    XR = 0x0008,
    YR = 0x0010,
    ZR = 0x0020,
    LX = 0x0040,
    LY = 0x0080,
    LZ = 0x0100,
    AX = 0x0200,
    AY = 0x0400,
    AZ = 0x0800,
    AXR = 0x1000,
    AYR = 0x2000,
    AZR = 0x4000,
    Types = 0x7FFF,
    RLoop = 0x8000,
}

/// <summary>A bone's position and rotation, in GoldSrc space.</summary>
public readonly record struct BoneTransform(Vector3 Position, Quaternion Rotation)
{
    public static readonly BoneTransform Identity = new(Vector3.Zero, Quaternion.Identity);

    /// <summary>A child transform given relative to this one, made absolute.</summary>
    public BoneTransform ToWorld(BoneTransform local) => new(PointToWorld(local.Position), Quaternion.Normalize(Rotation * local.Rotation));

    public Vector3 PointToWorld(Vector3 point) => Position + Vector3.Transform(point, Rotation);

    public Vector3 NormalToWorld(Vector3 normal) => Vector3.Transform(normal, Rotation);
}

internal static class BinaryReaderExtensions
{
    public static Vector3 ReadVector3(this BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
