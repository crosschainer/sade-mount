using N = System.Numerics;

using Sade.Contracts;

namespace Sade.Mount;

// These targets retain the reader's Unreal axes and centimeters. Left/right are the occupant's.
internal sealed record SteeringGripData(N.Vector3 Left, N.Vector3 Right, N.Vector3 Normal, N.Vector3 Up);
internal sealed record CarSeatData(N.Vector3 Pelvis, N.Vector3 LeftFoot, N.Vector3 RightFoot);
internal sealed record VehicleCabinData(SteeringGripData? Steering, CarSeatData[] Seats);

/// <summary>Finds the actual steering rim and cabin floor in decoded vehicle geometry.</summary>
internal static class VehicleCabinGeometry
{
    internal static VehicleCabinData? Create(ModelData model, VehicleData vehicle)
    {
        if (vehicle.Family != "road" || vehicle.RemoteControlled) return null;
        var front = vehicle.Sockets.FirstOrDefault(s => s.Name.Equals("ped_frontseat", StringComparison.OrdinalIgnoreCase));
        if (front is null) return null;
        var driver = front.Transform.Position;
        if (Math.Abs(driver.X) > 12.7f) driver.X = -Math.Abs(driver.X);
        var parts = model.Parts.Where(p => p.Lod == 0 && p.Indices.Length >= 3 && p.Vertices.Length > 0
            && p.MaterialId?.Contains("TransparentCar", StringComparison.OrdinalIgnoreCase) != true
            && p.Indices.All(i => (uint)i < p.Vertices.Length)).ToArray();
        var steering = FindSteering(parts, driver);
        // Enclosed cabins can still receive a floor fit without a recognized wheel.
        // Do not impose car footwells on tanks, quads or unrecognized exposed saddles.
        if (steering is null && vehicle.AnimationGroup.ToLowerInvariant() is not ("null" or "truck" or "van" or "bus" or "coach")) return null;
        var seats = new List<CarSeatData>();
        AddRow(front.Transform.Position);
        var rear = vehicle.Sockets.FirstOrDefault(s => s.Name.Equals("ped_backseat", StringComparison.OrdinalIgnoreCase)
            || s.Name.Equals("ped_rearseat", StringComparison.OrdinalIgnoreCase));
        // Bus/coach assets also carry legacy entry dummies named as rear seats below
        // the cabin. Do not mistake the vehicle's underbody for their footwell.
        if (rear is not null && rear.Transform.Position.Z >= front.Transform.Position.Z - 40)
            AddRow(rear.Transform.Position);
        return steering is null && seats.Count == 0 ? null : new(steering, seats.ToArray());

        void AddRow(N.Vector3 position)
        {
            if (Math.Abs(position.X) > 12.7f)
            {
                AddSeat(position with { X = -Math.Abs(position.X) });
                AddSeat(position with { X = Math.Abs(position.X) });
            }
            else AddSeat(position);
        }
        void AddSeat(N.Vector3 pelvis)
        {
            var left = Foot(parts, pelvis, -13);
            var right = Foot(parts, pelvis, 13);
            // Missing floor evidence should leave the stock seated legs alone.
            if (left is { } l && right is { } r) seats.Add(new(pelvis, l, r));
        }
    }

