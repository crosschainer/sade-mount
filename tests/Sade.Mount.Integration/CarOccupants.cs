// Real mounted cars and engine chairs in a disposable scene. No input injection or active-map edits.
var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var nativeType = assembly.GetType("Sade.Mount.NativeVehicle")!;
var runtime = assembly.GetType("Sade.Mount.VehicleRuntime")!;
var coordinate = assembly.GetType("Sade.Mount.CoordinateSpace")!;
var members = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var statics = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
var profiles = runtime.GetField("profiles", statics)!.GetValue(null)!;
var getCabin = runtime.GetMethod("TryGetCabin", statics) ?? throw new Exception("Install the current car occupant pose build first.");
var active = Game.ActiveScene; var activeCamera = active?.Camera;
var sourcePlayer = active?.GetAllComponents<PlayerController>().FirstOrDefault(p => p.IsValid && !p.IsProxy)?.Renderer;
var scene = new Scene();
var results = new System.Collections.Generic.List<object>();
var failures = new System.Collections.Generic.List<string>();
var controllers = new System.Collections.Generic.List<Component>();
object? Read(object value, string name) => value.GetType().GetProperty(name, members)!.GetValue(value);
Vector3 Convert(object value) => (Vector3)coordinate.GetMethod("Position", statics)!.Invoke(null, [value])!;
using (scene.Push())
try
{
    var ambient = scene.CreateObject().AddComponent<AmbientLight>(); ambient.Color = Color.White * .8f;
    var sun = scene.CreateObject().AddComponent<DirectionalLight>(); sun.WorldRotation = Rotation.From(new Angles(45, 120, 0)); sun.LightColor = Color.White * 2;
    var camera = scene.CreateObject().AddComponent<CameraComponent>(); camera.FieldOfView = 48;
    foreach (var name in new[] { "infernus", "premier", "banshee", "landstal", "linerun", "bus", "savanna" })
    {
        GameObject? vehicle = null;
        Component? controller = null;
        var occupants = new System.Collections.Generic.List<(BaseChair Chair, PlayerController Player, SkinnedModelRenderer Human, object? Seat, bool Driver)>();
        try
        {
        var path = $"mount://sade/vehicles/road/{name}.prefab";
        if (PrefabFile.Load(path) is null) throw new Exception("Missing car " + name);
        vehicle = GameObject.Clone(path);
        var body = vehicle.GetComponent<Rigidbody>();
        var car = vehicle.GetComponent<SkinnedModelRenderer>();
        var profile = profiles.GetType().GetProperty("Item")!.GetValue(profiles, [car.Model.Name])!;
        var cabinArgs = new object?[] { name, null };
        if (!(bool)getCabin.Invoke(null, cabinArgs)! || cabinArgs[1] is not { } cabin)
            throw new Exception(name + ": no extracted cabin targets.");
        var seats = ((Array)Read(cabin, "Seats")!).Cast<object>().ToArray();
        var steering = Read(cabin, "Steering");
        if (steering is null) throw new Exception(name + ": no steering grips.");
        var create = vehicle.Components.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition);
        controller = (Component)create.MakeGenericMethod(nativeType).Invoke(vehicle.Components, [false])!;
        controllers.Add(controller);
        nativeType.GetMethod("Initialize", members)!.Invoke(controller, [profile, body, car]);
        controller.Enabled = true; body.MotionEnabled = false;
        foreach (var chair in vehicle.GetComponentsInChildren<BaseChair>())
        {
            var pelvis = chair.GameObject.LocalPosition + Vector3.Up * 22;
            var seat = seats.FirstOrDefault(s => Convert(Read(s, "Pelvis")!).Distance(pelvis) < .1f);
            var playerObject = scene.CreateObject(); playerObject.Name = "Test car occupant";
            playerObject.WorldPosition = new Vector3(10000 + occupants.Count * 150, 10000, 10000);
            var human = new GameObject(playerObject, true, "Test occupant renderer").AddComponent<SkinnedModelRenderer>();
            human.Model = sourcePlayer?.Model ?? Model.Load("models/citizen_human/human.vmdl");
            human.CreateBoneObjects = true;
            if (sourcePlayer.IsValid())
            {
                human.BodyGroups = sourcePlayer.BodyGroups;
                foreach (var garment in sourcePlayer.GameObject.GetComponentsInChildren<SkinnedModelRenderer>().Where(r => r.BoneMergeTarget == sourcePlayer))
                {
                    var copy = new GameObject(human.GameObject, true, "Test outfit").AddComponent<SkinnedModelRenderer>();
                    copy.Model = garment.Model; copy.BodyGroups = garment.BodyGroups; copy.Tint = garment.Tint;
                    copy.MaterialGroup = garment.MaterialGroup; copy.BoneMergeTarget = human;
                }
            }
            var player = playerObject.AddComponent<PlayerController>(); player.Renderer = human;
            player.UseInputControls = false; player.UseCameraControls = false;
            player.Components.Create<Sandbox.Movement.SitMoveMode>();
            for (var frame = 0; frame < 3; frame++) scene.GameTick(.02);
            chair.Sit(player);
            occupants.Add((chair, player, human, seat, chair.GameObject.Tags.Has("sade_vehicle_driver", false)));
        }
        using var tick = scene.AddHook(GameObjectSystem.Stage.StartUpdate, 15,
            () => nativeType.GetMethod("Tick", members)!.Invoke(controller, [.02f]), "SADE tests", "Car occupant discovery");
        // Check the actual pose step, independent of movement/animation updates earlier in the frame.
        var nodeTransforms = new System.Collections.Generic.Dictionary<GameObject, Transform>();
        float maximumNodeMove = 0, minimumNodeRotationDot = 1;
        using var beforePose = scene.AddHook(GameObjectSystem.Stage.UpdateBones, 14, () =>
        {
            nodeTransforms.Clear();
            foreach (var occupant in occupants)
                foreach (var obj in new[] { occupant.Player.GameObject, occupant.Human.GameObject, occupant.Chair.GameObject })
                    nodeTransforms[obj] = obj.WorldTransform;
        }, "SADE tests", "Record seat and player transforms");
        using var afterPose = scene.AddHook(GameObjectSystem.Stage.UpdateBones, 16, () =>
        {
            foreach (var pair in nodeTransforms)
            {
                maximumNodeMove = Math.Max(maximumNodeMove, pair.Value.Position.Distance(pair.Key.WorldPosition));
                var a = pair.Value.Rotation; var b = pair.Key.WorldRotation;
                minimumNodeRotationDot = Math.Min(minimumNodeRotationDot, Math.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w));
            }
        }, "SADE tests", "Check visual-only seat fitting");
        float maximumPalmError = 0, maximumFootError = 0, maximumPelvisError = 0, maximumLengthError = 0;
        float maximumClothingError = 0, maximumAttachmentError = 0, maximumPassengerHandError = 0, minimumToeForward = 1;
        float maximumUnsupportedPoseError = 0, minimumUnsupportedRotationDot = 1;
        var samples = 0;
        for (var frame = 0; frame < 130; frame++)
        {
            if (frame >= 65)
            {
                vehicle.WorldPosition = new Vector3(frame * 1.3f, -frame * .6f, 15);
                vehicle.WorldRotation = Rotation.From(new Angles(MathF.Sin(frame * .04f) * 5, frame * 1.7f, MathF.Sin(frame * .08f) * 9));
            }
            scene.GameTick(.02);
            if (frame < 45) continue;
            samples++;
            foreach (var occupant in occupants)
            {
                var human = occupant.Human;
                if (occupant.Seat is null)
                {
                    // Incomplete cabin geometry must leave the stock animation alone.
                    // This is an explicit coverage result, not an invented foot target.
                    foreach (var boneName in new[] { "pelvis", "spine_2", "hold_L", "hold_R", "leg_upper_L", "leg_upper_R", "ankle_L", "ankle_R", "ball_L", "ball_R" })
                    {
                        human.TryGetBoneTransformAnimation(human.Model.Bones.GetBone(boneName), out var ordinary);
                        var actual = Bone(human, boneName);
                        maximumUnsupportedPoseError = Math.Max(maximumUnsupportedPoseError, actual.Position.Distance(ordinary.Position));
                        var a = actual.Rotation; var b = ordinary.Rotation;
                        minimumUnsupportedRotationDot = Math.Min(minimumUnsupportedRotationDot, Math.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w));
                    }
                    continue;
                }
                var pelvis = World(Read(occupant.Seat, "Pelvis")!);
                maximumPelvisError = Math.Max(maximumPelvisError, Bone(human, "pelvis").Position.Distance(pelvis));
                foreach (var (side, suffix) in new[] { ("Left", "L"), ("Right", "R") })
                {
                    var foot = Bone(human, "ankle_" + suffix);
                    maximumFootError = Math.Max(maximumFootError, foot.Position.Distance(World(Read(occupant.Seat, side + "Foot")!)));
                    if (occupant.Driver)
                        maximumPalmError = Math.Max(maximumPalmError, Bone(human, "hold_" + suffix).Position.Distance(World(Read(steering, side)!)));
                    else
                    {
                        human.TryGetBoneTransformAnimation(human.Model.Bones.GetBone("hold_" + suffix), out var originalHand);
                        human.TryGetBoneTransformAnimation(human.Model.Bones.GetBone("pelvis"), out var originalPelvis);
                        maximumPassengerHandError = Math.Max(maximumPassengerHandError,
                            (Bone(human, "hold_" + suffix).Position - Bone(human, "pelvis").Position).Distance(originalHand.Position - originalPelvis.Position));
                    }
                    foreach (var chain in new[] { new[] { "arm_upper_", "arm_lower_", "hand_" }, new[] { "leg_upper_", "leg_lower_", "ankle_" } })
                    {
                        var upper = Bone(human, chain[0] + suffix); var lower = Bone(human, chain[1] + suffix); var end = Bone(human, chain[2] + suffix);
                        human.TryGetBoneTransformAnimation(human.Model.Bones.GetBone(chain[0] + suffix), out var originalUpper);
                        human.TryGetBoneTransformAnimation(human.Model.Bones.GetBone(chain[1] + suffix), out var originalLower);
                        human.TryGetBoneTransformAnimation(human.Model.Bones.GetBone(chain[2] + suffix), out var originalEnd);
                        maximumLengthError = Math.Max(maximumLengthError, Math.Abs(upper.Position.Distance(lower.Position) - originalUpper.Position.Distance(originalLower.Position)));
                        maximumLengthError = Math.Max(maximumLengthError, Math.Abs(lower.Position.Distance(end.Position) - originalLower.Position.Distance(originalEnd.Position)));
                        maximumAttachmentError = Math.Max(maximumAttachmentError, human.GetBoneObject(chain[2] + suffix).WorldPosition.Distance(end.Position));
                    }
                    var toe = human.Model.Bones.GetBone("ball_" + suffix);
                    if (toe is null) throw new Exception("Human foot has no ball bone to verify its forward orientation.");
                    var toeDirection = human.SceneModel.GetBoneWorldTransform(toe.Index).Position - foot.Position;
                    var up = vehicle.WorldRotation.Up;
                    toeDirection = (toeDirection - up * Vector3.Dot(toeDirection, up)).Normal;
                    minimumToeForward = Math.Min(minimumToeForward, Vector3.Dot(toeDirection, vehicle.WorldRotation * Vector3.Left));
                }
                foreach (var garment in human.GameObject.GetComponentsInChildren<SkinnedModelRenderer>().Where(r => r.BoneMergeTarget == human))
                    foreach (var boneName in new[] { "spine_2", "leg_lower_L", "ankle_R", "hand_L" })
                    {
                        var bone = garment.Model.Bones.GetBone(boneName);
                        if (bone is not null) maximumClothingError = Math.Max(maximumClothingError, garment.SceneModel.GetBoneWorldTransform(bone.Index).Position.Distance(Bone(human, boneName).Position));
                    }
            }
        }
        var images = new System.Collections.Generic.List<string>();
        if (name is "banshee" or "infernus")
        {
            vehicle.WorldTransform = Transform.Zero; scene.GameTick(.02);
            var focus = occupants.Single(o => o.Driver).Chair.WorldPosition + Vector3.Up * 37;
            foreach (var (view, offset) in new[] { ("side", new Vector3(-125, 15, 45)), ("front", new Vector3(-70, 115, 75)) })
            {
                camera.WorldPosition = focus + offset; camera.WorldRotation = Rotation.LookAt(focus - camera.WorldPosition);
                using var bitmap = new Bitmap(1200, 900); camera.RenderToBitmap(bitmap, false);
                var image = System.IO.Path.Combine(exchangeDirectory, name + "-occupants-" + view + ".png");
                System.IO.File.WriteAllBytes(image, bitmap.ToPng()); images.Add(image);
            }
        }
        float maximumExitError = 0;
        foreach (var occupant in occupants)
        {
            occupant.Chair.Eject(occupant.Player); scene.GameTick(.02); scene.GameTick(.02);
            foreach (var boneName in new[] { "spine_2", "hold_L", "hold_R", "ankle_L", "ankle_R" })
            {
                occupant.Human.TryGetBoneTransformAnimation(occupant.Human.Model.Bones.GetBone(boneName), out var normal);
                maximumExitError = Math.Max(maximumExitError, normal.Position.Distance(Bone(occupant.Human, boneName).Position));
            }
            if (occupant.Human.SceneModel.HasBoneOverrides()) failures.Add(name + ": persistent bone overrides after exit.");
        }
        if (maximumPalmError > .6f || maximumFootError > 1 || maximumPelvisError > .05f || maximumLengthError > .015f || maximumClothingError > .1f || maximumAttachmentError > .02f || maximumNodeMove > .001f || minimumNodeRotationDot < .99999f || maximumPassengerHandError > .1f || minimumToeForward < .7f || maximumExitError > .02f || maximumUnsupportedPoseError > .02f || minimumUnsupportedRotationDot < .99999f)
            failures.Add($"{name}: palms={maximumPalmError}, feet={maximumFootError}, pelvis={maximumPelvisError}, limb lengths={maximumLengthError}, clothes={maximumClothingError}, attachments={maximumAttachmentError}, node move={maximumNodeMove}, node rotation dot={minimumNodeRotationDot}, passenger hands={maximumPassengerHandError}, toes forward={minimumToeForward}, exit={maximumExitError}, unsupported pose={maximumUnsupportedPoseError}, unsupported rotation dot={minimumUnsupportedRotationDot}");
        var unsupportedSeats = occupants.Where(o => o.Seat is null).Select(o => new { o.Chair.GameObject.Name, o.Driver, Position = o.Chair.GameObject.LocalPosition }).ToArray();
        // The bus can contain passenger sockets without usable floor geometry. The
        // selected passenger cars and truck must still exercise real driver grips.
        if (name != "bus" && !occupants.Any(o => o.Driver && o.Seat is not null)) failures.Add(name + ": representative driver was not fitted.");
        results.Add(new { name, Occupants = occupants.Count, FittedSeats = occupants.Count(o => o.Seat is not null), UnsupportedSeats = unsupportedSeats, samples, maximumPalmError, maximumFootError, maximumPelvisError, maximumLengthError, maximumClothingError, maximumAttachmentError, maximumNodeMove, minimumNodeRotationDot, maximumPassengerHandError, minimumToeForward, maximumExitError, maximumUnsupportedPoseError, minimumUnsupportedRotationDot, images });
        Vector3 World(object source) => vehicle.WorldTransform.PointToWorld(Convert(source));
        }
        catch (Exception error)
        {
            failures.Add(name + ": " + error.Message);
            results.Add(new { name, Error = error.ToString() });
        }
        finally
        {
            if (controller?.IsValid == true) nativeType.GetMethod("Shutdown", members)!.Invoke(controller, null);
            if (controller is not null) controllers.Remove(controller);
            foreach (var occupant in occupants) if (occupant.Player.IsValid) occupant.Player.GameObject.Destroy();
            if (vehicle.IsValid()) vehicle.Destroy();
            scene.GameTick(.02);
        }
    }
    if (active?.Camera != activeCamera) throw new Exception("Active camera changed.");
    return new { Passed = failures.Count == 0, ActiveSceneUnchanged = true, Cars = results, Failures = failures, Scope = "Human avatar, local engine chairs and visual fitting; no multiplayer or keyboard input assertion." };
}
finally
{
    foreach (var controller in controllers) if (controller.IsValid) nativeType.GetMethod("Shutdown", members)!.Invoke(controller, null);
    scene.Destroy();
}
Transform Bone(SkinnedModelRenderer human, string name) => human.SceneModel.GetBoneWorldTransform(human.Model.Bones.GetBone(name).Index);
