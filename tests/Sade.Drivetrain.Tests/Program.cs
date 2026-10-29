using Sade.Mount;

void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
void Settle(VehicleDrivetrain drive, float speed, float pedal, bool grounded = true, float seconds = 1)
{
    for (var time = 0f; time < seconds; time += .01f) drive.Update(speed, pedal, grounded, .01f);
}

// A steady accelerator must still produce the repeating rise/fall of a transmission.
foreach (var count in new[] { 1, 3, 4, 5, 6 })
{
    var drive = new VehicleDrivetrain(count, 15);
    var shifts = new List<(int Gear, float Before, float After)>();
    var pendingGear = 0; var before = 0f; var afterTime = 0f;
    var perGear = new Dictionary<int, List<float>>();
    for (var tick = 0; tick < 1800; tick++)
    {
        var speed = tick / 1800f;
        var previous = drive.Rpm;
        drive.Update(speed, 1, true, .01f);
        if (drive.Shifted)
        {
            pendingGear = drive.Gear; before = previous; afterTime = 0;
        }
        if (pendingGear > 0)
        {
            afterTime += .01f;
            if (afterTime >= .16f) { shifts.Add((pendingGear, before, drive.Rpm)); pendingGear = 0; }
        }
        if (!perGear.TryGetValue(drive.Gear, out var samples)) perGear[drive.Gear] = samples = [];
        samples.Add(drive.Rpm);
        Require(float.IsFinite(drive.Rpm) && drive.Rpm >= .12f && drive.Rpm <= 1.08f, "RPM must stay finite and bounded.");
    }
    Require(drive.Gear == count && shifts.Count == count - 1, $"A {count}-speed transmission must make exactly {count - 1} forward shifts.");
    foreach (var shift in shifts)
        Require(shift.Before - shift.After > .055f, $"Steady W must produce an audible RPM fall at shift {shift.Gear}: {shift.Before:F3} -> {shift.After:F3}.");
    foreach (var (gear, samples) in perGear)
        Require(samples.Max() - samples.Min() > .13f, $"RPM must rise through gear {gear}, not remain pinned by the accelerator.");
    Console.WriteLine($"PASS {count} gears: " + string.Join(", ", shifts.Select(s => $"{s.Gear - 1}->{s.Gear} {s.Before:F2}->{s.After:F2}")));
}

var normal = new VehicleDrivetrain();
Settle(normal, .55f, 1);
var loadedRpm = normal.Rpm;
Settle(normal, .55f, 0);
Require(normal.Rpm > loadedRpm - .02f && normal.Load < .01f, "Coasting at speed must preserve wheel-driven RPM while unloading the engine.");
Settle(normal, .55f, -1);
Require(normal.Gear > 0 && normal.Load < .01f, "S while still moving forward is engine braking, not reverse full throttle.");
Settle(normal, .01f, 0, seconds: 2);
Require(normal.Gear == 1 && normal.Rpm < .17f, "Slowing to a stop must downshift and return to idle.");
Settle(normal, 0, 1);
Require(normal.Rpm > .9f, "A held stationary accelerator must allow free revving.");
Settle(normal, 0, 0);
Require(normal.Rpm < .14f, "Released stationary throttle must fall smoothly back to idle.");
Settle(normal, .1f, 1, grounded: false);
Require(normal.Rpm > .98f, "Airborne wheels must be able to rev without artificial upshifts.");
Settle(normal, -.15f, -1);
Require(normal.Gear == 0 && normal.Load > .98f && normal.Rpm > .5f, "Reverse must use a loaded reverse gear and signed speed.");

var chatter = new VehicleDrivetrain();
Settle(chatter, .2f, 1);
Require(chatter.Gear == 2, "Fixture should start in second gear.");
for (var tick = 0; tick < 500; tick++)
{
    chatter.Update(chatter.ChangeUpSpeed(1) + (tick % 2 == 0 ? .002f : -.002f), 1, true, .01f);
    Require(chatter.Gear == 2 && !chatter.Shifted, "Speed jitter around an upshift threshold must not cause gear hunting.");
}

var light = new VehicleDrivetrain(5, 2);
var heavy = new VehicleDrivetrain(5, 50);
for (var tick = 0; tick < 10; tick++) { light.Update(.12f, 1, true, .01f); heavy.Update(.12f, 1, true, .01f); }
Require(light.Rpm > heavy.Rpm + .1f, "Installed engine inertia must affect rev response.");
var fastFrames = new VehicleDrivetrain(); var slowFrames = new VehicleDrivetrain();
for (var tick = 0; tick < 100; tick++) fastFrames.Update(.12f, 1, true, .01f);
for (var tick = 0; tick < 20; tick++) slowFrames.Update(.12f, 1, true, .05f);
Require(Math.Abs(fastFrames.Rpm - slowFrames.Rpm) < .0001f, "Smoothing must be independent of rendering frame rate for a constant target.");
var previousRpm = normal.Rpm;
normal.Update(float.NaN, float.NaN, true, float.NaN);
Require(normal.Rpm == previousRpm, "Invalid elapsed time cannot poison audio state.");
normal.Reset();
Require(normal.Gear == 1 && normal.Rpm == .12f && normal.Load == 0, "Leaving/reentering a vehicle must reset its audio transmission.");
Console.WriteLine("PASS idle/free-rev, engine braking, reverse, hysteresis, inertia and frame-rate smoothing.");
