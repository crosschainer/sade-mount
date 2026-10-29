using System.Numerics;

namespace Sade.Formats;

/// <summary>The single legacy GTA Z-up meters to Unreal Z-up centimeters bridge.</summary>
public static class GtaCoordinates
{
    public static Vector3 ToUnreal(Vector3 meters) => new(meters.X * 100, -meters.Y * 100, meters.Z * 100);
    public static Quaternion ToUnreal(Quaternion rotation) => Quaternion.Normalize(new(-rotation.X, rotation.Y, -rotation.Z, rotation.W));
}
