// Verifies a native UE AnimSequence in the actual engine, in a disposable scene.
var activeScene = Game.ActiveScene;
var activeCamera = activeScene?.Camera;
var scene = new Scene();
using (scene.Push())
try
{
    const string path = "mount://sade/animations/sanandreas/environment/props/skr_derrick01/sanandreas/environment/props/skr_derrick01_anim.vmdl";
    const string sequence = "SKR_derrick01_Anim";
    var model = Model.Load(path);
    if (model is null || model.IsError || !model.AnimationNames.Contains(sequence)) throw new Exception("Mounted native animation variant is missing its sequence.");
    var renderer = scene.CreateObject().Components.Create<SkinnedModelRenderer>();
    renderer.Model = model; renderer.UseAnimGraph = false; renderer.Sequence.Name = sequence; renderer.Sequence.Looping = true;
    scene.GameTick(.02);
    renderer.Sequence.Time = 0; renderer.SceneModel.Update(0);
    var first = renderer.GetBoneTransforms(false);
    renderer.Sequence.Time = .75f; renderer.SceneModel.Update(0);
    var second = renderer.GetBoneTransforms(false);
    var changed = new System.Collections.Generic.List<object>();
    foreach(var bone in model.Bones.AllBones)
    {
        var displacement = first[bone.Index].Position.Distance(second[bone.Index].Position);
        var rotation = first[bone.Index].Rotation.Distance(second[bone.Index].Rotation);
        if (displacement > .01f || rotation > .1f) changed.Add(new { bone.Name, Displacement = displacement, RotationDegrees = rotation });
    }
    if (changed.Count < 3) throw new Exception("Native UE sequence did not animate its articulated joints in the engine.");
    if (activeScene?.Camera != activeCamera) throw new Exception("Native animation probe changed the active camera.");
    return new { Passed = true, Path = path, Sequence = sequence, Duration = renderer.Sequence.Duration, ChangedBones = changed, ActiveSceneUnchanged = true };
}
finally { scene.Destroy(); }
