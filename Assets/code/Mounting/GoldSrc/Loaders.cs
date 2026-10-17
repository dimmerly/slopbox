// Ported from s&box's Sandbox.Mounting.GoldSrc (Copyright (c) 2025 Facepunch Studios Ltd, MIT licensed).
// Models are built as static meshes posed on their first sequence's first frame, rather than s&box's skinned,
// animated models, since a prop only needs the pose.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Slopbox.Mounting.GoldSrc.Mdl;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Slopbox.Mounting.GoldSrc;

/// <summary>Turns GoldSrc data into Prowl assets.</summary>
internal static class GoldSrcAssets
{
    /// <summary>Metres per GoldSrc unit. The games treat a unit as an inch.</summary>
    public const float Scale = 0.0254f;

    /// <summary>
    /// From GoldSrc's right handed space (X forward, Y left, Z up) to Prowl's left handed one (X right, Y up,
    /// Z forward). Studio models face +X, which becomes +Z, the way a prop faces.
    /// </summary>
    public static Float3 Position(Vector3 v) => new(-v.Y * Scale, v.Z * Scale, v.X * Scale);

    public static Float3 Direction(Vector3 v) => Float3.Normalize(new Float3(-v.Y, v.Z, v.X));

    /// <summary>
    /// A GoldSrc rotation in Prowl's space. Mirroring (x, y, z) to (-y, z, x) carries a rotation's axis the same
    /// way and turns its angle around, which for a quaternion's vector part is (y, -z, -x).
    /// </summary>
    public static Quaternion Rotation(System.Numerics.Quaternion q) => new(q.Y, -q.Z, -q.X, q.W);

    /// <summary>A texture from RGBA bytes, top row first, as the UVs expect.</summary>
    public static Texture2D Texture(string name, int width, int height, byte[] rgba, bool mipmaps = true)
    {
        var texture = new Texture2D((uint)width, (uint)height, TextureFormat.RGBA8, mipmaps, srgb: true) { Name = name };
        texture.SetData(new Memory<byte>(rgba));
        if (mipmaps) texture.GenerateMipmaps();
        return texture;
    }

    /// <summary>A Lit material for a GoldSrc texture and its render flags.</summary>
    public static Material Material(string name, Texture2D texture, LightingFlags flags)
    {
        var material = new Material(Shader.LoadDefault(DefaultShader.Lit)) { Name = name };
        material.SetTexture("_MainTex", texture);
        material.SetColor("_MainColor", Color.White);
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Roughness", (flags & LightingFlags.Chrome) != 0 ? 0.35f : 0.85f);

        if ((flags & LightingFlags.Additive) != 0)
        {
            material.Surface = new SurfaceSettings(SurfaceType.Transparent, SurfaceBlend.Additive, SurfaceCull.Off);
        }
        else if ((flags & LightingFlags.Masked) != 0)
        {
            material.Surface = new SurfaceSettings(SurfaceType.Cutout, SurfaceBlend.Alpha, SurfaceCull.Off);
            material.SetFloat("_AlphaCutoff", 0.5f);
        }

        // Fullbright surfaces, such as screens and lamps, light themselves.
        if ((flags & LightingFlags.FullBright) != 0)
        {
            material.SetInt("_Emission", 1);
            material.SetTexture("_EmissionTex", texture);
            material.SetColor("_EmissiveColor", Color.White);
            material.SetFloat("_EmissionIntensity", 1f);
        }

        return material;
    }
}

