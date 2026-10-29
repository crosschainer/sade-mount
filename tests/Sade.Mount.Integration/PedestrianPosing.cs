// Trusted run_csharp integration probe. Native Rigidbody unfreeze is the same transition
// performed by Sandbox's physgun on first pickup; this does not synthesize mouse input.
var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var nativeType = assembly.GetType("Sade.Mount.NativePedestrian")!;
var runtimeType = assembly.GetType("Sade.Mount.PedestrianRuntime")!;
var members = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var activeScene = Game.ActiveScene;
var activeCamera = activeScene?.Camera;
var scene = new Scene();
var controllers = new System.Collections.Generic.List<Component>();
IDisposable? discovery = null;
var phase = "setup";
using (scene.Push())
try
{
    scene.NavMesh.IsEnabled = false;
    var floor = scene.CreateObject(); floor.WorldPosition = new Vector3(0, 0, -10);
    var floorCollider = floor.Components.Create<BoxCollider>(); floorCollider.Scale = new Vector3(1000, 1000, 20); floorCollider.Static = true;
    const string path = "mount://sade/pedestrians/sk_smoke.prefab";
    if (PrefabFile.Load(path) is null) throw new Exception("Mounted Big Smoke prefab is unavailable.");
    var actor = GameObject.Clone(path) ?? throw new Exception("Mounted prefab did not clone.");
    actor.WorldPosition = new Vector3(0, 0, 5);
    var renderer = actor.GetComponentsInChildren<SkinnedModelRenderer>().Single();
    var profiles = runtimeType.GetField("profiles", statics)!.GetValue(null)!;
    var profile = profiles.GetType().GetProperty("Item")!.GetValue(profiles, [renderer.Model.Name])!;
    var native = Attach(actor, profile);
    Step(native, 3);
    var physics = renderer.GetComponent<ModelPhysics>() ?? throw new Exception("Controller did not create ModelPhysics.");
    if (physics.Bodies.Count < 10 || physics.Joints.Count != physics.Bodies.Count - 1 || physics.MotionEnabled || physics.Bodies.Any(b => b.Component.MotionEnabled))
        throw new Exception("Initial posing rig is not an articulated kinematic body set.");
    if (actor.GetComponent<CapsuleCollider>(true)?.Enabled == true) throw new Exception("Root capsule still blocks individual limb traces.");
    var hand = physics.Bodies.Single(b => b.Component.GameObject.Name == "R_Hand").Component;
    var head = physics.Bodies.Single(b => b.Component.GameObject.Name == "Head").Component;
    phase = "first limb trace";
    var trace = scene.Trace.Ray(head.WorldPosition + Vector3.Up * 40, head.WorldPosition).Run();
    if (!trace.Hit || trace.Component is not Rigidbody || trace.Body is null || trace.GameObject.GetComponentInParent<Component.IDamageable>() != native)
        throw new Exception($"Head trace cannot be grabbed or damaged through its NPC ancestor: hit={trace.Hit}, component={trace.Component?.GetType().Name}, object={trace.GameObject?.Name}.");
    if (!actor.GetComponent<NavMeshAgent>(true).Enabled) throw new Exception("Unposed NPC's navigation agent was disabled.");

    phase = "first limb pickup";
    hand.MotionEnabled = true;
    Step(native, 1);
    if (!actor.Tags.Has("sade_posed", false) || !physics.MotionEnabled || physics.Bodies.Any(b => !b.Component.MotionEnabled))
        throw new Exception("Unfreezing one limb did not activate the full pose rig.");
    var agent = actor.GetComponent<NavMeshAgent>(true);
    if (agent.Enabled || agent.UpdatePosition || agent.UpdateRotation || agent.IsNavigating || renderer.PlaybackRate != 0)
        throw new Exception("Posed NPC still runs navigation or sequence playback.");
    if ((bool)nativeType.GetMethod("NavigateTo", members)!.Invoke(native, [new Vector3(200, 0, 0), false])!)
        throw new Exception("Posed NPC accepted navigation.");
    if ((bool)nativeType.GetMethod("PlayAction", members)!.Invoke(native, ["IDLE_stance", false, 1f])!)
        throw new Exception("Posed NPC accepted an animation action.");

    phase = "limb freeze";
    hand.MotionEnabled = false;
    Step(native, 15);
    if (hand.MotionEnabled || !physics.MotionEnabled || physics.Bodies.Count(b => b.Component.MotionEnabled) != physics.Bodies.Count - 1)
        throw new Exception("Tick reset a deliberately frozen limb or disabled the ragdoll.");
    // Freeze the full posed arrangement for deterministic save/load comparisons.
    foreach (var body in physics.Bodies) body.Component.MotionEnabled = false;
    Step(native, 3);
    var beforeMove = actor.WorldPosition;
    var pelvisBefore = physics.Bodies.Single(b => b.Component.GameObject.Name == "Pelvis").Component.WorldPosition;
    foreach (var body in physics.Bodies) body.Component.WorldPosition += new Vector3(80, 30, 0);
    Step(native, 1);
    if (actor.WorldPosition.Distance(beforeMove + new Vector3(80, 30, 0)) > .1f)
        throw new Exception($"NPC root did not follow the physical pelvis after moving the pose: rootBefore={beforeMove}, rootAfter={actor.WorldPosition}, pelvisBefore={pelvisBefore}, pelvisAfter={physics.Bodies.Single(b => b.Component.GameObject.Name == "Pelvis").Component.WorldPosition}.");
    var snapshot = physics.Bodies.ToDictionary(b => b.Component.GameObject.Name, b => b.Component.WorldTransform);
    var healthBefore = (float)nativeType.GetProperty("Health", members)!.GetValue(native)!;
    ((Component.IDamageable)native).OnDamage(new DamageInfo(1, floor, floor));
    if ((float)nativeType.GetProperty("Health", members)!.GetValue(native)! != healthBefore - 1 || renderer.PlaybackRate != 0)
        throw new Exception("Posed damage did not update health passively.");
    nativeType.GetMethod("SyncWeaponPose", members)!.Invoke(native, null);

    phase = "save and restore";
    var json = actor.Serialize().ToJsonString();
    if (json.Contains("Sade.Mount.NativePedestrian")) throw new Exception("Transient NPC controller leaked into the saved scene.");
    nativeType.GetMethod("Shutdown", members)!.Invoke(native, null);
    actor.Destroy(); scene.GameTick(.02);
    var saved = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
    SceneUtility.MakeIdGuidsUnique(saved);
    var restored = new GameObject(false); restored.Deserialize(saved); restored.Enabled = true;
    // Exercise actual runtime discovery: a posed actor's NavMeshAgent is disabled and
    // absent from the active component index, so its ModelPhysics must lead us to it.
    var stateType = runtimeType.GetNestedType("SceneState", System.Reflection.BindingFlags.NonPublic)!;
    discovery = (IDisposable)Activator.CreateInstance(stateType, members, null, [scene, (Func<bool>)(() => false)], null)!;
    stateType.GetMethod("Discover", members)!.Invoke(discovery, null);
    var restoredNative = restored.Components.GetAll<Component>(FindMode.EverythingInSelf).SingleOrDefault(c => nativeType.IsInstanceOfType(c))
        ?? throw new Exception("Runtime discovery did not restore the posed NPC with its disabled agent.");
    Step(restoredNative, 3);
    var restoredPhysics = restored.GetComponentInChildren<ModelPhysics>();
    if (!restored.Tags.Has("sade_posed", false) || restored.GetComponent<NavMeshAgent>(true).Enabled || !restoredPhysics.MotionEnabled)
        throw new Exception("Restored scene lost persistent pose mode.");
    if (restoredPhysics.Bodies.Count != snapshot.Count || restoredPhysics.Bodies.Any(b => b.Component.MotionEnabled))
        throw new Exception("Restored scene lost per-limb freeze state.");
    float maxError = 0;
    foreach (var body in restoredPhysics.Bodies)
    {
        var expected = snapshot[body.Component.GameObject.Name];
        maxError = Math.Max(maxError, body.Component.WorldPosition.Distance(expected.Position));
        if (body.Component.WorldPosition.Distance(expected.Position) > .15f)
            throw new Exception($"Restored {body.Component.GameObject.Name} moved away from saved pose.");
    }
    if (activeScene?.Camera != activeCamera) throw new Exception("Posing probe changed the active camera.");
    return new { Passed = true, SavedBodies = snapshot.Count, HeadTrace = trace.Component.GetType().Name,
        FirstLimbActivatesPose = true, FrozenLimbPreserved = true, NavigationDisabled = true, PassiveDamage = true,
        DisabledAgentRediscovered = true, SaveRestoreMaximumPositionError = maxError, ActiveSceneUnchanged = true, ActualMouseInputTested = false, MultiplayerTested = false };

    Component Attach(GameObject obj, object useProfile)
    {
        var create = obj.Components.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition);
        var result = (Component)create.MakeGenericMethod(nativeType).Invoke(obj.Components, [false])!;
        controllers.Add(result);
        nativeType.GetMethod("Initialize", members)!.Invoke(result,
            [useProfile, obj.GetComponent<NavMeshAgent>(true), obj.GetComponentInChildren<SkinnedModelRenderer>()]);
        result.Enabled = true;
        return result;
    }
    void Step(Component controller, int count)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (discovery is null) nativeType.GetMethod("Tick", members)!.Invoke(controller, [.02f]);
            scene.GameTick(.02);
        }
    }
}
catch (Exception error) { throw new Exception("PedestrianPosing failed during " + phase + ": " + error.GetBaseException().Message, error); }
finally
{
    discovery?.Dispose();
    foreach (var controller in controllers)
        if (controller.IsValid) nativeType.GetMethod("Shutdown", members)!.Invoke(controller, null);
    scene.Destroy();
}
