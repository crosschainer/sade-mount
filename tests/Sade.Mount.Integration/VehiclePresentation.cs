// Real native Tick + seated engine player, in a disposable Scene. No keyboard injection.
var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var nativeType = assembly.GetType("Sade.Mount.NativeVehicle")!;
var runtime = assembly.GetType("Sade.Mount.VehicleRuntime")!;
var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var profiles = runtime.GetField("profiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
var results = new System.Collections.Generic.List<object>();
var audioFiles = new System.Collections.Generic.List<(string Vehicle, string Layer, SoundFile File)>();
var scene = new Scene();
using (scene.Push())
try
{
    foreach (var (name, family) in new[] { ("infernus", "road"), ("sanchez", "bike"), ("squalo", "boat"), ("dodo", "plane"), ("maverick", "helicopter") })
    {
        var path = $"mount://sade/vehicles/{family}/{name}.prefab";
        if (PrefabFile.Load(path) is null) throw new Exception(path);
        var vehicle = GameObject.Clone(path);
        var renderer = vehicle.GetComponent<SkinnedModelRenderer>();
        var body = vehicle.GetComponent<Rigidbody>();
        var profile = profiles.GetType().GetProperty("Item")!.GetValue(profiles, [renderer.Model.Name])!;
        var create = vehicle.Components.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition);
        var native = (Component)create.MakeGenericMethod(nativeType).Invoke(vehicle.Components, [false])!;
        nativeType.GetMethod("Initialize", flags)!.Invoke(native, [profile, body, renderer]);
        body.MotionEnabled = false;
        var dummy = scene.CreateObject(); dummy.Name = "Temporary presentation driver";
        var visual = new GameObject(dummy, true, "Human renderer");
        var human = visual.Components.Create<SkinnedModelRenderer>(); human.Model = Model.Load("models/citizen_human/human.vmdl");
        var driver = dummy.Components.Create<PlayerController>();
        driver.Renderer = human; driver.UseCameraControls = false; driver.UseInputControls = false;
        driver.Components.Create<Sandbox.Movement.SitMoveMode>();
        for (var i = 0; i < 3; i++) scene.GameTick(.02);
        var chair = vehicle.GetComponentsInChildren<BaseChair>().Single(c => c.GameObject.Tags.Has("sade_vehicle_driver", false));
        chair.Sit(driver);
        var tick = nativeType.GetMethod("Tick", flags)!;
        for (var i = 0; i < 150; i++) { scene.GameTick(.02); tick.Invoke(native, [.02f]); }
        if (!ReferenceEquals(nativeType.GetProperty("Driver", flags)!.GetValue(native), driver)) throw new Exception($"{name}: native Tick lost the seated driver.");
        var audio = nativeType.GetField("audio", flags)!.GetValue(native)!;
        var file = (SoundFile?)audio.GetType().GetField("engine", flags)!.GetValue(audio);
        var handle = (SoundHandle?)audio.GetType().GetField("engineHandle", flags)!.GetValue(audio);
        var idle = (SoundFile?)audio.GetType().GetField("idle", flags)!.GetValue(audio);
        if (file is null || handle is null || !handle.IsPlaying) throw new Exception($"{name}: native Tick did not start the original engine audio; file={file?.ResourcePath}, playing={handle?.IsPlaying}.");
        audioFiles.Add((name, "engine", file)); if (idle is not null) audioFiles.Add((name, "idle/rear", idle));
        var rotorDelta = 0f;
        if (family == "helicopter")
        {
            renderer.SceneModel.Update(0);
            renderer.TryGetBoneTransform("static_rotor", out var before);
            tick.Invoke(native, [.02f]); renderer.SceneModel.Update(0);
            renderer.TryGetBoneTransform("static_rotor", out var after);
            rotorDelta = Vector3.GetAngle(before.Rotation.Forward, after.Rotation.Forward);
            if (rotorDelta < 5) throw new Exception($"{name}: rendered main rotor did not rotate ({rotorDelta} degrees).");
        }
        results.Add(new { name, DriverMode = driver.Mode?.GetType().Name, Sound = file.ResourcePath, Playing = handle.IsPlaying,
            handle.Volume, handle.Pitch, RotorSpeed = nativeType.GetField("rotorSpeed", flags)!.GetValue(native), RotorDegreesPerFrame = rotorDelta });
        nativeType.GetMethod("Shutdown", flags)!.Invoke(native, null); vehicle.Destroy(); dummy.Destroy(); scene.GameTick(.02);
    }
}
finally { scene.Destroy(); }
// Decode after releasing Scene.Push; never hold an isolated scene context across an await.
var decoded = new System.Collections.Generic.List<object>();
foreach (var item in audioFiles)
{
    var samples = await item.File.GetSamplesAsync();
    if (samples is null || samples.Length == 0) throw new Exception($"{item.Vehicle}: native engine could not decode {item.File.ResourcePath}.");
    var rms = Math.Sqrt(samples.Average(x => (double)x * x)) / 32768;
    if (rms < .0001) throw new Exception($"{item.Vehicle}: native engine decoded silent audio {item.File.ResourcePath}.");
    decoded.Add(new { item.Vehicle, item.Layer, item.File.ResourcePath, item.File.Rate, item.File.Duration, Rms = rms });
}
return new { Passed = results.Count, Vehicles = results, Audio = decoded,
    Scope = "Actual Tick, engine chair, rendered rotor bones and decoded sound; physical keyboard and perceptual volume remain separate checks." };
