// Run as a trusted sbox-cli run_csharp method body after installing the current mount.
// Uses an isolated temporary Scene and destroys it; the active scene/camera remain unchanged.
// Deterministic controller state exercises physical simulation, not actual keyboard input.
var mount = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var runtime = mount.GetType("Sade.Mount.VehicleRuntime")!;
var nativeType = mount.GetType("Sade.Mount.NativeVehicle")!;
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var registry = runtime.GetField("profiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
var scene = new Scene();
var results = new System.Collections.Generic.List<object>();
using (scene.Push())
try
{
    var floor = scene.CreateObject();
    floor.Name = "SADE temporary physics floor";
    floor.WorldPosition = new Vector3(0, 0, -10);
    var floorCollider = floor.Components.Create<BoxCollider>();
    floorCollider.Scale = new Vector3(20000, 20000, 20);
    floorCollider.Static = true;
    foreach (var name in new[] { "infernus", "sanchez", "bmx", "squalo", "dodo", "maverick" })
    {
        var family = name switch { "infernus" => "road", "bmx" => "bicycle", "squalo" => "boat", "dodo" => "plane", "maverick" => "helicopter", _ => "bike" };
        GameObject? water = null;
        if (family == "boat")
        {
            floor.Enabled = false;
            water = scene.CreateObject();
            water.WorldPosition = new Vector3(0, 0, -300);
            var volume = water.Components.Create<BoxCollider>();
            volume.Center = new Vector3(0, 0, -200);
            volume.Scale = new Vector3(20000, 20000, 1000);
            volume.IsTrigger = true;
            volume.Static = true;
            var waterComponent = water.Components.Create<WaterVolume>();
            // Keep the surface at zero while separating it from the object's origin.
            // The engine component exposes an offset; Sandbox's own type uses the box top.
            waterComponent.GetType().GetProperty("SurfaceOffset")?.SetValue(waterComponent, 300f);
        }
        var path = $"mount://sade/vehicles/{family}/{name}.prefab";
        if (PrefabFile.Load(path) is null) throw new Exception($"Missing mounted vehicle: {path}");
        var vehicle = GameObject.Clone(path);
        vehicle.WorldPosition = new Vector3(0, 0, 100);
        var body = vehicle.GetComponent<Rigidbody>();
        var renderer = vehicle.GetComponent<SkinnedModelRenderer>();
        var profile = registry.GetType().GetProperty("Item")!.GetValue(registry, [renderer.Model.Name])!;
        var create = vehicle.Components.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition);
        var controller = (Component)create.MakeGenericMethod(nativeType).Invoke(vehicle.Components, [false])!;
        nativeType.GetMethod("Initialize", flags)!.Invoke(controller, [profile, body, renderer]);
        var fixedTick = nativeType.GetMethod("FixedTick", flags)!;
        var data = profile.GetType().GetProperty("Data")!.GetValue(profile)!;
        var sourceMass = (float)data.GetType().GetProperty("MassKg")!.GetValue(data)!;
        using var hook = scene.AddHook(GameObjectSystem.Stage.StartFixedUpdate, 15,
            () => fixedTick.Invoke(controller, [.02f]), "SADE tests", "Vehicle simulation");
        for (int i = 0; i < 150; i++) scene.GameTick(.02);
        var rest = vehicle.WorldPosition;
        object? waterDiscovery = null;
        if (water is not null)
        {
            // Discover the real game's component naturally. Never populate the private
            // cache here: this guards the global WaterVolume/engine type mismatch.
            var discovered = (Component[])nativeType.GetField("waterVolumes", flags)!.GetValue(controller)!;
            if (!discovered.Any(c => c.GameObject == water)) throw new Exception("The boat did not discover the game's actual WaterVolume component.");
            object[] surfaceArguments = [0f];
            if (!(bool)nativeType.GetMethod("WaterSurface", flags)!.Invoke(controller, surfaceArguments)! || Math.Abs((float)surfaceArguments[0]) > .01f)
                throw new Exception("The boat did not use the shifted water collider's top surface at z=0.");
            waterDiscovery = new { Type = discovered.First(c => c.GameObject == water).GetType().FullName, SurfaceHeight = surfaceArguments[0], ObjectOrigin = water.WorldPosition.z };
        }
        var dummy = scene.CreateObject();
        // Even a disabled PlayerController creates physical body colliders. Keep this
        // synthetic authority token away from the chassis; seating is tested separately.
        dummy.WorldPosition = new Vector3(100000, 100000, 100000);
        var driver = dummy.Components.Create<PlayerController>(false);
        nativeType.GetProperty("Driver", flags)!.SetValue(controller, driver);
        nativeType.GetField("throttle", flags)!.SetValue(controller, 1f);
        // This fixture deliberately bypasses Tick (which reads actual player input). Supply
        // its already-spooled state and persistent plane throttle explicitly.
        nativeType.GetField("rotorSpeed", flags)!.SetValue(controller, 1f);
        if (family == "helicopter") nativeType.GetField("lift", flags)!.SetValue(controller, 1f);
        if (family == "plane")
        {
            nativeType.GetField("aircraftThrottle", flags)!.SetValue(controller, 1f);
            // A wing requires runway speed and a pilot's rotation input; throttle alone
            // is not an automatic takeoff command. Match the dedicated rollout fixture.
            nativeType.GetField("pitch", flags)!.SetValue(controller, .15f);
        }
        for (int i = 0; i < (family == "plane" ? 650 : 150); i++) scene.GameTick(.02);
        var driven = vehicle.WorldPosition;
        var velocity = body.Velocity;
        if (Math.Abs(body.Mass - sourceMass) > 1) throw new Exception($"{name}: source mass was overwritten by model collider.");
        if (!float.IsFinite(driven.Length) || driven.Distance(rest) < 50 || velocity.Length < 40) throw new Exception($"{name}: failed to move under throttle: rest={rest}, driven={driven}, velocity={velocity}, mass={body.Mass}, proxy={body.IsProxy}, grounded={nativeType.GetProperty("Grounded", flags)!.GetValue(controller)}, correctScene={controller.Scene == scene}, waterCount={scene.GetAllComponents<WaterVolume>().Count()}, waterBounds={water?.GetComponent<BoxCollider>()?.LocalBounds}, waterPos={water?.WorldPosition}.");
        if (family is "road" or "bike" or "bicycle" && Vector3.Dot(vehicle.WorldRotation * Vector3.Up, Vector3.Up) < .65f) throw new Exception($"{name}: tipped over while driving straight.");
        if (family is "plane" or "helicopter" && driven.z < rest.z + 100) throw new Exception($"{name}: failed to take off.");
        if (family == "boat" && Math.Abs(driven.z) > 80) throw new Exception($"{name}: failed to float at the water surface.");
        nativeType.GetField("steering", flags)!.SetValue(controller, family == "plane" ? 0f : .5f);
        if (family == "plane") nativeType.GetField("pitch", flags)!.SetValue(controller, .2f);
        for (int i = 0; i < 100; i++) scene.GameTick(.02);
        results.Add(new { name, rest, driven, velocity, turned = vehicle.WorldPosition, rotation = vehicle.WorldRotation.Angles(), mass = body.Mass, waterDiscovery });
        hook.Dispose();
        nativeType.GetMethod("Shutdown", flags)!.Invoke(controller, null);
        vehicle.Destroy(); dummy.Destroy(); water?.Destroy(); floor.Enabled = true;
        scene.GameTick(.02);
    }
}
finally { scene.Destroy(); }
return new { Passed = results.Count, Vehicles = results, Scope = "Isolated physics with deterministic controller state; actual player keys are not tested." };
