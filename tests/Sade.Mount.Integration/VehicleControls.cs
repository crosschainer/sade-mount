// Run via run_csharp while the real local player is seated, then press the controls normally.
// This observer never injects input, moves the player, or changes the scene/map.
var scene = Game.ActiveScene;
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var vehicle = scene.GetAllComponents<Component>().FirstOrDefault(x => x.GetType().FullName == "Sade.Mount.NativeVehicle"
    && x.GetType().GetProperty("Driver", flags)?.GetValue(x) is PlayerController { IsProxy: false });
if (vehicle is null) throw new Exception("Seat the local player in a mounted vehicle before starting the observer.");
var type = vehicle.GetType();
var body = (Rigidbody)type.GetProperty("Body", flags)!.GetValue(vehicle)!;
var renderer = (SkinnedModelRenderer)type.GetProperty("Renderer", flags)!.GetValue(vehicle)!;
var samples = new System.Collections.Generic.List<object>();
var start = Time.NowDouble;
var next = start;
var wFrames = 0; var sFrames = 0; var leftFrames = 0; var pitchFrames = 0;
var first = vehicle.WorldPosition; var peakZ = first.z; var maximumSpeed = 0f; var maximumRotor = 0f;
using (scene.AddHook(GameObjectSystem.Stage.FinishUpdate, 50, () =>
{
    if (!vehicle.IsValid || Time.NowDouble < next) return;
    next = Time.NowDouble + .1;
    var w = Input.Keyboard.Down("w"); var s = Input.Keyboard.Down("s");
    var left = Input.Keyboard.Down("a"); var pitch = Input.Keyboard.Down("UPARROW") || Input.Keyboard.Down("KP_8");
    if (w) wFrames++; if (s) sFrames++; if (left) leftFrames++; if (pitch) pitchFrames++;
    var rotor = (float)type.GetField("rotorSpeed", flags)!.GetValue(vehicle)!;
    maximumRotor = Math.Max(maximumRotor, rotor); maximumSpeed = Math.Max(maximumSpeed, body.Velocity.Length); peakZ = Math.Max(peakZ, vehicle.WorldPosition.z);
    var audio = type.GetField("audio", flags)?.GetValue(vehicle);
    var handle = audio?.GetType().GetField("engineHandle", flags)?.GetValue(audio) as SoundHandle;
    renderer.TryGetBoneTransform("static_rotor", out var rotorPose);
    samples.Add(new {
        t = Time.NowDouble - start, w, s, left, pitch, Analog = Input.AnalogMove,
        Throttle = type.GetField("throttle", flags)!.GetValue(vehicle), Lift = type.GetField("lift", flags)!.GetValue(vehicle),
        Steering = type.GetField("steering", flags)!.GetValue(vehicle), Cyclic = type.GetField("pitch", flags)!.GetValue(vehicle),
        RotorSpeed = rotor, RotorPose = rotorPose, EngineAudioPlaying = handle?.IsPlaying ?? false,
        Position = vehicle.WorldPosition, Velocity = body.Velocity, IsProxy = body.IsProxy
    });
}, "SADE validation", "Observe actual seated vehicle input"))
{
    await Task.Delay(10000, cancellationToken);
}
var path = System.IO.Path.Combine(exchangeDirectory, "vehicle-player-controls.json");
System.IO.File.WriteAllText(path, Json.Serialize(samples));
return new { path, Samples = samples.Count, WObserved = wFrames, SObserved = sFrames, LeftObserved = leftFrames, PitchObserved = pitchFrames,
    MaximumSpeed = maximumSpeed, MaximumRotor = maximumRotor, VerticalGain = peakZ - first.z,
    Note = "Observed real input only. A control with zero observed frames remains untested." };
