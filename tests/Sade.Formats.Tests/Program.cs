using System.Numerics;
using System.Text;
using System.Text.Json;
using Sade.Contracts;
using Sade.Formats;

if (args.Length > 0)
{
    using var input = new MemoryStream();
    Console.OpenStandardInput().CopyTo(input);
    if (args[0] == "ifp-stdin")
    {
        var diagnostics = new List<Diagnostic>();
        var bank = IfpParser.Parse("diagnostic", input.ToArray(), diagnostics.Add);
        Console.WriteLine(JsonSerializer.Serialize(new { clips = bank.Clips.Length, warnings = diagnostics.Count, selected = bank.Clips.Where(c => c.Name.Contains("fat", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("stance", StringComparison.OrdinalIgnoreCase)).Select(c => new { c.Name, c.Duration, tracks = c.Tracks.Length }) }));
    }
    else if (args[0] == "col-stdin")
    {
        var models = ColParser.Parse(input.ToArray(), "diagnostic");
        Console.WriteLine(JsonSerializer.Serialize(new { count = models.Count, models = models.Select(m => new { m.Name, vertices = m.Collision.Vertices.Length, triangles = m.Collision.Indices.Length / 3, boxes = m.Collision.Boxes.Length, spheres = m.Collision.Spheres.Length }).Take(5) }));
    }
    else throw new ArgumentException("Unknown diagnostic command.");
    return;
}

var tests = new (string Name, Action Run)[]
{
    ("ANP3 fixed-point data uses 60 ticks, source meters, and continuity", TestPacked),
    ("ANPK chunk padding, quaternion conjugation, and channels", TestChunked),
    ("ANPK source anomalies produce bounded poses and explicit diagnostics", TestChunkAnomalies),
    ("ANPK repairs only the bounded leading guard pose", TestChunkLeadingGuard),
    ("ANPK trims negative pre-roll only for identical constant poses", TestChunkPreroll),
    ("IFP rejects malformed sizes, types, and times", TestMalformed),
    ("COL2/COL3 authored primitives and reflected triangle winding", TestCollision),
    ("Collision rejects out-of-range offsets and indices", TestBadCollision),
    ("Binding samples endpoints, preserves reference channels, and removes travel", TestBinding),
    ("Binding catches incompatible and duplicate track assignments", TestBadBinding),
    ("Pedestrian metadata and explicit Smoke preset", TestPedMetadata)
};
foreach (var (name, run) in tests) { run(); Console.WriteLine($"PASS {name}"); }
Console.WriteLine($"{tests.Length} tests passed.");

static void TestPacked()
{
    var clip = IfpParser.Parse("ped", Packed()).Clips.Single();
    Near(clip.Duration, 80f / 60);
    Equal(clip.Tracks[0].BoneTag, 0);
    NearVector(clip.Tracks[0].Keys[1].Position!.Value, new Vector3(1, 2, 0.5f));
    Near(Quaternion.Dot(clip.Tracks[0].Keys[0].Rotation, clip.Tracks[0].Keys[1].Rotation), 1);
    Equal(IfpParser.Parse("ped", [.. Packed(), 0, 0, 0]).Clips.Length, 1);
}
static void TestChunked()
{
    var clip = IfpParser.Parse("cuts", Chunked()).Clips.Single();
    Equal(clip.Name, "ab");
    Near(clip.Duration, 1.5f);
    Near(clip.Tracks[0].Keys[0].Rotation.Z, -MathF.Sqrt(0.5f));
    NearVector(clip.Tracks[0].Keys[1].Scale!.Value, new Vector3(1, 2, 3));
}
static void TestMalformed()
{
    var bytes = Packed();
    Throws<InvalidDataException>(() => IfpParser.Parse("x", bytes[..^1]));
    var badTime = Packed(); BitConverter.GetBytes((short)100).CopyTo(badTime, 116);
    Throws<InvalidDataException>(() => IfpParser.Parse("x", badTime));
    var badType = Packed(); BitConverter.GetBytes(9).CopyTo(badType, 96);
    Throws<InvalidDataException>(() => IfpParser.Parse("x", badType));
    var badSize = Packed(); BitConverter.GetBytes(uint.MaxValue).CopyTo(badSize, 4);
    Throws<InvalidDataException>(() => IfpParser.Parse("x", badSize));
    var badTail = Packed().Concat(new byte[] { 1 }).ToArray();
    Throws<InvalidDataException>(() => IfpParser.Parse("x", badTail));
}
static void TestChunkAnomalies()
{
    var data = Chunked();
    var offset = data.AsSpan().IndexOf(Encoding.ASCII.GetBytes("KRTS")) + 8;
    var terminal = offset + 44;
    data.AsSpan(terminal, 16).Clear();
    BitConverter.GetBytes(1e20f).CopyTo(data, terminal + 16);
    var diagnostics = new List<Diagnostic>();
    var keys = IfpParser.Parse("test", data, diagnostics.Add).Clips[0].Tracks[0].Keys;
    NearVector(keys[1].Position!.Value, keys[0].Position!.Value);
    Near(keys[1].Time, 1.5f);
    Equal(diagnostics.Count, 1);
    data = Chunked();
    BitConverter.GetBytes(1.5f).CopyTo(data, offset + 40);
    BitConverter.GetBytes(0f).CopyTo(data, terminal + 40);
    keys = IfpParser.Parse("test", data, diagnostics.Add).Clips[0].Tracks[0].Keys;
    Near(keys[0].Time, 0); Near(keys[1].Time, 1.5f);
    Equal(diagnostics.Count, 2);
}
static void TestChunkLeadingGuard()
{
    var data = Chunked([0f, 0.02f, 1.5f]);
    var offset = data.AsSpan().IndexOf(Encoding.ASCII.GetBytes("KRTS")) + 8;
    data.AsSpan(offset, 16).Clear();
    BitConverter.GetBytes(float.NaN).CopyTo(data, offset + 16);
    var diagnostics = new List<Diagnostic>();
    var keys = IfpParser.Parse("guard", data, diagnostics.Add).Clips[0].Tracks[0].Keys;
    Near(keys[0].Time, 0);
    Equal(keys[0] with { Time = keys[1].Time }, keys[1]);
    Equal(diagnostics.Count, 1);
    // A corrupt pose cannot be bridged over an arbitrary interval or an interior key.
    BitConverter.GetBytes(0.021f).CopyTo(data, offset + 44 + 40);
    Throws<InvalidDataException>(() => IfpParser.Parse("guard", data));
    var interior = Chunked([0f, 0.02f, 1.5f]);
    interior.AsSpan(offset + 44, 16).Clear();
    Throws<InvalidDataException>(() => IfpParser.Parse("guard", interior));
    var both = Chunked([0f, 0.02f]);
    both.AsSpan(offset, 16).Clear(); both.AsSpan(offset + 44, 16).Clear();
    Throws<InvalidDataException>(() => IfpParser.Parse("guard", both));
    var singleton = Chunked([0f]); singleton.AsSpan(offset, 16).Clear();
    Throws<InvalidDataException>(() => IfpParser.Parse("guard", singleton));
    var badTime = Chunked([0f, 0.02f]); BitConverter.GetBytes(float.NaN).CopyTo(badTime, offset + 40);
    Throws<InvalidDataException>(() => IfpParser.Parse("guard", badTime));
}
static void TestChunkPreroll()
{
    var data = Chunked([-43.666668f, 1.5f]);
    var offset = data.AsSpan().IndexOf(Encoding.ASCII.GetBytes("KRTS")) + 8;
    data.AsSpan(offset, 40).CopyTo(data.AsSpan(offset + 44, 40));
    var diagnostics = new List<Diagnostic>();
    var keys = IfpParser.Parse("preroll", data, diagnostics.Add).Clips[0].Tracks[0].Keys;
    Near(keys[0].Time, 0); Near(keys[1].Time, 1.5f);
    Equal(keys[0] with { Time = keys[1].Time }, keys[1]);
    Equal(diagnostics.Count, 1);
    // Negative moving keys and wholly negative tracks are not this export pattern.
    Throws<InvalidDataException>(() => IfpParser.Parse("preroll", Chunked([-0.000333f, 1.5f])));
    BitConverter.GetBytes(-1f).CopyTo(data, offset + 44 + 40);
    Throws<InvalidDataException>(() => IfpParser.Parse("preroll", data));
}
static void TestCollision()
{
    foreach (var version in new[] { "COL2", "COL3" })
    {
        var m = ColParser.Parse(Collision(version)).Single();
        Equal(m.Name, "road"); Equal(m.ModelId, 3992);
        NearVector(m.Collision.Vertices[1], new Vector3(100, -200, 300));
        Equal(string.Join(',', m.Collision.Indices), "0,2,1");
        NearVector(m.Collision.Spheres[0].Center, new Vector3(100, -200, 300));
        Near(m.Collision.Spheres[0].Radius, 50);
        NearVector(m.Collision.Boxes[0].Extents, new Vector3(100, 200, 300));
    }
    Equal(ColParser.Parse([.. Collision("COL2"), .. Collision("COL3"), 0, 0]).Count, 2);
}
static void TestBadCollision()
{
    var offset = Collision("COL3"); BitConverter.GetBytes(uint.MaxValue).CopyTo(offset, 96);
    Throws<InvalidDataException>(() => ColParser.Parse(offset));
    var index = Collision("COL3"); BitConverter.GetBytes(ushort.MaxValue).CopyTo(index, index.Length - 8);
    Throws<InvalidDataException>(() => ColParser.Parse(index));
    Throws<InvalidDataException>(() => ColParser.Parse(Collision("COL2")[..^1]));
    var diagnostics = new List<Diagnostic>();
    Equal(ColParser.Parse([.. offset, .. Collision("COL2")], "mixed", diagnostics.Add, skipInvalidRecords: true).Count, 1);
    Equal(diagnostics.Count, 1);
}
static void TestBinding()
{
    var bank = IfpParser.Parse("ped", Packed());
    var model = Model();
    var clip = AnimationBinder.Bind(bank, model).Single();
    Near((clip.Frames.Length - 1) / clip.FrameRate, 80f / 60);
    NearVector(clip.Frames[^1][0].Position, new Vector3(110, -180, 80));
    NearVector(clip.Frames[0][1].Position, model.Bones[1].LocalBind.Position);
    Near(clip.RootSpeed, MathF.Sqrt(100 * 100 + 200 * 200) / (80f / 60));
    var stationary = AnimationBinder.Bind(bank, model, true).Single();
    NearVector(stationary.Frames[^1][0].Position, new Vector3(10, 20, 80));
    Near(stationary.RootSpeed, clip.RootSpeed);
    var aliasBank = bank with { Clips = [bank.Clips[0] with { Tracks = [bank.Clips[0].Tracks[0] with { Name = "Normal" }] }] };
    NearVector(AnimationBinder.Bind(aliasBank, model).Single().Frames[^1][0].Position, clip.Frames[^1][0].Position);
    Equal(AnimationBinder.Bind(bank, model, clips: []).Length, 0);
    var constant = bank.Clips[0] with { Duration = 0, Tracks = [bank.Clips[0].Tracks[0] with { Keys = [bank.Clips[0].Tracks[0].Keys[0]] }] };
    Equal(AnimationBinder.Bind(bank with { Clips = [constant] }, model).Single().Frames.Length, 1);
}
static void TestBadBinding()
{
    var bank = IfpParser.Parse("ped", Packed());
    Throws<KeyNotFoundException>(() => AnimationBinder.Bind(bank, Model(), clips: ["missing"]));
    var duplicate = bank.Clips[0] with { Tracks = [bank.Clips[0].Tracks[0], bank.Clips[0].Tracks[0]] };
    Throws<InvalidDataException>(() => AnimationBinder.Bind(bank with { Clips = [duplicate] }, Model()));
    var missing = bank.Clips[0] with { Tracks = [bank.Clips[0].Tracks[0] with { Name = "unrelated", BoneTag = null }] };
    Throws<InvalidDataException>(() => AnimationBinder.Bind(bank with { Clips = [missing] }, Model()));
}
static void TestPedMetadata()
{
    const string peds = "# example\npeds\n46, HMYRI, HMYRI, CIVMALE, STAT_SUIT_GUY, man, 1983\nend\n";
    const string groups = "man, ped, walkcycle, 4\nWALK_civi\nrun_civi\nsprint_panic\nIDLE_stance\nend\nfatman, ped, walkcycle, 4\nWALK_fat\nrun_fat\nwomen_runpanic\nIdlestance_fat\nend";
    var metadata = PedMetadata.Parse(peds, groups);
    Equal(metadata.GetPreset("/Game/SanAndreas/Characters/Peds/SK_smoke.SK_smoke")!.Walk, "WALK_fat");
    Equal(metadata.GetPreset("SK_hmyri")!.Group, "man");
    Equal(metadata.GetPreset("SK_hmyri")!.BankId, "ifp:ped");
    Equal(metadata.GetPreset("SK_unknown"), null);
    Throws<InvalidDataException>(() => PedMetadata.Parse(peds, groups[..^3]));
}

static ModelData Model() => new()
{
    Id = "test", Parts = [], Bones = [
        new BoneData("Root", -1, new TransformData(new Vector3(10, 20, 30), Quaternion.Identity, Vector3.One), TransformData.Identity),
        new BoneData("Pelvis", 0, new TransformData(new Vector3(0, 0, 12), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), Vector3.One), TransformData.Identity)]
};

