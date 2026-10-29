// Run after installing the current mount. This creates/destroys one private physics scene.
// The official Sandbox map, camera and local player are never modified.
var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="sade");
var type = assembly.GetType("Sade.Mount.NativeVehicle")!;
var runtime = assembly.GetType("Sade.Mount.VehicleRuntime")!;
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var registry = runtime.GetField("profiles",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
var scene = new Scene(); var originalScene = Game.ActiveScene; var originalCamera=originalScene?.Camera;
var results = new System.Collections.Generic.List<object>();
using(scene.Push())
try
{
    var floor=scene.CreateObject(); floor.WorldPosition=Vector3.Down*10;
    var floorCollider=floor.Components.Create<BoxCollider>(); floorCollider.Scale=new Vector3(30000,30000,20); floorCollider.Static=true;
    foreach(var name in new[]{"sanchez","bmx"})
    foreach(var turn in new[]{1f,-1f})
    {
        var tuple=Create(name=="bmx"?"bicycle":"bike",name,new Vector3(0,0,70));
        var vehicle=tuple.Item1;var controller=tuple.Item2;var body=tuple.Item3;
        using var hook=scene.AddHook(GameObjectSystem.Stage.StartFixedUpdate,15,()=>type.GetMethod("FixedTick",flags)!.Invoke(controller,[.02f]),"SADE tests","Bike dynamics");
        var driver=type.GetProperty("Driver",flags)!.GetValue(controller);
        type.GetProperty("Driver",flags)!.SetValue(controller,null);
        Step(150);
        var parkedUpright=(vehicle.WorldRotation*Vector3.Up).z;var parkedSpin=body.AngularVelocity.Length;
        if(parkedUpright<.98f || parkedSpin>.05f)throw new Exception($"{name} parked balance: up={parkedUpright}, spin={parkedSpin}");
        type.GetProperty("Driver",flags)!.SetValue(controller,driver);
        Set(controller,"throttle",1f);Step(150);
        var startSpeed=body.Velocity.Length;var initialHeading=vehicle.WorldRotation*new Vector3(0,1,0);
        Set(controller,"steering",turn*.6f);
        var minimumUpright=1f;var totalLean=0f;var maxVerticalSpeed=0f;
        for(var i=0;i<90;i++)
        {
            Step(1);
            var up=vehicle.WorldRotation*Vector3.Up;var right=(vehicle.WorldRotation*new Vector3(1,0,0)).WithZ(0).Normal;
            minimumUpright=Math.Min(minimumUpright,up.z); if(i>40)totalLean+=Vector3.Dot(up,right);
            maxVerticalSpeed=Math.Max(maxVerticalSpeed,Math.Abs(body.Velocity.z));
        }
        var meanLean=totalLean/49;var heading=vehicle.WorldRotation*new Vector3(0,1,0);
        var yawSign=Vector3.Cross(initialHeading,heading).z;
        if(startSpeed<100 || minimumUpright<.45f || meanLean*turn>-.015f || yawSign*turn<.1f || body.Velocity.x*turn>-10)
            throw new Exception($"{name} turn{turn}: speed={startSpeed}, upright={minimumUpright}, inward lean={meanLean}, yaw={yawSign}, lateral velocity={body.Velocity.x}");
        if(maxVerticalSpeed>40)throw new Exception(name+": flat-floor steering bounced excessively: "+maxVerticalSpeed);
        results.Add(new{Family=name,Turn=turn,ParkedUpright=parkedUpright,ParkedSpin=parkedSpin,StartSpeed=startSpeed,MinimumUpright=minimumUpright,MeanLean=meanLean,YawSign=yawSign,LateralVelocity=body.Velocity.x,MaxVerticalSpeed=maxVerticalSpeed});
        hook.Dispose();Remove(vehicle,controller);Step(2);
    }
    {
        var tuple=Create("plane","dodo",new Vector3(0,0,1000));var plane=tuple.Item1;var controller=tuple.Item2;var body=tuple.Item3;
        using var hook=scene.AddHook(GameObjectSystem.Stage.StartFixedUpdate,15,()=>type.GetMethod("FixedTick",flags)!.Invoke(controller,[.02f]),"SADE tests","Plane dynamics");
        Set(controller,"aircraftThrottle",0f);Step(35);
        var slowDescent=body.Velocity.z;
        if(slowDescent>-100)throw new Exception("Zero-forward-speed plane was supported instead of falling: "+slowDescent);
        var bank=Rotation.FromAxis(new Vector3(0,1,0),25);
        plane.WorldPosition=new Vector3(0,0,1200);plane.WorldRotation=bank;body.Velocity=new Vector3(0,1100,0);body.AngularVelocity=Vector3.Zero;
        Set(controller,"aircraftThrottle",.7f);Set(controller,"pitch",0f);Set(controller,"roll",0f);Set(controller,"steering",0f);
        Step(45);var bankYaw=body.AngularVelocity.z;var bankSideVelocity=body.Velocity.x;
        if(bankYaw>=-.03f || bankSideVelocity<10)throw new Exception($"Right bank did not produce a right turn: yaw={bankYaw}, side velocity={bankSideVelocity}");
        results.Add(new{Family="dodo",LowSpeedDescent=slowDescent,RightBankYaw=bankYaw,RightBankSideVelocity=bankSideVelocity});
        hook.Dispose();Remove(plane,controller);Step(2);
    }
    {
        var tuple=Create("plane","dodo",new Vector3(0,0,110));var plane=tuple.Item1;var controller=tuple.Item2;var body=tuple.Item3;
        using var hook=scene.AddHook(GameObjectSystem.Stage.StartFixedUpdate,15,()=>type.GetMethod("FixedTick",flags)!.Invoke(controller,[.02f]),"SADE tests","Plane rollout");
        Step(120);var groundHeight=plane.WorldPosition.z;
        Set(controller,"aircraftThrottle",1f);Set(controller,"pitch",.15f);
        var maxHeight=groundHeight;var maximumVerticalSpeed=0f;var firstAirSpeed=0f;
        for(var i=0;i<650;i++)
        {
            Step(1);maxHeight=Math.Max(maxHeight,plane.WorldPosition.z);maximumVerticalSpeed=Math.Max(maximumVerticalSpeed,Math.Abs(body.Velocity.z));
            if(firstAirSpeed==0&&plane.WorldPosition.z>groundHeight+30)firstAirSpeed=Vector3.Dot(body.Velocity,plane.WorldRotation*new Vector3(0,1,0));
        }
        if(firstAirSpeed<350 || maxHeight<groundHeight+80 || maximumVerticalSpeed>800)
            throw new Exception($"Dodo takeoff failed/hovered/bounced: takeoff airspeed={firstAirSpeed}, heightgain={maxHeight-groundHeight}, maximumvertical={maximumVerticalSpeed}");
        results.Add(new{Family="dodo",TakeoffAirspeed=firstAirSpeed,HeightGain=maxHeight-groundHeight,MaximumVerticalSpeed=maximumVerticalSpeed});
        hook.Dispose();Remove(plane,controller);
    }
    if(originalScene?.Camera!=originalCamera)throw new Exception("Dynamics test changed active camera.");
    return new{Passed=true,Vehicles=results,ActiveSceneUnchanged=true};

    (GameObject,Component,Rigidbody) Create(string family,string name,Vector3 position)
    {
        var path=$"mount://sade/vehicles/{family}/{name}.prefab";
        if(PrefabFile.Load(path)is null)throw new Exception("Missing "+path);
        var vehicle=GameObject.Clone(path);vehicle.WorldPosition=position;
        var body=vehicle.GetComponent<Rigidbody>();var renderer=vehicle.GetComponent<SkinnedModelRenderer>();
        var profile=registry.GetType().GetProperty("Item")!.GetValue(registry,[renderer.Model.Name])!;
        var create=vehicle.Components.GetType().GetMethods().Single(m=>m.Name=="Create"&&m.IsGenericMethodDefinition);
        var controller=(Component)create.MakeGenericMethod(type).Invoke(vehicle.Components,[false])!;
        type.GetMethod("Initialize",flags)!.Invoke(controller,[profile,body,renderer]);
        // PlayerController creates its body/collider during initialization even while disabled.
        // Keep the synthetic driver away from the bike; parenting it inside the chassis would
        // create overlapping physics bodies and invalidate the touchdown/lean measurements.
        var driverObject=scene.CreateObject();driverObject.WorldPosition=new Vector3(100000,100000,100000);var driver=driverObject.Components.Create<PlayerController>(false);
        type.GetProperty("Driver",flags)!.SetValue(controller,driver);
        return(vehicle,controller,body);
    }
    void Remove(GameObject vehicle,Component controller){var driver=(PlayerController?)type.GetProperty("Driver",flags)!.GetValue(controller);type.GetMethod("Shutdown",flags)!.Invoke(controller,null);driver?.GameObject.Destroy();vehicle.Destroy();}
    void Set(Component controller,string field,float value)=>type.GetField(field,flags)!.SetValue(controller,value);
    void Step(int count){for(var i=0;i<count;i++){cancellationToken.ThrowIfCancellationRequested();scene.GameTick(.02);}}
}
finally{scene.Destroy();}
