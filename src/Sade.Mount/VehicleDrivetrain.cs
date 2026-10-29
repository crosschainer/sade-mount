namespace Sade.Mount;

/// <summary>
/// Stateful audio transmission. Gear count and inertia come from handling.dat;
/// speed ranges follow the original transmission's normalized gear spacing.
/// RPM, clutch slip and smoothing are this mount's audio model, not copied game code.
/// </summary>
internal sealed class VehicleDrivetrain(int gearCount = 5, float engineInertia = 15)
{
    private readonly int gearCount = Math.Clamp(gearCount, 1, 6);
    private readonly float inertia = float.IsFinite(engineInertia) ? Math.Clamp(engineInertia, 1, 100) : 15;
    private bool initialized;
    private float shiftRemaining, stationaryTime;

    internal int Gear { get; private set; } = 1; // Zero is reverse.
    internal float Rpm { get; private set; } = .12f;
    internal float Load { get; private set; }
    internal bool Shifted { get; private set; }
    internal float ShiftGain => shiftRemaining > 0 ? .58f : 1;

    internal void Update(float signedSpeedRatio, float pedal, bool grounded, float delta)
    {
        if (!float.IsFinite(delta) || delta <= 0) return;
        delta = Math.Min(delta, .1f);
        var signedSpeed = float.IsFinite(signedSpeedRatio) ? Math.Clamp(signedSpeedRatio, -2, 2) : 0;
        pedal = float.IsFinite(pedal) ? Math.Clamp(pedal, -1, 1) : 0;
        var speed = Math.Abs(signedSpeed);
        var reverse = signedSpeed < -.015f || speed < .015f && pedal < -.05f;
        var load = reverse ? Math.Max(-pedal, 0) : Math.Max(pedal, 0);
        Shifted = false;
        shiftRemaining = Math.Max(0, shiftRemaining - delta);
        stationaryTime = speed < .012f && load > .05f ? stationaryTime + delta : 0;

        if (!initialized)
        {
            initialized = true;
            Gear = reverse ? 0 : 1;
            while (Gear > 0 && Gear < gearCount && speed > ChangeUpSpeed(Gear)) Gear++;
        }
        else if (reverse != (Gear == 0))
        {
            ChangeGear(reverse ? 0 : 1);
        }
        else if (grounded && Gear > 0 && shiftRemaining <= 0)
        {
            if (Gear < gearCount && speed > ChangeUpSpeed(Gear)) ChangeGear(Gear + 1);
            else if (Gear > 1 && speed < ChangeDownSpeed(Gear)) ChangeGear(Gear - 1);
        }

        // Wheel RPM remains audible with the accelerator released: braking/coasting
        // unloads the engine rather than abruptly returning its pitch to idle.
        var wheelRpm = .12f + .88f * speed / (Gear == 0 ? .3f : GearMaximumSpeed(Gear));
        var launchSlip = .12f + .26f * load * (1 - Math.Clamp(speed / .1f, 0, 1));
        var targetRpm = Math.Max(wheelRpm, launchSlip);
        if (!grounded || stationaryTime > .35f)
        {
            // Free wheels or a held stationary throttle can rev independently of road speed.
            var freeRpm = .12f + .88f * load;
            targetRpm = grounded ? Math.Max(targetRpm, freeRpm) : freeRpm;
        }
        targetRpm = Math.Clamp(targetRpm, .12f, 1.08f);
        var response = Math.Clamp(9 / MathF.Sqrt(inertia / 10), 3, 15);
        // The short rev fall during an upshift must survive smoothing, without a pitch step.
        if (targetRpm < Rpm && shiftRemaining > 0) response = Math.Max(response, 14);
        Rpm = Smooth(Rpm, targetRpm, response, delta);
        Load = Smooth(Load, load * ShiftGain, load > Load ? 9 : 6, delta);
    }

    internal void Reset()
    {
        initialized = false;
        Gear = 1; Rpm = .12f; Load = 0; Shifted = false;
        shiftRemaining = 0; stationaryTime = 0;
    }

    // Factual normalized gear limits from cTransmission::InitGearRatios:
    // https://github.com/gta-reversed/gta-reversed/blob/b53415bde650ed9af94212f77e8d8d842696ecb2/source/game_sa/cTransmission.cpp
    internal float GearMaximumSpeed(int gear) => (float)gear / gearCount * (1 - .5f / gearCount) + .5f / gearCount;
    internal float ChangeUpSpeed(int gear) => gear >= gearCount ? 1 : GearBoundary(gear, .6667f);
    internal float ChangeDownSpeed(int gear) => gear <= 1 ? 0 : GearBoundary(gear - 1, .42f);

    private float GearBoundary(int gear, float fraction)
    {
        var previous = gear <= 1 ? 0 : GearMaximumSpeed(gear - 1);
        return previous + (GearMaximumSpeed(gear) - previous) * fraction;
    }

    private void ChangeGear(int gear)
    {
        Gear = gear;
        Shifted = true;
        shiftRemaining = .22f;
    }

    internal static float Smooth(float current, float target, float response, float delta)
        => current + (target - current) * (1 - MathF.Exp(-response * Math.Max(delta, 0)));
}
