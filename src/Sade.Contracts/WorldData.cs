using System.Numerics;

namespace Sade.Contracts;

/// <summary>Cooked exterior component placements, in Unreal centimeters and axes.</summary>
public sealed record WorldPlacementData(string Id, string ModelId, TransformData Transform)
{
    public string?[] Materials { get; init; } = [];
    public bool Visible { get; init; } = true;
    public bool Collision { get; init; } = true;
    public float DrawDistance { get; init; }
    public bool Prominent { get; init; }
}

public sealed record WorldModelBounds(string ModelId, Vector3 Min, Vector3 Max);
public sealed record WorldData(string Fingerprint, WorldPlacementData[] Placements, string[] FailedSections)
{
    public WorldModelBounds[] Bounds { get; init; } = [];
    public bool BoundsComplete { get; init; }
}
