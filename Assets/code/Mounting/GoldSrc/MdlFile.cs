// Ported from s&box's Sandbox.Mounting.GoldSrc (Copyright (c) 2025 Facepunch Studios Ltd, MIT licensed).
// Textures come out as RGBA bytes rather than engine textures, so parsing needs no renderer, and poses are
// worked out one frame at a time rather than every frame of every sequence up front.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Slopbox.Mounting.GoldSrc.Mdl;

/// <summary>A texture from a model, expanded from its palette to RGBA, top row first.</summary>
public sealed record TextureImage(string Name, int Width, int Height, LightingFlags Flags, byte[] Rgba);

/// <summary>A parsed GoldSrc studio model (.mdl, version 10).</summary>
public sealed class MdlFile
{
    public Header Header => _header;

    private readonly Header _header;
    private readonly BodyParts[] _bodyParts;
    private readonly Model[] _models;
    private readonly Mesh[] _meshes;
    private readonly Vector3[] _vertices;
    private readonly Vector3[] _normals;
    private readonly TriVert[] _triVerts;
    private readonly byte[] _boneVertices;
    private readonly byte[] _boneNormals;
    private readonly Bone[] _bones;
    private readonly Attachment[] _attachments;
    private readonly TextureData[] _textureDatas;
    private readonly TextureImage[] _textures;
    private readonly BoundingBox[] _hitBoxes;
    private readonly SequenceDesc[] _sequences;
    private readonly SequenceGroup[] _sequenceGroups;
    private readonly short[] _skinRefs;

    private readonly int[] _bodyPartModelOffsets;
    private readonly int[] _modelMeshOffsets;
    private readonly int[] _modelVertexOffsets;
    private readonly int[] _modelNormalOffsets;
    private readonly int[] _meshTriVertOffsets;

    public int NumBodyParts => _bodyParts.Length;
    public IReadOnlyList<Bone> Bones => _bones;
    public IReadOnlyList<SequenceDesc> Sequences => _sequences;
    public IReadOnlyList<BoundingBox> HitBoxes => _hitBoxes;
    public IReadOnlyList<Attachment> Attachments => _attachments;

    public static string GetNullTerminatedString(byte[] bytes)
    {
        int length = Array.IndexOf(bytes, (byte)0);
        return length < 0 ? Encoding.ASCII.GetString(bytes) : Encoding.ASCII.GetString(bytes, 0, length);
    }

    /// <summary>True if the file at <paramref name="path"/> is a GoldSrc model with geometry, rather than one of
    /// its texture ("t.mdl") or sequence ("01.mdl") files, or a model from another engine.</summary>
    public static bool IsModelWithGeometry(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(stream);
        if (stream.Length < Header.NumBodyPartsOffset + 4) return false;
        if (reader.ReadInt32() != Header.Magic || reader.ReadInt32() != Header.GoldSrcVersion) return false;

        stream.Seek(Header.NumBodyPartsOffset, SeekOrigin.Begin);
        return reader.ReadInt32() > 0;
    }

    public BodyParts GetBodyPart(int bodyPartIndex) => _bodyParts[bodyPartIndex];

    public Model GetModel(int bodyPartIndex, int modelIndex) => _models[GetModelIndex(bodyPartIndex, modelIndex)];

    public Mesh GetMesh(int bodyPartIndex, int modelIndex, int meshIndex) => _meshes[GetMeshIndex(bodyPartIndex, modelIndex, meshIndex)];

    private int GetModelIndex(int bodyPartIndex, int modelIndex) => _bodyPartModelOffsets[bodyPartIndex] + modelIndex;

    private int GetMeshIndex(int bodyPartIndex, int modelIndex, int meshIndex) => _modelMeshOffsets[GetModelIndex(bodyPartIndex, modelIndex)] + meshIndex;

