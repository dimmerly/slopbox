using System;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>Places spawnables where the player is looking, resting on the surface and facing them.</summary>
public static class Spawner
{
    public const float Reach = 1000f;

    /// <summary>Spawns at the crosshair and records an undo. Returns the new object, or null.</summary>
    public static GameObject SpawnAtCrosshair(Player player, Spawnable spawnable)
    {
        GameManager game = GameManager.Instance;
        Ray aim = player.AimRay;
        Float3 point, normal;
        if (player.Trace(Reach, out RaycastHit hit))
        {
            point = hit.Point;
            normal = hit.Normal;
        }
        else
        {
            point = aim.Origin + aim.Direction * 6f;
            normal = Float3.UnitY;
        }

        // Face the player, like gmod: the prop's front points back along the aim.
        Quaternion rotation = Quaternion.FromEuler(0f, player.Yaw + 180f, 0f);
        GameObject go = Spawn(spawnable, point, normal, rotation);
        if (go.IsValid())
        {
            game.Undo.Add(spawnable.Name, go);
            GameAudio.Play("spawn", 0.4f, 0.96f + Random.Shared.NextSingle() * 0.08f);
        }
        return go;
    }

    /// <summary>Builds <paramref name="spawnable"/> so it rests on a surface at <paramref name="point"/>.</summary>
    public static GameObject Spawn(Spawnable spawnable, Float3 point, Float3 normal, Quaternion rotation)
    {
        GameObject go;
        try
        {
            go = spawnable.Create();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            go = null;
        }

        if (go.IsNotValid())
        {
            Notify.Error($"Could not spawn {spawnable.Name}");
            return null;
        }

        // Push the object out along the normal until the bounds corner deepest into the surface touches it.
        AABB bounds = LocalBounds(go);
        Float3 localDown = Quaternion.Inverse(rotation) * -normal;
        Float3 deepest = new(
            localDown.X >= 0f ? bounds.Max.X : bounds.Min.X,
            localDown.Y >= 0f ? bounds.Max.Y : bounds.Min.Y,
            localDown.Z >= 0f ? bounds.Max.Z : bounds.Min.Z);
        float lift = -Float3.Dot(rotation * deepest, normal);

        go.Transform.Position = point + normal * (lift + 0.01f);
        go.Transform.Rotation = rotation;
        Scene.Current.Add(go);
        return go;
    }

    /// <summary>The bounds of every mesh under <paramref name="root"/>, in the root's local space.</summary>
    public static AABB LocalBounds(GameObject root)
    {
        Float3 min = new(float.MaxValue), max = new(float.MinValue);
        bool any = false;

        void Add(Mesh mesh, Transform transform)
        {
            if (mesh.IsNotValid()) return;
            foreach (Float3 corner in mesh.bounds.GetCorners())
            {
                Float3 local = root.Transform.InverseTransformPoint(transform.TransformPoint(corner));
                min = Maths.Min(min, local);
                max = Maths.Max(max, local);
                any = true;
            }
        }

        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true, true))
            Add(renderer.Mesh, renderer.Transform);

        // Skinned meshes are measured in their rest pose.
        foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true, true))
            Add(renderer.SharedMesh, renderer.Transform);

        return any ? new AABB(min, max) : new AABB(new Float3(-0.5f), new Float3(0.5f));
    }
}
