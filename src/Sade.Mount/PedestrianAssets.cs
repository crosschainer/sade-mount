using Sandbox;
using Sandbox.Mounting;
using Sade.Contracts;

namespace Sade.Mount;

public sealed partial class SadeMount
{
    private Dictionary<string, WeaponData> weapons = new(StringComparer.OrdinalIgnoreCase);
    internal WeaponData? GetWeapon(string? key) => key is null ? null : weapons.GetValueOrDefault(key);
    internal WeaponData? DefaultWeapon(string id)
    {
        var name = id.Split('/')[^1].Split('.')[0].ToLowerInvariant();
        var faction = Reader.GetPedestrianPreset(id)?.Faction;
        if (faction is "Ballas" or "Grove" or "Vagos" or "Rifa" or "DaNang" or "Mafia" or "Triads" or "Aztecas" or "Gang9" or "Gang10")
            return GetWeapon(name.EndsWith('2') ? "micro_uzi" : name.EndsWith('3') ? "ak47" : "colt45");
        return null;
    }
    internal static string LoadoutPath(AssetEntry entry, string weaponId) => $"models/loadouts/{weaponId}/{CleanPath(entry.Path)}";
    internal string ActorModelPath(string id, string? weaponId) => weaponId is null ? ModelPath(id) : $"mount://sade/{LoadoutPath(entries[id], weaponId)}.vmdl";

    internal void RegisterActor(Model model, string path, string id, PedestrianPreset preset, AnimationClipData[] clips, WeaponData? weapon)
    {
        float Speed(string name, float fallback) => clips.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { RootSpeed: > 0.1f } clip ? clip.RootSpeed * CoordinateSpace.UnitsPerCentimeter : fallback;
        var profile = new PedestrianRuntime.Profile(preset.Name, preset.Idle, preset.Walk, preset.Run, Speed(preset.Walk, 45), Speed(preset.Run, 120), preset.Faction)
        {
            EnemyFactions = preset.EnemyFactions,
            Weapon = weapon is null ? null : MakeWeaponProfile(weapon)
        };
        PedestrianRuntime.Register(model.Name, profile);
        PedestrianRuntime.Register(path, profile);
    }

    private PedestrianRuntime.WeaponProfile MakeWeaponProfile(WeaponData data) => new(data.Name,
        string.IsNullOrEmpty(data.ModelId) ? "" : ModelPath(data.ModelId), PathFor(AssetKind.Sound, data.FireSoundId) ?? "",
        PathFor(AssetKind.Sound, data.ReloadSoundId), "sade_fire", "sade_reload", data.Damage,
        data.RangeCentimeters * CoordinateSpace.UnitsPerCentimeter, data.ShotsPerSecond > 0 ? 1 / data.ShotsPerSecond : 1,
        data.ReloadSeconds, data.ClipSize, data.FirePitch)
    {
        MuzzleOffset = CoordinateSpace.Position(data.MuzzleOffset), WeaponBone = data.WeaponBone,
        ReloadFinishSoundPath = PathFor(AssetKind.Sound, data.ReloadFinishSoundId),
        ReloadSoundTime = data.ReloadSoundTime, ReloadFinishSoundTime = data.ReloadFinishSoundTime,
        Accuracy = data.Accuracy, AimWithArm = data.AimWithArm,
        FireDelay = Math.Clamp(data.FireTime - data.FireLoopStart, 0, data.ShotsPerSecond > 0 ? 1 / data.ShotsPerSecond : 1),
        Kind = data.Kind, CombatEnabled = data.CombatEnabled, PelletCount = data.PelletCount, SpreadDegrees = data.SpreadDegrees,
        ProjectileSpeed = data.ProjectileSpeedCentimeters * CoordinateSpace.UnitsPerCentimeter,
        ProjectileGravity = data.ProjectileGravityCentimeters * CoordinateSpace.UnitsPerCentimeter,
        FuseSeconds = data.FuseSeconds, BlastRadius = data.BlastRadiusCentimeters * CoordinateSpace.UnitsPerCentimeter,
        EffectSeconds = data.EffectSeconds,
        ProjectileModelPath = string.IsNullOrEmpty(data.ProjectileModelId) ? null : ModelPath(data.ProjectileModelId),
        ImpactSoundPath = PathFor(AssetKind.Sound, data.ImpactSoundId), ExplosionSoundPath = PathFor(AssetKind.Sound, data.ExplosionSoundId),
        LoopingFireSound = data.LoopingFireSound,
        FireStartSoundPath = PathFor(AssetKind.Sound, data.FireStartSoundId), FireStopSoundPath = PathFor(AssetKind.Sound, data.FireStopSoundId),
        MeleeAttacks = data.MeleeAttacks.Select(x => new PedestrianRuntime.MeleeAttackProfile(x.Clip, x.HitTime, x.ChainTime, x.Damage,
            x.RadiusCentimeters * CoordinateSpace.UnitsPerCentimeter, x.HitLevel)).ToArray()
    };

