using System.Globalization;
using System.Numerics;
using System.Text;
using Sade.Contracts;
using Sade.Formats;

namespace Sade.Reader;

public sealed partial class GameReader
{
    private WeaponData[]? weapons;

    /// <summary>Every active weapon.dat record, with standard-skill firearms and installed melee combos.</summary>
    public IReadOnlyList<WeaponData> GetWeapons()
    {
        lock (gate)
        {
            if (weapons is not null) return weapons;
            var rows = DataRows(ReadOriginalData("weapon.dat")).ToArray();
            var aims = rows.Where(r => r[0] == "%" && r.Length >= 10).ToDictionary(r => r[1], StringComparer.OrdinalIgnoreCase);
            var models = ReadOriginalData("default.ide").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('#')[0].Split(',', StringSplitOptions.TrimEntries))
                .Where(r => r.Length >= 2 && int.TryParse(r[0], out var n) && n >= 300 && n <= 399)
                .ToDictionary(r => Integer(r[0]), r => r[1]);
            var combos = ReadMeleeCombos(ReadOriginalData("melee.dat"));
            var result = new List<WeaponData>();
            foreach (var row in rows.Where(r => r[0] == "£" || r[0] == "$" && r.Length >= 26 && r[14] == "1"))
            {
                if (!WeaponMappings.TryGetValue(row[1], out var map)) throw new InvalidDataException($"Unmapped installed weapon '{row[1]}'.");
                var melee = row[0] == "£";
                if (row.Length < (melee ? 12 : 26)) throw new InvalidDataException($"Truncated weapon '{row[1]}'.");
                var modelNumber = Integer(row[5]);
                var modelId = modelNumber < 0 ? "" : FindAsset($"/Game/SanAndreas/Weapons/SM_{models[modelNumber]}", AssetKind.StaticMesh);
                var combo = melee && row[1] != "PARACHUTE" ? combos[row[8] == "UNARMED" ? "UNARMED_1" : row[8]] : null;
                var group = combo?.Group ?? row[8];
                var anim = WeaponAnimations.GetValueOrDefault(group);
                if (map.Id is "camera" or "detonator" or "parachute") anim = null;
                var bank = anim is null ? null : LoadAnimationBank("ifp:" + anim.Bank);
                string Clip(string name) => name.Length == 0 || bank is null ? "" :
                    bank.Clips.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? throw new InvalidDataException($"{map.Id}: {bank.Id} has no '{name}' clip.");
                var fire = Clip(anim?.Fire ?? "");
                var reload = Clip(anim?.Reload ?? "");
                var additional = (anim?.Additional ?? []).Select(Clip).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                MeleeAttackData[] attacks = combo is null || anim is null ? [] : combo.Attacks.Select((a, i) =>
                    new MeleeAttackData(a[0], Clip(anim.Attacks[i]), Number(a[1]) / 30, Number(a[2]) / 30,
                        Number(a[5]), Number(a[3]) * 100, a[4])).ToArray();
                var start = melee ? 0 : Number(row[18]) / 30;
                var rawEnd = melee ? attacks.FirstOrDefault()?.ChainTime ?? .5f : Number(row[19]) / 30;
                var end = melee ? rawEnd : start + MathF.Floor(.1f + (rawEnd - start) * 50) / 50 - .006f;
                if (end <= start) throw new InvalidDataException($"Invalid weapon animation loop for {map.Id}.");
                var flags = melee ? 0 : uint.Parse(row[25], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                aims.TryGetValue(row[8], out var aim);
                var thrown = map.Kind is "grenade" or "molotov" or "teargas" or "satchel";
                var rocket = map.Kind is "rocket" or "homing_rocket";
                var notes = new List<string>();
                if (thrown || rocket) notes.Add("Flight speed, gravity and fuse are explicit mount adapter values; weapon.dat labels projectile columns old_shot_data. Grenade/molotov/rocket blast radii follow CExplosion.");
                if (map.Kind == "melee" || combo is not null && map.Kind == "tool") notes.Add("Melee damage/timing/radius come from melee.dat. Swing/impact sounds use named original bank samples; the original ped audio mix is not reconstructed.");
                if (map.Kind == "teargas") notes.Add("Gas cloud radius/lifetime are mount adapter values.");
                if (map.Id is "chainsaw" or "minigun" or "extinguisher") notes.Add("DE's procedural cue is resolved to its installed start/loop SoundWaves.");
                if (map.Kind == "satchel") notes.Add("Original satchels use a detonator; autonomous NPCs remotely detonate armed charges after at least five seconds while fighting.");
                result.Add(new WeaponData(map.Id, map.Name, modelId, SoundId(map.Sound), OptionalSound(map.ReloadA),
                    bank?.Id ?? "", fire, reload, Number(row[4]) * 100, melee ? attacks.FirstOrDefault()?.Damage ?? 0 : Number(row[10]),
                    melee ? 1 : Integer(row[9]), 1 / (end - start), melee ? 0 : ReloadDuration(flags, aim),
                    melee ? Vector3.Zero : GtaCoordinates.ToUnreal(new Vector3(Number(row[11]), Number(row[12]), Number(row[13]))))
                {
                    Kind = map.Kind, SourceName = row[1], ModelNumber = modelNumber, Slot = Integer(row[7]), CombatEnabled = map.Combat,
                    FirePitch = map.Pitch, ReloadFinishSoundId = OptionalSound(map.ReloadB),
                    ReloadSoundTime = aim is null ? 0 : Number(aim[6]) / 1000, ReloadFinishSoundTime = aim is null ? 0 : Number(aim[7]) / 1000,
                    FireLoopStart = start, FireLoopEnd = end, FireTime = melee ? attacks.FirstOrDefault()?.HitTime ?? 0 : Number(row[20]) / 30,
                    Accuracy = melee ? 1 : Number(row[16]), AimWithArm = (flags & 0x2) != 0,
                    PelletCount = map.Kind == "shotgun" ? map.Id == "spas12" ? 4 : 15 : 1,
                    SpreadDegrees = map.Kind == "shotgun" ? MathF.Atan(.05f) * 180 / MathF.PI : 0,
                    ProjectileSpeedCentimeters = rocket ? 4000 : thrown ? 1400 : 0, ProjectileGravityCentimeters = thrown ? 981 : 0,
                    FuseSeconds = map.Kind switch { "grenade" => 3, "teargas" => 2, "satchel" => 5, "rocket" or "homing_rocket" => 6, _ => 0 },
                    BlastRadiusCentimeters = map.Kind switch { "rocket" or "homing_rocket" => 1000, "grenade" or "satchel" => 900, "molotov" => 600, "teargas" => 500, _ => 0 },
                    EffectSeconds = map.Kind switch { "molotov" => 3, "teargas" => 10, "flame" => 2.3f, "spray" or "extinguisher" => .25f, _ => 0 },
                    ProjectileModelId = rocket ? FindAsset("/Game/SanAndreas/Weapons/SM_missile", AssetKind.StaticMesh) : thrown ? modelId : null,
                    ImpactSoundId = OptionalSound(map.Impact), ExplosionSoundId = rocket || map.Kind is "grenade" or "molotov" or "satchel" ? SoundId("EXPLOSIONS/3") : null,
                    LoopingFireSound = map.Id is "chainsaw" or "minigun" or "flamethrower" or "spraycan" or "extinguisher",
                    FireStartSoundId = map.Id switch { "chainsaw" => SoundId("CHAINSAW_P/0_Start"), "minigun" => SoundId("11_Start"), "extinguisher" => SoundId("9_Start"), _ => null },
                    FireStopSoundId = map.Id switch { "chainsaw" => SoundId("CHAINSAW_P/2"), "minigun" => SoundId("63"), _ => null },
                    AdditionalAnimationClips = additional, MeleeAttacks = attacks, Notes = notes.Count == 0 ? null : string.Join(" ", notes)
                });
            }
            if (result.Select(w => w.SourceName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
                throw new InvalidDataException("Duplicate standard-skill weapon records.");
            return weapons = result.ToArray();
        }
    }

    private string ReadOriginalData(string filename)
    {
        var path = Provider.Files.Keys.FirstOrDefault(p => p.EndsWith("/GTASA/data/" + filename, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Installed GTA {filename} is missing.");
        return Encoding.Latin1.GetString(Provider.SaveAsset(path));
    }
    private static IEnumerable<string[]> DataRows(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Where(r => r.Length > 0);
    private static Dictionary<string, WeaponMeleeCombo> ReadMeleeCombos(string text)
    {
        var result = new Dictionary<string, WeaponMeleeCombo>(StringComparer.OrdinalIgnoreCase);
        string? name = null; string group = ""; var attacks = new List<string[]>();
        foreach (var row in DataRows(text))
        {
            if (row[0] == "START_COMBO") { name = row[1]; group = ""; attacks = []; }
            else if (name is not null && row[0] == "ANIMGROUP") group = row[1];
            else if (name is not null && row[0] is "ATTACK1" or "ATTACK2" or "ATTACK3" or "AGROUND" or "AMOVING")
            {
                if (row.Length < 8) throw new InvalidDataException($"Truncated melee attack in {name}.");
                attacks.Add(row);
            }
            else if (name is not null && row[0] == "END_COMBO")
            {
                if (group.Length == 0 || attacks.Count != 5) throw new InvalidDataException($"Incomplete melee combo {name}.");
                result.Add(name, new(group, attacks.ToArray())); name = null;
            }
        }
        if (name is not null) throw new InvalidDataException($"Unterminated melee combo {name}.");
        return result;
    }
    private static float ReloadDuration(uint flags, string[]? aim)
    {
        if ((flags & 0x1000) != 0) return (flags & 0x800) != 0 ? 2 : 1;
        if ((flags & 0x8000) != 0) return 1;
        if (aim is not null) foreach (var i in new[] { 6, 8, 7, 9 }) if (Number(aim[i]) + 100 > 400) return (Number(aim[i]) + 100) / 1000;
        return .4f;
    }
    private string SoundId(string sample)
    {
        if (sample.Length == 0) return "";
        var parts = sample.Contains('/') ? sample.Split('/') : ["WEAPONS", sample];
        var separator = parts[1].IndexOf('_');
        var index = separator < 0 ? parts[1] : parts[1][..separator];
        var suffix = separator < 0 ? "" : parts[1][separator..];
        return FindAsset($"/Game/SanAndreas/Audio/SFX/GENRL/{parts[0]}/S_{Integer(index):000}{suffix}", AssetKind.Sound);
    }
    private string? OptionalSound(string sample) => sample.Length == 0 ? null : SoundId(sample);
    private string FindAsset(string package, AssetKind kind) => entries.FirstOrDefault(e => e.Kind == kind && e.Id.Equals(package + "." + package.Split('/')[^1], StringComparison.OrdinalIgnoreCase))?.Id
        ?? throw new FileNotFoundException($"Required installed {kind} asset {package} is missing.");
    private static float Number(string text)
    {
        var value = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return float.IsFinite(value) ? value : throw new InvalidDataException("Non-finite weapon value.");
    }
    private static int Integer(string text) => int.Parse(text, CultureInfo.InvariantCulture);

    // Factual associations independently represented from gta-reversed commit
    // e80afe41b7fed5c2f9f8bb2d87e0cc2466d5e1c1: WeaponInfo.cpp, Weapon.cpp, Explosion.cpp;
    // Audio/Entities/AEWeaponAudioEntity.cpp + AEExplosionAudioEntity.cpp, Audio/Enums/SoundIDs.h;
    // Animation/AnimAssocDefinitions.cpp + AnimAssocAnimations.h. No game payloads embedded.
    // Columns: source | id | label | kind | fire sample | reload A | reload B | pitch | impact | combat.
    private static readonly Dictionary<string, WeaponMapping> WeaponMappings = """
UNARMED|unarmed|Unarmed|melee|87||||58
BRASSKNUCKLE|brassknuckle|Brass knuckles|melee|87||||44
GOLFCLUB|golfclub|Golf club|melee|86||||48
NIGHTSTICK|nightstick|Nightstick|melee|86||||48
KNIFE|knife|Knife|melee|88||||57
BASEBALLBAT|bat|Baseball bat|melee|34||||35
SHOVEL|shovel|Shovel|melee|88||||75
POOLCUE|poolcue|Pool cue|melee|86||||67
KATANA|katana|Katana|melee|88||||56
CHAINSAW|chainsaw|Chainsaw|chainsaw|CHAINSAW_P/0_Loop
DILDO1|dildo1|Gift: purple dildo|tool|86||||50|false
DILDO2|dildo2|Gift: dildo|tool|86||||50|false
VIBE1|vibe1|Gift: vibrator|tool|86||||50|false
VIBE2|vibe2|Gift: silver vibrator|tool|86||||50|false
FLOWERS|flowers|Gift: flowers|tool|86||||54|false
CANE|cane|Cane|tool|47||||46|false
PARACHUTE|parachute|Parachute|tool|65|||||false
GRENADE|grenade|Grenade|grenade
TEARGAS|teargas|Tear gas|teargas
MOLOTOV|molotov|Molotov cocktail|molotov
ROCKET|rocket_projectile|Rocket projectile|projectile||||||false
ROCKET_HS|homing_projectile|Homing rocket projectile|projectile||||||false
FREEFALL_BOMB|freefall_bomb|Freefall bomb projectile|projectile||||||false
PISTOL|colt45|9mm pistol|hitscan|6|55|66|1.41421
PISTOL_SILENCED|silenced|Silenced pistol|hitscan|24|55|66
DESERT_EAGLE|desert_eagle|Desert Eagle|hitscan|6|55|51|0.94387001
SHOTGUN|shotgun|Pump shotgun|shotgun|21|72|71
SAWNOFF|sawnoff|Sawed-off shotgun|shotgun|21|70|69|0.79369998
SPAS12|spas12|Combat shotgun|shotgun|21|72|71
MICRO_UZI|micro_uzi|Micro Uzi|hitscan|0|85|84
TEC9|tec9|Tec-9|hitscan|0|85|84|1.25992
MP5|mp5|MP5|hitscan|17|85|84
AK47|ak47|AK-47|hitscan|3|32|31
M4|m4|M4|hitscan|3|32|31
COUNTRYRIFLE|countryrifle|Country rifle|hitscan|26|32||0.89
SNIPERRIFLE|sniper|Sniper rifle|hitscan|26|55|32
RLAUNCHER|rocket_launcher|Rocket launcher|rocket|68
RLAUNCHER_HS|heatseeker|Heat-seeking rocket launcher|homing_rocket|68
FTHROWER|flamethrower|Flamethrower|flame|VEHICLE_GEN/26|||0.79369998
MINIGUN|minigun|Minigun|hitscan|11_Loop
SATCHEL_CHARGE|satchel|Satchel charge|satchel
DETONATOR|detonator|Detonator|tool|49|||||false
SPRAYCAN|spraycan|Spray can|spray|28
EXTINGUISHER|extinguisher|Fire extinguisher|extinguisher|9_Loop|||0.79369998
CAMERA|camera|Camera|tool|45|||||false
NIGHTVISION|nightvision|Night vision goggles|tool|64|||||false
INFRARED|infrared|Thermal goggles|tool|64|||||false
""".Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Split('|')).ToDictionary(r => r[0], r =>
    new WeaponMapping(r[1], r[2], r[3], r.ElementAtOrDefault(4) ?? "", r.ElementAtOrDefault(5) ?? "", r.ElementAtOrDefault(6) ?? "",
        r.ElementAtOrDefault(7) is { Length: > 0 } pitch ? Number(pitch) : 1, r.ElementAtOrDefault(8) ?? "", r.ElementAtOrDefault(9) != "false"), StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, WeaponAnimation> WeaponAnimations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["colt45"] = new("colt45", "colt45_fire", "colt45_reload"),
        ["silenced"] = new("silenced", "Silence_fire", "Silence_reload"),
        ["python"] = new("python", "python_fire", "python_reload"),
        ["shotgun"] = new("shotgun", "shotgun_fire"),
        ["sawnoff"] = new("colt45", "colt45_fire", "sawnoff_reload"),
        ["buddy"] = new("buddy", "buddy_fire", "buddy_reload"),
        ["uzi"] = new("uzi", "UZI_fire", "UZI_reload"),
        ["rifle"] = new("rifle", "RIFLE_fire", "RIFLE_load"),
        ["sniper"] = new("rifle", "RIFLE_fire", "RIFLE_load"),
        ["grenade"] = new("grenade", "WEAPON_throw", Additional: ["WEAPON_start_throw", "WEAPON_throwu"]),
        ["rocket"] = new("rocket", "RocketFire"), ["flame"] = new("flame", "FLAME_fire"),
        ["spraycan"] = new("spraycan", "spraycan_fire"), ["goggles"] = new("goggles", "goggles_put_on"),
        ["melee_1"] = Melee("ped", ["FightA_1", "FightA_2", "FightA_3", "FightA_G", "FightA_M"], "FIGHTIDLE", "FightA_Block"),
        ["bbbat_1"] = Melee("baseball", ["Bat_1", "Bat_2", "Bat_3", "Bat_4", "BAT_PART"], "Bat_Idle", "Bat_Block"),
        ["knife_1"] = Melee("knife", ["knife_1", "knife_2", "knife_3", "knife_G", "knife_part"], "WEAPON_knifeidle", "knife_block"),
        ["sword_1"] = Melee("sword", ["sword_1", "sword_2", "sword_3", "sword_4", "sword_part"], "sword_idle", "sword_block"),
        ["csaw_1"] = Melee("chainsaw", ["CSaw_1", "CSaw_2", "CSaw_3", "CSaw_G", "CSaw_Part"], "Idle_CSaw", "CSaw_Part"),
        ["dildo_1"] = Melee("dildo", ["Dildo_1", "Dildo_2", "Dildo_3", "Dildo_G", "Dildo_1"], "Dildo_idle", "Dildo_block"),
        ["flowers_1"] = Melee("flowers", ["Flower_attack", "Flower_attack", "Flower_attack", "Flower_attack", "Flower_attack"], "", "")
    };
    private static WeaponAnimation Melee(string bank, string[] attacks, string idle, string block) =>
        new(bank, attacks[0], Additional: attacks.Concat(new[] { idle, block }).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()) { Attacks = attacks };
    private sealed record WeaponMapping(string Id, string Name, string Kind, string Sound, string ReloadA, string ReloadB, float Pitch, string Impact, bool Combat);
    private sealed record WeaponAnimation(string Bank, string Fire, string Reload = "", string[]? Additional = null) { internal string[] Attacks { get; init; } = []; }
    private sealed record WeaponMeleeCombo(string Group, string[][] Attacks);
}