    public int GetVertices(int bodyPartIndex, int modelIndex, Vector3[] destArray, int destOffset = 0)
    {
        int modelOffset = GetModelIndex(bodyPartIndex, modelIndex);
        Model model = _models[modelOffset];
        if (destArray != null) Array.Copy(_vertices, _modelVertexOffsets[modelOffset], destArray, destOffset, model.NumVerts);
        return model.NumVerts;
    }

    public int GetNormals(int bodyPartIndex, int modelIndex, Vector3[] destArray, int destOffset = 0)
    {
        int modelOffset = GetModelIndex(bodyPartIndex, modelIndex);
        Model model = _models[modelOffset];
        if (destArray != null) Array.Copy(_normals, _modelNormalOffsets[modelOffset], destArray, destOffset, model.NumNorms);
        return model.NumNorms;
    }

    public int GetBoneVertices(int bodyPartIndex, int modelIndex, byte[] destArray, int destOffset = 0)
    {
        int modelOffset = GetModelIndex(bodyPartIndex, modelIndex);
        Model model = _models[modelOffset];
        if (destArray != null) Array.Copy(_boneVertices, _modelVertexOffsets[modelOffset], destArray, destOffset, model.NumVerts);
        return model.NumVerts;
    }

    /// <summary>The bone each normal follows. Normals are indexed on their own, not by vertex.</summary>
    public int GetBoneNormals(int bodyPartIndex, int modelIndex, byte[] destArray, int destOffset = 0)
    {
        int modelOffset = GetModelIndex(bodyPartIndex, modelIndex);
        Model model = _models[modelOffset];
        if (destArray != null) Array.Copy(_boneNormals, _modelNormalOffsets[modelOffset], destArray, destOffset, model.NumNorms);
        return model.NumNorms;
    }

    public int GetTriVerts(int bodyPartIndex, int modelIndex, int meshIndex, TriVert[] destArray, int destOffset = 0)
    {
        int meshOffset = GetMeshIndex(bodyPartIndex, modelIndex, meshIndex);
        Mesh mesh = _meshes[meshOffset];
        if (destArray != null) Array.Copy(_triVerts, _meshTriVertOffsets[meshOffset] * 3, destArray, destOffset, mesh.NumTris * 3);
        return mesh.NumTris * 3;
    }

    public int GetTextureIndex(int skinRef, int skinFamily = 0)
    {
        int index = (skinFamily * _header.NumSkinRef) + skinRef;
        return index < 0 || index >= _skinRefs.Length ? -1 : _skinRefs[index];
    }

    public TextureImage GetTexture(int index) => index < 0 || index >= _textures.Length ? null : _textures[index];

    // ----------------------------------------------------------------
    //  Animation
    // ----------------------------------------------------------------

    /// <summary>
    /// Each bone's absolute transform at one frame of a sequence, as the game draws it. Falls back to the
    /// reference pose without sequences. <paramref name="sequenceGroups"/> opens a sequence group's file;
    /// group 0 is this file's own <paramref name="self"/>.
    /// </summary>
    public BoneTransform[] GetPose(BinaryReader self, Func<int, BinaryReader> sequenceGroups, int sequence = 0, int frame = 0)
    {
        var local = new BoneTransform[_bones.Length];

        BinaryReader reader = null;
        SequenceDesc seq = default;
        if (sequence >= 0 && sequence < _sequences.Length)
        {
            seq = _sequences[sequence];
            reader = seq.SeqGroup == 0 ? self : sequenceGroups?.Invoke(seq.SeqGroup);
            frame = Math.Clamp(frame, 0, Math.Max(0, seq.NumFrames - 1));
        }

        if (reader == null)
        {
            for (int i = 0; i < _bones.Length; i++)
                local[i] = new BoneTransform(_bones[i].Position, AngleQuaternion(_bones[i].Rotation));
        }
        else
        {
            reader.BaseStream.Seek(seq.AnimIndex, SeekOrigin.Begin);
            var anims = new Anim[_bones.Length];
            for (int i = 0; i < anims.Length; i++) anims[i] = new Anim(reader);

            for (int i = 0; i < _bones.Length; i++)
            {
                Bone bone = _bones[i];
                int animOffset = seq.AnimIndex + (i * Anim.Size);
                Vector3 position = bone.Position + (GetAnimValue(reader, frame, anims[i].PositionOffsets, animOffset) * bone.PositionScale);
                Quaternion rotation = AngleQuaternion(GetRotationAnimValue(reader, frame, anims[i].RotationOffsets, animOffset, bone));

                // The motion bone carries the walk; keep the model where it stands.
                if (seq.MotionBone == i)
                {
                    if (seq.MotionType.HasFlag(MotionFlags.X)) position.X = 0;
                    if (seq.MotionType.HasFlag(MotionFlags.Y)) position.Y = 0;
                    if (seq.MotionType.HasFlag(MotionFlags.Z)) position.Z = 0;
                }

                local[i] = new BoneTransform(position, rotation);
            }
        }

        var world = new BoneTransform[_bones.Length];
        for (int i = 0; i < world.Length; i++)
        {
            int parent = _bones[i].Parent;
            world[i] = parent >= 0 && parent < i ? world[parent].ToWorld(local[i]) : local[i];
        }
        return world;
    }

