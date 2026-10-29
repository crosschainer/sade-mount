using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

namespace Sade.Reader;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Reader diagnostics: smoke, audit-banks, inspect <object path>, bones <object path>, find <text>, catalog; optional SADE_GAME_DIR environment variable.");
            return 0;
        }
        var game = Environment.GetEnvironmentVariable("SADE_GAME_DIR") ?? @"C:\Program Files (x86)\Steam\steamapps\common\GTA San Andreas - The Definitive Edition";
        if (args[0] is "smoke" or "audit-banks") return RunSmoke(game, args[0] == "audit-banks");
        if (args[0] == "weapons") return RunWeapons(game);
        if (args[0] == "pose-stats")
        {
            using var reader = new GameReader(); reader.Initialize(new(game, Path.Combine(Path.GetTempPath(), "sade-reader-smoke")));
            var model = reader.LoadModel(args[1]); var preset = reader.GetPedestrianPreset(args[1])!;
            var raw = reader.LoadAnimationBank(preset.BankId);
            foreach (var name in new[] { preset.Idle, preset.Walk, preset.Run })
            {
                var clip = raw.Clips.Single(c=>c.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
                var animation = reader.LoadAnimations(preset.BankId,model,false,[name]).Single();
                Console.WriteLine($"CLIP {name}");
                foreach (var bone in model.Bones.Take(3)) Console.WriteLine($"BIND {bone.Name} {bone.LocalBind}");
                foreach (var track in clip.Tracks.Take(3)) Console.WriteLine($"RAW {track.Name} tag{track.BoneTag} first{track.Keys[0]} last{track.Keys[^1]}");
                Console.WriteLine($"BOUND root {animation.Frames[0][0]}");
            }
            return 0;
        }
        if (args[0] == "mesh-stats")
        {
            using var reader = new GameReader();
            reader.Initialize(new(game, Path.Combine(Path.GetTempPath(), "sade-reader-smoke")));
            var model = reader.LoadModel(args[1]);
            foreach (var part in model.Parts.Where(p => p.Lod == 0))
            {
                int positive = 0, negative = 0;
                for (int i = 0; i < part.Indices.Length; i += 3)
                {
                    var a = part.Vertices[part.Indices[i]]; var b = part.Vertices[part.Indices[i + 1]]; var c = part.Vertices[part.Indices[i + 2]];
                    var dot = System.Numerics.Vector3.Dot(System.Numerics.Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal + b.Normal + c.Normal);
                    if (dot > 0) positive++; if (dot < 0) negative++;
                }
                Console.WriteLine($"{part.Name} face-dot-normal +{positive} -{negative}; normal length {part.Vertices.Min(v=>v.Normal.Length())}..{part.Vertices.Max(v=>v.Normal.Length())}; color {part.Vertices.Aggregate(System.Numerics.Vector4.Zero,(sum,v)=>sum+v.Color)/part.Vertices.Length}; tangent W [{string.Join(',',part.Vertices.Select(v=>v.Tangent.W).Distinct())}]");
            }
            return 0;
        }
        if (args[0] is "vehicles" or "audit-vehicles")
        {
            using var reader = new GameReader();
            reader.Initialize(new(game, Path.Combine(Path.GetTempPath(), "sade-reader-smoke")), d => Console.WriteLine($"[{d.Asset}] {d.Message}"));
            var vehicles = reader.GetVehicles();
            foreach (var group in vehicles.GroupBy(v => v.Family)) Console.WriteLine($"VEHICLES {group.Key}: {group.Count()}");
            if (args[0] == "audit-vehicles")
            {
                if (vehicles.Count != 212) throw new InvalidDataException("Expected all 212 installed vehicles.ide models.");
                foreach (var v in vehicles)
                {
                    if (v.MassKg <= 0 || !float.IsFinite(v.MaxSpeed) || v.Sockets.Any(s => !float.IsFinite(s.Transform.Position.Length()))) throw new InvalidDataException("Invalid vehicle metadata: " + v.Id);
                    if (v.Family is "bike" or "bicycle" && v.Sockets.Count(s => s.Name.StartsWith("wheel_")) < 2) throw new InvalidDataException("Missing original bike wheel bones: " + v.Id);
                }
                foreach (var name in new[] { "infernus", "landstal", "sanchez", "bmx", "squalo", "dodo", "maverick", "vortex" })
                {
                    var v = vehicles.Single(v => v.Id == name);
                    var model = reader.LoadModel(v.ModelId);
                    if (model.Parts.Length == 0 || model.Collision.IsEmpty || model.Parts.Any(p => p.Indices.Any(i => i < 0 || i >= p.Vertices.Length))) throw new InvalidDataException("Invalid driveable geometry/collision: " + name);
                    if (!v.Sockets.Any(s => s.Name == "ped_frontseat")) throw new InvalidDataException("Missing installed driver seat: " + name);
                    if (v.WheelModelId is { } wheel) { var wm = reader.LoadModel(wheel); Console.WriteLine($"WHEEL {name}: min={wm.BoundsMin} max={wm.BoundsMax}"); }
                    Console.WriteLine($"PASS VEHICLE {name}: {v.Family}, {model.Parts.Where(p=>p.Lod==0).Sum(p=>p.Indices.Length / 3)} intact triangles, {model.Bones.Length} bones, {v.Sockets.Length} dummies, collision={model.Collision.Source}");
                }
                Console.WriteLine("PASS all 212 source vehicle metadata and representative driveable family geometry.");
            }
            else foreach (var vehicle in vehicles.Where(v => args.Length < 2 || v.Id == args[1])) Console.WriteLine(JsonConvert.SerializeObject(vehicle));
            return 0;
        }
        var oodle = Path.Combine(game, "Engine/Binaries/ThirdParty/Oodle/Win64/oo2core_8_win64.dll");
        OodleHelper.Initialize(new OodleDotNet.Oodle(oodle));
        using var provider = new DefaultFileProvider(Path.Combine(game, "Gameface/Content/Paks"), SearchOption.TopDirectoryOnly,
            new VersionContainer(EGame.GAME_GTATheTrilogyDefinitiveEdition), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.Mount();
        Console.WriteLine($"Files: {provider.Files.Count}, archives: {provider.MountedVfs.Count}");
        if (args[0] == "data")
        {
            var path = provider.Files.Keys.First(p => p.EndsWith("/" + args[1], StringComparison.OrdinalIgnoreCase));
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(provider.SaveAsset(path)));
        }
        else if (args[0] == "find")
        {
            foreach (var path in provider.Files.Keys.Where(p => p.Contains(args[1], StringComparison.OrdinalIgnoreCase))) Console.WriteLine(path);
        }
        else if (args[0] == "catalog")
        {
            foreach (var path in provider.Files.Keys.Where(p => p.EndsWith("AssetRegistry.bin", StringComparison.OrdinalIgnoreCase)))
            {
                using var reader = provider.CreateReader(path);
                var registry = new FAssetRegistryState(reader);
                Console.WriteLine($"Registry: {path}, {registry.PreallocatedAssetDataBuffers.Length}");
                foreach (var group in registry.PreallocatedAssetDataBuffers.GroupBy(a => a.AssetClass.Text).OrderByDescending(g => g.Count()))
                    Console.WriteLine($"{group.Key}: {group.Count()} {group.First().ObjectPath}");
                foreach (var item in registry.PreallocatedAssetDataBuffers.Where(a => a.AssetClass.Text.Contains("IMG") || a.AssetClass.Text.Contains("IFP")))
                    Console.WriteLine($"Container: {item.AssetClass.Text} {item.ObjectPath}");
            }
        }
        else if (args[0] == "anim-codecs")
        {
            using var registryReader = provider.CreateReader("Gameface/AssetRegistry.bin");
            var registry = new FAssetRegistryState(registryReader);
            var codecs = new Dictionary<string, int>();
            var failures = 0;
            foreach (var item in registry.PreallocatedAssetDataBuffers.Where(a => a.AssetClass.Text == "AnimSequence"))
            {
                try
                {
                    var animation = provider.LoadPackageObject<CUE4Parse.UE4.Assets.Exports.Animation.UAnimSequence>(item.ObjectPath);
                    var codec = animation.CompressedDataStructure?.GetType().Name ?? "Raw";
                    codecs[codec] = codecs.GetValueOrDefault(codec) + 1;
                }
                catch (Exception e) { failures++; Console.WriteLine($"ANIMATION FAILURE {item.ObjectPath}: {e.Message}"); }
            }
            foreach (var (codec, count) in codecs) Console.WriteLine($"{codec}: {count}");
            return failures == 0 ? 0 : 1;
        }
        else if (args[0] is "inspect" or "bones" or "sockets")
        {
            var value = provider.LoadPackageObject(args[1]);
            Console.WriteLine(value.GetType().FullName);
            if (args[0] == "sockets")
                foreach (var socket in value.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex[]>("Sockets", []))
                    Console.WriteLine(JsonConvert.SerializeObject(socket.Load(), Formatting.Indented));
            if (args[0] == "inspect") Console.WriteLine(JsonConvert.SerializeObject(value.Properties.ToDictionary(p => p.Name.Text, p => p.Tag), Formatting.Indented));
            if (value is CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh sm)
            {
                Console.WriteLine($"Static: {sm.RenderData?.LODs?.Length} LODs, {sm.Materials.Length} materials");
                Console.WriteLine(JsonConvert.SerializeObject(sm.BodySetup.Load(), Formatting.Indented));
            }
            if (value is CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh sk)
            {
                Console.WriteLine($"Skeletal: {sk.LODModels?.Length} LODs, {sk.Materials.Length} materials");
                for (var i = 0; i < sk.ReferenceSkeleton.FinalRefBoneInfo.Length; i++)
                    Console.WriteLine($"{i} {sk.ReferenceSkeleton.FinalRefBoneInfo[i].Name.Text} parent={sk.ReferenceSkeleton.FinalRefBoneInfo[i].ParentIndex} {JsonConvert.SerializeObject(sk.ReferenceSkeleton.FinalRefBonePose[i])}");
                foreach (var ud in sk.AssetUserData ?? []) Console.WriteLine($"UserData {JsonConvert.SerializeObject(ud.Load(), Formatting.Indented)}");
            }
        }
        return 0;
    }

    private static int RunSmoke(string game, bool auditBanks)
    {
        using var reader = new GameReader();
        var catalog = reader.Initialize(new(game, Path.Combine(Path.GetTempPath(), "sade-reader-smoke")), d => Console.WriteLine($"[{d.Asset}] {d.Message}"));
        foreach (var group in catalog.GroupBy(e => e.Kind)) Console.WriteLine($"Catalog {group.Key}: {group.Count()}");
        foreach (var id in new[] { "/Game/SanAndreas/Characters/Peds/SK_smoke", "/Game/SanAndreas/Characters/Peds/SK_hmyri", "/Game/SanAndreas/Characters/Peds/SK_ballas1", "/Game/SanAndreas/Environment/LA/LAs/SM_nt_aircon1dbl" })
        {
            var model = reader.LoadModel(id);
            Console.WriteLine($"Model {id}: {model.Parts.Length} sections, {model.Parts.Sum(p => p.Vertices.Length)} vertices, {model.Bones.Length} bones; {model.Collision.Source} hulls={model.Collision.Hulls.Length} boxes={model.Collision.Boxes.Length} spheres={model.Collision.Spheres.Length} capsules={model.Collision.Capsules.Length}; skeleton={model.SkeletonId}");
            if (model.Parts.Any(p => p.Indices.Any(i => i < 0 || i >= p.Vertices.Length))) throw new InvalidDataException("Bad mesh indices");
            foreach (var materialId in model.Parts.Select(p => p.MaterialId).OfType<string>().Distinct())
            {
                var material = reader.LoadMaterial(materialId);
                Console.WriteLine($"Material {JsonConvert.SerializeObject(material)}");
                foreach (var textureId in new[] { material.ColorTexture, material.NormalTexture, material.RoughnessTexture, material.MetallicTexture, material.OpacityTexture }.OfType<string>().Distinct())
                {
                    if (!catalog.Any(e => e.Id.Equals(textureId, StringComparison.OrdinalIgnoreCase))) Console.WriteLine($"MISSING DEPENDENCY {textureId}");
                    var texture = reader.LoadTexture(textureId);
                    Console.WriteLine($"Texture {textureId}: {texture.Format} {texture.Mips[0].Width}x{texture.Mips[0].Height} mips={texture.Mips.Length}");
                }
            }
            if (reader.GetPedestrianPreset(id) is { } preset)
            {
                var clips = reader.LoadAnimations(preset.BankId, model, true, [preset.Idle, preset.Walk, preset.Run]);
                Console.WriteLine($"Preset {JsonConvert.SerializeObject(preset)} => {string.Join(",", clips.Select(c => $"{c.Name}:{c.Frames.Length}@{c.FrameRate}"))}");
                if (clips.Length != 3) throw new InvalidDataException("Missing locomotion clips");
            }
        }
        foreach (var id in new[] { "/Game/SanAndreas/Audio/SFX/GENRL/FRONTEND_MENU/S_008", "/Game/SanAndreas/Audio/Streams/New/RG/TRK_RG_002" })
        {
            var sound = reader.LoadSound(id);
            Console.WriteLine($"Audio {id}: {sound.Format} {sound.Data.Length} bytes {sound.SampleRate}Hz {sound.Channels}channels");
        }
        var bankFailures = 0;
        foreach (var entry in catalog.Where(e => e.Kind == Sade.Contracts.AssetKind.AnimationBank && (auditBanks || e.Id is "ifp:ped" or "ifp:prolog1" or "ifp:prolog3" or "ifp:fat")))
        {
            try { var bank = reader.LoadAnimationBank(entry.Id); Console.WriteLine($"Bank {entry.Id}: {bank.Clips.Length} clips"); }
            catch (InvalidDataException e) { bankFailures++; Console.WriteLine($"BANK FAILURE {entry.Id}: {e.Message}"); }
        }
        var animation = catalog.First(e => e.Id.Contains("/SKR_derrick01_Anim.", StringComparison.OrdinalIgnoreCase));
        var pairedModel = catalog.First(e => e.Kind == Sade.Contracts.AssetKind.SkeletalMesh && e.SkeletonId == animation.SkeletonId);
        var native = reader.LoadAnimations(animation.Id, reader.LoadModel(pairedModel.Id));
        Console.WriteLine($"Native animation {animation.Id} on {pairedModel.Id}: {native.Length} sequences {native[0].Frames.Length} frames");
        Console.WriteLine(bankFailures == 0 ? "PASS: installed-game reader smoke" : $"FAIL: {bankFailures} animation banks are unreadable.");
        return bankFailures == 0 ? 0 : 1;
    }

    private static int RunWeapons(string game)
    {
        using var reader = new GameReader();
        reader.Initialize(new(game, Path.Combine(Path.GetTempPath(), "sade-reader-smoke")), d => Console.WriteLine($"[{d.Asset}] {d.Message}"));
        var ped = reader.LoadModel("/Game/SanAndreas/Characters/Peds/SK_ballas1");
        var weapons = reader.GetWeapons();
        var modelCount = 0;
        var soundIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clipCount = 0;
        foreach (var weapon in weapons)
        {
            Console.WriteLine($"Weapon {JsonConvert.SerializeObject(weapon)}");
            if (!string.IsNullOrEmpty(weapon.ModelId))
            {
                var model = reader.LoadModel(weapon.ModelId);
                if (model.Parts.Length == 0) throw new InvalidDataException($"Empty weapon model {weapon.Id}");
                modelCount++;
                Console.WriteLine($"Weapon model {weapon.Id}: {model.Parts.Sum(p => p.Vertices.Length)} vertices; bounds {model.BoundsMin}..{model.BoundsMax}");
            }
            foreach (var soundId in new[] { weapon.FireSoundId, weapon.ReloadSoundId, weapon.ReloadFinishSoundId, weapon.ImpactSoundId, weapon.ExplosionSoundId, weapon.FireStartSoundId, weapon.FireStopSoundId }.OfType<string>().Where(s => s.Length > 0).Distinct())
            {
                if (!soundIds.Add(soundId)) continue;
                var sound = reader.LoadSound(soundId);
                if (!sound.Data.AsSpan().StartsWith("OggS"u8)) throw new InvalidDataException($"Expected OGG {soundId}");
                Console.WriteLine($"Weapon audio {soundId}: {sound.Data.Length} bytes");
            }
            var clips = new[] { weapon.FireClip, weapon.ReloadClip }.Concat(weapon.AdditionalAnimationClips).Concat(weapon.MeleeAttacks.Select(a => a.Clip))
                .Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (clips.Length > 0)
            {
                var animations = reader.LoadAnimations(weapon.AnimationBankId, ped, false, clips);
                if (animations.Length != clips.Length) throw new InvalidDataException($"Incomplete weapon animations {weapon.Id}");
                clipCount += animations.Length;
                Console.WriteLine($"Weapon clips {weapon.Id}: {string.Join(", ", animations.Select(a => $"{a.Name}:{a.Frames.Length}"))}");
            }
            if (weapon.Kind is "tool" or "projectile" && weapon.CombatEnabled) throw new InvalidDataException($"Noncombat item enabled as weapon: {weapon.Id}");
            if (weapon.Kind == "shotgun" && weapon.PelletCount is not (4 or 15)) throw new InvalidDataException($"Invalid source pellet count: {weapon.Id}");
        }
        if (weapons.Count != 47 || modelCount != 46) throw new InvalidDataException($"Incomplete installed roster: {weapons.Count} profiles, {modelCount} model references.");
        foreach (var pair in new[] { ("colt45", 25f), ("micro_uzi", 20f), ("tec9", 20f), ("mp5", 25f), ("ak47", 30f), ("m4", 30f) })
            if (weapons.Single(w => w.Id == pair.Item1).Damage != pair.Item2) throw new InvalidDataException($"Changed standard-skill damage {pair.Item1}");
        Console.WriteLine($"PASS: {weapons.Count} installed weapon profiles, {modelCount} models, {soundIds.Count} unique audio samples, {clipCount} bound attack/reload/support animations");
        return 0;
    }
}
