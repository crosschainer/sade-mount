using System.Numerics;
using System.Text;
using Sade.Contracts;
using Sade.Formats;

internal static class SurfaceTests
{
    internal static void Run()
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Malformed surface input was accepted."); }
        var table = SurfaceAudioTable.Parse("# installed-table layout\nDEFAULT 1 0 0 0 0 0 0 0 0\nLAWN 0 1 0 0 0 0 0 0 0\nDECK 0 0 0 0 1 0 0 0 0\n");
        Check(table[0] == SurfaceAudio.Concrete && table[1] == SurfaceAudio.Grass && table[2] == SurfaceAudio.Wood && table[255] == SurfaceAudio.Unknown, "Table row IDs and audio columns must survive parsing.");
        Reject(() => SurfaceAudioTable.Parse("GRASS 0 1 0 0 0 0 0 0 0"));
        Reject(() => SurfaceAudioTable.Parse("DEFAULT 2 0 0 0 0 0 0 0 0"));
        Reject(() => SurfaceAudioTable.Parse("DEFAULT 1 0 0 0 0 0 0 0 0\nDEFAULT 1 0 0 0 0 0 0 0 0"));
        var source = new CollisionData
        {
            Vertices = [new(0, 0, 0), new(1000, 0, 0), new(0, 1000, 0), new(2000, 0, 0), new(3000, 0, 0), new(2000, 1000, 0)],
            Indices = [0, 1, 2, 3, 4, 5], MaterialIds = [1, 2]
        };
        var exact = CollisionSurfaceTransfer.Apply(source, source, table, out var fallback);
        Check(exact.Surfaces.SequenceEqual(new[] { SurfaceAudio.Grass, SurfaceAudio.Wood }) && fallback == 0, "Original faces must retain their exact labels.");
        var local = source with { MaterialIds = [] };
        var transferred = CollisionSurfaceTransfer.Apply(local, source, table, out fallback);
        Check(transferred.Surfaces.SequenceEqual(exact.Surfaces) && fallback == 0, "Aligned replacement triangles need the original labels.");
        var baked = local with { Vertices = local.Vertices.Select(p => p * 2 + new Vector3(250000, 160000, 1500)).ToArray() };
        transferred = CollisionSurfaceTransfer.Apply(baked, source, table, out fallback);
        Check(transferred.Surfaces.SequenceEqual(exact.Surfaces) && fallback == 0, "World-baked and scaled geometry must match labels in COL space.");
        Check(ReferenceEquals(transferred.Vertices, baked.Vertices) && ReferenceEquals(transferred.Indices, baked.Indices), "Surface transfer must never change collision geometry.");
        var uniform = source with { MaterialIds = [1, 1] };
        transferred = CollisionSurfaceTransfer.Apply(baked, uniform, table, out fallback);
        Check(transferred.Surfaces.All(s => s == SurfaceAudio.Grass) && fallback == 0, "A uniform source surface needs no geometric registration.");
        var square = new CollisionData { Vertices = [new(0, 0, 0), new(1000, 0, 0), new(1000, 1000, 0), new(0, 1000, 0)], Indices = [0, 1, 2, 0, 2, 3], MaterialIds = [1, 2] };
        var shifted = square with { Vertices = square.Vertices.Select(p => p + new Vector3(250000, 160000, 1500)).ToArray(), MaterialIds = [] };
        transferred = CollisionSurfaceTransfer.Apply(shifted, square, table, out fallback);
        Check(fallback == 2 && transferred.Surfaces.Distinct().Count() == 1, "Geometrically ambiguous placements must use the reported dominant source group.");
        Reject(() => CollisionSurfaceTransfer.Apply(local, source with { MaterialIds = [1] }, table, out _));

        using var bytes = new MemoryStream();
        using (var w = new BinaryWriter(bytes, Encoding.ASCII, true))
        {
            w.Write("COLL"u8); w.Write(0); w.Write(Encoding.ASCII.GetBytes("surface")); w.Write(new byte[15]); w.Write((ushort)1);
            for (var i = 0; i < 10; i++) w.Write(0f);
            w.Write(0); w.Write(0); w.Write(0); // No spheres, lines or boxes.
            w.Write(3);
            foreach (var p in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
            w.Write(1); w.Write(0); w.Write(1); w.Write(2); w.Write((byte)2); w.Write(new byte[3]);
        }
        var raw = bytes.ToArray(); BitConverter.GetBytes(raw.Length - 8).CopyTo(raw, 4);
        var legacy = ColParser.Parse(raw).Single().Collision;
        Check(legacy.MaterialIds.SequenceEqual(new byte[] { 2 }) && legacy.Indices.SequenceEqual(new[] { 0, 2, 1 }), "COLL face materials must survive winding conversion.");
    }
}
