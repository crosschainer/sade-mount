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
public sealed record PedestrianPreset(string Name, string BankId, string Idle, string Walk, string Run, string Group);
