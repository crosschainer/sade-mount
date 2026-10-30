using System.Numerics;
using Sade.Contracts;
using Sade.Formats;

internal static class WorldPropTests
{
    internal static void Run()
    {
        var catalog = WorldPropCatalog.Parse("; object.dat separators and special responses\ncrate, 20, 20 0.99, 0.03, 50, 0, 2.5, 20, 2, 1, 0\nlamp 100 100 .99 .1 50 100 1 0 1 0 0\ndoor 30 30 .99 .1 50 0 1 0 6 0 0\nfixed 99999 100 .99 .1 50 0 1 0 0 0 0\n");
        ModelData Model(string name) => new() { Id = "/Game/SM_" + name + ".SM_" + name, Parts = [], BoundsMin = new(-50, -50, 0), BoundsMax = new(50, 50, 100) };
        void Check(bool value, string text) { if (!value) throw new Exception(text); }
        var crate = Model("crate");
        Check(catalog.Get(crate)?.Mass == 20, "Only a matching authored physical definition supplies a prop's mass.");
        Check(catalog.Get(Model("road")) is null && catalog.Get(Model("lamp")) is null && catalog.Get(Model("door")) is null && catalog.Get(Model("fixed")) is null,
            "Unknown models, anchored special responses and immovable sentinels must stay fixed.");
        Check(catalog.Get(crate with { BoundsMax = new(5000) }) is null, "Building-sized pieces must not become movable.");
        Check(catalog.Get(crate with { BoundsMin = new(250000), BoundsMax = new(250100) }) is null, "World-baked terrain must not become movable.");
        Check(catalog.Get(crate with { Bones = [new("root", -1, TransformData.Identity, TransformData.Identity)] }) is null, "Skeletal map pieces must not become loose props.");
        VertexData V(Vector3 p) => new(p, Vector3.UnitZ, default, default, Vector4.One, default, default);
        var vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        var geometry = crate with { Parts = [new("crate", null, vertices.Select(V).ToArray(), [0, 1, 2, 0, 2, 3])] };
        var collision = WorldPropCatalog.MovableCollision(geometry);
        Check(collision.Hulls.Length == 1 && collision.Indices.Length == 0 && collision.Hulls[0].Length == 4, "A loose prop without authored convex collision gets a small convex body, never a simulated triangle mesh.");
        var authored = geometry with { Collision = new CollisionData { Boxes = [new(Vector3.Zero, Vector3.One, Quaternion.Identity)], Vertices = vertices, Indices = [0, 1, 2] } };
        var retained = WorldPropCatalog.MovableCollision(authored);
        Check(ReferenceEquals(retained.Boxes, authored.Collision.Boxes) && retained.Indices.Length == 0, "Movable props keep authored convex primitives and omit static concave triangles.");
    }
}
