using System.Numerics;

namespace Sade.Contracts;

public enum AssetKind { StaticMesh, SkeletalMesh, Texture, Material, Sound, Animation, AnimationBank }

public sealed record AssetEntry(string Id, string Path, AssetKind Kind, string Name)
{
    public string? SkeletonId { get; init; }
    public string? AnimationGroup { get; init; }
}

public sealed record ReaderOptions(string GameDirectory, string CacheDirectory);
public sealed record Diagnostic(string Asset, string Message, bool Error = false);

/// <summary>Boundary between the engine adapter and the isolated reader. All geometry uses Unreal axes and centimeters.</summary>
public interface IGameReader : IDisposable
{
    IReadOnlyList<AssetEntry> Initialize(ReaderOptions options, Action<Diagnostic>? report = null);
    ModelData LoadModel(string id);
    TextureData LoadTexture(string id);
    MaterialData LoadMaterial(string id);
    SoundData LoadSound(string id);
    AnimationBankData LoadAnimationBank(string id);
    AnimationClipData[] LoadAnimations(string id, ModelData model, bool inPlace = false, string[]? clips = null);
    PedestrianPreset? GetPedestrianPreset(string modelId);
    IReadOnlyList<WeaponData> GetWeapons();
    IReadOnlyList<VehicleData> GetVehicles();
    string Fingerprint { get; }
}

public readonly record struct TransformData(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static TransformData Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);
}

public readonly record struct BoneIndices(int X, int Y, int Z, int W);
public readonly record struct VertexData(Vector3 Position, Vector3 Normal, Vector4 Tangent, Vector2 UV, Vector4 Color, BoneIndices Bones, Vector4 Weights);
public sealed record MeshPartData(string Name, string? MaterialId, VertexData[] Vertices, int[] Indices, int Lod = 0);
public sealed record BoneData(string Name, int Parent, TransformData LocalBind, TransformData WorldBind);
public sealed record CollisionBox(Vector3 Center, Vector3 Extents, Quaternion Rotation);
public sealed record CollisionSphere(Vector3 Center, float Radius);
public sealed record CollisionCapsule(Vector3 Start, Vector3 End, float Radius);
public sealed record CollisionData
{
    public Vector3[] Vertices { get; init; } = [];
    public int[] Indices { get; init; } = [];
    public Vector3[][] Hulls { get; init; } = [];
    public CollisionBox[] Boxes { get; init; } = [];
    public CollisionSphere[] Spheres { get; init; } = [];
    public CollisionCapsule[] Capsules { get; init; } = [];
    public bool IsEmpty => Vertices.Length == 0 && Hulls.Length == 0 && Boxes.Length == 0 && Spheres.Length == 0 && Capsules.Length == 0;
    public string Source { get; init; } = "none";
}
public sealed record ModelData
{
    public required string Id { get; init; }
    public required MeshPartData[] Parts { get; init; }
    public BoneData[] Bones { get; init; } = [];
    public CollisionData Collision { get; init; } = new();
    public string? SkeletonId { get; init; }
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
}

public sealed record TextureMip(int Width, int Height, byte[] Data);
public sealed record TextureData(string Id, string Format, bool Srgb, TextureMip[] Mips);
public sealed record MaterialData
{
    public required string Id { get; init; }
    public string? ColorTexture { get; init; }
    public string? NormalTexture { get; init; }
    public string? RoughnessTexture { get; init; }
    public string? MetallicTexture { get; init; }
    public string? PackedTexture { get; init; }
    public bool CharacterPackedLayout { get; init; }
    public bool UsePackedPbr { get; init; }
    public string? OpacityTexture { get; init; }
    public string? EmissiveTexture { get; init; }
    public Vector4 Tint { get; init; } = Vector4.One;
    public float Roughness { get; init; } = 0.7f;
    public float Metallic { get; init; }
    public float Opacity { get; init; } = 1;
    public float AlphaCutoff { get; init; } = 0.333f;
    public bool TwoSided { get; init; }
    public string BlendMode { get; init; } = "Opaque";
}
public sealed record SoundData(string Id, string Format, byte[] Data, int SampleRate = 0, int Channels = 0, bool Looping = false);

