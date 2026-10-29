using System.Reflection;
using System.Text.Json;
using Sade.Contracts;
using Sade.Reader;

var scratch = Path.Combine(Path.GetTempPath(), "sade-reader-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var pak = Path.Combine(scratch, "fingerprint.pak");
    File.WriteAllBytes(pak, new byte[2048]);
    var fingerprintMethod = typeof(GameReader).GetMethod("ComputeFingerprint", BindingFlags.Static | BindingFlags.NonPublic)!;
    string Fingerprint() => (string)fingerprintMethod.Invoke(null, [new[] { pak }])!;
    var first = Fingerprint();
    var timestamp = File.GetLastWriteTimeUtc(pak);
    using (var stream = new FileStream(pak, FileMode.Open, FileAccess.Write)) { stream.Position = stream.Length - 1; stream.WriteByte(42); }
    File.SetLastWriteTimeUtc(pak, timestamp);
    Check(first != Fingerprint(), "PAK footer change invalidates the fingerprint even with identical length and mtime.");
    first = Fingerprint(); File.SetLastWriteTimeUtc(pak, timestamp.AddSeconds(1));
    Check(first != Fingerprint(), "PAK timestamp change invalidates the fingerprint.");

    var game = args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("SADE_GAME_DIR");
    if (string.IsNullOrWhiteSpace(game)) { Console.WriteLine("PASS fingerprint tests; supply game installation path for read-only integration tests."); return; }
    var cacheDirectory = Path.Combine(scratch, "cache");
    using var reader = new GameReader();
    var diagnostics = new List<Diagnostic>();
    var options = new ReaderOptions(game, cacheDirectory);
    var original = reader.Initialize(options, diagnostics.Add).ToArray();
    SpeechTests.Run(reader, original);
    if (args.Contains("--speech-only", StringComparer.Ordinal)) return;
    var fingerprint = reader.Fingerprint;
    var cachePath = Directory.GetFiles(cacheDirectory, "catalog-*.json").Single();
    var valid = File.ReadAllBytes(cachePath);
    var marker = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(cachePath, marker);
    var warm = reader.Initialize(options, diagnostics.Add);
    Check(warm.Count == original.Length && reader.Fingerprint == fingerprint && File.GetLastWriteTimeUtc(cachePath) == marker, "Warm initialization reuses an unchanged catalog.");

    void RejectCache(byte[] bytes, string label)
    {
        File.WriteAllBytes(cachePath, bytes); diagnostics.Clear();
        Check(reader.Initialize(options, diagnostics.Add).Count == original.Length, label + " rebuilds from the actual game registry.");
        Check(diagnostics.Any(d => d.Asset == "cache"), label + " produces a diagnostic.");
    }
    RejectCache("{"u8.ToArray(), "Truncated JSON");
    using var parsed = JsonDocument.Parse(valid);
    var version = parsed.RootElement.GetProperty("Version").GetString();
    RejectCache(JsonSerializer.SerializeToUtf8Bytes(new { Version = version, Fingerprint = fingerprint, Entries = (object?)null, Banks = new { }, Containers = Array.Empty<string>() }), "Null catalog collections");
    RejectCache(JsonSerializer.SerializeToUtf8Bytes(new { Version = version, Fingerprint = fingerprint, Entries = new[] { new { Id = "/Game/x.x", Path = "../escape", Kind = 0, Name = "x" } }, Banks = new { }, Containers = Array.Empty<string>() }), "Invalid catalog path");
    using (var large = new FileStream(cachePath, FileMode.Create)) large.SetLength(128L * 1024 * 1024 + 1);
    diagnostics.Clear();
    Check(reader.Initialize(options, diagnostics.Add).Count == original.Length && diagnostics.Any(d => d.Asset == "cache" && d.Message.Contains("128 MiB")), "Oversized catalog is rejected before JSON allocation.");

    var ids = original.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var (model, faction) in new[] { ("ballas1", "Ballas"), ("fam1", "Grove"), ("lsv1", "Vagos"), ("sfr1", "Rifa"), ("dnb1", "DaNang"), ("vmaff1", "Mafia"), ("triada", "Triads"), ("vla1", "Aztecas") })
    {
        var preset = reader.GetPedestrianPreset($"/Game/SanAndreas/Characters/Peds/SK_{model}");
        Check(preset is not null && preset.Faction == faction && !preset.EnemyFactions.Contains(faction) && preset.EnemyFactions.Length == 9, "Installed gang allegiance and source hostility: " + faction);
    }
    Check(reader.GetPedestrianPreset("SK_bmydj") is { Faction: "Civilian", EnemyFactions.Length: 0 }, "Gang-style walking animation does not assign civilian to a gang.");
    var sourceRelationships = Sade.Formats.PedMetadata.Parse("peds\n102, BALLAS1, BALLAS1, GANG1, STAT_GANG1, gang1\nend", "gang1, ped, walkcycle, 4\nwalk\nrun\nsprint\nidle\nend", "GANG1\nHate GANG2 PLAYER1\nRespect GANG1");
    Check(sourceRelationships.GetPreset("SK_ballas1")!.EnemyFactions.SequenceEqual(new[] { "Grove" }), "Player hostility is excluded while source gang hostility is retained.");
    foreach (var id in new[] { "/Game/SanAndreas/Characters/Peds/SK_smoke", "/Game/SanAndreas/Environment/LA/LAs/SM_nt_aircon1dbl" })
    {
        var model = reader.LoadModel(id);
        foreach (var materialId in model.Parts.Select(p => p.MaterialId).OfType<string>().Distinct())
        {
            Check(ids.Contains(materialId), "Material dependency is registered: " + materialId);
            var material = reader.LoadMaterial(materialId);
            foreach (var textureId in new[] { material.ColorTexture, material.NormalTexture, material.RoughnessTexture, material.MetallicTexture, material.PackedTexture, material.OpacityTexture, material.EmissiveTexture }.OfType<string>())
                Check(ids.Contains(textureId), "Texture dependency is registered: " + textureId);
        }
    }
    var smokeClothes = reader.LoadMaterial("/Game/SanAndreas/Cutscene/Characters/MI_cssmoke_clothes.MI_cssmoke_clothes");
    Check(smokeClothes.CharacterPackedLayout && smokeClothes.UsePackedPbr && smokeClothes.PackedTexture?.Contains("T_cssmoke_clothes_P") == true,
        "Smoke inherits IsPacked and resolves the clothing metallic/roughness mask.");
    var glasses = reader.LoadMaterial("/Game/SanAndreas/Cutscene/Characters/MI_Glass_Smoke.MI_Glass_Smoke");
    Check(!glasses.CharacterPackedLayout && !glasses.UsePackedPbr && glasses.BlendMode == "Translucent", "Glasses retain separate glass shading.");
    var cj = reader.LoadModel(Sade.Mount.PlayerOutfit.ModelId);
    var vest = reader.LoadModel(Sade.Mount.PlayerOutfit.VestId);
    var jeans = reader.LoadModel(Sade.Mount.PlayerOutfit.JeansId);
    var dressed = Sade.Mount.PlayerOutfit.Dress(cj, vest, jeans);
    Check(dressed.Parts.Any(p => p.MaterialId?.EndsWith("/MI_vest.MI_vest") == true), "CJ wears the default white vest.");
    Check(dressed.Parts.Count(p => p.Name.StartsWith("vest/")) == vest.Parts.Length, "CJ's bare upper body is replaced once.");
    Check(cj.Parts.Where(p => p.MaterialId?.Contains("sneaker") == true || p.MaterialId?.Contains("player_face") == true)
        .All(p => dressed.Parts.Contains(p)), "CJ keeps his original head and shoes.");
    Check(dressed.Parts.First(p => p.Name.StartsWith("jeans/")).Vertices.Select(v => v.UV).SequenceEqual(jeans.Parts[0].Vertices.Select(v => v.UV)), "CJ uses the standalone jeans UVs.");
    var leftHelper = Array.FindIndex(cj.Bones, b => b.Name == "L_breast");
    Check(dressed.Parts.Where(p => p.Lod == 0).SelectMany(p => p.Vertices).All(v =>
        !(v.Bones.X == leftHelper && v.Weights.X > .05f || v.Bones.Y == leftHelper && v.Weights.Y > .05f || v.Bones.Z == leftHelper && v.Weights.Z > .05f || v.Bones.W == leftHelper && v.Weights.W > .05f)),
        "The dressed CJ uses the vest's correct left upper-arm weights.");
    var walks = reader.LoadAnimations("ifp:ped", dressed, true, ["WALK_player", "run_player"]);
    Check(walks.All(c => c.Frames.All(f => f[leftHelper] == dressed.Bones[leftHelper].LocalBind)), "CJ's DE arm helper stays in its reference pose throughout locomotion.");
    Console.WriteLine("PASS installed-game cache recovery and transitive fixture dependencies.");
}
finally
{
    // Delete only this run's newly created, resolved temporary directory.
    var temporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sade-reader-tests")) + Path.DirectorySeparatorChar;
    if (Path.GetFullPath(scratch).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) Directory.Delete(scratch, recursive: true);
}

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidDataException(description);
    Console.WriteLine("PASS " + description);
}
