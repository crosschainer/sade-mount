using NVector3 = System.Numerics.Vector3;
using Sade.Contracts;

namespace Sade.Formats;

/// <summary>DE bakes some environment meshes into world space but leaves their classic COL local.</summary>
public static class BakedMeshCollision
{
    public static CollisionData Resolve(ModelData model, CollisionData legacy)
    {
        // Only replace an unmistakable coordinate-space mismatch. Small differences between
        // authored render and collision geometry are normal and must retain the authored COL.
        if (model.Bones.Length != 0 || legacy.Vertices.Length == 0) return legacy;
        var renderCenter = (model.BoundsMin + model.BoundsMax) * .5f;
        var collisionMin = legacy.Vertices.Aggregate(NVector3.Min);
        var collisionMax = legacy.Vertices.Aggregate(NVector3.Max);
        var gap = NVector3.Max(NVector3.Max(model.BoundsMin - collisionMax, collisionMin - model.BoundsMax), NVector3.Zero);
        if (renderCenter.Length() < 50_000 || gap.Length() < 5_000) return legacy;

        return FromMesh(model, "world-baked render geometry; local COL does not overlap");
    }

    public static ModelData ForStaticWorld(ModelData model)
    {
        // Simple convex collision fills streets, porches and underpasses in concave meshes.
        // Keep detailed surfaces for streamed static geometry; ordinary props retain their hulls.
        if (model.Bones.Length != 0 || model.Collision.Hulls.Length == 0) return model;
        return model with { Collision = FromMesh(model, "static world surfaces instead of convex simple collision") };
    }

    private static CollisionData FromMesh(ModelData model, string reason)
    {
        // The original placement is absent from the retained IPLs for many of these meshes.
        // Use actual LOD0 triangles rather than guessing a translation or filling holes with a box.
        var vertices = new List<NVector3>();
        var indices = new List<int>();
        foreach (var part in model.Parts.Where(p => p.Lod == 0))
        {
            if (part.Indices.Length % 3 != 0 || part.Indices.Any(i => (uint)i >= part.Vertices.Length))
                throw new InvalidDataException(model.Id + ": invalid collision mesh indices.");
            var offset = vertices.Count;
            vertices.AddRange(part.Vertices.Select(v => v.Position));
            indices.AddRange(part.Indices.Select(i => checked(i + offset)));
        }
        if (indices.Count == 0) throw new InvalidDataException(model.Id + ": baked mesh has no LOD0 collision triangles.");
        return new CollisionData
        {
            Vertices = vertices.ToArray(), Indices = indices.ToArray(),
            Source = "Derived LOD0 mesh (" + reason + ")"
        };
    }
}