    private static SteeringGripData? FindSteering(MeshPartData[] parts, N.Vector3 seat)
    {
        // Most cars share a steering material, but some use that material on unrelated
        // trim as well. Others (Savanna, Broadway, Slamvan) use their interior material.
        // Connected rim geometry, rather than the material's bounds, identifies the wheel.
        SteeringGripData? best = null;
        var bestScore = float.MaxValue;
        foreach (var part in parts.OrderByDescending(p => p.MaterialId?.Contains("steering", StringComparison.OrdinalIgnoreCase) == true))
        {
            var named = part.MaterialId?.Contains("steering", StringComparison.OrdinalIgnoreCase) == true;
            if (!named && best is not null) continue;
            foreach (var points in Components(part))
            {
                if (points.Length < 24) continue;
                var min = points.Aggregate(N.Vector3.Min); var max = points.Aggregate(N.Vector3.Max);
                var center = (min + max) * .5f;
                var size = max - min;
                if (size.X is < 20 or > 50 || size.Y > 50 || size.Z > 50
                    || Math.Abs(center.X - seat.X) > 25 || center.Y > seat.Y - 15 || center.Y < seat.Y - 85
                    || center.Z < seat.Z + 15 || center.Z > seat.Z + 65) continue;

                var mean = points.Aggregate(N.Vector3.Zero, (a, p) => a + p) / points.Length;
                float yy = 0, yz = 0, zz = 0;
                foreach (var p in points) { var d = p - mean; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z; }
                var eigen = (yy + zz + MathF.Sqrt((yy - zz) * (yy - zz) + 4 * yz * yz)) * .5f;
                var up = Math.Abs(yz) > .0001f ? N.Vector3.Normalize(new N.Vector3(0, yz, eigen - yy))
                    : zz >= yy ? N.Vector3.UnitZ : N.Vector3.UnitY;
                if (up.Z < 0) up = -up;
                if (up.Z < .3f) continue;
                var normal = N.Vector3.Cross(up, N.Vector3.UnitX); // Towards the driver, source +Y.
                var low = points.Min(p => N.Vector3.Dot(p, up)); var high = points.Max(p => N.Vector3.Dot(p, up));
                var height = high - low;
                var ratio = height / size.X;
                if (ratio is < .82f or > 1.18f) continue; // Reject spokes, dashboard panels and columns.
                center = N.Vector3.UnitX * center.X + up * ((low + high) * .5f) + normal * N.Vector3.Dot(mean, normal);
                var radius = size.X * .5f;
                var thickness = points.Max(p => N.Vector3.Dot(p, normal)) - points.Min(p => N.Vector3.Dot(p, normal));
                if (thickness > radius * .45f) continue;
                var quadrants = new bool[4]; var outer = 0; var outside = 0;
                foreach (var p in points)
                {
                    var x = p.X - center.X; var z = N.Vector3.Dot(p - center, up);
                    var radial = MathF.Sqrt(x * x + z * z) / radius;
                    if (radial > 1.13f) outside++;
                    if (radial < .78f) continue;
                    outer++; quadrants[(x < 0 ? 0 : 1) + (z < 0 ? 0 : 2)] = true;
                }
                if (outside > points.Length / 12 || outer < points.Length / 3 || quadrants.Any(p => !p)) continue;
                var score = Math.Abs(ratio - 1) * 30 + Math.Abs(center.X - seat.X) * .05f
                    + Math.Abs(center.Y - seat.Y + 38) * .015f;
                if (score >= bestScore) continue;
                var left = Rim(-1); var right = Rim(1);
                best = new(left, right, normal, up); bestScore = score;

                N.Vector3 Rim(int side)
                {
                    // Average the rim's front/back surface so the palm wraps the tube,
                    // rather than landing on its outermost polygon or on the spoke hub.
                    var edge = points.Where(p => (p.X - center.X) * side > radius * .9f).ToArray();
                    return edge.Aggregate(N.Vector3.Zero, (a, p) => a + p) / edge.Length;
                }
            }
        }
        return best;
    }

    private static N.Vector3? Foot(MeshPartData[] parts, N.Vector3 pelvis, float side)
    {
        const float ankleHeight = 10;
        // Shorter truck footwells end before a car's typical pedal distance.
        foreach (var forward in new[] { 60f, 50f, 40f })
        {
            var x = pelvis.X + side; var y = pelvis.Y - forward;
            var floor = float.NegativeInfinity;
            // Test the heel and toe area against render triangles; a single convex vehicle
            // collider encloses the cabin and cannot describe where an occupant's feet go.
            foreach (var offset in new[] { new N.Vector2(0, 0), new N.Vector2(-4, -12), new N.Vector2(4, -12) })
            foreach (var part in parts)
            for (var i = 0; i + 2 < part.Indices.Length; i += 3)
            {
                var a = part.Vertices[part.Indices[i]].Position;
                var b = part.Vertices[part.Indices[i + 1]].Position;
                var c = part.Vertices[part.Indices[i + 2]].Position;
                var normal = N.Vector3.Cross(b - a, c - a);
                if (normal.LengthSquared() < .0001f || Math.Abs(normal.Z) < normal.Length() * .7f) continue;
                var px = x + offset.X; var py = y + offset.Y;
                var denominator = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (Math.Abs(denominator) < .0001f) continue;
                var u = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / denominator;
                var v = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / denominator;
                if (u < 0 || v < 0 || u + v > 1) continue;
                var z = u * a.Z + v * b.Z + (1 - u - v) * c.Z;
                if (z < pelvis.Z - 8 && z > pelvis.Z - 65) floor = Math.Max(floor, z);
            }
            if (float.IsFinite(floor)) return new N.Vector3(x, y, floor + ankleHeight);
        }
        return null;
    }

    private static IEnumerable<N.Vector3[]> Components(MeshPartData part)
    {
        var parent = Enumerable.Range(0, part.Vertices.Length).ToArray();
        var used = new bool[parent.Length];
        var welded = new Dictionary<(int, int, int), int>();
        for (var i = 0; i < part.Vertices.Length; i++)
        {
            var p = part.Vertices[i].Position;
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) yield break;
            var key = ((int)MathF.Round(p.X * 100), (int)MathF.Round(p.Y * 100), (int)MathF.Round(p.Z * 100));
            if (welded.TryGetValue(key, out var previous)) Join(i, previous); else welded[key] = i;
        }
        for (var i = 0; i + 2 < part.Indices.Length; i += 3)
        {
            var a = part.Indices[i]; var b = part.Indices[i + 1]; var c = part.Indices[i + 2];
            if ((uint)a >= parent.Length || (uint)b >= parent.Length || (uint)c >= parent.Length) yield break;
            used[a] = used[b] = used[c] = true; Join(a, b); Join(a, c);
        }
        foreach (var group in Enumerable.Range(0, parent.Length).Where(i => used[i]).GroupBy(Find))
            yield return group.Select(i => part.Vertices[i].Position).Distinct().ToArray();
        int Find(int index)
        {
            while (parent[index] != index) { parent[index] = parent[parent[index]]; index = parent[index]; }
            return index;
        }
        void Join(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
    }
}
