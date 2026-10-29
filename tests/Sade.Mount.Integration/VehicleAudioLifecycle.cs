// A real wall-clock audio regression with an isolated seated listener, then an exterior listener.
// Scene contexts are released before awaits. No active map, camera or player state is changed.
var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var nativeType = assembly.GetType("Sade.Mount.NativeVehicle")!;
var runtime = assembly.GetType("Sade.Mount.VehicleRuntime")!;
var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var profiles = runtime.GetField("profiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
var scene = new Scene();
Component native = null; GameObject vehicle = null; AudioListener listenerComponent = null;
SoundHandle firstHandle = null;
var results = new System.Collections.Generic.List<object>();
try
{
    using(scene.Push())
    {
        var path = "mount://sade/vehicles/road/infernus.prefab";
        PrefabFile.Load(path); vehicle = GameObject.Clone(path);
        var renderer = vehicle.GetComponent<SkinnedModelRenderer>(); var body = vehicle.GetComponent<Rigidbody>();
        var profile = profiles.GetType().GetProperty("Item")!.GetValue(profiles, [renderer.Model.Name])!;
        var create = vehicle.Components.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition);
        native = (Component)create.MakeGenericMethod(nativeType).Invoke(vehicle.Components, [false])!;
        nativeType.GetMethod("Initialize", flags)!.Invoke(native, [profile, body, renderer]); body.MotionEnabled = false;
        var dummy = scene.CreateObject(); dummy.Name="Temporary audio test driver";
        var visual = new GameObject(dummy,true,"Human renderer"); var human=visual.Components.Create<SkinnedModelRenderer>();human.Model=Model.Load("models/citizen_human/human.vmdl");
        var driver=dummy.Components.Create<PlayerController>();driver.Renderer=human;driver.UseCameraControls=false;driver.UseInputControls=false;driver.Components.Create<Sandbox.Movement.SitMoveMode>();
        for(var i=0;i<3;i++)scene.GameTick(.02);
        var chair=vehicle.GetComponentsInChildren<BaseChair>().Single(c=>c.GameObject.Tags.Has("sade_vehicle_driver",false));chair.Sit(driver);
        var listenerObject=scene.CreateObject();listenerObject.WorldPosition=chair.EyePosition.WorldPosition;
        listenerComponent=listenerObject.Components.Create<AudioListener>(); listenerComponent.UseCameraDirection=false;
    }
    for(var i=0;i<35;i++)
    {
        using(scene.Push())
        {
            scene.GameTick(.1);
            nativeType.GetMethod("Tick",flags)!.Invoke(native,[.1f]);
            var audio=nativeType.GetField("audio",flags)!.GetValue(native)!;
            var handle=(SoundHandle)audio.GetType().GetField("engineHandle",flags)!.GetValue(audio)!;
            firstHandle ??= handle;
            if (!ReferenceEquals(firstHandle, handle) || !handle.IsPlaying) throw new Exception("The original car engine loop stopped or restarted during wall-clock playback.");
            var listener=listenerComponent.GetType().GetField("_listener",flags)!.GetValue(listenerComponent);
            var model=handle.GetType().GetMethod("GetDirectSoundModel",flags)?.Invoke(handle,[listener]);
            var acoustic=model?.GetType().GetMethod("GetParams",flags)?.Invoke(model,null);
            if(i>2)
            {
                var bands=acoustic?.GetType().GetField("TransmissionBands",flags)?.GetValue(acoustic);
                if(bands is null)throw new Exception("The engine did not create a direct-sound model for the isolated listener.");
                foreach(var band in new[]{"Low","Mid","High"})
                    if((float)bands.GetType().GetProperty(band)!.GetValue(bands)! < .95f)
                        throw new Exception($"The car's own hull occluded its engine audio ({band}).");
            }
            var listenerPosition=listenerComponent.WorldPosition;
            var trace=scene.Trace.Ray(handle.Position,listenerPosition).Run();
            results.Add(new{i,SameHandle=ReferenceEquals(firstHandle,handle),handle.IsPlaying,handle.Volume,handle.Pitch,handle.Amplitude,handle.OcclusionEnabled,
                handle.Position,listenerPosition,handle.Distance,SoundScene=(handle.GetType().GetProperty("Scene",flags)?.GetValue(handle)as Scene)?.Name,
                ListenerScene=listener?.GetType().GetProperty("Scene")?.GetValue(listener)?.ToString(),
                Occlusion=acoustic?.GetType().GetFields(flags).ToDictionary(f=>f.Name,f=>f.GetValue(acoustic)?.ToString()),
                Transmission=model?.GetType().GetProperty("SmoothedTransmission",flags)?.GetValue(model)?.ToString(),
                Diffraction=model?.GetType().GetProperty("SmoothedDiffraction",flags)?.GetValue(model)?.ToString(),Blocked=trace.Hit,trace.StartedSolid});
            if(i==17) listenerComponent.WorldPosition=vehicle.WorldPosition+new Vector3(-200,0,50);
        }
        await Task.Delay(100,cancellationToken);
    }
}
finally
{
    using(scene.Push()) {if(native?.IsValid==true)nativeType.GetMethod("Shutdown",flags)!.Invoke(native,null);scene.Destroy();}
}
return results;
