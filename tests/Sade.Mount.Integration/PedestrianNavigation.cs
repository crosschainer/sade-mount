// Trusted run_csharp integration probe. All geometry and actors live in a disposable scene.
var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var nativeType = assembly.GetType("Sade.Mount.NativePedestrian")!;
var runtimeType = assembly.GetType("Sade.Mount.PedestrianRuntime")!;
var members = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var activeScene = Game.ActiveScene;
var activeCamera = activeScene?.Camera;
var scene = new Scene();
Component? native = null;
var phase = "scene setup";
using (scene.Push())
try
{
    scene.NavMesh.IsEnabled = false;
    var floor = scene.CreateObject(); floor.WorldPosition = new Vector3(0, 0, -10);
    var floorCollider = floor.Components.Create<BoxCollider>(); floorCollider.Scale = new Vector3(1000, 400, 20); floorCollider.Static = true;
    const string prefabPath = "mount://sade/pedestrians/sk_smoke.prefab";
    if (PrefabFile.Load(prefabPath) is null) throw new Exception("Mounted Big Smoke prefab is unavailable.");
    var actor = GameObject.Clone(prefabPath) ?? throw new Exception("Mounted Big Smoke prefab did not clone.");
    phase = "actor cloned";
    actor.WorldPosition = new Vector3(-220, 0, 0);
    var renderer = actor.GetComponentsInChildren<SkinnedModelRenderer>().Single();
    var agent = actor.GetComponent<NavMeshAgent>();
    var profiles = runtimeType.GetField("profiles", statics)!.GetValue(null)!;
    phase = "profile lookup";
    var originalProfile = profiles.GetType().GetProperty("Item")!.GetValue(profiles, [renderer.Model.Name])!;
    object Value(string name) => originalProfile.GetType().GetProperty(name, members)!.GetValue(originalProfile)!;
    // Copy only locomotion values; the isolated test has no combat factions or weapon.
    var profile = Activator.CreateInstance(originalProfile.GetType(), members, null,
        ["Navigation regression", Value("Idle"), Value("Walk"), Value("Run"), Value("WalkSpeed"), Value("RunSpeed"), "civilian"], null)!;
    phase = "controller creation";
    var create = actor.Components.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition);
    native = (Component)create.MakeGenericMethod(nativeType).Invoke(actor.Components, [false])!;
    nativeType.GetMethod("Initialize", members)!.Invoke(native, [profile, agent, renderer]); native.Enabled = true;
    phase = "no navigation";
    var tick = nativeType.GetMethod("Tick", members)!;
    var navigate = nativeType.GetMethod("NavigateTo", members)!;
    bool HasTarget() => (bool)nativeType.GetProperty("HasMovementTarget", members)!.GetValue(native)!;
    float RetryWait() => (float)nativeType.GetField("wait", members)!.GetValue(native)!;
    void Step(int count)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tick.Invoke(native, [.02f]);
            scene.GameTick(.02);
        }
    }
    var start = actor.WorldPosition;
    Step(250);
    if (actor.WorldPosition.Distance(start) > .01f || HasTarget() || agent.IsNavigating || renderer.Sequence.Name != (string)Value("Idle") || RetryWait() <= 0)
        throw new Exception("No-nav actor must stay idle in place and schedule another navigation attempt.");
    if ((bool)navigate.Invoke(native, [new Vector3(220, 0, 0), false])!) throw new Exception("No-nav route was accepted.");
    var noNav = new { Sequence = renderer.Sequence.Name, Displacement = actor.WorldPosition.Distance(start), RetrySeconds = RetryWait() };

    scene.NavMesh.IsEnabled = true;
    phase = "open floor navigation";
    scene.NavMesh.CustomBounds = true;
    scene.NavMesh.Bounds = BBox.FromPositionAndSize(new Vector3(0, 0, 100), new Vector3(1050, 480, 260));
    if (!await scene.NavMesh.Generate(scene.PhysicsWorld)) throw new Exception("Test floor navmesh generation failed.");
    Step(2);
    var destination = new Vector3(220, 0, 0);
    if (!(bool)navigate.Invoke(native, [destination, false])!) throw new Exception("Open floor route was rejected.");
    Step(25);
    if (actor.WorldPosition.Distance(start) < 2) throw new Exception("Navmesh became available but pedestrian did not resume moving.");
    // Simulate a path cancelled by a dynamic-world change: the native stale-path timeout must release it.
    agent.Stop(); Step(50);
    if (HasTarget() || RetryWait() <= 0) throw new Exception("Cancelled path was not abandoned with a retry delay.");
    var cancelled = new { HasTarget = HasTarget(), RetrySeconds = RetryWait() };

    var wall = scene.CreateObject(); wall.WorldPosition = new Vector3(0, 0, 100);
    phase = "blocked navigation";
    var wallCollider = wall.Components.Create<BoxCollider>(); wallCollider.Scale = new Vector3(40, 460, 200); wallCollider.Static = true;
    await scene.NavMesh.Generate(scene.PhysicsWorld); Step(2);
    var blocked = !(bool)navigate.Invoke(native, [destination, false])!;
    if (!blocked) throw new Exception("A wall spanning the entire walkable floor did not block the route.");
    wall.Destroy(); scene.GameTick(.02);
    phase = "navigation recovery";
    await scene.NavMesh.Generate(scene.PhysicsWorld); Step(2);
    agent.SetAgentPosition(actor.WorldPosition);
    if (!(bool)navigate.Invoke(native, [destination, false])!) throw new Exception("Route did not recover after removing the wall.");
    var recoveryStart = actor.WorldPosition;
    Step(75);
    var recoveredDistance = actor.WorldPosition.Distance(recoveryStart);
    if (recoveredDistance < 15) throw new Exception("Recovered route produced no actual engine movement.");
    if (activeScene?.Camera != activeCamera) throw new Exception("Navigation probe changed the active scene camera.");
    return new { Passed = true, NoNav = noNav, Cancelled = cancelled, WallRouteRejected = blocked, RecoveredDistance = recoveredDistance, ActiveSceneUnchanged = true };
}
catch(Exception error) { throw new Exception("PedestrianNavigation failed during " + phase, error); }
finally
{
    if (native?.IsValid == true) nativeType.GetMethod("Shutdown", members)!.Invoke(native, null);
    scene.Destroy();
}
