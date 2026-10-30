using System.Globalization;
using System.Numerics;
using Sade.Contracts;

namespace Sade.Formats;

/// <summary>Only authored physical objects can become movable map props.</summary>
public sealed class WorldPropCatalog
{
    private readonly Dictionary<string, (float Mass, int Response)> objects = new(StringComparer.OrdinalIgnoreCase);

    public static WorldPropCatalog Parse(string text)
    {
        var result = new WorldPropCatalog();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split(';', '#')[0].Replace(',', ' ');
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 12) continue;
            if (!float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mass) || !float.IsFinite(mass)
                || !int.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var response))
                throw new InvalidDataException("Invalid object.dat physics row: " + fields[0]);
            result.objects[fields[0]] = (mass, response);
        }
        return result;
    }

    public WorldPropData? Get(ModelData model)
    {
        var name = model.Id.Split('/')[^1].Split('.')[0];
        if (name.StartsWith("SM_", StringComparison.OrdinalIgnoreCase)) name = name[3..];
        if (model.Bones.Length != 0 || !objects.TryGetValue(name, out var data)) return null;
        // Lamps, attached fences, swinging/locked doors and hanging pieces stay structural.
        // 99999 is the game's immovable sentinel; a lower limit also excludes heavy scenery.
        if (data.Mass is < 1 or > 1000 || data.Response is not (0 or 2 or 3 or 9)) return null;
        var size = model.BoundsMax - model.BoundsMin;
        var largest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        var center = (model.BoundsMin + model.BoundsMax) * .5f;
        if (!float.IsFinite(size.LengthSquared()) || size.X < 0 || size.Y < 0 || size.Z < 0
            || largest is < 1 or > 800 || center.Length() > Math.Max(500, largest * 2)) return null;
        return new(data.Mass, SurfaceAudio.Unknown);
    }

    public static CollisionData MovableCollision(ModelData model)
    {
        var source = model.Collision;
        if (source.Hulls.Length + source.Boxes.Length + source.Spheres.Length + source.Capsules.Length > 0)
            return source with { Vertices = [], Indices = [], MaterialIds = [], Surfaces = [] };
        var positions = model.Parts.Where(p => p.Lod == 0).SelectMany(p => p.Vertices).Select(v => v.Position).Distinct().ToArray();
        var support = new HashSet<Vector3>();
        for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++) for (var z = -1; z <= 1; z++)
        {
            if (x == 0 && y == 0 && z == 0) continue;
            var direction = new Vector3(x, y, z);
            if (positions.Length > 0) support.Add(positions.MaxBy(v => Vector3.Dot(v, direction)));
        }
        return support.Count < 4 ? new() : new CollisionData { Hulls = [support.ToArray()], Source = "Derived convex loose prop (object.dat; no authored convex collision)" };
    }
}