    private IReadOnlyList<AssetEntry> RegisterFeatured(MountContext context, IReadOnlyList<AssetEntry> catalog)
    {
        var picks = new (string Model, string Label)[]
        {
            ("SK_Player_Combined", "00_cj"), ("SK_smoke", "01_big_smoke"), ("SK_sweet", "02_sweet"), ("SK_ryder2", "03_ryder"),
            ("SK_fam1", "10_grove_street"), ("SK_ballas1", "11_ballas"), ("SK_lsv1", "12_vagos"),
            ("SK_vla1", "13_aztecas"), ("SK_sfr1", "14_rifa"), ("SK_triada", "15_triads"),
            ("SK_dnb1", "16_da_nang_boys"), ("SK_maffa", "17_mafia")
        };
        var representatives = new List<AssetEntry>();
        foreach (var (name, label) in picks)
        {
            var entry = catalog.FirstOrDefault(x => x.Kind == AssetKind.SkeletalMesh && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry is null || Reader.GetPedestrianPreset(entry.Id) is not { } preset) continue;
            context.Add(ResourceType.PrefabFile, $"00_featured/{label}", new PedestrianLoader(entry.Id, preset));
            context.Add(ResourceType.Model, $"00_featured/{label}", new AliasLoader(ModelPath(entry.Id)));
            representatives.Add(entry);
        }
        foreach (var name in new[] { "SM_nt_aircon1dbl", "SM_lamppost1", "SM_bin1", "SM_cj_dumpster", "SM_lamppost3", "SM_veg_palm02" })
        {
            var entry = catalog.FirstOrDefault(x => x.Kind == AssetKind.StaticMesh && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry is not null) context.Add(ResourceType.Model, $"00_featured/props/{name.ToLowerInvariant()}", new AliasLoader(ModelPath(entry.Id)));
        }
        return representatives;
    }

    private void RegisterLoadouts(MountContext context, IReadOnlyList<AssetEntry> representatives)
    {
        foreach (var weapon in weapons.Values)
        {
            if (!string.IsNullOrEmpty(weapon.ModelId))
                context.Add(ResourceType.Model, $"01_weapons/{weapon.Kind}/{weapon.Id}", new AliasLoader(ModelPath(weapon.ModelId)));
            foreach (var entry in representatives)
            {
                var preset = Reader.GetPedestrianPreset(entry.Id)!;
                context.Add(ResourceType.Model, LoadoutPath(entry, weapon.Id), new ModelLoader(entry.Id, weaponId: weapon.Id));
                context.Add(ResourceType.Model, $"loadouts/{weapon.Kind}/{weapon.Id}/{entry.Name.ToLowerInvariant()}", new AliasLoader(ActorModelPath(entry.Id, weapon.Id)));
                context.Add(ResourceType.PrefabFile, $"loadouts/{weapon.Kind}/{weapon.Id}/{entry.Name.ToLowerInvariant()}", new PedestrianLoader(entry.Id, preset, weapon.Id));
            }
        }
    }
}

internal sealed class AliasLoader(string target) : ResourceLoader<SadeMount>
{
    protected override Task<object> LoadAsync() => Host.GetByPath(target).GetOrCreate();
}
