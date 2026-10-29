// Trusted run_csharp probe. Creates the same built-in Prop as Sandbox's mount spawner,
// then verifies native promotion without a navmesh that could conceal a sideways spawn.
var mount = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var runtime = mount.GetType("Sade.Mount.PedestrianRuntime")!;
var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
var stateType = runtime.GetNestedType("SceneState", System.Reflection.BindingFlags.NonPublic)!;
var activeScene = Game.ActiveScene;
var activeCamera = activeScene?.Camera;
var results = new System.Collections.Generic.List<object>();
var scene = new Scene();
using (scene.Push())
try
{
    scene.NavMesh.IsEnabled = false;
    var floor = scene.CreateObject(); floor.WorldPosition = new Vector3(0, 0, -10);
    var collider = floor.AddComponent<BoxCollider>(); collider.Static = true; collider.Scale = new Vector3(1200, 1200, 20);
    foreach (var path in new[] { "mount://sade/00_featured/00_cj.vmdl", "mount://sade/00_featured/01_big_smoke.vmdl" })
    foreach (var delayed in new[] { false, true })
    {
        var model = Model.Load(path);
        if (model is null || model.IsError) throw new Exception("Mounted pedestrian unavailable: " + path);
        IDisposable? state = null;
        GameObject? actor = null;
        try
        {
            if (!delayed) state = CreateState();
            var spawn = new Transform(new Vector3(25, -40, 0), Rotation.FromYaw(37), 1.25f);
            actor = new GameObject(false, "First-frame pedestrian regression");
            actor.WorldTransform = spawn.WithPosition(spawn.Position + spawn.Up * -model.Bounds.Mins.z * 1.25f);
            actor.AddComponent<Prop>().Model = model;
            actor.Enabled = true;
            // Simulate loading an existing scene whose Prop already had time to run its
            // root-driving ModelPhysics. Fresh spawns must not need this recovery path.
            if (delayed)
            {
                for (var i = 0; i < 17; i++) scene.GameTick(.02);
                state = CreateState();
            }
            var minimumHeadUp = 1f;
            for (var frame = 1; frame <= 20; frame++)
            {
                scene.GameTick(.02);
                if (!actor.Tags.Has("sade_npc", false)) throw new Exception(path + ": not promoted before its first rendered frame.");
                if (actor.WorldRotation.Up.z < .999f) throw new Exception(path + ": inherited the pelvis's tilted basis: " + actor.WorldRotation.Angles());
                var renderer = actor.GetComponentInChildren<SkinnedModelRenderer>();
                renderer.TryGetBoneTransform("Head", out var head); renderer.TryGetBoneTransform("Pelvis", out var pelvis);
                var up = (head.Position - pelvis.Position).Normal.z;
                minimumHeadUp = Math.Min(minimumHeadUp, up);
                if (up < .8f) throw new Exception(path + $": frame {frame} is lying sideways (head-up={up}).");
                var physics = renderer.GetComponent<ModelPhysics>();
                if (physics.MotionEnabled || actor.Tags.Has("sade_posed", false) || physics.Bodies.Any(b => b.Component.MotionEnabled))
                    throw new Exception(path + ": fresh NPC entered a dynamic ragdoll.");
                if (!delayed && actor.WorldPosition.Distance(spawn.Position) > .05f)
                    throw new Exception(path + ": immediate promotion shifted the requested feet position: " + actor.WorldPosition);
            }
            var heading = actor.WorldRotation.Forward.Dot(spawn.Rotation.Forward);
            if (heading < .95f) throw new Exception(path + ": promotion lost the requested spawn heading.");
            results.Add(new { Path = path, DelayedPropRecovery = delayed, UprightFromFirstFrame = true,
                MinimumHeadUp = minimumHeadUp, HeadingDot = heading, Feet = actor.WorldPosition });
        }
        finally { state?.Dispose(); actor?.Destroy(); scene.GameTick(.02); }
    }
    if (activeScene?.Camera != activeCamera) throw new Exception("First-frame regression changed the active camera.");
    return new { Passed = true, Cases = results, ActiveSceneUnchanged = true };
    IDisposable CreateState() => (IDisposable)Activator.CreateInstance(stateType, flags, null, [scene, (Func<bool>)(() => false)], null)!;
}
finally { scene.Destroy(); }
