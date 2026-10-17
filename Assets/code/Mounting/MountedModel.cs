using System;
using System.Collections.Generic;
using System.Linq;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox.Mounting;

/// <summary>A sequence a mounted model can play, such as "idle1" or "walk".</summary>
public readonly record struct SequenceInfo(string Name, float Fps, int FrameCount, bool Loops);

/// <summary>The animations of a mounted model with a skeleton.</summary>
public interface IModelAnimation
{
    IReadOnlyList<SequenceInfo> Sequences { get; }

    /// <summary>The sequence the model rests in, and the pose its meshes were built in.</summary>
    int DefaultSequence { get; }

    /// <summary>
    /// Every bone's transform relative to the model at <paramref name="frame"/> of <paramref name="sequence"/>, in
    /// Prowl's space. A fractional frame blends the two around it.
    /// </summary>
    void Sample(int sequence, float frame, Float3[] positions, Quaternion[] rotations);
}

/// <summary>
/// A model built from a mounted game's file: a mesh per part, each with a material per submesh, in Prowl's
/// space (metres, Y up, facing +Z). What a <see cref="ResourceType.Model"/> loader gives. The meshes, materials
/// and textures are runtime assets shared by every instance; disposing the model frees them.
/// <para>
/// A model with <see cref="Bones"/> is skinned: its meshes are built in the bones' rest pose, each vertex
/// following one bone, and instances move their bones to animate.
/// </para>
/// </summary>
public sealed class MountedModel : IDisposable
{
    /// <summary>One mesh of the model.</summary>
    public sealed record Part(string Name, Mesh Mesh, Material[] Materials)
    {
        /// <summary>The materials of each skin, the first being <see cref="Materials"/>. Null for one skin.</summary>
        public Material[][] Skins { get; init; }
    }

    /// <summary>How many skins the model has to choose from.</summary>
    public int SkinCount => Parts.Count > 0 && Parts[0].Skins is { Length: > 0 } skins ? skins.Length : 1;

    /// <summary>
    /// Puts <paramref name="skin"/>'s materials on an <see cref="Instantiate"/>d copy of this model, as the
    /// entities of a map ask for, such as a door's colour.
    /// </summary>
    public void ApplySkin(GameObject root, int skin)
    {
        if (root.IsNotValid()) return;
        int i = 0;
        foreach (GameObject child in root.Children)
        {
            if (child.Name == "Skeleton") continue;
            if (i >= Parts.Count) break;
            Part part = Parts[i++];
            if (part.Skins is not { Length: > 0 } skins) continue;
            Material[] materials = skins[Math.Clamp(skin, 0, skins.Length - 1)];
            if (child.GetComponent<MeshRenderer>() is { } renderer) renderer.Materials = [.. materials];
            else if (child.GetComponent<SkinnedMeshRenderer>() is { } skinned) skinned.Materials = [.. materials];
        }
    }

    /// <summary>A bone, in its rest pose relative to the model.</summary>
    public sealed record Bone(string Name, Float3 Position, Quaternion Rotation)
    {
        /// <summary>The index of the bone it hangs from, or -1. Only skeletons kept as a hierarchy have parents.</summary>
        public int Parent { get; init; } = -1;
    }

    /// <summary>True if the bones form a hierarchy, as a character's do, rather than all hanging from the model.</summary>
    public bool HasHierarchy => Bones.Any(b => b.Parent >= 0);

    public string Name { get; }
    public IReadOnlyList<Part> Parts { get; }
    public IReadOnlyList<Bone> Bones { get; }

    /// <summary>The model's sequences, or null for a model that cannot animate.</summary>
    public IModelAnimation Animation { get; }

    /// <summary>Every texture the materials use, kept for disposal.</summary>
    public IReadOnlyList<Texture2D> Textures { get; }

    public MountedModel(string name, IReadOnlyList<Part> parts, IReadOnlyList<Texture2D> textures,
        IReadOnlyList<Bone> bones = null, IModelAnimation animation = null)
    {
        Name = name;
        Parts = parts;
        Textures = textures;
        Bones = bones ?? [];
        Animation = Bones.Count > 0 ? animation : null;
    }

    /// <summary>
    /// A new object showing the model, a child per part, not in any scene. A skinned model also gets its bones
    /// under a "Skeleton" child and a <see cref="ModelAnimator"/> playing its resting sequence. Bones in a
    /// <see cref="HasHierarchy"/> skeleton keep their own names and hang from their parents, for an
    /// <see cref="Animator"/> to bind by name.
    /// </summary>
    public GameObject Instantiate()
    {
        var root = new GameObject(Name);

        Transform[] bones = null;
        if (Bones.Count > 0)
        {
            var skeleton = new GameObject("Skeleton");
            skeleton.SetParent(root, false);
            bones = new Transform[Bones.Count];
            bool hierarchy = HasHierarchy;
            for (int i = 0; i < Bones.Count; i++)
            {
                Bone source = Bones[i];
                // Numbered when flat, so two bones of one name still resolve to their own paths.
                var bone = new GameObject(hierarchy ? source.Name : $"{i} {source.Name}");
                bone.SetParent(hierarchy && source.Parent >= 0 ? bones[source.Parent].GameObject : skeleton, false);
                // Placed relative to the model; the parents are already in place.
                bone.Transform.Position = root.Transform.TransformPoint(source.Position);
                bone.Transform.Rotation = root.Transform.Rotation * source.Rotation;
                bones[i] = bone.Transform;
            }
        }

        foreach (Part part in Parts)
        {
            var child = new GameObject(part.Name);
            child.SetParent(root, false);

            if (bones != null)
            {
                var skinned = child.AddComponent<SkinnedMeshRenderer>();
                skinned.SharedMesh = part.Mesh;
                skinned.Materials = [.. part.Materials];
                skinned.SetBones(bones, bones[0]);
            }
            else
            {
                var renderer = child.AddComponent<MeshRenderer>();
                renderer.Mesh = part.Mesh;
                renderer.Materials = [.. part.Materials];
            }
        }

        if (bones != null && Animation != null)
            root.AddComponent<ModelAnimator>().Bind(this, bones);

        return root;
    }

    public void Dispose()
    {
        var materials = new HashSet<Material>();
        foreach (Part part in Parts)
        {
            if (part.Mesh.IsValid()) part.Mesh.Dispose();
            foreach (Material material in part.Materials) materials.Add(material);
        }
        foreach (Material material in materials)
            if (material.IsValid()) material.Dispose();
        foreach (Texture2D texture in Textures)
            if (texture.IsValid()) texture.Dispose();
    }
}
