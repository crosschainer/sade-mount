using Sandbox;
using Sade.Contracts;

namespace Sade.Mount;

/// <summary>Original vehicle-bank loops, with one persistent handle per layer.</summary>
internal sealed class NativeVehicleAudio(int modelNumber, string family, int gearCount = 5, float engineInertia = 15) : IDisposable
{
    private SoundFile? idle, engine;
    private SoundHandle? idleHandle, engineHandle;
    private bool loaded;
    private float sourcePitch = 1;
    private readonly VehicleDrivetrain drivetrain = new(gearCount, engineInertia);
    private float continuousRev, engineVolume, idleVolume;
    private static readonly Sandbox.Diagnostics.Logger Log = new("SADE vehicle audio");

    internal void Update(Vector3 position, bool occupied, float speedRatio, float pedal, float rotorSpeed, float delta = .02f, bool grounded = true)
    {
        var aircraft = family is "helicopter" or "plane";
        var running = occupied || aircraft && rotorSpeed > .02f;
        if (!running) { Stop(); return; }
        if (!loaded) Load();
        // Preserve the original rotor envelope, which already follows rotor spin-up/down.
        if (family == "helicopter")
        {
            var rotorPitch = sourcePitch * Math.Clamp(.55f + rotorSpeed * .45f, .55f, 1);
            UpdateLayer(ref engineHandle, engine, position, rotorPitch, rotorSpeed * .65f, 6000);
            UpdateLayer(ref idleHandle, idle, position, rotorPitch, rotorSpeed * .25f, 6000);
            return;
        }
        delta = float.IsFinite(delta) ? Math.Clamp(delta, 0, .1f) : .02f;
        float pitch, idlePitch, targetEngineVolume, targetIdleVolume;
        if (family is "plane" or "boat" or "hovercraft")
        {
            // Aircraft throttle is persistent; boats load their engine against the propeller.
            // Neither should simulate road gears or infer RPM from an airborne wheel contact.
            var load = Math.Clamp(Math.Abs(pedal), 0, 1);
            var targetRev = family == "plane" ? load : Math.Clamp(load * .8f + Math.Abs(speedRatio) * .2f, 0, 1);
            continuousRev = VehicleDrivetrain.Smooth(continuousRev, targetRev, 3, delta);
            pitch = sourcePitch * (.72f + continuousRev * .78f);
            idlePitch = pitch;
            var spin = family == "plane" ? rotorSpeed : 1;
            targetEngineVolume = spin * (.2f + continuousRev * .5f);
            targetIdleVolume = spin * (.2f + continuousRev * .08f);
        }
        else
        {
            drivetrain.Update(speedRatio, pedal, grounded, delta);
            var rpm = drivetrain.Rpm;
            pitch = sourcePitch * ((family == "bike" ? .65f : .7f) + rpm * (family == "bike" ? 1.45f : 1.15f));
            idlePitch = sourcePitch * (.92f + rpm * .18f);
            targetEngineVolume = (.08f + rpm * .35f + drivetrain.Load * .3f) * drivetrain.ShiftGain;
            targetIdleVolume = .48f * MathF.Pow(1 - Math.Clamp(rpm, 0, 1), 2) * (1 - drivetrain.Load * .4f);
        }
        engineVolume = VehicleDrivetrain.Smooth(engineVolume, targetEngineVolume, 12, delta);
        idleVolume = VehicleDrivetrain.Smooth(idleVolume, targetIdleVolume, 8, delta);
        UpdateLayer(ref engineHandle, engine, position, pitch, engineVolume, aircraft ? 6000 : 2400);
        UpdateLayer(ref idleHandle, idle, position, idlePitch, idleVolume, aircraft ? 6000 : 1800);
    }

    private void Load()
    {
        loaded = true;
        if (family is "bicycle" or "trailer" or "train" || !VehicleAudioBanks.TryGet(modelNumber, out var player, out var dummy, out sourcePitch)) return;
        if (Sandbox.Mounting.Directory.Get("sade") is not SadeMount mount) return;
        try
        {
            // Original CAEVehicleAudioEntity: cars/bikes use dummy-bank0 for rev and1
            // for idle; aircraft use player-bank0/1 for front/rear; boats dummy-bank0.
            var aircraft = family is "helicopter" or "plane";
            var jet = modelNumber is 519 or 520 or 577 or 592;
            // Shamal/Hydra/AT400/Andromada intentionally have no per-model bank:
            // ProcessGenericJet uses VEHICLE_GEN HARRIER_FRONT(10)/HARRIER_REAR(11).
            var bank = jet ? "VEHICLE_GEN" : aircraft || string.IsNullOrEmpty(dummy) ? player : dummy;
            engine = Resolve(bank, jet ? 10 : 0);
            if (family is not ("boat" or "hovercraft")) idle = Resolve(bank, jet ? 11 : 1);
            if (engine is null) Log.Warning($"Vehicle {modelNumber}: original engine loop {bank}/S_000 is not mounted.");

            SoundFile? Resolve(string bankName, int sample)
            {
                if (string.IsNullOrEmpty(bankName)) return null;
                var id = $"/Game/SanAndreas/Audio/SFX/GENRL/{bankName}/S_{sample:000}";
                // Some DE banks wrap their sustained wave in a cue with a separate _Loop export.
                var path = mount.PathFor(AssetKind.Sound, id + "_Loop") ?? mount.PathFor(AssetKind.Sound, id);
                return path is null ? null : SoundFile.Load(path);
            }
        }
        catch (Exception e) { Log.Warning($"Vehicle {modelNumber}: original engine audio could not load: {e.Message}"); }
    }

    private static void UpdateLayer(ref SoundHandle? handle, SoundFile? file, Vector3 position, float pitch, float volume, float distance)
    {
        if (file is null) return;
        if (!handle.IsValid() || !handle!.IsPlaying)
        {
            handle?.Stop();
            handle = Sound.PlayFile(file);
            if (!handle.IsValid()) return;
            handle.ListenLocal = false;
            handle.DistanceAttenuation = true;
            handle.Distance = distance;
            handle.Falloff = ExteriorFalloff(handle.Falloff, distance);
        }
        handle!.Position = position;
        handle.Pitch = Math.Clamp(pitch, .25f, 3);
        handle.Volume = Math.Clamp(volume, 0, 1);
    }

    private static Curve ExteriorFalloff(Curve original, float distance)
    {
        // Keep cabin/nearby gain, then use the full configured distance for exterior
        // decay. The engine default is already down to .22 at only 5% of that range.
        // Sampling the existing near curve preserves its level without raising Volume.
        var frames = new List<Curve.Frame>();
        for (var units = 0; units <= 64; units += 4)
            frames.Add(new Curve.Frame(units / distance, original.Evaluate(units / distance)) { Mode = Curve.HandleMode.Linear });
        frames.Add(new Curve.Frame(50 / distance, original.Evaluate(50 / distance)) { Mode = Curve.HandleMode.Linear });
        frames.Add(new Curve.Frame(1, 0) { Mode = Curve.HandleMode.Linear });
        return new Curve(frames.OrderBy(f => f.Time));
    }

    private void Stop()
    {
        idleHandle?.Stop(.1f); idleHandle = null;
        engineHandle?.Stop(.1f); engineHandle = null;
        drivetrain.Reset(); continuousRev = 0; engineVolume = 0; idleVolume = 0;
    }

    public void Dispose() => Stop();
}
