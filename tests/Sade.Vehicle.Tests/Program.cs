using Sade.Mount;
using Sade.Reader;
using Sade.Contracts;

void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
Require(VehicleDriving.Axis(true, false, 0) == 1, "W must give positive collective even when seated movement is zero.");
Require(VehicleDriving.Axis(false, true, 1) == -1, "Physical S must override stale positive walking input.");
Require(VehicleDriving.Axis(true, true, 1) == 0, "Opposite physical controls must cancel.");
Require(VehicleDriving.Axis(false, false, .6f) == .6f, "Analog controls must remain available.");
const float infernusTop = 6666.667f / 2.54f;
var launch = VehicleDriving.DriveAcceleration(30, 0, infernusTop, 1);
Require(launch > 600 && launch < 1000, "Original low-gear envelope must produce useful launch acceleration in Source units.");
Require(VehicleDriving.DriveAcceleration(30, infernusTop, infernusTop, 1) == 0, "Forward speed limit must stop propulsion.");
Require(VehicleDriving.DriveAcceleration(30, -infernusTop * .31f, infernusTop, -1) == 0, "Reverse must have its own lower limit.");
Require(VehicleDriving.DriveAcceleration(24, 0, 4444.444f / 2.54f, 1) < launch, "Lower source acceleration must remain slower.");
var roadUp = 25 / 2.54f; var roadDown = 10 / 2.54f;
var restCompression = (roadUp + roadDown) / (4 * 1.2f) - roadDown;
Require(Math.Abs(VehicleDriving.RoadSuspensionLoad(restCompression, roadUp, roadDown, 1.2f) - 1) < .00001f,
    "Original Infernus spring equilibrium must support its weight below the socket bind position.");
Require(restCompression < -1 && VehicleDriving.RoadSuspensionLoad(-roadDown, roadUp, roadDown, 1.2f) == 0,
    "A fully extended spring must unload instead of pulling or retaining artificial preload.");
Require(VehicleDriving.RoadSuspensionLoad(roadUp + 100, roadUp, roadDown, 1.2f) == 4 * 1.2f,
    "Suspension compression must stop at the source bump limit.");
var brakeAcceleration = VehicleDriving.RoadBrakeAcceleration(11);
var idealStopMeters = 1200 * 1200 / (2 * brakeAcceleration) * .0254f;
Require(idealStopMeters is > 20 and < 30, "Road brake response must stop 110km/h in the intended 20–30m envelope before drag.");
Require(Math.Abs(VehicleDriving.BrakeAxleShare(.51f, true) + VehicleDriving.BrakeAxleShare(.51f, false) - 1) < .00001f,
    "Source front/rear brake distribution must preserve total pedal force.");
var speed = 0f;
for (var i = 0; i < 250; i++) speed += (VehicleDriving.DriveAcceleration(30, speed, infernusTop, 1) - speed * .0425f) * .02f;
Require(speed > 1000 && speed < infernusTop, "Five seconds of road propulsion should exceed 90km/h without exceeding source top speed.");
Require(VehicleDriving.HelicopterLift(800, 0, 1, 0, 1.2f) == 0, "Stopped rotors cannot generate lift.");
Require(VehicleDriving.HelicopterLift(800, 0, 1, 1, 1.2f) > 800, "W must overcome gravity at full rotor speed.");
Require(VehicleDriving.HelicopterLift(800, 0, -1, 1, 1.2f) < 800, "S must descend.");
Require(VehicleDriving.HelicopterLift(800, 0, 0, 1, 1.2f) == 800, "Neutral collective must hover.");
Require(VehicleDriving.HelicopterLift(800, 2000, 1, 1, 1.2f) >= 0, "Vertical control cannot apply downward rotor thrust.");
var left = VehicleDriving.BikeSteering(1, 650, 62, 35, 43, 800);
var right = VehicleDriving.BikeSteering(-1, 650, 62, 35, 43, 800);
Require(left.YawRate > 0 && left.LeanRadians > 0 && left.WheelSteerRadians > 0, "Left steering must turn left and lean inward toward -Right.");
Require(Math.Abs(left.YawRate + right.YawRate) < .00001f && Math.Abs(left.LeanRadians + right.LeanRadians) < .00001f, "Bike turning must be symmetric.");
Require(Math.Abs(MathF.Tan(left.LeanRadians) - 650 * left.YawRate / 800) < .00001f, "Bike lean must balance centripetal acceleration.");
var fast = VehicleDriving.BikeSteering(1, 2000, 62, 35, 43, 800);
Require(fast.WheelSteerRadians < left.WheelSteerRadians && fast.LeanRadians <= 43 * MathF.PI / 180 + .00001f, "Fast steering must reduce wheel lock and respect the original maximum lean.");
Require(VehicleDriving.BikeSteering(1, 0, 62, 35, 43, 800).LeanRadians == 0, "A stationary bike must balance upright.");
Require(VehicleDriving.BikeSteering(1, -100, 62, 35, 43, 800).YawRate < 0, "Reverse steering must reverse yaw.");
Require(VehicleDriving.PlaneLiftAcceleration(0, 800, 0, .02f, .15f, 800) == 0, "A stationary wing must not hover.");
Require(VehicleDriving.PlaneLiftAcceleration(-800, 800, 0, .02f, .15f, 800) == 0, "Backward airspeed must not generate forward-wing lift.");
var slowLift = VehicleDriving.PlaneLiftAcceleration(400, 800, 0, .02f, .15f, 800);
Require(Math.Abs(VehicleDriving.PlaneLiftAcceleration(800, 800, 0, .02f, .15f, 800) - slowLift * 4) < .001f, "Wing lift must follow dynamic pressure.");
Require(VehicleDriving.PlaneLiftCoefficient(45 * MathF.PI / 180, .02f, .15f) < VehicleDriving.PlaneLiftCoefficient(13 * MathF.PI / 180, .02f, .15f), "A stalled wing must lose lift after its peak angle.");
Require(VehicleDriving.PlaneControlAuthority(0, 800) == 0 && VehicleDriving.PlaneControlAuthority(200, 800) < .1f, "Control surfaces must lose authority at low speed.");
Require(VehicleDriving.PlaneBankTurn(.4f, 800, 800) < 0 && VehicleDriving.PlaneBankTurn(-.4f, 800, 800) > 0, "Banked plane turns must follow the lowered wing, including the mounted +Y axis convention.");
Console.WriteLine("PASS bike inward lean/turn limits and wing dynamic-pressure/stall/banked-turn mechanics.");
Require(VehicleAudioBanks.TryGet(487, out var player, out _, out var pitch) && player == "MAVERICK" && pitch == 1, "Maverick must use its real rotor bank.");
Require(!VehicleAudioBanks.TryGet(399, out _, out _, out _) && !VehicleAudioBanks.TryGet(612, out _, out _, out _), "Audio mappings must not wrap unknown model IDs.");
Console.WriteLine("PASS vehicle controls, transmission envelope, helicopter lift and source-bank identity.");

