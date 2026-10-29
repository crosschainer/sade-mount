using N = System.Numerics;
using Sade.Contracts;

namespace Sade.Mount;

internal static class PlayerOutfit
{
    internal const string ModelId = "/Game/SanAndreas/Characters/Player/SK_Player_Combined.SK_Player_Combined";
    internal const string VestId = "/Game/SanAndreas/Characters/Player/SK_vest.SK_vest";
    internal const string JeansId = "/Game/SanAndreas/Characters/Player/SK_jeans.SK_jeans";

    internal static ModelData Dress(ModelData body, ModelData vest, ModelData jeans)
    {
        var names = body.Bones.Select((b, i) => (b.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.OrdinalIgnoreCase);
        bool IsTorso(MeshPartData part) => part.MaterialId?.EndsWith("/MI_player_torso.MI_player_torso", StringComparison.OrdinalIgnoreCase) == true &&
            part.Vertices.Any(v => IsSpine(v.Bones.X, v.Weights.X) || IsSpine(v.Bones.Y, v.Weights.Y) || IsSpine(v.Bones.Z, v.Weights.Z) || IsSpine(v.Bones.W, v.Weights.W));
        bool IsSpine(int index, float weight) => weight > 0 && body.Bones[index].Name is "Spine_1" or "Spine_2";

        var jeansMaterials = jeans.Parts.Select(p => p.MaterialId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parts = body.Parts.Where(p => !IsTorso(p) && (p.MaterialId is null || !jeansMaterials.Contains(p.MaterialId))).ToList();
        // Use the actual modular clothing meshes; the combined preview also has altered
        // UVs and weights on its trousers. Keep its separate hands, head and shoes.
        foreach (var (label, clothing) in new[] { ("vest", vest), ("jeans", jeans) })
        {
            var remap = clothing.Bones.Select(b => names.TryGetValue(b.Name, out var index) ? index : throw new InvalidDataException($"CJ outfit bone '{b.Name}' is missing from the body.")).ToArray();
            foreach (var lod in body.Parts.Select(p => p.Lod).Distinct())
            {
                var sourceLod = clothing.Parts.Select(p => p.Lod).Distinct().MinBy(l => Math.Abs(l - lod));
                foreach (var part in clothing.Parts.Where(p => p.Lod == sourceLod))
                    parts.Add(part with { Name = label + "/" + part.Name, Lod = lod, Vertices = part.Vertices.Select(v => Rebind(v, clothing, remap)).ToArray() });
            }
        }
        var positions = parts.Where(p => p.Lod == 0).SelectMany(p => p.Vertices).Select(v => v.Position).ToArray();
        return body with { Parts = parts.ToArray(), BoundsMin = positions.Aggregate(N.Vector3.Min), BoundsMax = positions.Aggregate(N.Vector3.Max) };

        VertexData Rebind(VertexData vertex, ModelData clothing, int[] remap)
        {
            var indices = new[] { vertex.Bones.X, vertex.Bones.Y, vertex.Bones.Z, vertex.Bones.W };
            var weights = new[] { vertex.Weights.X, vertex.Weights.Y, vertex.Weights.Z, vertex.Weights.W };
            var position = N.Vector3.Zero; var normal = N.Vector3.Zero; var tangent = N.Vector3.Zero;
            for (var i = 0; i < 4; i++)
            {
                if (weights[i] <= 0) continue;
                var from = clothing.Bones[indices[i]].WorldBind;
                var to = body.Bones[remap[indices[i]]].WorldBind;
                var rotation = N.Quaternion.Normalize(to.Rotation * N.Quaternion.Inverse(from.Rotation));
                position += (N.Vector3.Transform((N.Vector3.Transform(vertex.Position - from.Position, N.Quaternion.Inverse(from.Rotation)) / from.Scale) * to.Scale, to.Rotation) + to.Position) * weights[i];
                normal += N.Vector3.Transform(vertex.Normal, rotation) * weights[i];
                tangent += N.Vector3.Transform(new N.Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), rotation) * weights[i];
            }
            return vertex with { Position = position, Normal = N.Vector3.Normalize(normal), Tangent = new(N.Vector3.Normalize(tangent), vertex.Tangent.W),
                Bones = new(remap[indices[0]], remap[indices[1]], remap[indices[2]], remap[indices[3]]) };
        }
    }
}
