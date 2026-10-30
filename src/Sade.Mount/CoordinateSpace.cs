using N = System.Numerics;
using Sade.Contracts;

namespace Sade.Mount;

internal static class CoordinateSpace
{
    public const float UnitsPerCentimeter = 1f / 2.54f;
    public static Vector3 Position(N.Vector3 value) => new(value.X * UnitsPerCentimeter, -value.Y * UnitsPerCentimeter, value.Z * UnitsPerCentimeter);
    public static N.Vector3 SourcePosition(Vector3 value) => new(value.x / UnitsPerCentimeter, -value.y / UnitsPerCentimeter, value.z / UnitsPerCentimeter);
    public static Vector3 Direction(N.Vector3 value) => new(value.X, -value.Y, value.Z);
    public static Rotation Rotation(N.Quaternion value) => new(-value.X, value.Y, -value.Z, value.W);
    public static Transform Transform(TransformData value) => new(Position(value.Position), Rotation(value.Rotation), new Vector3(value.Scale.X, value.Scale.Y, value.Scale.Z));
    public static int[] Indices(int[] source)
    {
        if (source.Length % 3 != 0) throw new InvalidDataException("Triangle index buffer is not divisible by three.");
        // Unreal and Source use opposite front-face conventions. Mirroring Y already
        // accounts for that difference; swapping indices again turns the mesh inside out.
        return (int[])source.Clone();
    }
}
