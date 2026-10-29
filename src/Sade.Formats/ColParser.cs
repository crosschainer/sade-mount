using System.Numerics;
using Sade.Contracts;

namespace Sade.Formats;

public sealed record NamedCollisionData(string Name, int ModelId, CollisionData Collision);

/// <summary>Reads classic COLL/COL2/COL3 records, retaining authored mesh, box and sphere collision.</summary>
public static class ColParser
{
    public static IReadOnlyList<NamedCollisionData> Parse(ReadOnlySpan<byte> bytes, string source = "COL", Action<Diagnostic>? report = null, bool skipInvalidRecords = false)
    {
        var result = new List<NamedCollisionData>();
        var input = new BinaryCursor(bytes);
        while (input.Remaining > 0)
        {
            if (input.Data[input.Position..].IndexOfAnyExcept((byte)0) < 0) break;
            var start = input.Position;
            if (skipInvalidRecords && input.Remaining < 8)
            {
                report?.Invoke(new Diagnostic(source, $"Stopped at truncated collision header at byte {start}; retained {result.Count} earlier records.", true));
                break;
            }
            var magic = input.Text(4);
            if (magic is not ("COLL" or "COL2" or "COL3"))
            {
                if (!skipInvalidRecords) throw new InvalidDataException($"Unknown collision record '{magic}' at byte {start}.");
                report?.Invoke(new Diagnostic(source, $"Stopped at unknown collision record '{magic}' at byte {start}; retained {result.Count} earlier records.", true));
                break;
            }
            var length = input.UInt32();
            if (length < 64 || length > input.Remaining) throw new InvalidDataException("Collision record size exceeds its archive.");
            input.Skip((int)length);
            var record = bytes.Slice(start, checked((int)length + 8));
            var r = new BinaryCursor(record) { Position = 8 };
            var name = r.Text(22).Trim();
            if (name.Length == 0) throw new InvalidDataException("Collision model has an empty name.");
            var modelId = r.UInt16();
            try
            {
                // Bounds precede all versions' primitive data. Validate floats before using any offsets.
                for (var f = 0; f < 10; f++) r.Single();
                result.Add(new NamedCollisionData(name, modelId,
                    magic == "COLL" ? ReadLegacy(ref r, source) : ReadModern(ref r, magic, source)));
            }
            catch (InvalidDataException error) when (skipInvalidRecords)
            {
                // The outer record extent was checked first, so recovery never searches arbitrary payload
                // bytes for magic. Some shipped legacy peds.col records have impossible primitive counts.
                report?.Invoke(new Diagnostic(source, $"Skipped malformed collision '{name}': {error.Message}", true));
            }
        }
        return result;
    }

    private static CollisionData ReadModern(ref BinaryCursor r, string magic, string source)
    {
        var spheresCount = r.UInt16();
        var boxesCount = r.UInt16();
        var facesCount = r.UInt16();
        r.Skip(2); // Number of lines, padding.
        r.UInt32(); // Flags (face groups/shadow mesh do not change primary geometry).
        var sphereOffset = r.UInt32();
        var boxOffset = r.UInt32();
        r.UInt32(); // Lines/cones offset.
        var vertexOffset = r.UInt32();
        var faceOffset = r.UInt32();
        r.UInt32(); // Triangle planes offset.
        if (magic == "COL3") r.Skip(12); // Shadow mesh count and offsets.
        var minimumOffset = r.Position;
        var sr = Section(r.Data, sphereOffset, spheresCount, 20, minimumOffset);
        var spheres = new CollisionSphere[spheresCount];
        for (var i = 0; i < spheres.Length; i++)
        {
            var center = ReadPosition(ref sr);
            var radius = ReadRadius(ref sr);
            sr.Skip(4);
            spheres[i] = new CollisionSphere(center, radius);
        }
        var br = Section(r.Data, boxOffset, boxesCount, 28, minimumOffset);
        var boxes = ReadBoxes(ref br, boxesCount);
        var fr = Section(r.Data, faceOffset, facesCount, 8, minimumOffset);
        var indices = new int[facesCount * 3];
        var vertexCount = 0;
        for (var i = 0; i < facesCount; i++)
        {
            var a = fr.UInt16(); var b = fr.UInt16(); var c = fr.UInt16();
            fr.Skip(2);
            indices[i * 3] = a; indices[i * 3 + 1] = c; indices[i * 3 + 2] = b;
            vertexCount = Math.Max(vertexCount, Math.Max(a, Math.Max(b, c)) + 1);
        }
        var vr = Section(r.Data, vertexOffset, vertexCount, 6, minimumOffset);
        var vertices = new Vector3[vertexCount];
        for (var i = 0; i < vertices.Length; i++) vertices[i] = GtaCoordinates.ToUnreal(new Vector3(vr.Int16(), vr.Int16(), vr.Int16()) / 128f);
        return new CollisionData { Source = source, Vertices = vertices, Indices = indices, Boxes = boxes, Spheres = spheres };
    }

