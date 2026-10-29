using N = System.Numerics;
using Sade.Contracts;

namespace Sade.Mount;

/// <summary>Assembles the separate GTA wheel meshes for ordinary model thumbnails and previews.</summary>
internal static class VehiclePreviewGeometry
{
    internal static ModelData Assemble(ModelData body, ModelData wheel, VehicleData vehicle)
    {
        var originalRadius = Math.Max(wheel.BoundsMax.Y - wheel.BoundsMin.Y, wheel.BoundsMax.Z - wheel.BoundsMin.Z) / 2;
        if (!float.IsFinite(originalRadius) || originalRadius <= 0) throw new InvalidDataException($"{wheel.Id}: invalid wheel bounds.");
        var parts = new List<MeshPartData>(body.Parts);
        var min = body.BoundsMin;
        var max = body.BoundsMax;
        var lods = body.Parts.Select(p => p.Lod).Distinct().ToArray();
        foreach (var socket in vehicle.Sockets.Where(s => s.Name.StartsWith("wheel_", StringComparison.OrdinalIgnoreCase) && !s.Name.EndsWith("_dummy", StringComparison.OrdinalIgnoreCase)))
        {
            var front = socket.Name.EndsWith("f", StringComparison.OrdinalIgnoreCase) || socket.Name.Contains("front", StringComparison.OrdinalIgnoreCase);
            var radius = front ? vehicle.WheelRadiusFront : vehicle.WheelRadiusRear;
            var scale = radius > 0 ? radius / originalRadius : 1;
            var bone = Math.Max(0, Array.FindIndex(body.Bones, b => b.Name.Equals(socket.Name, StringComparison.OrdinalIgnoreCase)));
            var rotation = socket.Name.StartsWith("wheel_l", StringComparison.OrdinalIgnoreCase)
                ? N.Quaternion.CreateFromAxisAngle(N.Vector3.UnitZ, MathF.PI) : N.Quaternion.Identity;
            foreach (var part in wheel.Parts.Where(p => p.Lod == 0))
            {
                var vertices = part.Vertices.Select(v =>
                {
                    var position = socket.Transform.Position + N.Vector3.Transform(v.Position * scale, rotation);
                    var tangent = N.Vector3.Transform(new N.Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z), rotation);
                    min = N.Vector3.Min(min, position); max = N.Vector3.Max(max, position);
                    return v with { Position = position, Normal = N.Vector3.Transform(v.Normal, rotation),
                        Tangent = new N.Vector4(tangent, v.Tangent.W), Bones = new(bone, 0, 0, 0), Weights = new(1, 0, 0, 0) };
                }).ToArray();
                foreach (var lod in lods) parts.Add(part with { Name = socket.Name + "/" + part.Name, Vertices = vertices, Lod = lod });
            }
        }
        return body with { Parts = parts.ToArray(), BoundsMin = min, BoundsMax = max };
    }
}