/// <summary>A studio model (.mdl), with its texture file ("t.mdl") and sequence files ("01.mdl") beside it.</summary>
internal sealed class ModelLoader(string fullPath) : ResourceLoader<GameMount>
{
    protected override object Load()
    {
        byte[] bytes = File.ReadAllBytes(fullPath);
        MdlFile mdl = MdlFile.FromStream(new MemoryStream(bytes));
        string stem = System.IO.Path.ChangeExtension(fullPath, null);

        // Older models keep their textures in a second file.
        MdlFile textureMdl = mdl;
        if (mdl.Header.NumTextures == 0 && File.Exists(stem + "t.mdl"))
            textureMdl = MdlFile.FromStream(new MemoryStream(File.ReadAllBytes(stem + "t.mdl")));

        // The meshes are built in the resting sequence's first frame, which is also the skeleton's rest pose.
        int rest = mdl.FindIdleSequence();
        BoneTransform[] pose;
        var groups = new Dictionary<int, BinaryReader>();
        using (var self = new BinaryReader(new MemoryStream(bytes)))
        {
            pose = mdl.GetPose(self, group =>
            {
                if (groups.TryGetValue(group, out BinaryReader open)) return open;
                string file = $"{stem}{group:D2}.mdl";
                open = File.Exists(file) ? new BinaryReader(new MemoryStream(File.ReadAllBytes(file))) : null;
                groups[group] = open;
                return open;
            }, rest);
        }
        foreach (BinaryReader reader in groups.Values) reader?.Dispose();

        // The skeleton in Prowl's space, flat: every bone is placed relative to the model, not to its parent.
        var bones = new MountedModel.Bone[pose.Length];
        var bindPoses = new Float4x4[pose.Length];
        var boneNames = new string[pose.Length];
        for (int i = 0; i < pose.Length; i++)
        {
            Float3 position = GoldSrcAssets.Position(pose[i].Position);
            Quaternion rotation = GoldSrcAssets.Rotation(pose[i].Rotation);
            string name = mdl.Bones[i].Name;
            bones[i] = new MountedModel.Bone(string.IsNullOrWhiteSpace(name) ? $"bone {i}" : name, position, rotation);
            boneNames[i] = $"{i} {bones[i].Name}";
            Float4x4.Invert(Float4x4.CreateTRS(position, rotation, Float3.One), out bindPoses[i]);
        }

        var parts = new List<MountedModel.Part>();
        var textures = new List<Texture2D>();
        var materials = new Dictionary<int, Material>();

        Material MaterialFor(int textureIndex)
        {
            if (materials.TryGetValue(textureIndex, out Material material)) return material;

            TextureImage image = textureMdl.GetTexture(textureIndex);
            if (image == null)
            {
                material = Materials.Lit(new Color(0.6f, 0.6f, 0.6f, 1f));
            }
            else
            {
                Texture2D texture = GoldSrcAssets.Texture(image.Name, image.Width, image.Height, image.Rgba,
                    mipmaps: (image.Flags & LightingFlags.NoMips) == 0);
                textures.Add(texture);
                material = GoldSrcAssets.Material(image.Name, texture, image.Flags);
            }
            materials[textureIndex] = material;
            return material;
        }

        // Each body part shows its first choice, as the game does by default.
        for (int bodyPartIndex = 0; bodyPartIndex < mdl.NumBodyParts; ++bodyPartIndex)
        {
            BodyParts bodyPart = mdl.GetBodyPart(bodyPartIndex);
            if (bodyPart.NumModels == 0) continue;

            const int modelIndex = 0;
            Mdl.Model model = mdl.GetModel(bodyPartIndex, modelIndex);
            if (model.NumMesh == 0 || model.NumVerts == 0) continue;

            var vertices = new Vector3[model.NumVerts];
            var normals = new Vector3[model.NumNorms];
            var vertexBones = new byte[model.NumVerts];
            var normalBones = new byte[model.NumNorms];
            mdl.GetVertices(bodyPartIndex, modelIndex, vertices);
            mdl.GetNormals(bodyPartIndex, modelIndex, normals);
            mdl.GetBoneVertices(bodyPartIndex, modelIndex, vertexBones);
            mdl.GetBoneNormals(bodyPartIndex, modelIndex, normalBones);

            var positions = new List<Float3>();
            var outNormals = new List<Float3>();
            var uvs = new List<Float2>();
            var boneIndices = new List<Float4>();
            var indices = new List<uint>();
            var submeshes = new List<SubMeshDescriptor>();
            var partMaterials = new List<Material>();

            for (int meshIndex = 0; meshIndex < model.NumMesh; ++meshIndex)
            {
                Mdl.Mesh mesh = mdl.GetMesh(bodyPartIndex, modelIndex, meshIndex);
                if (mesh.NumTris == 0) continue;

                int textureIndex = textureMdl.GetTextureIndex(mesh.SkinRef);
                TextureImage image = textureMdl.GetTexture(textureIndex);
                float s = 1f / (image?.Width ?? 64), t = 1f / (image?.Height ?? 64);

                var triVerts = new TriVert[mesh.NumTris * 3];
                mdl.GetTriVerts(bodyPartIndex, modelIndex, meshIndex, triVerts);

                int start = indices.Count;
                var vertexMap = new Dictionary<TriVert, uint>();

                // Mirroring into Prowl's left handed space turns the winding around, so each triangle is read backwards.
                for (int corner = triVerts.Length - 1; corner >= 0; corner--)
                {
                    TriVert triVert = triVerts[corner];
                    if (!vertexMap.TryGetValue(triVert, out uint index))
                    {
                        int v = Math.Clamp((int)triVert.VertIndex, 0, vertices.Length - 1);
                        int n = Math.Clamp((int)triVert.NormIndex, 0, Math.Max(0, normals.Length - 1));
                        BoneTransform vertexBone = Bone(pose, vertexBones[v]);

                        index = (uint)positions.Count;
                        positions.Add(GoldSrcAssets.Position(vertexBone.PointToWorld(vertices[v])));
                        outNormals.Add(normals.Length > 0
                            ? GoldSrcAssets.Direction(Bone(pose, normalBones[n]).NormalToWorld(normals[n]))
                            : Float3.UnitY);
                        uvs.Add(new Float2(triVert.S * s, triVert.T * t));
                        // Prowl's skinning counts bones from 1; 0 means no bone.
                        boneIndices.Add(new Float4(vertexBones[v] < pose.Length ? vertexBones[v] + 1 : 0, 0f, 0f, 0f));
                        vertexMap[triVert] = index;
                    }
                    indices.Add(index);
                }

                submeshes.Add(new SubMeshDescriptor(start, indices.Count - start));
                partMaterials.Add(MaterialFor(textureIndex));
            }

            if (submeshes.Count == 0) continue;

            var prowlMesh = new Prowl.Runtime.Resources.Mesh
            {
                Name = string.IsNullOrWhiteSpace(model.Name) ? bodyPart.Name : model.Name,
                IndexFormat = positions.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16,
                Vertices = [.. positions],
                Normals = [.. outNormals],
                UV = [.. uvs],
                Indices = [.. indices],
            };
            prowlMesh.SetSubMeshCount(submeshes.Count);
            for (int i = 0; i < submeshes.Count; i++) prowlMesh.SetSubMesh(i, submeshes[i]);
            prowlMesh.RecalculateBounds();

            // Every vertex follows exactly one bone. The bind poses undo the rest pose the vertices were built in.
            if (bindPoses.Length > 0)
            {
                prowlMesh.BoneIndices = [.. boneIndices];
                prowlMesh.BoneWeights = Enumerable.Repeat(new Float4(1f, 0f, 0f, 0f), positions.Count).ToArray();
                prowlMesh.BindPoses = bindPoses;
                prowlMesh.BoneNames = boneNames;
            }

            parts.Add(new MountedModel.Part(bodyPart.Name, prowlMesh, [.. partMaterials]));
        }

        if (bones.Length == 0) return new MountedModel(Name, parts, textures);
        return new MountedModel(Name, parts, textures, bones, new MdlAnimation(mdl, bytes, stem, rest));
    }

