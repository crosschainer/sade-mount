// Real original loop playback with synthetic road-speed/accelerator inputs, in an isolated
// scene. This verifies sustained handles and audible pitch shifts, not physical keyboard input.
var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var audioType = assembly.GetType("Sade.Mount.NativeVehicleAudio")!;
var runtime = assembly.GetType("Sade.Mount.VehicleRuntime")!;
var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var profiles = runtime.GetField("profiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
var fixtures = new System.Collections.Generic.List<(string Name, object Audio, object Drive, int Gears, float Inertia)>();
var firstHandles = new System.Collections.Generic.Dictionary<string, SoundHandle>();
var samples = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<(int Tick, int Gear, float Rpm, float Pitch, float Volume, float Amplitude)>>();
var scene = new Scene();
var result = new System.Collections.Generic.List<object>();
try
{
    using (scene.Push())
    {
        var listener = scene.CreateObject().Components.Create<AudioListener>(); listener.UseCameraDirection = false;
        foreach (var (name, folder) in new[] { ("infernus", "road"), ("sanchez", "bike") })
        {
            var path = $"mount://sade/vehicles/{folder}/{name}.prefab";
            PrefabFile.Load(path); var vehicle = GameObject.Clone(path);
            vehicle.GetComponent<Rigidbody>().MotionEnabled = false;
            var renderer = vehicle.GetComponent<SkinnedModelRenderer>();
            var profile = profiles.GetType().GetProperty("Item")!.GetValue(profiles, [renderer.Model.Name])!;
            var data = profile.GetType().GetProperty("Data")!.GetValue(profile)!;
            var type = data.GetType();
            var gears = (int)type.GetProperty("GearCount")!.GetValue(data)!;
            var inertia = (float)type.GetProperty("EngineInertia")!.GetValue(data)!;
            var audio = Activator.CreateInstance(audioType, flags, null,
                [type.GetProperty("ModelNumber")!.GetValue(data)!, type.GetProperty("Family")!.GetValue(data)!, gears, inertia], null)!;
            var drive = audioType.GetField("drivetrain", flags)!.GetValue(audio)!;
            fixtures.Add((name, audio, drive, gears, inertia));
            samples[name] = [];
            vehicle.Destroy();
        }
    }
    for (var tick = 0; tick < 360; tick++)
    {
        using (scene.Push())
        {
            scene.GameTick(.05);
            foreach (var fixture in fixtures)
            {
                var position = new Vector3(fixture.Name == "infernus" ? -60 : 60, 0, 0);
                audioType.GetMethod("Update", flags)!.Invoke(fixture.Audio, [position, true, tick / 360f, 1f, 1f, .05f, true]);
                var handle = (SoundHandle)audioType.GetField("engineHandle", flags)!.GetValue(fixture.Audio)!;
                if (handle is null || !handle.IsPlaying) throw new Exception($"{fixture.Name}: original engine loop is not playing.");
                if (!firstHandles.TryGetValue(fixture.Name, out var first)) firstHandles[fixture.Name] = handle;
                else if (!ReferenceEquals(first, handle)) throw new Exception($"{fixture.Name}: engine loop restarted during an RPM/gear change.");
                if (!ReferenceEquals(handle.GetType().GetProperty("Scene", flags)!.GetValue(handle), scene))
                    throw new Exception($"{fixture.Name}: audio escaped its isolated scene.");
                var driveType = fixture.Drive.GetType();
                var gear = (int)driveType.GetProperty("Gear", flags)!.GetValue(fixture.Drive)!;
                var rpm = (float)driveType.GetProperty("Rpm", flags)!.GetValue(fixture.Drive)!;
                samples[fixture.Name].Add((tick, gear, rpm, handle.Pitch, handle.Volume, handle.Amplitude));
            }
        }
        await Task.Delay(50, cancellationToken);
    }
    foreach (var fixture in fixtures)
    {
        var frames = samples[fixture.Name];
        var shifts = new System.Collections.Generic.List<object>();
        for (var i = 1; i < frames.Count - 4; i++)
        {
            if (frames[i].Gear == frames[i - 1].Gear) continue;
            var before = frames[i - 1]; var after = frames[i + 3];
            if (before.Pitch - after.Pitch < .05f) throw new Exception($"{fixture.Name}: shift {before.Gear}->{after.Gear} did not reduce actual SoundHandle pitch ({before.Pitch}->{after.Pitch}).");
            shifts.Add(new { From = before.Gear, To = after.Gear, PitchBefore = before.Pitch, PitchAfter = after.Pitch, RpmBefore = before.Rpm, RpmAfter = after.Rpm });
        }
        if (shifts.Count != fixture.Gears - 1) throw new Exception($"{fixture.Name}: wrong number of shifts ({shifts.Count}).");
        if (frames.Max(f => f.Amplitude) < .01f) throw new Exception($"{fixture.Name}: engine reported silent playback throughout the sustained probe.");
        result.Add(new { fixture.Name, fixture.Gears, fixture.Inertia, PersistentHandle = true, FrameCount = frames.Count,
            MaximumAmplitude = frames.Max(f => f.Amplitude), Shifts = shifts,
            Frames = frames.Where(f => f.Tick % 10 == 0).Select(f => new { f.Tick, f.Gear, f.Rpm, f.Pitch, f.Volume, f.Amplitude }).ToArray() });
    }
}
finally
{
    using (scene.Push())
    {
        foreach (var fixture in fixtures) ((IDisposable)fixture.Audio).Dispose();
        scene.Destroy();
    }
}
return new { Passed = result.Count, Scope = "Real original loop playback with deterministic audio-transmission input; no player/map/camera changes, no keyboard test.", Vehicles = result };
