// Actual mounted chairs, player animation and native rider fitting in a disposable scene.
var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var nativeType = assembly.GetType("Sade.Mount.NativeVehicle")!;
var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var profiles = assembly.GetType("Sade.Mount.VehicleRuntime")!.GetField("profiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
var coordinate = assembly.GetType("Sade.Mount.CoordinateSpace")!;
var active = Game.ActiveScene; var activeCamera = active?.Camera;
var sourcePlayer = Player.FindLocalPlayer()?.Controller?.Renderer;
var scene = new Scene();
var results = new System.Collections.Generic.List<object>();
var controllers = new System.Collections.Generic.List<Component>();
object Read(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
using (scene.Push())
try
{
    var ambient = scene.CreateObject().AddComponent<AmbientLight>(); ambient.Color = Color.White * .8f;
    var sun = scene.CreateObject().AddComponent<DirectionalLight>(); sun.WorldRotation = Rotation.From(new Angles(45, 120, 0)); sun.LightColor = Color.White * 2;
    var camera = scene.CreateObject().AddComponent<CameraComponent>(); camera.FieldOfView = 44;
    foreach (var name in new[] { "nrg500", "sanchez", "bmx", "pcj600", "faggio", "freeway", "bike", "mtbike", "fcr900", "copbike", "bf400", "wayfarer" })
    {
        var folder = name is "bmx" or "bike" or "mtbike" ? "bicycle" : "bike";
        var path = $"mount://sade/vehicles/{folder}/{name}.prefab";
        if (PrefabFile.Load(path) is null) throw new Exception("Missing bike " + name);
        var vehicle = GameObject.Clone(path); var body = vehicle.GetComponent<Rigidbody>(); body.MotionEnabled = false;
        var bike = vehicle.GetComponent<SkinnedModelRenderer>();
        var profile = profiles.GetType().GetProperty("Item")!.GetValue(profiles, [bike.Model.Name])!;
        var data = Read(profile, "Data"); var sockets = ((Array)Read(data, "Sockets")).Cast<object>().ToArray();
        Vector3 Socket(string socketName)
        {
            var socket = sockets.Single(s => (string)Read(s, "Name") == socketName);
            var local = (Transform)coordinate.GetMethod("Transform")!.Invoke(null, [Read(socket, "Transform")])!;
            return vehicle.WorldTransform.ToWorld(local).Position;
        }
        var create = vehicle.Components.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition);
        var controller = (Component)create.MakeGenericMethod(nativeType).Invoke(vehicle.Components, [false])!;
        controllers.Add(controller); nativeType.GetMethod("Initialize", flags)!.Invoke(controller, [profile, body, bike]);
        controller.Enabled = true;
        var playerObject = scene.CreateObject(); playerObject.WorldPosition = new Vector3(10000, 10000, 10000);
        var human = new GameObject(playerObject, true, "Test rider").AddComponent<SkinnedModelRenderer>();
        human.Model = sourcePlayer?.Model ?? Model.Load("models/citizen_human/human.vmdl");
        human.CreateBoneObjects = true;
        if (sourcePlayer.IsValid())
        {
            human.BodyGroups = sourcePlayer.BodyGroups;
            foreach (var garment in sourcePlayer.GameObject.GetComponentsInChildren<SkinnedModelRenderer>().Where(r => r.BoneMergeTarget == sourcePlayer))
            {
                var copy = new GameObject(human.GameObject, true, "Test outfit").AddComponent<SkinnedModelRenderer>();
                copy.Model = garment.Model; copy.BodyGroups = garment.BodyGroups; copy.Tint = garment.Tint; copy.MaterialGroup = garment.MaterialGroup; copy.BoneMergeTarget = human;
            }
        }
        var player = playerObject.AddComponent<PlayerController>(); player.Renderer = human; player.UseInputControls = false; player.UseCameraControls = false;
        player.Components.Create<Sandbox.Movement.SitMoveMode>();
        for (var frame = 0; frame < 3; frame++) scene.GameTick(.02);
        var chair = vehicle.GetComponentsInChildren<BaseChair>().Single(c => c.GameObject.Tags.Has("sade_vehicle_driver", false));
        chair.Sit(player);
        // Native Tick discovers the chair occupant; UpdateBones applies the rider pose itself.
        using var tick = scene.AddHook(GameObjectSystem.Stage.StartUpdate, 15,
            () => nativeType.GetMethod("Tick", flags)!.Invoke(controller, [.02f]), "SADE tests", "Bike occupant discovery");
        float maximumPalmError = 0, maximumPelvisError = 0, maximumLengthError = 0, clothingError = 0, attachmentError = 0;
        var sampleCount = 0;
        for (var frame = 0; frame < 130; frame++)
        {
            if (frame >= 65)
            {
                vehicle.WorldPosition = new Vector3(frame * 1.3f, -frame * .6f, 15);
                vehicle.WorldRotation = Rotation.From(new Angles(0, frame * 1.7f, MathF.Sin(frame * .08f) * 15));
            }
            scene.GameTick(.02);
            if (frame < 45) continue;
            sampleCount++;
            maximumPelvisError = Math.Max(maximumPelvisError, Bone("pelvis").Position.Distance(Socket("ped_frontseat")));
            foreach (var (side, suffix) in new[] { ("left", "L"), ("right", "R") })
            {
                maximumPalmError = Math.Max(maximumPalmError, Bone("hold_" + suffix).Position.Distance(Socket(side + "_handle")));
                attachmentError = Math.Max(attachmentError, human.GetBoneObject("hand_" + suffix).WorldPosition.Distance(Bone("hand_" + suffix).Position));
                var upper = Bone("arm_upper_" + suffix); var lower = Bone("arm_lower_" + suffix); var hand = Bone("hand_" + suffix);
                human.TryGetBoneTransformAnimation(human.Model.Bones.GetBone("arm_upper_" + suffix), out var animatedUpper);
                human.TryGetBoneTransformAnimation(human.Model.Bones.GetBone("arm_lower_" + suffix), out var animatedLower);
                human.TryGetBoneTransformAnimation(human.Model.Bones.GetBone("hand_" + suffix), out var animatedHand);
                maximumLengthError = Math.Max(maximumLengthError, Math.Abs(upper.Position.Distance(lower.Position) - animatedUpper.Position.Distance(animatedLower.Position)));
                maximumLengthError = Math.Max(maximumLengthError, Math.Abs(lower.Position.Distance(hand.Position) - animatedLower.Position.Distance(animatedHand.Position)));
            }
            foreach (var garment in human.GameObject.GetComponentsInChildren<SkinnedModelRenderer>().Where(r => r.BoneMergeTarget == human))
            {
                var bone = garment.Model.Bones.GetBone("spine_2");
                if (bone is not null) clothingError = Math.Max(clothingError, garment.SceneModel.GetBoneWorldTransform(bone.Index).Position.Distance(Bone("spine_2").Position));
            }
        }
        if (maximumPalmError > .6f || maximumPelvisError > .01f || maximumLengthError > .01f || clothingError > .1f || attachmentError > .01f)
            throw new Exception($"{name}: palm={maximumPalmError}, saddle={maximumPelvisError}, arm length={maximumLengthError}, clothing={clothingError}, attachment={attachmentError}");
        string? image = null;
        if (name is "nrg500" or "sanchez" or "bmx")
        {
            vehicle.WorldTransform = Transform.Zero; scene.GameTick(.02);
            camera.WorldPosition = new Vector3(-130, 100, 65); camera.WorldRotation = Rotation.LookAt(new Vector3(0, 0, 25) - camera.WorldPosition);
            using var bitmap = new Bitmap(1200, 900); camera.RenderToBitmap(bitmap, false);
            image = System.IO.Path.Combine(exchangeDirectory, name + "-rider.png"); System.IO.File.WriteAllBytes(image, bitmap.ToPng());
        }
        var beforeExit = player.GameObject.WorldTransform;
        chair.Eject(player); scene.GameTick(.02); scene.GameTick(.02);
        human.TryGetBoneTransformAnimation(human.Model.Bones.GetBone("spine_2"), out var normalSpine);
        var exitError = normalSpine.Position.Distance(Bone("spine_2").Position);
        if (exitError > .01f || human.SceneModel.HasBoneOverrides()) throw new Exception(name + ": riding pose survived exiting.");
        results.Add(new { name, Samples = sampleCount, maximumPalmError, maximumPelvisError, maximumLengthError, clothingError, attachmentError, exitError, image });
        nativeType.GetMethod("Shutdown", flags)!.Invoke(controller, null); controllers.Remove(controller);
        playerObject.Destroy(); vehicle.Destroy(); scene.GameTick(.02);
        Transform Bone(string boneName) => human.SceneModel.GetBoneWorldTransform(human.Model.Bones.GetBone(boneName).Index);
    }
    if (active?.Camera != activeCamera) throw new Exception("Active camera changed.");
    return new { Passed = results.Count, ActiveSceneUnchanged = true, Bikes = results };
}
finally
{
    foreach (var controller in controllers) if (controller.IsValid) nativeType.GetMethod("Shutdown", flags)!.Invoke(controller, null);
    scene.Destroy();
}
