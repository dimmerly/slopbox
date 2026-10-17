using System;
using System.Runtime.CompilerServices;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox.Mounting;

/// <summary>
/// What surface each mounted material is, such as "concrete" or "metal_box", the way Source materials name it
/// with $surfaceprop, and which material lies at a point a ray hit.
/// </summary>
public static class MaterialSurfaces
{
    private static readonly ConditionalWeakTable<Material, string> s_surfaces = new();

    public static void Set(Material material, string surface)
    {
        if (material.IsNotValid() || string.IsNullOrWhiteSpace(surface)) return;
        s_surfaces.AddOrUpdate(material, surface.Trim().ToLowerInvariant());
    }

    /// <summary>The surface a material was made with, or null.</summary>
    public static string Get(Material material)
        => material.IsValid() && s_surfaces.TryGetValue(material, out string surface) ? surface : null;

    /// <summary>
    /// The material drawn where a ray from <paramref name="origin"/> along <paramref name="direction"/> hit: for a
    /// mesh collider, the submesh of the triangle hit; otherwise the first material of the object's renderer.
    /// </summary>
    public static Material MaterialAt(RaycastHit hit, Float3 origin, Float3 direction)
    {
        if (hit.Transform == null) return null;
        GameObject go = hit.Transform.GameObject;

        if (hit.Collider is MeshCollider { Mesh: { } mesh } collider && mesh.IsValid()
            && collider.GameObject.GetComponent<MeshRenderer>() is { } renderer && renderer.Materials is { Count: > 0 } materials)
        {
            int submesh = SubmeshHit(mesh, collider.Transform, origin, direction);
            if (submesh >= 0 && submesh < materials.Count) return materials[submesh];
            return materials[0];
        }

        MeshRenderer any = go.GetComponent<MeshRenderer>();
        if (any.IsNotValid()) any = go.GetComponentInChildren<MeshRenderer>();
        return any.IsValid() && any.Materials is { Count: > 0 } list ? list[0] : null;
    }

    /// <summary>A mesh's triangles, read once: corners, and the submesh each belongs to.</summary>
    private sealed class Triangles
    {
        public Float3[] Vertices;
        public uint[] Indices;
        public int[] Submesh;   // per triangle
    }

    private static readonly ConditionalWeakTable<Mesh, Triangles> s_triangles = new();

    private static Triangles TrianglesOf(Mesh mesh)
    {
        if (s_triangles.TryGetValue(mesh, out Triangles cached)) return cached;
        var triangles = new Triangles { Vertices = mesh.Vertices, Indices = mesh.Indices };
        if (triangles.Vertices == null || triangles.Indices == null) return null;
        triangles.Submesh = new int[triangles.Indices.Length / 3];
        for (int s = 0; s < mesh.SubMeshCount; s++)
        {
            SubMeshDescriptor sub = mesh.GetSubMesh(s);
            for (int i = sub.IndexStart / 3; i < (sub.IndexStart + sub.IndexCount) / 3 && i < triangles.Submesh.Length; i++)
                triangles.Submesh[i] = s;
        }
        s_triangles.AddOrUpdate(mesh, triangles);
        return triangles;
    }

    /// <summary>
    /// The submesh of the nearest triangle the ray crosses within <paramref name="reach"/> metres, or -1.
    /// Triangles wholly off to one side of the ray's reach are skipped before the exact test.
    /// </summary>
    private static int SubmeshHit(Mesh mesh, Transform transform, Float3 origin, Float3 direction, float reach = 4f)
    {
        Triangles triangles = TrianglesOf(mesh);
        if (triangles == null) return -1;
        Float3[] v = triangles.Vertices;
        uint[] indices = triangles.Indices;

        Float3 o = transform.InverseTransformPoint(origin);
        Float3 end = transform.InverseTransformPoint(origin + direction * reach);
        Float3 d = Float3.Normalize(end - o);
        Float3 lo = Maths.Min(o, end), hi = Maths.Max(o, end);

        int best = -1;
        float nearest = float.MaxValue;
        for (int i = 0, tri = 0; i + 2 < indices.Length; i += 3, tri++)
        {
            Float3 a = v[indices[i]], b = v[indices[i + 1]], c = v[indices[i + 2]];
            if (MathF.Max(a.X, MathF.Max(b.X, c.X)) < lo.X || MathF.Min(a.X, MathF.Min(b.X, c.X)) > hi.X) continue;
            if (MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) < lo.Z || MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) > hi.Z) continue;
            if (MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) < lo.Y || MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) > hi.Y) continue;
            if (Intersect(o, d, a, b, c, out float t) && t < nearest)
            {
                nearest = t;
                best = triangles.Submesh[tri];
            }
        }
        return best;
    }

    // Möller–Trumbore, both sides.
    private static bool Intersect(Float3 o, Float3 d, Float3 a, Float3 b, Float3 c, out float t)
    {
        t = 0f;
        Float3 e1 = b - a, e2 = c - a;
        Float3 p = Float3.Cross(d, e2);
        float det = Float3.Dot(e1, p);
        if (det > -1e-7f && det < 1e-7f) return false;
        float inv = 1f / det;
        Float3 s = o - a;
        float u = Float3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        Float3 q = Float3.Cross(s, e1);
        float v = Float3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Float3.Dot(e2, q) * inv;
        return t >= 0f;
    }
}
