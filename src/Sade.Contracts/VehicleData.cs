using System.Numerics;

namespace Sade.Contracts;

/// <summary>Installed vehicles.ide and handling.cfg metadata. Geometry uses Unreal axes/centimeters; speed is cm/s.</summary>
public sealed record VehicleData(string Id, string Name, string ModelId, string Family, string HandlingId)
{
    public int ModelNumber { get; init; }
    public string AnimationGroup { get; init; } = "null";
    public float MassKg { get; init; }
    public float TurnMass { get; init; }
    public Vector3 CenterOfMass { get; init; }
    public float MaxSpeed { get; init; }
    public float EngineAcceleration { get; init; }
    public float BrakeDeceleration { get; init; }
    public float Traction { get; init; }
    public float TractionLoss { get; init; }
    public float SteeringLockDegrees { get; init; }
    public float SuspensionForce { get; init; }
    public float SuspensionDamping { get; init; }
    public float SuspensionUpper { get; init; }
    public float SuspensionLower { get; init; }
    public float SuspensionBias { get; init; }
    public float Drag { get; init; }
    public int SubmergedPercent { get; init; }
    public string DriveType { get; init; } = "R";
    public float WheelRadiusFront { get; init; }
    public float WheelRadiusRear { get; init; }
    public ulong ModelFlags { get; init; }
    public ulong HandlingFlags { get; init; }
    public string? WheelModelId { get; init; }
    public VehicleSocketData[] Sockets { get; init; } = [];
    public float[] FlyingHandling { get; init; } = [];
    public float[] BoatHandling { get; init; } = [];
    public float[] BikeHandling { get; init; } = [];
    public bool RemoteControlled { get; init; }
}

public sealed record VehicleSocketData(string Name, TransformData Transform);
