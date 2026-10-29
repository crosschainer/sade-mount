// Official Sandbox MountSpawner -> automatic discovery/hooks -> owned temporary driver.
// Uses only temporary objects; the real local player, camera and map are never changed.
var player = Player.FindLocalPlayer() ?? throw new Exception("Join the official Sandbox game first.");
var scene = Game.ActiveScene;
var camera = scene.Camera;
var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var staticFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
var objects = new System.Collections.Generic.List<GameObject>();
GameObject dummy = null;
var samples = new System.Collections.Generic.List<object>();
try
{
    var origin = camera.WorldPosition + camera.WorldRotation.Forward.WithZ(0).Normal * 320 + camera.WorldRotation.Right * 140;
    var floor = scene.Trace.Ray(origin + Vector3.Up * 100, origin + Vector3.Down * 500).IgnoreGameObjectHierarchy(player.GameObject).Run();
    if (floor.Hit && !floor.StartedSolid) origin = floor.EndPosition + Vector3.Up * 10;
    var spawner = new MountSpawner("mount://sade/00_featured/vehicles/infernus.vmdl");
    await spawner.Loading;
    if (!spawner.HasModel) throw new Exception("Original Infernus model could not load.");
    objects.AddRange(await spawner.Spawn(new Transform(origin), player));
    var car = objects.Single(); car.Name = "Temporary automatic vehicle audio check";
    Component native = null;
    for (var i=0;i<60 && native is null;i++)
    {
        await Task.Delay(50,cancellationToken);
        native = car.Components.GetAll<Component>(FindMode.EverythingInSelf).FirstOrDefault(c=>c.GetType().FullName=="Sade.Mount.NativeVehicle");
    }
    if(native is null) throw new Exception("Automatic VehicleRuntime discovery did not promote the actual MountSpawner object.");
    var body=car.GetComponent<Rigidbody>(); body.MotionEnabled=false;
    var chair=car.GetComponentsInChildren<BaseChair>().Single(c=>c.GameObject.Tags.Has("sade_vehicle_driver",false));
    dummy=scene.CreateObject();dummy.Name="Temporary owned audio-check driver";dummy.WorldPosition=origin+Vector3.Up*80;
    var visual=new GameObject(dummy,true,"Human renderer");var human=visual.Components.Create<SkinnedModelRenderer>();human.Model=Model.Load("models/citizen_human/human.vmdl");
    var driver=dummy.Components.Create<PlayerController>();driver.Renderer=human;driver.UseCameraControls=false;driver.UseInputControls=false;driver.UseLookControls=false;
    driver.Components.Create<Sandbox.Movement.SitMoveMode>();
    dummy.NetworkSpawn(true,player.Network.Owner);
    await Task.Delay(150,cancellationToken);
    chair.Sit(driver);
    SoundHandle original=null;
    var type=native.GetType();
    var listenerType=typeof(SoundHandle).Assembly.GetType("Sandbox.Audio.Listener")!;
    for(var i=0;i<35;i++)
    {
        await Task.Delay(100,cancellationToken);
        if(type.GetProperty("Driver",flags)!.GetValue(native)!=driver)throw new Exception("Automatic seat/controller tick did not retain the owned temporary driver.");
        var audio=type.GetField("audio",flags)!.GetValue(native)!;
        var handle=(SoundHandle?)audio.GetType().GetField("engineHandle",flags)!.GetValue(audio);
        if(handle is null||!handle.IsPlaying)throw new Exception("Automatic native Tick did not maintain a playing engine loop.");
        original??=handle;
        if(!ReferenceEquals(original,handle))throw new Exception("Automatic runtime restarted the engine loop during the observation window.");
        var soundScene=handle.GetType().GetProperty("Scene",flags)!.GetValue(handle) as Scene;
        var listeners=((System.Collections.IEnumerable)listenerType.GetProperty("Active",staticFlags)!.GetValue(null)!).Cast<object>()
            .Where(l=>ReferenceEquals(listenerType.GetProperty("Scene",flags)!.GetValue(l),soundScene)).ToArray();
        if(!ReferenceEquals(soundScene,scene)||listeners.Length==0)throw new Exception("Engine audio and the actual game's listener belong to different scenes.");
        var acoustic=handle.GetType().GetMethod("GetDirectSoundModel",flags)?.Invoke(handle,[listeners[0]]);
        samples.Add(new {i,handle.IsPlaying,handle.Amplitude,handle.Volume,handle.Pitch,handle.Position,ListenerCount=listeners.Length,
            ListenerPosition=listenerType.GetProperty("Position",flags)!.GetValue(listeners[0]),
            Transmission=acoustic?.GetType().GetProperty("SmoothedTransmission",flags)?.GetValue(acoustic)?.ToString(),
            DriverOwned=driver.Network.Owner==player.Network.Owner,BodyProxy=body.IsProxy});
    }
    if(scene.Camera!=camera)throw new Exception("The temporary driver changed the active camera.");
    return new{Passed=true,Spawn="Real MountSpawner",Discovery="Automatic",DriverMode=driver.Mode?.GetType().Name,Samples=samples,
        Scope="Real Sandbox lifecycle and existing listener; no real-player seat or keyboard input was changed."};
}
finally
{
    if(dummy?.IsValid==true)dummy.Destroy();
    foreach(var obj in objects)if(obj.IsValid)obj.Destroy();
}
