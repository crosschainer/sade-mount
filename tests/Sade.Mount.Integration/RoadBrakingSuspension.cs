// Trusted run_csharp probe. All objects live in a private scene; actual player input is
// not synthesized. Source metadata, tire rendering and native physics are exercised.
var mount = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var native = mount.GetType("Sade.Mount.NativeVehicle")!;
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var registry = mount.GetType("Sade.Mount.VehicleRuntime")!.GetField("profiles", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
var activeScene = Game.ActiveScene; var activeCamera = activeScene?.Camera;
var scene = new Scene();
using (scene.Push())
try
{
    var floor = scene.CreateObject(); floor.WorldPosition = Vector3.Down * 10;
    var floorCollider = floor.Components.Create<BoxCollider>();
    floorCollider.Scale = new Vector3(30000, 30000, 20); floorCollider.Static = true;
    var path = "mount://sade/vehicles/road/infernus.prefab";
    if (PrefabFile.Load(path) is null) throw new Exception("Missing mounted Infernus");
    var car = GameObject.Clone(path); car.WorldPosition = Vector3.Up * 100;
    var body = car.GetComponent<Rigidbody>(); var renderer = car.GetComponent<SkinnedModelRenderer>();
    var profile = registry.GetType().GetProperty("Item")!.GetValue(registry, [renderer.Model.Name])!;
    var data = profile.GetType().GetProperty("Data")!.GetValue(profile)!;
    var create = car.Components.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition);
    var controller = (Component)create.MakeGenericMethod(native).Invoke(car.Components, [false])!;
    native.GetMethod("Initialize", flags)!.Invoke(controller, [profile, body, renderer]);
    var wheelList = ((System.Collections.IEnumerable)native.GetField("wheels", flags)!.GetValue(controller)!).Cast<object>().ToArray();
    var travelUp = Read("SuspensionUpper") / 2.54f;
    var travelDown = -Read("SuspensionLower") / 2.54f;
    using var hook = scene.AddHook(GameObjectSystem.Stage.StartFixedUpdate, 15,
        () => native.GetMethod("FixedTick", flags)!.Invoke(controller, [.02f]), "SADE tests", "Road brakes and suspension");
    Step(400);
    native.GetMethod("Tick", flags)!.Invoke(controller, [.02f]);
    var rest = car.WorldPosition;
    // GTA SetupSuspensionLines predicts 72.7083cm for installed Infernus handling.
    if (Math.Abs(rest.z - 72.7083f / 2.54f) > .3f || body.Velocity.Length > .5f)
        throw new Exception($"Incorrect source ride equilibrium: position={rest}, velocity={body.Velocity}");
    var colliderClearance = car.GetComponent<ModelCollider>().LocalBounds.Mins.z + rest.z;
    if (colliderClearance < 8) throw new Exception("The chassis is resting on its collider instead of the tires: " + colliderClearance);
    var wheelResults = new System.Collections.Generic.List<object>();
    foreach (var wheel in wheelList)
    {
        var type = wheel.GetType();
        var name = (string)type.GetField("Name", flags)!.GetValue(wheel)!;
        var radius = (float)type.GetField("Radius", flags)!.GetValue(wheel)!;
        var extension = (float)type.GetField("Extension", flags)!.GetValue(wheel)!;
        var bind = (Vector3)type.GetField("Position", flags)!.GetValue(wheel)!;
        var visual = (GameObject)type.GetField("Visual", flags)!.GetValue(wheel)!;
        if (Math.Abs(visual.WorldPosition.z - radius) > .1f || Math.Abs(extension - 1.06627f) > .15f || visual.LocalPosition.z >= bind.z)
            throw new Exception($"{name}: wheel did not meet the ground below its bind socket: center={visual.WorldPosition.z}, radius={radius}, extension={extension}");
        wheelResults.Add(new { Name = name, GroundContactError = visual.WorldPosition.z - radius, Extension = extension, LocalCenter = visual.LocalPosition });
    }

    var dummy = scene.CreateObject(); dummy.WorldPosition = new Vector3(100000, 100000, 100000);
    var driver = dummy.Components.Create<PlayerController>(false);
    native.GetProperty("Driver", flags)!.SetValue(controller, driver);
    var measurements = new System.Collections.Generic.List<object>();
    var footDistance = 0f; var handbrakeDistance = 0f;
    foreach (var mode in new[] { "coast", "foot", "handbrake" })
    {
        car.WorldTransform = new Transform(rest); body.Velocity = new Vector3(0, 1200, 0); body.AngularVelocity = Vector3.Zero;
        native.GetField("throttle", flags)!.SetValue(controller, mode == "foot" ? -1f : 0f);
        native.GetField("brake", flags)!.SetValue(controller, mode == "handbrake");
        var start = car.WorldPosition; var seconds = 0f; var minimumUpright = 1f;
        for (var i = 0; i < 250; i++)
        {
            Step(1); seconds += .02f;
            minimumUpright = Math.Min(minimumUpright, (car.WorldRotation * Vector3.Up).z);
            CheckTravel();
            if (body.Velocity.Length < 20) break;
        }
        var distanceMeters = car.WorldPosition.Distance(start) * .0254f;
        if (minimumUpright < .95f) throw new Exception(mode + " braking destabilized the chassis");
        if (mode == "coast" && body.Velocity.Length < 850) throw new Exception("Coasting accidentally applied the brakes");
        if (mode == "foot")
        {
            footDistance = distanceMeters;
            if (seconds > 2.1f || distanceMeters is < 20 or > 30 || body.Velocity.Length >= 20)
                throw new Exception($"Foot brake missed 110km/h stopping envelope: {seconds}s, {distanceMeters}m, {body.Velocity}");
        }
        if (mode == "handbrake")
        {
            handbrakeDistance = distanceMeters;
            if (seconds > 3.2f || distanceMeters > 45 || body.Velocity.Length >= 20)
                throw new Exception($"Rear handbrake failed to stop: {seconds}s, {distanceMeters}m, {body.Velocity}");
        }
        measurements.Add(new { Mode = mode, Seconds = seconds, DistanceMeters = distanceMeters, FinalSpeed = body.Velocity.Length, MinimumUpright = minimumUpright });
    }
    if (handbrakeDistance <= footDistance) throw new Exception("Rear-only handbrake unexpectedly outperformed all-wheel pedal braking");
    if (activeScene?.Camera != activeCamera) throw new Exception("The test changed the active camera");
    native.GetMethod("Shutdown", flags)!.Invoke(controller, null);
    return new { Passed = true, SourceRideHeight = rest.z, ColliderClearance = colliderClearance, Wheels = wheelResults, Braking = measurements, ActiveSceneUnchanged = true };

    float Read(string name) => (float)data.GetType().GetProperty(name)!.GetValue(data)!;
    void Step(int count) { for (var i = 0; i < count; i++) { cancellationToken.ThrowIfCancellationRequested(); scene.GameTick(.02); } }
    void CheckTravel()
    {
        foreach (var wheel in wheelList)
        {
            var extension = (float)wheel.GetType().GetField("Extension", flags)!.GetValue(wheel)!;
            if (extension < -travelUp - .001f || extension > travelDown + .001f)
                throw new Exception("Wheel escaped its source bump/droop stops: " + extension);
        }
    }
}
finally { scene.Destroy(); }
