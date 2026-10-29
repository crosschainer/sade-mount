using N = System.Numerics;

namespace Sade.Mount;

/// <summary>Engine-independent two-link arm and muzzle alignment math.</summary>
internal static class ArmAimSolver
{
    internal static bool Solve(N.Vector3 shoulder, N.Vector3 elbow, N.Vector3 wrist, N.Vector3 target,
        N.Vector3 poleDirection, out N.Vector3 solvedElbow, out N.Vector3 solvedWrist)
    {
        solvedElbow = elbow; solvedWrist = wrist;
        var upper = N.Vector3.Distance(shoulder, elbow);
        var lower = N.Vector3.Distance(elbow, wrist);
        var delta = target - shoulder;
        if (!Finite(delta) || !float.IsFinite(upper + lower) || upper < .001f || lower < .001f || delta.LengthSquared() < 1e-10f) return false;
        var direction = N.Vector3.Normalize(delta);
        var distance = Math.Clamp(delta.Length(), Math.Abs(upper - lower) + .0001f, upper + lower - .0001f);
        var pole = poleDirection - direction * N.Vector3.Dot(poleDirection, direction);
        if (pole.LengthSquared() < 1e-8f)
        {
            pole = N.Vector3.Cross(direction, Math.Abs(direction.Z) < .9f ? N.Vector3.UnitZ : N.Vector3.UnitY);
        }
        pole = N.Vector3.Normalize(pole);
        var along = (upper * upper + distance * distance - lower * lower) / (2 * distance);
        var perpendicular = MathF.Sqrt(Math.Max(0, upper * upper - along * along));
        solvedElbow = shoulder + direction * along + pole * perpendicular;
        solvedWrist = shoulder + direction * distance;
        return Finite(solvedElbow) && Finite(solvedWrist);
    }

    internal static N.Quaternion Between(N.Vector3 from, N.Vector3 to)
    {
        if (from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f) return N.Quaternion.Identity;
        from = N.Vector3.Normalize(from); to = N.Vector3.Normalize(to);
        var dot = Math.Clamp(N.Vector3.Dot(from, to), -1, 1);
        if (dot > .999999f) return N.Quaternion.Identity;
        if (dot < -.999999f)
        {
            var axis = N.Vector3.Cross(from, Math.Abs(from.Z) < .9f ? N.Vector3.UnitZ : N.Vector3.UnitY);
            return N.Quaternion.CreateFromAxisAngle(N.Vector3.Normalize(axis), MathF.PI);
        }
        return N.Quaternion.Normalize(new N.Quaternion(N.Vector3.Cross(from, to), 1 + dot));
    }

    /// <summary>GTA firearms point along the weapon/right-hand local +X. Preserve authored wrist roll.</summary>
    internal static N.Quaternion AimMuzzle(N.Quaternion rotation, N.Vector3 wrist, N.Vector3 muzzleOffset, N.Vector3 target)
    {
        for (var i = 0; i < 4; i++)
        {
            var muzzle = wrist + N.Vector3.Transform(muzzleOffset, rotation);
            var delta = target - muzzle;
            if (delta.LengthSquared() < 1e-8f) break;
            var correction = Between(N.Vector3.Transform(N.Vector3.UnitX, rotation), delta);
            rotation = N.Quaternion.Normalize(correction * rotation);
        }
        return rotation;
    }

    internal static N.Quaternion AimTorso(N.Vector3 pivot, N.Vector3 wrist, N.Quaternion wristRotation, N.Vector3 muzzleOffset, N.Vector3 target)
    {
        var correction = N.Quaternion.Identity;
        for (var i = 0; i < 5; i++)
        {
            var position = pivot + N.Vector3.Transform(wrist - pivot, correction);
            var rotation = correction * wristRotation;
            var muzzle = position + N.Vector3.Transform(muzzleOffset, rotation);
            correction = N.Quaternion.Normalize(Between(N.Vector3.Transform(N.Vector3.UnitX, rotation), target - muzzle) * correction);
        }
        return correction;
    }

    private static bool Finite(N.Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