    private static CollisionData ReadLegacy(ref BinaryCursor r, string source)
    {
        var sphereCount = r.Count(r.Remaining / 20, "sphere");
        var spheres = new CollisionSphere[sphereCount];
        for (var i = 0; i < spheres.Length; i++)
        {
            var radius = ReadRadius(ref r);
            var center = ReadPosition(ref r);
            r.Skip(4);
            spheres[i] = new CollisionSphere(center, radius);
        }
        var lineCount = r.Count(r.Remaining / 24, "line");
        r.Skip(checked(lineCount * 24));
        var boxCount = r.Count(r.Remaining / 28, "box");
        var boxes = ReadBoxes(ref r, boxCount);
        var vertexCount = r.Count(r.Remaining / 12, "vertex");
        var vertices = new Vector3[vertexCount];
        for (var i = 0; i < vertexCount; i++) vertices[i] = ReadPosition(ref r);
        var faceCount = r.Count(r.Remaining / 16, "face");
        var indices = new int[checked(faceCount * 3)];
        for (var i = 0; i < faceCount; i++)
        {
            var a = r.Int32(); var b = r.Int32(); var c = r.Int32(); r.Skip(4);
            if ((uint)a >= vertexCount || (uint)b >= vertexCount || (uint)c >= vertexCount) throw new InvalidDataException("Collision triangle references a missing vertex.");
            indices[i * 3] = a; indices[i * 3 + 1] = c; indices[i * 3 + 2] = b;
        }
        return new CollisionData { Source = source, Vertices = vertices, Indices = indices, Boxes = boxes, Spheres = spheres };
    }

    private static BinaryCursor Section(ReadOnlySpan<byte> record, uint offset, int count, int stride, int minimumOffset)
    {
        if (count == 0) return new BinaryCursor([]);
        var start = (long)offset + 4; // Offsets are relative to the size field, four bytes after magic.
        var size = (long)count * stride;
        if (offset == 0 || start < minimumOffset || start + size > record.Length) throw new InvalidDataException("Collision data offset or count is outside its record.");
        return new BinaryCursor(record.Slice((int)start, (int)size));
    }

    private static CollisionBox[] ReadBoxes(ref BinaryCursor r, int count)
    {
        var boxes = new CollisionBox[count];
        for (var i = 0; i < count; i++)
        {
            var a = ReadPosition(ref r); var b = ReadPosition(ref r); r.Skip(4);
            var min = Vector3.Min(a, b); var max = Vector3.Max(a, b);
            boxes[i] = new CollisionBox((min + max) * 0.5f, (max - min) * 0.5f, Quaternion.Identity);
        }
        return boxes;
    }
    private static Vector3 ReadPosition(ref BinaryCursor r) => GtaCoordinates.ToUnreal(IfpParser.ReadVector(ref r));
    private static float ReadRadius(ref BinaryCursor r)
    {
        var radius = r.Single();
        if (radius < 0) throw new InvalidDataException("Collision sphere has a negative radius.");
        return radius * 100;
    }
}
