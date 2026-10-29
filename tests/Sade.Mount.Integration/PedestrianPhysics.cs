// Trusted run_csharp integration probe after installing the generated articulated rig.
// Uses actual ModelPhysics in a temporary scene; never changes the active map/player/camera.
var activeScene = Game.ActiveScene;
var activeCamera = activeScene?.Camera;
var scene = new Scene();
var results = new System.Collections.Generic.List<object>();
using (scene.Push())
try
{
    var floor = scene.CreateObject(); floor.WorldPosition = new Vector3(0, 0, -12);
    var box = floor.Components.Create<BoxCollider>(); box.Scale = new Vector3(1800, 1800, 24); box.Static = true;
    foreach(var path in new[] {
        "mount://sade/models/sanandreas/characters/player/sk_player_combined.vmdl",
        "mount://sade/models/sanandreas/characters/peds/sk_smoke.vmdl" })
    {
        var model = Model.Load(path);
        if (model is null || model.IsError || model.Physics?.Parts.Count != 16 || model.Physics.Joints.Count != 15)
            throw new Exception(path + ": install the current generated 16-body/15-joint rig before running this test.");
        var actor = scene.CreateObject(); actor.Name = "Temporary articulated pedestrian";
        actor.WorldPosition = Vector3.Up * (-model.Bounds.Mins.z + 24);
        var renderer = actor.Components.Create<SkinnedModelRenderer>(); renderer.Model = model; renderer.UseAnimGraph = false;
        renderer.Sequence.Name = model.AnimationNames.First(n => n.StartsWith("idle", StringComparison.OrdinalIgnoreCase));
        renderer.Sequence.Looping = true;
        Step(2);
        var physics = actor.Components.Create<ModelPhysics>(false);
        physics.Model = model; physics.Renderer = renderer; physics.IgnoreRoot = true; physics.MotionEnabled = false; physics.Enabled = true;
        Step(10);
        if (physics.Bodies.Count != 16 || physics.Joints.Count != 15 || Math.Abs(physics.Bodies.Sum(b=>b.Component.MassOverride) - 80) > .05f)
            throw new Exception(path + $": engine articulated count/mass mismatch: bodies={physics.Bodies.Count}, joints={physics.Joints.Count}, mass={physics.Mass}, sourceMass={model.Physics.Parts.Sum(p=>p.Mass)}.");
        var maxKinematicError = 0f;
        for(var i=0;i<45;i++)
        {
            Step(1);
            var poses = renderer.GetBoneTransforms(true);
            foreach(var body in physics.Bodies)
            {
                var pose = poses[body.Bone];
                maxKinematicError = Math.Max(maxKinematicError, body.Component.WorldPosition.Distance(pose.Position));
                if(body.Component.MotionEnabled) throw new Exception("Idle limb unexpectedly became dynamic.");
            }
        }
        if(maxKinematicError>1.5f) throw new Exception(path + ": keyframed collision is detached from its animated bones: " + maxKinematicError);

        physics.MotionEnabled = true;
        Step(2);
        if (Math.Abs(physics.Mass - 80) > .05f) throw new Exception(path + ": dynamic total mass differs from 80kg.");
        var maxAnchorDrift = 0f; var maxSpread = 0f;
        for(var i=0;i<300;i++)
        {
            Step(1);
            var center = physics.Bodies[0].Component.WorldPosition;
            foreach(var body in physics.Bodies)
            {
                var p = body.Component.WorldPosition;
                if(!p.IsFinite || !body.Component.Velocity.IsFinite || p.z < -20 || p.Length > 1000)
                    throw new Exception(path + ": a limb became nonfinite, fell through the floor, or flew away.");
                maxSpread = Math.Max(maxSpread, p.Distance(center));
            }
            maxAnchorDrift = Math.Max(maxAnchorDrift, AnchorError(physics));
        }
        var settledSpeed = physics.Bodies.Max(b => b.Component.Velocity.Length);
        if(maxSpread>130 || maxAnchorDrift>8 || settledSpeed>100)
            throw new Exception(path + $": unstable articulated fall: spread={maxSpread}, joint drift={maxAnchorDrift}, final speed={settledSpeed}");

        var forearm = physics.Bodies.Single(b => model.Bones.AllBones.Single(bone=>bone.Index==b.Bone).Name == "L_ForeArm").Component;
        var before = forearm.WorldPosition; var goal = before + new Vector3(4, 0, 10);
        forearm.MotionEnabled = false;
        for(var i=1;i<=30;i++) { forearm.WorldPosition = Vector3.Lerp(before,goal,i/30f); Step(1); }
        Step(100);
        var frozenError = forearm.WorldPosition.Distance(goal);
        var posedJointError = AnchorError(physics);
        if(frozenError>.1f || posedJointError>5 || physics.Bodies.Count(b=>b.Component.MotionEnabled)!=15)
            throw new Exception(path + $": frozen limb pose broke articulation: frozen error={frozenError}, joint drift={posedJointError}");
        results.Add(new { Path=path, Bodies=physics.Bodies.Count, Joints=physics.Joints.Count, ConfiguredMass=model.Physics.Parts.Sum(p=>p.Mass), DynamicMassAfterForearmFreeze=physics.Mass,
            MaxKinematicError=maxKinematicError, SixSecondFall=new {MaxSpread=maxSpread,MaxAnchorDrift=maxAnchorDrift,SettledSpeed=settledSpeed},
            FrozenForearmError=frozenError, PosedJointError=posedJointError });
        actor.Destroy(); Step(2);
    }
    if(activeScene?.Camera!=activeCamera) throw new Exception("Articulation test changed the active camera.");
    return new { Passed=true, Pedestrians=results, ActiveSceneUnchanged=true };
    void Step(int count) { for(var i=0;i<count;i++) { cancellationToken.ThrowIfCancellationRequested(); scene.GameTick(.02); } }
    float AnchorError(ModelPhysics physics) => physics.Joints.Max(j =>
        j.Body1.Component.WorldTransform.PointToWorld(j.LocalFrame1.Position).Distance(
        j.Body2.Component.WorldTransform.PointToWorld(j.LocalFrame2.Position)));
}
finally { scene.Destroy(); }