    private static BoneTransform Bone(BoneTransform[] pose, int index)
        => index < pose.Length ? pose[index] : BoneTransform.Identity;
}

/// <summary>A mip texture from a WAD3 archive.</summary>
internal sealed class WadTextureLoader(Wad wad, string lumpName) : ResourceLoader<GameMount>
{
    protected override object Load()
    {
        byte[] lumpData = wad.GetLumpData(lumpName);
        if (lumpData == null) return null;

        using var ms = new MemoryStream(lumpData);
        using var br = new BinaryReader(ms);

        string textureName = Mdl.MdlFile.GetNullTerminatedString(br.ReadBytes(16));
        int width = br.ReadInt32();
        int height = br.ReadInt32();
        var offsets = new uint[4];
        for (int i = 0; i < 4; i++) offsets[i] = br.ReadUInt32();
        if (offsets[0] == 0 || width <= 0 || height <= 0) return null;

        // The palette follows the smallest mip.
        ms.Seek((width * height / 64) + offsets[3], SeekOrigin.Begin);
        short paletteSize = br.ReadInt16();
        if (paletteSize != 256)
        {
            Debug.LogWarning($"Unexpected palette size {paletteSize} in {Path}");
            return null;
        }
        byte[] palette = br.ReadBytes(768);

        ms.Seek(offsets[0], SeekOrigin.Begin);
        int length = width * height;
        byte[] data = br.ReadBytes(length);

        // "{" textures are cut out where they use the last palette entry.
        bool masked = textureName.StartsWith('{');
        var rgba = new byte[length * 4];
        for (int i = 0, o = 0; i < length; i++)
        {
            int index = data[i], p = index * 3;
            rgba[o++] = palette[p];
            rgba[o++] = palette[p + 1];
            rgba[o++] = palette[p + 2];
            rgba[o++] = masked && index == 255 ? (byte)0 : (byte)255;
        }

        return GoldSrcAssets.Texture(textureName, width, height, rgba);
    }
}

/// <summary>A material for a WAD texture.</summary>
internal sealed class WadMaterialLoader(WadTextureLoader texture) : ResourceLoader<GameMount>
{
    protected override object Load()
    {
        var tex = texture.GetOrCreate<Texture2D>();
        if (tex.IsNotValid()) return null;
        LightingFlags flags = texture.Name.StartsWith('{') ? LightingFlags.Masked : 0;
        return GoldSrcAssets.Material(Name, tex, flags);
    }
}

/// <summary>A sound file.</summary>
internal sealed class WavSoundLoader(string fullPath) : ResourceLoader<GameMount>
{
    protected override object Load() => new AudioClip(File.ReadAllBytes(fullPath), isUnique: true) { ClipName = Name };
}
