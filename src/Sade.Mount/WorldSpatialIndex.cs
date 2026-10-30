using N = System.Numerics;
using Sade.Contracts;

namespace Sade.Mount;

/// <summary>Metadata-only grid. Geometry and scene objects are never needed for a distance query.</summary>
internal sealed class WorldSpatialIndex
{
    private readonly Dictionary<(int X, int Y), List<int>> cells = [];
    private readonly float cellSize;
    private readonly Dictionary<string, (N.Vector3 Center, float Radius)> bounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<int>> byModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> large = [];
    private readonly (int X, int Y)[] placementCells;
    internal WorldData Data { get; }

    internal WorldSpatialIndex(WorldData data, float cellMeters = 128)
    {
        Data = data; cellSize = Math.Clamp(cellMeters, 32, 512) * 100;
        placementCells = new (int, int)[data.Placements.Length];
        foreach (var box in data.Bounds) bounds[box.ModelId] = ((box.Min + box.Max) * .5f, (box.Max - box.Min).Length() * .5f);
        for (var i = 0; i < data.Placements.Length; i++)
        {
            var placement = data.Placements[i];
            if (!byModel.TryGetValue(placement.ModelId, out var users)) byModel.Add(placement.ModelId, users = []);
            users.Add(i);
            var (center, radius) = Shape(placement);
            var key = Cell(center); placementCells[i] = key;
            if (radius > cellSize * 2) large.Add(i);
            if (!cells.TryGetValue(key, out var entries)) cells.Add(key, entries = []);
            entries.Add(i);
        }
    }

    internal void SetBounds(ModelData model)
    {
        var center = (model.BoundsMin + model.BoundsMax) * .5f;
        var radius = (model.BoundsMax - model.BoundsMin).Length() * .5f;
        if (!float.IsFinite(radius)) return;
        bounds[model.Id] = (center, radius);
        if (!byModel.TryGetValue(model.Id, out var users)) return;
        foreach (var id in users)
        {
            var (at, extent) = Shape(Data.Placements[id]);
            var cell = Cell(at); var previous = placementCells[id];
            if (cell != previous)
            {
                cells[previous].Remove(id);
                if (!cells.TryGetValue(cell, out var entries)) cells.Add(cell, entries = []);
                entries.Add(id); placementCells[id] = cell;
            }
            if (extent > cellSize * 2) large.Add(id); else large.Remove(id);
        }
    }

    internal List<(int Index, float DistanceSquared)> Query(IReadOnlyList<N.Vector3> focus, float radiusMeters, IReadOnlySet<int> resident, float hysteresis)
    {
        var radius = Math.Clamp(radiusMeters, 64, 2000) * 100;
        var outer = radius * Math.Clamp(hysteresis, 1.05f, 1.5f);
        // A ground model can extend beyond its placement pivot. Keep two extra grid
        // cells in the residency ring; per-object render bounds are culled by the engine.
        var margin = cellSize * 2;
        var result = new Dictionary<int, float>();
        foreach (var at in focus)
        {
            if (!float.IsFinite(at.LengthSquared())) continue;
            var low = Cell(at - new N.Vector3(outer + margin)); var high = Cell(at + new N.Vector3(outer + margin));
            foreach (var id in large) Consider(id);
            for (var x = low.X; x <= high.X; x++)
            for (var y = low.Y; y <= high.Y; y++)
            {
                if (!cells.TryGetValue((x, y), out var entries)) continue;
                foreach (var index in entries)
                {
                    Consider(index);
                }
            }
            void Consider(int index)
            {
                var placement = Data.Placements[index];
                var (center, extent) = Shape(placement);
                var delta = center - at;
                var edge = Math.Max(0, MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y) - extent);
                var distance = edge * edge;
                var limit = resident.Contains(index) ? outer : radius;
                if (bounds.ContainsKey(placement.ModelId)) limit = Math.Min(limit, Math.Max(2500, extent * 75) * (resident.Contains(index) ? hysteresis : 1));
                if (placement.DrawDistance > 0) limit = Math.Min(limit, Math.Max(1000, placement.DrawDistance) * (resident.Contains(index) ? hysteresis : 1));
                if (distance > limit * limit) return;
                if (!result.TryGetValue(index, out var old) || distance < old) result[index] = distance;
            }
        }
        return result.Select(p => (Index: p.Key, DistanceSquared: p.Value))
            .OrderBy(p => Data.Placements[p.Index].Prominent ? p.DistanceSquared * .15f : p.DistanceSquared + 25_000_000)
            .ThenBy(p => p.Index).ToList();
    }

    private (N.Vector3 Center, float Radius) Shape(WorldPlacementData placement)
    {
        if (!bounds.TryGetValue(placement.ModelId, out var box)) return (placement.Transform.Position, cellSize * 2);
        var center = placement.Transform.Position + N.Vector3.Transform(box.Center * placement.Transform.Scale, placement.Transform.Rotation);
        var scale = N.Vector3.Abs(placement.Transform.Scale);
        return (center, box.Radius * Math.Max(scale.X, Math.Max(scale.Y, scale.Z)));
    }

    private (int X, int Y) Cell(N.Vector3 p) => ((int)MathF.Floor(p.X / cellSize), (int)MathF.Floor(p.Y / cellSize));
}
