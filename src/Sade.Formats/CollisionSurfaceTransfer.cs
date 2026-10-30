using System.Numerics;
using Sade.Contracts;

namespace Sade.Formats;

/// <summary>Transfers audio labels only. Render-derived collision vertices and indices never move.</summary>
public static class CollisionSurfaceTransfer
{
    public static CollisionData Apply(CollisionData target, CollisionData source, SurfaceAudioTable table, out int fallbackTriangles, Action<string>? diagnostic = null)
    {
        fallbackTriangles = 0;
        var count = target.Indices.Length / 3;
        if (count == 0 || source.MaterialIds.Length == 0) return target;
        if (source.Indices.Length != source.MaterialIds.Length * 3) throw new InvalidDataException("COL surface count does not match its triangles.");
        var labels = source.MaterialIds.Select(id => table[id]).ToArray();
        if (ReferenceEquals(target, source)) return target with { Surfaces = labels };
        var dominant = labels.GroupBy(s => s).MaxBy(g => g.Count())!.Key;
        if (labels.All(s => s == dominant)) return target with { Surfaces = Enumerable.Repeat(dominant, count).ToArray() };
        var tree = new TriangleTree(source, labels);
        var min = target.Vertices.Aggregate(Vector3.Min); var max = target.Vertices.Aggregate(Vector3.Max);
        var center = (min + max) * .5f;
        var samples = Enumerable.Range(0, Math.Min(96, count)).SelectMany(i =>
        {
            var face = i * count / Math.Min(96, count); var index = face * 3;
            return new[] { Center(target, face), target.Vertices[target.Indices[index]], target.Vertices[target.Indices[index + 1]], target.Vertices[target.Indices[index + 2]] };
        }).ToArray();
        var candidates = new List<(Quaternion Rotation, float Scale, Vector3 Offset, float Error)>();
        var local = Vector3.Distance(center, tree.Center) < 5000;
        if (local) Fit(Quaternion.Identity, 1, Vector3.Zero, false);
        for (var turn = 0; turn < (local && candidates[0].Error <= 2 ? 0 : 4); turn++)
        {
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn * MathF.PI / 2);
            var size = max - min;
            var projected = Vector3.Abs(Vector3.Transform(size, rotation));
            var sourceSize = tree.Size;
            var axis = projected.X >= projected.Y ? 0 : 1;
            var scale = sourceSize[axis] / Math.Max(projected[axis], .001f);
            if (scale is >= .125f and <= 8)
                Fit(rotation, scale, tree.Center - Vector3.Transform(center, rotation) * scale, true);
        }
        void Fit(Quaternion rotation, float scale, Vector3 offset, bool refine)
        {
            // Refine the translation using the best 75% of correspondences. New DE details
            // and missing classic faces must not pull the labels away from matching surfaces.
            if (refine)
                for (var step = 0; step < 8; step++)
                {
                    var deltas = samples.Select(p =>
                    {
                        var at = Vector3.Transform(p, rotation) * scale + offset;
                        var hit = tree.Nearest(at);
                        return (Delta: hit.Point - at, Distance: hit.Distance);
                    }).OrderBy(p => p.Distance).Take(Math.Max(1, samples.Length * 3 / 4)).ToArray();
                    var correction = deltas.Aggregate(Vector3.Zero, (sum, p) => sum + p.Delta) / deltas.Length;
                    offset += correction;
                    if (correction.LengthSquared() < .01f) break;
                }
            var errors = samples.Select(p => MathF.Sqrt(tree.Nearest(Vector3.Transform(p, rotation) * scale + offset).Distance) / scale).Order().ToArray();
            var error = errors.Take(Math.Max(1, (int)Math.Ceiling(errors.Length * .9))).Average();
            candidates.Add((rotation, scale, offset, error));
        }
        if (candidates.Count == 0) { fallbackTriangles = count; return target with { Surfaces = Enumerable.Repeat(dominant, count).ToArray() }; }
        var ranked = candidates.OrderBy(c => c.Error).ToArray();
        var best = ranked[0];
        bool Ambiguous()
        {
            foreach (var other in ranked.Skip(1).Where(c => c.Error <= best.Error + 2))
                foreach (var p in samples)
                    if (tree.Nearest(Vector3.Transform(p, best.Rotation) * best.Scale + best.Offset).Surface != tree.Nearest(Vector3.Transform(p, other.Rotation) * other.Scale + other.Offset).Surface) return true;
            return false;
        }
        if (best.Error > 200 || Ambiguous())
        {
            diagnostic?.Invoke("Surface label alignment is ambiguous or differs from classic COL; retaining its dominant audio group.");
            fallbackTriangles = count;
            return target with { Surfaces = Enumerable.Repeat(dominant, count).ToArray() };
        }
        var surfaces = new SurfaceAudio[count];
        for (var i = 0; i < count; i++)
        {
            var hit = tree.Nearest(Vector3.Transform(Center(target, i), best.Rotation) * best.Scale + best.Offset);
            if (hit.Distance <= 300 * 300 * best.Scale * best.Scale) surfaces[i] = hit.Surface;
            else { surfaces[i] = dominant; fallbackTriangles++; }
        }
        return target with { Surfaces = surfaces };
    }

    private static Vector3 Center(CollisionData data, int face)
    {
        var i = face * 3;
        return (data.Vertices[data.Indices[i]] + data.Vertices[data.Indices[i + 1]] + data.Vertices[data.Indices[i + 2]]) / 3;
    }

    private sealed class TriangleTree
    {
        private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, SurfaceAudio Surface)
        {
            internal Vector3 Min => Vector3.Min(A, Vector3.Min(B, C));
            internal Vector3 Max => Vector3.Max(A, Vector3.Max(B, C));
            internal Vector3 Center => (A + B + C) / 3;
        }
        private sealed record Node(Vector3 Min, Vector3 Max, int Start, int Count, Node? Left = null, Node? Right = null);
        private readonly Triangle[] triangles;
        private readonly Node root;
        internal Vector3 Center => (root.Min + root.Max) * .5f;
        internal Vector3 Size => root.Max - root.Min;
        internal TriangleTree(CollisionData data, SurfaceAudio[] labels)
        {
            triangles = new Triangle[labels.Length];
            for (var i = 0; i < triangles.Length; i++)
            {
                var index = i * 3;
                if ((uint)data.Indices[index] >= data.Vertices.Length || (uint)data.Indices[index + 1] >= data.Vertices.Length || (uint)data.Indices[index + 2] >= data.Vertices.Length)
                    throw new InvalidDataException("Surface transfer references a missing COL vertex.");
                triangles[i] = new(data.Vertices[data.Indices[index]], data.Vertices[data.Indices[index + 1]], data.Vertices[data.Indices[index + 2]], labels[i]);
            }
            root = Build(0, triangles.Length);
        }
        private Node Build(int start, int count)
        {
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            for (var i = start; i < start + count; i++) { min = Vector3.Min(min, triangles[i].Min); max = Vector3.Max(max, triangles[i].Max); }
            if (count <= 8) return new(min, max, start, count);
            var size = max - min; var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            Array.Sort(triangles, start, count, Comparer<Triangle>.Create((a, b) => a.Center[axis].CompareTo(b.Center[axis])));
            var half = count / 2;
            return new(min, max, start, count, Build(start, half), Build(start + half, count - half));
        }
        internal (Vector3 Point, float Distance, SurfaceAudio Surface) Nearest(Vector3 point)
        {
            var best = (Point: Vector3.Zero, Distance: float.MaxValue, Surface: SurfaceAudio.Unknown);
            float Bound(Node n) => Vector3.DistanceSquared(point, Vector3.Clamp(point, n.Min, n.Max));
            void Visit(Node n)
            {
                if (Bound(n) >= best.Distance) return;
                if (n.Left is { } left && n.Right is { } right)
                {
                    if (Bound(left) < Bound(right)) { Visit(left); Visit(right); } else { Visit(right); Visit(left); }
                    return;
                }
                for (var i = n.Start; i < n.Start + n.Count; i++)
                {
                    var t = triangles[i]; var at = Closest(point, t.A, t.B, t.C); var distance = Vector3.DistanceSquared(point, at);
                    if (distance < best.Distance) best = (at, distance, t.Surface);
                }
            }
            Visit(root); return best;
        }
        private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a; var ac = c - a; var ap = p - a;
            var d1 = Vector3.Dot(ab, ap); var d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;
            var bp = p - b; var d3 = Vector3.Dot(ab, bp); var d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;
            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            var cp = p - c; var d5 = Vector3.Dot(ab, cp); var d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return c;
            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            var va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6));
            var sum = va + vb + vc;
            if (MathF.Abs(sum) < 1e-12f) return a;
            return a + ab * (vb / sum) + ac * (vc / sum);
        }
    }
}
