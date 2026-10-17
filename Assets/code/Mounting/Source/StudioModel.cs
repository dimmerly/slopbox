using System;
using System.Collections.Generic;
using System.Text;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Slopbox.Mounting.GoldSrc;
using Vector3 = System.Numerics.Vector3;

namespace Slopbox.Mounting.Source;

/// <summary>A Source studio model (.mdl) with its vertices (.vvd) and strips (.vtx), as a static model.</summary>
internal sealed class StudioModelLoader(string gamePath, SourceFile file) : ResourceLoader<SourceMount>
{
    protected override object Load() => StudioModel.Build(Name, gamePath, file.Read(), Host);
}

/// <summary>
/// Builds <see cref="MountedModel"/>s from Source studio models, versions 44 to 49: the first choice of every body
/// part at its highest detail, posed as compiled, with a material per mesh for every skin. Vertices come from the
/// .vvd, triangles from the .vtx, both checked against the .mdl's checksum. A skinned build also keeps the
/// skeleton, as a hierarchy, and each vertex's bone weights, for characters that animate.
/// </summary>
internal static class StudioModel
{
    private const int VertexSize = 48;
    private const int BoneSize = 216;

    public static MountedModel Build(string name, string gamePath, byte[] mdl, IContent content, bool skinned = false)
    {
        if (mdl == null || mdl.Length < 408 || Encoding.ASCII.GetString(mdl, 0, 4) != "IDST") return null;
        int version = I32(mdl, 4);
        if (version is < 44 or > 49) return null;
        int checksum = I32(mdl, 8);

        string stem = gamePath[..^4];
        byte[] vvd = content.Read(stem + ".vvd");
        byte[] vtx = content.Read(stem + ".dx90.vtx") ?? content.Read(stem + ".dx80.vtx") ?? content.Read(stem + ".sw.vtx") ?? content.Read(stem + ".vtx");
        if (vvd == null || vtx == null) return null;
        if (I32(vvd, 8) != checksum || I32(vtx, 16) != checksum) return null;

        Vertex[] vertices = ReadVertices(vvd);
        if (vertices == null) return null;

        MountedModel.Bone[] bones = skinned ? ReadBones(mdl) : null;
        if (bones is { Length: 0 }) bones = null;

        Material[][] skinMaterials = ReadSkins(mdl, content, out List<Material> made);

        int bodyPartCount = I32(mdl, 232), bodyPartIndex = I32(mdl, 236);
        int vtxBodyParts = I32(vtx, 28), vtxBodyPartOffset = I32(vtx, 32);
        if (vtxBodyParts != bodyPartCount) return null;

        var parts = new List<MountedModel.Part>();
        foreach (int stripGroupSize in new[] { 25, 33 })
        {
            parts.Clear();
            bool valid = true;
            for (int b = 0; b < bodyPartCount && valid; b++)
            {
                int bodyPart = bodyPartIndex + b * 16;
                if (I32(mdl, bodyPart + 4) == 0) continue;
                string partName = String(mdl, bodyPart + I32(mdl, bodyPart));
                int model = bodyPart + I32(mdl, bodyPart + 12);   // the first choice

                int vtxBodyPart = vtxBodyPartOffset + b * 8;
                int vtxModel = vtxBodyPart + I32(vtx, vtxBodyPart + 4);
                valid = BuildPart(mdl, vtx, vertices, model, vtxModel, stripGroupSize, partName, skinMaterials, bones, parts);
            }
            if (valid) break;
        }

        if (parts.Count == 0)
        {
            foreach (Material material in made) material.Dispose();
            return null;
        }
        return new MountedModel(name, parts, [], bones);
    }