/// <summary>Raw legacy keys retain their source units. The binder converts translation from GTA meters to Unreal centimeters.</summary>
public readonly record struct AnimationKey(float Time, Quaternion Rotation, Vector3? Position, Vector3? Scale = null);
public sealed record AnimationTrack(string Name, int? BoneTag, AnimationKey[] Keys);
public sealed record RawAnimationClip(string Name, AnimationTrack[] Tracks, float Duration);
public sealed record AnimationBankData(string Id, RawAnimationClip[] Clips);
/// <summary>Uniformly sampled parent-local bone frames, in the same axes/units as ModelData. Frames include both endpoints.</summary>
public sealed record AnimationClipData(string Name, float FrameRate, TransformData[][] Frames, bool Looping = true, bool Additive = false, float RootSpeed = 0);
public sealed record PedestrianPreset(string Name, string BankId, string Idle, string Walk, string Run, string Group)
{
    public string Faction { get; init; } = "Civilian";
    public string[] EnemyFactions { get; init; } = [];
    public PedestrianSpeechData? Speech { get; init; }
}

/// <summary>Original GTA speech bank and inclusive sample ranges; contains metadata only, never audio.</summary>
public sealed record SpeechRangeData(string Context, int First, int Last, float CooldownSeconds);
public sealed record SpeechVoiceData(string Id, string Bank, SpeechRangeData[] Contexts);
public sealed record PedestrianSpeechData(SpeechVoiceData[] Voices, SpeechVoiceData PainVoice, bool Female);

/// <summary>Installed weapon.dat standard-skill profile. Distances/offsets use Unreal centimeters; timing is seconds.</summary>
public sealed record WeaponData(string Id, string Name, string ModelId, string FireSoundId, string? ReloadSoundId,
    string AnimationBankId, string FireClip, string ReloadClip, float RangeCentimeters, float Damage,
    int ClipSize, float ShotsPerSecond, float ReloadSeconds, Vector3 MuzzleOffset, string WeaponBone = "R_Hand")
{
    public string? AimClip { get; init; }
    public float FirePitch { get; init; } = 1;
    public string? ReloadFinishSoundId { get; init; }
    public float ReloadSoundTime { get; init; }
    public float ReloadFinishSoundTime { get; init; }
    public float FireLoopStart { get; init; }
    public float FireLoopEnd { get; init; }
    public float FireTime { get; init; }
    public float Accuracy { get; init; } = 1;
    /// <summary>weapon.dat flag 0x2 (CWeaponInfo::bAimWithArm): aim the right arm instead of the two-handed upper-body pose.</summary>
    public bool AimWithArm { get; init; }
    public string Kind { get; init; } = "hitscan";
    public string SourceName { get; init; } = "";
    public int ModelNumber { get; init; }
    public int Slot { get; init; }
    public bool CombatEnabled { get; init; } = true;
    public int PelletCount { get; init; } = 1;
    public float SpreadDegrees { get; init; }
    public float ProjectileSpeedCentimeters { get; init; }
    public float ProjectileGravityCentimeters { get; init; }
    public float FuseSeconds { get; init; }
    public float BlastRadiusCentimeters { get; init; }
    public float EffectSeconds { get; init; }
    public string? ProjectileModelId { get; init; }
    public string? ImpactSoundId { get; init; }
    public string? ExplosionSoundId { get; init; }
    public bool LoopingFireSound { get; init; }
    public string? FireStartSoundId { get; init; }
    public string? FireStopSoundId { get; init; }
    public string[] AdditionalAnimationClips { get; init; } = [];
    public MeleeAttackData[] MeleeAttacks { get; init; } = [];
    public string? Notes { get; init; }
}

/// <summary>One installed melee.dat attack. Frame times are converted from 30Hz to seconds.</summary>
public sealed record MeleeAttackData(string Name, string Clip, float HitTime, float ChainTime,
    float Damage, float RadiusCentimeters, string HitLevel);
