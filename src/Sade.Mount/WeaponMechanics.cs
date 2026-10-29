using NVector3 = System.Numerics.Vector3;

namespace Sade.Mount;

/// <summary>Engine-independent calculations shared by native weapon simulation and regression checks.</summary>
internal static class WeaponMechanics
{
    internal static bool AreRivals(string left, IReadOnlyCollection<string> leftEnemies, string right, IReadOnlyCollection<string> rightEnemies)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return false;
        if (leftEnemies.Contains(right, StringComparer.OrdinalIgnoreCase) || rightEnemies.Contains(left, StringComparer.OrdinalIgnoreCase)) return true;
        // Older cached profiles predate the source relationship metadata.
        if (leftEnemies.Count != 0 || rightEnemies.Count != 0) return false;
        return (left.Equals("ballas", StringComparison.OrdinalIgnoreCase) && right.Equals("grove", StringComparison.OrdinalIgnoreCase)) ||
            (left.Equals("grove", StringComparison.OrdinalIgnoreCase) && right.Equals("ballas", StringComparison.OrdinalIgnoreCase));
    }

    internal static float BlastFalloff(float distance, float radius)
    {
        if (!float.IsFinite(distance) || !float.IsFinite(radius) || radius <= 0) return 0;
        return Math.Clamp(1 - Math.Max(0, distance) / radius, 0, 1);
    }

    internal static bool TryBallisticVelocity(NVector3 start, NVector3 target, float speed, float gravity, out NVector3 velocity)
    {
        velocity = default;
        if (!float.IsFinite(speed) || !float.IsFinite(gravity) || speed <= 0 || gravity <= 0) return false;
        var delta = target - start;
        if (!float.IsFinite(delta.LengthSquared())) return false;
        var horizontal = new NVector3(delta.X, delta.Y, 0);
        var distance = horizontal.Length();
        if (distance < 0.001f) return false;
        var speed2 = (double)speed * speed;
        var discriminant = speed2 * speed2 - gravity * (gravity * (double)distance * distance + 2 * delta.Z * speed2);
        if (discriminant < 0) return false;
        var tangent = (speed2 - Math.Sqrt(discriminant)) / (gravity * distance);
        var cosine = 1 / Math.Sqrt(1 + tangent * tangent);
        velocity = horizontal / distance * (float)(speed * cosine) + NVector3.UnitZ * (float)(speed * cosine * tangent);
        return float.IsFinite(velocity.LengthSquared());
    }

    internal static NVector3[] PelletDirections(NVector3 forward, int count, float spreadDegrees, Random random)
    {
        count = Math.Clamp(count, 1, 64);
        if (!float.IsFinite(forward.LengthSquared()) || forward.LengthSquared() < 0.000001f) throw new ArgumentException("A shot needs a finite direction.");
        forward = NVector3.Normalize(forward);
        var side = NVector3.Normalize(NVector3.Cross(forward, Math.Abs(forward.Z) > 0.95f ? NVector3.UnitY : NVector3.UnitZ));
        var up = NVector3.Cross(side, forward);
        var spread = Math.Tan(Math.Clamp(float.IsFinite(spreadDegrees) ? spreadDegrees : 0, 0, 45) * Math.PI / 180);
        var rays = new NVector3[count];
        for (var i = 0; i < count; i++)
        {
            var radius = Math.Sqrt(random.NextDouble()) * spread;
            var angle = random.NextDouble() * Math.Tau;
            rays[i] = NVector3.Normalize(forward + side * (float)(radius * Math.Cos(angle)) + up * (float)(radius * Math.Sin(angle)));
        }
        return rays;
    }
}