if (args.Contains("--installed"))
{
    var game = Environment.GetEnvironmentVariable("SADE_GAME_DIR") ?? @"C:\Program Files (x86)\Steam\steamapps\common\GTA San Andreas - The Definitive Edition";
    using var reader = new GameReader();
    var catalog = reader.Initialize(new(game, Path.Combine(Path.GetTempPath(), "sade-vehicle-audio-audit")));
    var transparentCar = reader.LoadMaterial("/Game/Common/Materials/Vehicle/MI_TransparentCar.MI_TransparentCar");
    Require(transparentCar.BlendMode == "Masked" && transparentCar.TwoSided && transparentCar.Tint.W == 0 && transparentCar.AlphaCutoff > 0,
        "Original invisible vehicle underbody/wheel-well geometry must remain clipped through inherited ColorMult alpha.");
    var smoke = reader.LoadModel("/Game/SanAndreas/Characters/Peds/SK_smoke.SK_smoke");
    Require(smoke.Parts.Where(p => p.Lod == 0).All(p => reader.LoadMaterial(p.MaterialId!).Tint.W == 1),
        "Environment opacity handling must not change character-master tint semantics.");
    var infernus = reader.GetVehicles().Single(v => v.Id == "infernus");
    Require(Math.Abs(infernus.BrakeBias - .51f) < .00001f && infernus.SuspensionUpper == 25 && infernus.SuspensionLower == -10,
        "Installed handling.cfg brake bias and signed suspension limits were parsed incorrectly.");
    Console.WriteLine("PASS source masked vehicle transparency, unchanged character tint, and Infernus suspension/brake metadata.");
    var ids = catalog.Where(x => x.Kind == AssetKind.Sound).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var count = 0;
    foreach (var vehicle in reader.GetVehicles())
    {
        Require(VehicleAudioBanks.TryGet(vehicle.ModelNumber, out player, out var dummy, out pitch), $"{vehicle.Id}: no source-bank mapping.");
        count++;
        if (vehicle.RemoteControlled || vehicle.Family is "bicycle" or "train" or "trailer") continue;
        var jet = vehicle.ModelNumber is 519 or 520 or 577 or 592;
        var bank = jet ? "VEHICLE_GEN" : vehicle.Family is "helicopter" or "plane" || string.IsNullOrEmpty(dummy) ? player : dummy;
        foreach (var sample in jet ? new[] { 10, 11 } : vehicle.Family is "boat" or "hovercraft" ? new[] { 0 } : new[] { 0, 1 })
        {
            var path = $"/Game/SanAndreas/Audio/SFX/GENRL/{bank}/S_{sample:000}";
            var id = ids.FirstOrDefault(x => x.Split('.')[0].Equals(path + "_Loop", StringComparison.OrdinalIgnoreCase))
                ?? ids.FirstOrDefault(x => x.Split('.')[0].Equals(path, StringComparison.OrdinalIgnoreCase));
            Require(id is not null, $"{vehicle.Id}: missing {path}.");
            if (!loaded.Add(id!)) continue;
            var sound = reader.LoadSound(id!);
            Require(sound.Data.Length > 0 && sound.SampleRate > 0, $"{id}: empty engine audio.");
            Require(sound.Looping, $"{id}: expected original sustained engine loop.");
        }
    }
    Console.WriteLine($"PASS {count} installed vehicle mappings and {loaded.Count} decoded unique original engine/rotor loops.");
}