    /// <summary>
    /// The skeleton in the pose the vertices were bound in, each bone relative to the model, with its parent, in
    /// the file's order, which puts parents first. Taken from each bone's pose-to-bone matrix rather than its
    /// default position and rotation, which can differ from the bind pose.
    /// </summary>
    private static MountedModel.Bone[] ReadBones(byte[] mdl)
    {
        int count = I32(mdl, 156), start = I32(mdl, 160);
        if (count <= 0 || start <= 0 || start + (long)count * BoneSize > mdl.Length) return [];

        var bones = new MountedModel.Bone[count];
        for (int i = 0; i < count; i++)
        {
            int bone = start + i * BoneSize;
            int parent = I32(mdl, bone + 4);
            if (parent >= i) return [];   // parents must come first

            // Pose-to-bone is a 3x4 matrix, rows first, taking the model into the bone. Its rows are the bone's
            // axes in the model, and the bone sits where it takes the origin back from.
            int m = bone + 96;
            var x = new Vector3(F32(mdl, m), F32(mdl, m + 4), F32(mdl, m + 8));
            var y = new Vector3(F32(mdl, m + 16), F32(mdl, m + 20), F32(mdl, m + 24));
            var z = new Vector3(F32(mdl, m + 32), F32(mdl, m + 36), F32(mdl, m + 40));
            var origin = -(x * F32(mdl, m + 12) + y * F32(mdl, m + 28) + z * F32(mdl, m + 44));
            var axes = new System.Numerics.Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f);
            Float3 position = GoldSrcAssets.Position(origin);
            Quaternion rotation = GoldSrcAssets.Rotation(System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.CreateFromRotationMatrix(axes)));
            string name = String(mdl, bone + I32(mdl, bone));
            bones[i] = new MountedModel.Bone(string.IsNullOrWhiteSpace(name) ? $"bone {i}" : name, position, rotation) { Parent = parent };
        }
        return bones;
    }

    private struct Vertex
    {
        public Vector3 Position, Normal;
        public Float2 UV;
        public Float4 Bones, Weights;
    }

    /// <summary>The highest detail vertices, put in order by the fixup table where the file has one.</summary>
    private static Vertex[] ReadVertices(byte[] vvd)
    {
        if (vvd.Length < 64 || Encoding.ASCII.GetString(vvd, 0, 4) != "IDSV") return null;
        int lod0 = I32(vvd, 16);
        int fixupCount = I32(vvd, 48), fixupStart = I32(vvd, 52), dataStart = I32(vvd, 56);
        if (lod0 < 0 || dataStart <= 0 || dataStart + (long)lod0 * VertexSize > vvd.Length) return null;

        Vertex Read(int index)
        {
            int at = dataStart + index * VertexSize;
            return new Vertex
            {
                Position = new Vector3(F32(vvd, at + 16), F32(vvd, at + 20), F32(vvd, at + 24)),
                Normal = new Vector3(F32(vvd, at + 28), F32(vvd, at + 32), F32(vvd, at + 36)),
                UV = new Float2(F32(vvd, at + 40), F32(vvd, at + 44)),
                // Up to three bones, each with its weight, then how many are used. Prowl counts bones from 1,
                // keeping 0 for none.
                Bones = new Float4(
                    vvd[at + 15] > 0 ? vvd[at + 12] + 1 : 0f,
                    vvd[at + 15] > 1 ? vvd[at + 13] + 1 : 0f,
                    vvd[at + 15] > 2 ? vvd[at + 14] + 1 : 0f, 0f),
                Weights = new Float4(
                    vvd[at + 15] > 0 ? F32(vvd, at) : 0f,
                    vvd[at + 15] > 1 ? F32(vvd, at + 4) : 0f,
                    vvd[at + 15] > 2 ? F32(vvd, at + 8) : 0f, 0f),
            };
        }

        var vertices = new Vertex[lod0];
        if (fixupCount <= 0)
        {
            for (int i = 0; i < lod0; i++) vertices[i] = Read(i);
            return vertices;
        }

        int count = 0;
        for (int f = 0; f < fixupCount; f++)
        {
            int at = fixupStart + f * 12;
            int lod = I32(vvd, at), source = I32(vvd, at + 4), n = I32(vvd, at + 8);
            if (lod < 0) continue;
            for (int i = 0; i < n && count < lod0; i++)
            {
                if (dataStart + (long)(source + i + 1) * VertexSize > vvd.Length) return null;
                vertices[count++] = Read(source + i);
            }
        }
        return vertices;
    }

    /// <summary>A material per texture for each skin family, by the model's skin table.</summary>
    private static Material[][] ReadSkins(byte[] mdl, IContent content, out List<Material> made)
    {
        made = new List<Material>();
        int textureCount = I32(mdl, 204), textureIndex = I32(mdl, 208);
        int cdCount = I32(mdl, 212), cdIndex = I32(mdl, 216);
        int skinRefs = I32(mdl, 220), families = Math.Max(1, I32(mdl, 224)), skinIndex = I32(mdl, 228);

        var dirs = new List<string>();
        for (int i = 0; i < cdCount; i++) dirs.Add(String(mdl, I32(mdl, cdIndex + i * 4)));
        if (dirs.Count == 0) dirs.Add("");

        var byTexture = new Material[textureCount];
        Material missing = null;
        for (int t = 0; t < textureCount; t++)
        {
            int texture = textureIndex + t * 64;
            string textureName = String(mdl, texture + I32(mdl, texture));
            bool found = false;
            foreach (string dir in dirs)
            {
                SourceMaterial material = content.Material(dir.Replace('\\', '/').TrimEnd('/') + "/" + textureName.Replace('\\', '/'));
                if (material == null) continue;
                byTexture[t] = material.NoDraw ? null : material.Material;
                found = true;
                break;
            }
            if (!found)
            {
                if (missing == null)
                {
                    missing = Materials.Lit(new Color(0.6f, 0.6f, 0.6f, 1f));
                    made.Add(missing);
                }
                byTexture[t] = missing;
            }
        }

        var skins = new Material[families][];
        for (int family = 0; family < families; family++)
        {
            skins[family] = new Material[Math.Max(skinRefs, textureCount)];
            for (int r = 0; r < skins[family].Length; r++)
            {
                int texture = r;
                if (r < skinRefs && skinIndex > 0)
                {
                    int at = skinIndex + (family * skinRefs + r) * 2;
                    if (at + 2 <= mdl.Length) texture = BitConverter.ToInt16(mdl, at);
                }
                skins[family][r] = texture >= 0 && texture < textureCount ? byTexture[texture] : missing;
            }
        }
        return skins;
    }

    /// <summary>One body part's first model at its highest detail. False if the strip layout did not fit.</summary>
    private static bool BuildPart(byte[] mdl, byte[] vtx, Vertex[] vertices, int model, int vtxModel, int stripGroupSize,
        string partName, Material[][] skins, MountedModel.Bone[] bones, List<MountedModel.Part> parts)
    {
        int meshCount = I32(mdl, model + 72), meshIndex = I32(mdl, model + 76);
        int modelFirstVertex = I32(mdl, model + 84) / VertexSize;
        string modelName = String(mdl, model, 64);

        if (I32(vtx, vtxModel) <= 0) return true;   // no detail levels
        int lod = vtxModel + I32(vtx, vtxModel + 4);
        int vtxMeshCount = I32(vtx, lod), vtxMeshOffset = I32(vtx, lod + 4);
        if (vtxMeshCount != meshCount) return false;

        var positions = new List<Float3>();
        var normals = new List<Float3>();
        var uvs = new List<Float2>();
        var boneIndices = new List<Float4>();
        var boneWeights = new List<Float4>();
        var indices = new List<uint>();
        var submeshes = new List<SubMeshDescriptor>();
        var meshMaterials = new List<int>();

        for (int m = 0; m < meshCount; m++)
        {
            int mesh = model + meshIndex + m * 116;
            int material = I32(mdl, mesh);
            if (material >= 0 && material < skins[0].Length && skins[0][material] == null) continue;   // never drawn
            int meshFirstVertex = modelFirstVertex + I32(mdl, mesh + 12);

            int vtxMesh = lod + vtxMeshOffset + m * 9;
            int groupCount = I32(vtx, vtxMesh), groupOffset = I32(vtx, vtxMesh + 4);
            int start = indices.Count;

            for (int g = 0; g < groupCount; g++)
            {
                int group = vtxMesh + groupOffset + g * stripGroupSize;
                if (group + stripGroupSize > vtx.Length) return false;
                int vertCount = I32(vtx, group), vertOffset = I32(vtx, group + 4);
                int indexCount = I32(vtx, group + 8), indexOffset = I32(vtx, group + 12);
                if (vertCount < 0 || indexCount < 0 || indexCount % 3 != 0) return false;
                if (group + vertOffset + (long)vertCount * 9 > vtx.Length || group + indexOffset + (long)indexCount * 2 > vtx.Length) return false;

                int baseVertex = positions.Count;
                for (int v = 0; v < vertCount; v++)
                {
                    int original = BitConverter.ToUInt16(vtx, group + vertOffset + v * 9 + 4);
                    int index = meshFirstVertex + original;
                    if (index < 0 || index >= vertices.Length) return false;
                    Vertex vertex = vertices[index];
                    positions.Add(GoldSrcAssets.Position(vertex.Position));
                    normals.Add(GoldSrcAssets.Direction(vertex.Normal));
                    uvs.Add(vertex.UV);
                    boneIndices.Add(vertex.Bones);
                    boneWeights.Add(vertex.Weights);
                }

                for (int i = 0; i < indexCount; i += 3)
                {
                    int a = BitConverter.ToUInt16(vtx, group + indexOffset + i * 2);
                    int b = BitConverter.ToUInt16(vtx, group + indexOffset + (i + 1) * 2);
                    int c = BitConverter.ToUInt16(vtx, group + indexOffset + (i + 2) * 2);
                    if (a >= vertCount || b >= vertCount || c >= vertCount) return false;
                    // Kept in the file's order: mirroring into Prowl's space turns it the way Prowl draws as the front.
                    indices.Add((uint)(baseVertex + a));
                    indices.Add((uint)(baseVertex + b));
                    indices.Add((uint)(baseVertex + c));
                }
            }

            if (indices.Count == start) continue;
            submeshes.Add(new SubMeshDescriptor(start, indices.Count - start));
            meshMaterials.Add(material);
        }

        if (submeshes.Count == 0) return true;

        var prowlMesh = new Mesh
        {
            Name = string.IsNullOrWhiteSpace(modelName) ? partName : modelName,
            IndexFormat = positions.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16,
            Vertices = [.. positions],
            Normals = [.. normals],
            UV = [.. uvs],
            Indices = [.. indices],
        };
        if (bones != null)
        {
            prowlMesh.BoneIndices = [.. boneIndices];
            prowlMesh.BoneWeights = [.. boneWeights];
            var bindPoses = new Float4x4[bones.Length];
            var boneNames = new string[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                Float4x4.Invert(Float4x4.CreateTRS(bones[i].Position, bones[i].Rotation, Float3.One), out bindPoses[i]);
                boneNames[i] = bones[i].Name;
            }
            prowlMesh.BindPoses = bindPoses;
            prowlMesh.BoneNames = boneNames;
        }
        prowlMesh.SetSubMeshCount(submeshes.Count);
        for (int i = 0; i < submeshes.Count; i++) prowlMesh.SetSubMesh(i, submeshes[i]);
        prowlMesh.RecalculateBounds();

        var perSkin = new Material[skins.Length][];
        for (int s = 0; s < skins.Length; s++)
        {
            perSkin[s] = new Material[meshMaterials.Count];
            for (int i = 0; i < meshMaterials.Count; i++)
            {
                int r = meshMaterials[i];
                Material material = r >= 0 && r < skins[s].Length ? skins[s][r] : null;
                if (material.IsNotValid() && s > 0) material = perSkin[0][i];
                if (material.IsNotValid()) material = skins[0][0];
                perSkin[s][i] = material;
            }
        }

        parts.Add(new MountedModel.Part(partName, prowlMesh, perSkin[0]) { Skins = perSkin });
        return true;
    }

    private static int I32(byte[] data, int at) => at >= 0 && at + 4 <= data.Length ? BitConverter.ToInt32(data, at) : 0;

    private static float F32(byte[] data, int at) => BitConverter.ToSingle(data, at);

    private static string String(byte[] data, int at, int max = 256)
    {
        if (at < 0 || at >= data.Length) return "";
        int end = at;
        while (end < data.Length && end - at < max && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, at, end - at);
    }
}
