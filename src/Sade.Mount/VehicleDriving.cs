namespace Sade.Mount;

/// <summary>Unit conversions and bounded arcade controls, independent of game input and physics.</summary>
internal static class VehicleDriving
{
    internal static float Axis(bool positive, bool negative, float analog = 0) =>
        positive || negative ? (positive ? 1 : 0) - (negative ? 1 : 0) : Math.Clamp(analog, -1, 1);

    internal static float Approach(float value, float target, float step) =>
        value < target ? Math.Min(value + Math.Max(step, 0), target) : Math.Max(value - Math.Max(step, 0), target);

    internal static float DriveAcceleration(float sourceAcceleration, float speed, float topSpeed, float throttle)
    {
        var limit = Math.Max(1, topSpeed * (throttle < 0 ? .3f : 1));
        var ratio = Math.Clamp(Math.Abs(speed) / limit, 0, 1);
        if (ratio >= 1) return 0;
        // cHandlingDataMgr and cTransmission each apply .4, then low gears multiply by
        // 1 + 3*t*t. Blend the original gear envelope continuously because this adapter
        // has no discrete transmission. Convert metres/second² to Source inches/second².
        var lowGear = 1 + 3 * (1 - ratio) * (1 - ratio);
        return Math.Clamp(sourceAcceleration * .16f * 39.37008f * lowGear, 0, 1400) * Math.Clamp((1 - ratio) * 10, 0, 1);
    }

    internal static float RoadSuspensionLoad(float compression, float travelUp, float travelDown, float sourceForce)
    {
        // CAutomobile::SetupSuspensionLines: rest extension is
        // travelDown - (travelUp + travelDown) / (4 * force), rather than the socket bind.
        var travel = Math.Max(travelUp + travelDown, .1f);
        return 4 * Math.Clamp(sourceForce, .3f, 5) * Math.Clamp((travelDown + compression) / travel, 0, 1);
    }

    internal static float RoadBrakeAcceleration(float sourceDeceleration) =>
        // Road-only response adapter: source relative strengths are preserved, with a
        // firmer pedal for Sandbox's higher gravity. Tire traction still limits the force.
        Math.Clamp(sourceDeceleration, .1f, 30) * 39.37008f * 1.5f;

    internal static float BrakeAxleShare(float bias, bool front) =>
        front ? Math.Clamp(bias, .05f, .95f) : 1 - Math.Clamp(bias, .05f, .95f);

    internal static float HelicopterLift(float gravity, float verticalSpeed, float collective, float spool, float power)
    {
        var targetSpeed = Math.Clamp(collective, -1, 1) * 300 * Math.Clamp(power, .7f, 1.6f);
        var acceleration = gravity + (targetSpeed - verticalSpeed) * 2;
        return Math.Clamp(acceleration, 0, gravity * 2.2f) * Math.Clamp(spool, 0, 1);
    }

    internal readonly record struct BikeTurn(float WheelSteerRadians, float YawRate, float LeanRadians);

    internal static BikeTurn BikeSteering(float input, float speed, float wheelBase, float steeringLockDegrees,
        float maximumLeanDegrees, float gravity)
    {
        var length = Math.Max(wheelBase, 30);
        var angle = Math.Clamp(input, -1, 1) * Math.Clamp(steeringLockDegrees, 10, 45) * MathF.PI / 180;
        // Slow steering remains maneuverable, while fast turns obey the lateral acceleration
        // supportable by the installed bike's MaxLean rather than snapping to full lock.
        var yaw = speed / length * MathF.Tan(angle);
        var maxLean = Math.Clamp(maximumLeanDegrees, 20, 48) * MathF.PI / 180;
        var limit = Math.Min(1.5f, Math.Max(gravity, 1) * MathF.Tan(maxLean) / Math.Max(Math.Abs(speed), 40));
        yaw = Math.Clamp(yaw, -limit, limit);
        var wheel = Math.Abs(speed) > 1 ? MathF.Atan(yaw * length / speed) : angle;
        // Positive steering turns left (+yaw). The rider must lean left, toward -Right.
        var lean = MathF.Atan(speed * yaw / Math.Max(gravity, 1));
        return new(wheel, yaw, lean);
    }

    internal static float PlaneControlAuthority(float speed, float referenceSpeed) =>
        Math.Clamp(Math.Max(speed, 0) * Math.Max(speed, 0) / Math.Max(referenceSpeed * referenceSpeed, 1), 0, 1);

    internal static float PlaneLiftCoefficient(float angleOfAttack, float formLift, float attackLift)
    {
        var slope = Math.Clamp(attackLift * 30, 2.5f, 6);
        var incidence = 3 * MathF.PI / 180;
        var angle = Math.Clamp(angleOfAttack + incidence, -MathF.PI / 2, MathF.PI / 2);
        var baseLift = Math.Clamp(formLift * 20, .12f, .6f);
        const float stallAngle = 16 * MathF.PI / 180;
        var attached = baseLift + slope * Math.Clamp(angle, -stallAngle, stallAngle);
        if (Math.Abs(angle) <= stallAngle) return attached;
        // A separated wing loses lift beyond its stall angle. At ninety degrees it cannot
        // support a hovering aircraft, regardless of engine power or vertical fall speed.
        var stalled = Math.Clamp((MathF.PI / 2 - Math.Abs(angle)) / (MathF.PI / 2 - stallAngle), 0, 1);
        return attached * stalled * stalled;
    }

    internal static float PlaneLiftAcceleration(float forwardSpeed, float referenceSpeed, float angleOfAttack,
        float formLift, float attackLift, float gravity)
    {
        var pressure = Math.Max(forwardSpeed, 0) / Math.Max(referenceSpeed, 1);
        return Math.Clamp(gravity * pressure * pressure * PlaneLiftCoefficient(angleOfAttack, formLift, attackLift), -gravity * 4, gravity * 4);
    }

    internal static float PlaneBankTurn(float bankRadians, float forwardSpeed, float gravity)
    {
        if (forwardSpeed < 80) return 0;
        // Mounted vehicles face +Y; positive roll lowers the right wing, hence negative yaw.
        return -gravity * MathF.Tan(Math.Clamp(bankRadians, -.9f, .9f)) / Math.Max(forwardSpeed, 80);
    }
}