static byte[] Packed(short secondTime = 80)
{
    var payload = Build(w =>
    {
        Text(w, "ped", 24); w.Write(1); Text(w, "WALK_fat", 24); w.Write(1); w.Write(32); w.Write(1);
        Text(w, "Root", 24); w.Write(4); w.Write(2); w.Write(0);
        foreach (var second in new[] { false, true })
        {
            w.Write((short)0); w.Write((short)0); w.Write((short)0); w.Write((short)(second ? -4096 : 4096));
            w.Write(second ? secondTime : (short)0);
            w.Write((short)(second ? 1024 : 0)); w.Write((short)(second ? 2048 : 0)); w.Write((short)(second ? 512 : 0));
        }
    });
    return Build(w => { w.Write(Encoding.ASCII.GetBytes("ANP3")); w.Write(payload.Length); w.Write(payload); });
}
static byte[] Chunked(float[]? times = null)
{
    times ??= [0f, 1.5f];
    var bone = Build(w =>
    {
        Chunk(w, "ANIM", Build(x => { Text(x, "Root", 28); x.Write(times.Length); x.Write(0); x.Write(0); x.Write(0); }));
        Chunk(w, "KRTS", Build(x =>
        {
            foreach (var time in times)
            {
                x.Write(0f); x.Write(0f); x.Write(MathF.Sqrt(0.5f)); x.Write(MathF.Sqrt(0.5f));
                Vector(x, new Vector3(time, 0, 0)); Vector(x, new Vector3(1, 2, 3)); x.Write(time);
            }
        }));
    });
    var data = Build(w =>
    {
        Chunk(w, "INFO", Build(x => { x.Write(1); Text(x, "x", 2); }));
        Chunk(w, "NAME", Encoding.ASCII.GetBytes("ab\0"));
        Chunk(w, "DGAN", Build(x => { Chunk(x, "INFO", Build(y => { y.Write(1); y.Write(0); })); Chunk(x, "CPAN", bone); }));
    });
    return Build(w => { w.Write(Encoding.ASCII.GetBytes("ANPK")); w.Write(data.Length); w.Write(data); });
}
static byte[] Collision(string version)
{
    var header = version == "COL3" ? 120 : 108;
    var bytes = Build(w =>
    {
        w.Write(Encoding.ASCII.GetBytes(version)); w.Write(0); Text(w, "road", 22); w.Write((ushort)3992);
        for (var i = 0; i < 10; i++) w.Write(0f);
        w.Write((ushort)1); w.Write((ushort)1); w.Write((ushort)1); w.Write((ushort)0); w.Write(2);
        w.Write(header - 4); w.Write(header + 20 - 4); w.Write(0); w.Write(header + 48 - 4); w.Write(header + 66 - 4); w.Write(0);
        if (version == "COL3") { w.Write(0); w.Write(0); w.Write(0); }
        Vector(w, new Vector3(1, 2, 3)); w.Write(0.5f); w.Write(0);
        Vector(w, new Vector3(-1, -2, -3)); Vector(w, new Vector3(1, 2, 3)); w.Write(0);
        foreach (var v in new[] { Vector3.Zero, new Vector3(1, 2, 3), new Vector3(1, 0, 0) })
        { w.Write((short)(v.X * 128)); w.Write((short)(v.Y * 128)); w.Write((short)(v.Z * 128)); }
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)0);
    });
    BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
    return bytes;
}
static byte[] Build(Action<BinaryWriter> action) { using var ms = new MemoryStream(); using var w = new BinaryWriter(ms); action(w); return ms.ToArray(); }
static void Text(BinaryWriter w, string text, int size) { var bytes = Encoding.ASCII.GetBytes(text); w.Write(bytes); w.Write(new byte[size - bytes.Length]); }
static void Vector(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
static void Chunk(BinaryWriter w, string tag, byte[] data) { w.Write(Encoding.ASCII.GetBytes(tag)); w.Write(data.Length); w.Write(data); w.Write(new byte[(4 - (data.Length & 3)) & 3]); }
static void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"Expected {expected}, got {actual}."); }
static void Near(float actual, float expected) { if (MathF.Abs(actual - expected) > 0.0001f) throw new Exception($"Expected {expected}, got {actual}."); }
static void NearVector(Vector3 actual, Vector3 expected) { if (Vector3.Distance(actual, expected) > 0.0001f) throw new Exception($"Expected {expected}, got {actual}."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