    /// <summary>GoldSrc's ACT_IDLE.</summary>
    private const int IdleActivity = 1;

    /// <summary>The sequence to pose a prop on: the first idle, else the first sequence, which is often a walk.</summary>
    public int FindIdleSequence()
    {
        for (int i = 0; i < _sequences.Length; i++)
            if (_sequences[i].Activity == IdleActivity) return i;
        for (int i = 0; i < _sequences.Length; i++)
            if (_sequences[i].Label.StartsWith("idle", StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private static Vector3 GetAnimValue(BinaryReader reader, int frame, ushort[] offsets, int baseOffset)
    {
        var value = Vector3.Zero;
        for (int i = 0; i < 3; ++i)
        {
            if (offsets[i] == 0) continue;
            value[i] = ReadAnimValue(reader, frame, baseOffset + offsets[i]);
        }
        return value;
    }

    private static Vector3 GetRotationAnimValue(BinaryReader reader, int frame, ushort[] offsets, int baseOffset, Bone bone)
    {
        Vector3 value = bone.Rotation;
        Vector3 scale = bone.RotationScale;
        for (int i = 0; i < 3; ++i)
        {
            if (offsets[i] == 0) continue;
            value[i] += ReadAnimValue(reader, frame, baseOffset + offsets[i]) * scale[i];
        }
        return value;
    }

    // Run length encoded: each span has a count of stored values and a count of frames they cover, the last
    // stored value held for the rest.
    private static short ReadAnimValue(BinaryReader reader, int frame, int offset)
    {
        reader.BaseStream.Seek(offset, SeekOrigin.Begin);
        byte valid = reader.ReadByte();
        byte total = reader.ReadByte();

        int span = frame;
        while (total <= span)
        {
            if (total == 0) return 0;   // a corrupt span would loop forever
            span -= total;
            offset += (valid + 1) * 2;
            reader.BaseStream.Seek(offset, SeekOrigin.Begin);
            valid = reader.ReadByte();
            total = reader.ReadByte();
        }

        offset += valid > span ? (span + 1) * 2 : valid * 2;
        reader.BaseStream.Seek(offset, SeekOrigin.Begin);
        return reader.ReadInt16();
    }

    /// <summary>GoldSrc's AngleQuaternion: roll, pitch and yaw in radians about X, Y and Z.</summary>
    public static Quaternion AngleQuaternion(Vector3 angles)
    {
        (float sy, float cy) = MathF.SinCos(angles.Z * 0.5f);
        (float sp, float cp) = MathF.SinCos(angles.Y * 0.5f);
        (float sr, float cr) = MathF.SinCos(angles.X * 0.5f);

        return new Quaternion(
            (sr * cp * cy) - (cr * sp * sy),
            (cr * sp * cy) + (sr * cp * sy),
            (cr * cp * sy) - (sr * sp * cy),
            (cr * cp * cy) + (sr * sp * sy));
    }

    // ----------------------------------------------------------------
    //  Parsing
    // ----------------------------------------------------------------

    public static MdlFile FromStream(Stream stream) => new(stream);

    private MdlFile(Stream ms)
    {
        using var br = new BinaryReader(ms, Encoding.ASCII, leaveOpen: true);

        _header = new Header(br);

        ms.Seek(_header.BodyPartIndex, SeekOrigin.Begin);
        _bodyParts = new BodyParts[_header.NumBodyParts];
        for (int i = 0; i < _bodyParts.Length; i++) _bodyParts[i] = new BodyParts(br);

        _bodyPartModelOffsets = new int[_bodyParts.Length];
        _models = new Model[_bodyParts.Sum(x => x.NumModels)];
        _modelMeshOffsets = new int[_models.Length];
        _modelVertexOffsets = new int[_models.Length];
        _modelNormalOffsets = new int[_models.Length];

        int modelsRead = 0;
        for (int index = 0; index < _bodyParts.Length; index++)
        {
            BodyParts bodyPart = _bodyParts[index];
            _bodyPartModelOffsets[index] = modelsRead;

            ms.Seek(bodyPart.ModelIndex, SeekOrigin.Begin);
            for (int i = 0; i < bodyPart.NumModels; i++) _models[modelsRead + i] = new Model(br);
            modelsRead += bodyPart.NumModels;
        }

        _meshes = new Mesh[_models.Sum(x => x.NumMesh)];
        _vertices = new Vector3[_models.Sum(x => x.NumVerts)];
        _boneVertices = new byte[_vertices.Length];
        _normals = new Vector3[_models.Sum(x => x.NumNorms)];
        _boneNormals = new byte[_normals.Length];
        _meshTriVertOffsets = new int[_meshes.Length];

        int meshesRead = 0, verticesRead = 0, normalsRead = 0;
        for (int index = 0; index < _models.Length; index++)
        {
            Model model = _models[index];
            _modelMeshOffsets[index] = meshesRead;
            _modelVertexOffsets[index] = verticesRead;
            _modelNormalOffsets[index] = normalsRead;

            ms.Seek(model.MeshIndex, SeekOrigin.Begin);
            for (int i = 0; i < model.NumMesh; i++) _meshes[meshesRead + i] = new Mesh(br);
            meshesRead += model.NumMesh;

            ms.Seek(model.VertIndex, SeekOrigin.Begin);
            for (int i = 0; i < model.NumVerts; i++) _vertices[verticesRead + i] = br.ReadVector3();

            ms.Seek(model.VertInfoIndex, SeekOrigin.Begin);
            for (int i = 0; i < model.NumVerts; i++) _boneVertices[verticesRead + i] = br.ReadByte();
            verticesRead += model.NumVerts;

            ms.Seek(model.NormInfoIndex, SeekOrigin.Begin);
            for (int i = 0; i < model.NumNorms; i++) _boneNormals[normalsRead + i] = br.ReadByte();

            ms.Seek(model.NormIndex, SeekOrigin.Begin);
            for (int i = 0; i < model.NumNorms; i++) _normals[normalsRead + i] = br.ReadVector3();
            normalsRead += model.NumNorms;
        }

        // Triangles come as strips and fans; flatten them to a list.
        _triVerts = new TriVert[_meshes.Sum(x => x.NumTris) * 3];
        int trisRead = 0;
        for (int index = 0; index < _meshes.Length; index++)
        {
            Mesh mesh = _meshes[index];
            _meshTriVertOffsets[index] = trisRead;

            ms.Seek(mesh.TriIndex, SeekOrigin.Begin);
            int meshEnd = (trisRead + mesh.NumTris) * 3;
            while (true)
            {
                short triTypeNum = br.ReadInt16();
                if (triTypeNum == 0) break;

                int indexCount = Math.Abs(triTypeNum);
                int triangleCount = indexCount - 2;

                var strip = new TriVert[indexCount];
                for (int i = 0; i < indexCount; ++i) strip[i] = new TriVert(br);

                Span<int> corner = stackalloc int[3];
                for (int v = 0; v < triangleCount; ++v)
                {
                    if (triTypeNum < 0)
                    {
                        corner[0] = v + 2; corner[1] = v + 1; corner[2] = 0;
                    }
                    else if ((v % 2) == 0)
                    {
                        corner[0] = v + 2; corner[1] = v + 1; corner[2] = v;
                    }
                    else
                    {
                        corner[0] = v; corner[1] = v + 1; corner[2] = v + 2;
                    }

                    int dst = (trisRead + v) * 3;
                    if (dst + 3 > meshEnd) break;
                    for (int k = 0; k < 3; ++k) _triVerts[dst + k] = strip[corner[k]];
                }

                trisRead += triangleCount;
            }

            // Trust the mesh's count, whatever the commands added up to.
            trisRead = _meshTriVertOffsets[index] + mesh.NumTris;
        }

        _textureDatas = new TextureData[_header.NumTextures];
        ms.Seek(_header.TextureIndex, SeekOrigin.Begin);
        for (int i = 0; i < _textureDatas.Length; i++) _textureDatas[i] = new TextureData(br);

        _textures = new TextureImage[_header.NumTextures];
        for (int i = 0; i < _textureDatas.Length; ++i)
        {
            TextureData data = _textureDatas[i];
            int pixelCount = data.Width * data.Height;

            ms.Seek(data.Index, SeekOrigin.Begin);
            byte[] pixels = br.ReadBytes(pixelCount);
            byte[] palette = br.ReadBytes(256 * 3);
            if (pixels.Length < pixelCount || palette.Length < 256 * 3) continue;

            // Masked textures cut out the last palette entry.
            bool masked = (data.Flags & LightingFlags.Masked) != 0;
            var rgba = new byte[pixelCount * 4];
            for (int j = 0; j < pixelCount; ++j)
            {
                int index = pixels[j];
                int p = index * 3, d = j * 4;
                rgba[d] = palette[p];
                rgba[d + 1] = palette[p + 1];
                rgba[d + 2] = palette[p + 2];
                rgba[d + 3] = masked && index == 255 ? (byte)0 : (byte)255;
            }

            _textures[i] = new TextureImage(data.Name, data.Width, data.Height, data.Flags, rgba);
        }

        _skinRefs = new short[_header.NumSkinFamilies * _header.NumSkinRef];
        ms.Seek(_header.SkinIndex, SeekOrigin.Begin);
        for (int i = 0; i < _skinRefs.Length; i++) _skinRefs[i] = br.ReadInt16();

        _bones = new Bone[_header.NumBones];
        ms.Seek(_header.BoneIndex, SeekOrigin.Begin);
        for (int i = 0; i < _bones.Length; i++) _bones[i] = new Bone(br);

        _attachments = new Attachment[_header.NumAttachments];
        ms.Seek(_header.AttachmentIndex, SeekOrigin.Begin);
        for (int i = 0; i < _attachments.Length; i++) _attachments[i] = new Attachment(br);

        _hitBoxes = new BoundingBox[_header.NumHitBoxes];
        ms.Seek(_header.HitBoxIndex, SeekOrigin.Begin);
        for (int i = 0; i < _hitBoxes.Length; i++) _hitBoxes[i] = new BoundingBox(br);

        _sequenceGroups = new SequenceGroup[_header.NumSeqGroups];
        ms.Seek(_header.SeqGroupIndex, SeekOrigin.Begin);
        for (int i = 0; i < _sequenceGroups.Length; i++) _sequenceGroups[i] = new SequenceGroup(br);

        _sequences = new SequenceDesc[_header.NumSeq];
        ms.Seek(_header.SeqIndex, SeekOrigin.Begin);
        for (int i = 0; i < _sequences.Length; i++) _sequences[i] = new SequenceDesc(br);
    }
}
