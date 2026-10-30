using System.Runtime.InteropServices;
using Sandbox;
using Sade.Contracts;

namespace Sade.Mount;

/// <summary>Map geometry has an owned lifetime, independent of permanently cached spawn-menu resources.</summary>
internal static class WorldModelBuilder
{
    internal static Model Build(ModelData data, string name, string?[] overrides, Func<string?, Material> material, float lodDistance)
    {
        var builder = Model.Builder.WithName(name).WithMass(data.WorldProp?.Mass ?? 1000);
        foreach (var part in data.Parts)
        {
            if (part.Lod > 3 || part.Vertices.Length == 0 || part.Indices.Length == 0) continue;
            var vertices = new Vertex[part.Vertices.Length];
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            for (var i = 0; i < vertices.Length; i++)
            {
                var v = part.Vertices[i]; var p = CoordinateSpace.Position(v.Position);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                vertices[i] = new(p, CoordinateSpace.Direction(v.Normal),
                    new Vector4(v.Tangent.X, -v.Tangent.Y, v.Tangent.Z, -v.Tangent.W),
                    new Vector2(v.UV.X, v.UV.Y), new Color(v.Color.X, v.Color.Y, v.Color.Z, v.Color.W));
            }
            if (part.Indices.Any(i => (uint)i >= vertices.Length)) throw new InvalidDataException(data.Id + ": invalid world mesh indices.");
            var mesh = new Mesh(material(MaterialId(part, overrides)));
            mesh.CreateVertexBuffer(vertices.Length, vertices);
            mesh.CreateIndexBuffer(part.Indices.Length, CoordinateSpace.Indices(part.Indices));
            mesh.Bounds = new(min, max);
            builder.AddMesh(mesh, part.Lod).WithLodDistance(part.Lod, part.Lod * lodDistance);
            if (part.Lod == 0) builder.AddTraceMesh(vertices.Select(v => v.Position).ToArray(), CoordinateSpace.Indices(part.Indices));
        }
        ModelLoader.AddCollision(builder, data.Collision);
        return builder.Create();
    }

    internal static string? MaterialId(MeshPartData part, string?[] overrides)
    {
        // Reader section names retain their original material-slot index.
        var fields = part.Name.Split('_');
        return fields.Length > 1 && int.TryParse(fields[1], out var slot) && slot >= 0 && slot < overrides.Length
            && overrides[slot] is { } replacement ? replacement : part.MaterialId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Vertex(Vector3 position, Vector3 normal, Vector4 tangent, Vector2 uv, Color32 color)
    {
        [VertexLayout.Position] public Vector3 Position = position;
        [VertexLayout.Normal] public Vector3 Normal = normal;
        [VertexLayout.Tangent] public Vector4 Tangent = tangent;
        [VertexLayout.TexCoord] public Vector2 UV = uv;
        [VertexLayout.Color] public Color32 Color = color;
    }
}
