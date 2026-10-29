using Sandbox;

namespace Sade.Mount;

/// <summary>Samples authored fingers without changing the rider's seated animation graph.</summary>
internal static class RiderHandPose
{
    private const string SequenceName = "HoldItem_HandPoses";
    // The authored bank spans frames 1–100. Frame 8 curls the fingers around a small grip.
    private const float GripCycle = 7.0f / 99.0f;

    /// <summary>
    /// Returns parent-local finger and hold transforms for this model. The caller owns the
    /// resulting cache and must discard it when its avatar model changes.
    /// </summary>
    internal static bool TrySample(Model model, SceneWorld world, out Dictionary<int, Transform> pose)
    {
        pose = [];
        if (model is null || model.IsError || !world.IsValid()) return false;

        var bones = model.Bones.AllBones.Where(b =>
            b.Name.StartsWith("finger_", StringComparison.Ordinal) || b.Name is "hold_L" or "hold_R").ToArray();
        if (!bones.Any(b => b.Name.StartsWith("finger_", StringComparison.Ordinal)) ||
            !bones.Any(b => b.Name == "hold_L") || !bones.Any(b => b.Name == "hold_R")) return false;

        SceneModel? sampler = null;
        try
        {
            sampler = new SceneModel(world, model, Transform.Zero)
            {
                RenderingEnabled = false,
                UseAnimGraph = false
            };
            if (!sampler.CurrentSequence.SequenceNames.Contains(SequenceName, StringComparer.Ordinal)) return false;
            sampler.CurrentSequence.Name = SequenceName;
            // Initialize the sequence and finish any initial transition before seeking. This
            // advances only this invisible sampler; it does not tick the scene or rider.
            sampler.Update(1.0f);
            sampler.CurrentSequence.TimeNormalized = GripCycle;
            sampler.Update(0);

            var sampled = new Dictionary<int, Transform>(bones.Length);
            foreach (var bone in bones)
            {
                var local = sampler.GetParentSpaceBone(bone.Index);
                if (!Finite(local)) return false;
                sampled.Add(bone.Index, local);
            }
            pose = sampled;
            return true;
        }
        finally
        {
            sampler?.Delete();
        }
    }

    private static bool Finite(Transform value) =>
        float.IsFinite(value.Position.x) && float.IsFinite(value.Position.y) && float.IsFinite(value.Position.z) &&
        float.IsFinite(value.Rotation.x) && float.IsFinite(value.Rotation.y) &&
        float.IsFinite(value.Rotation.z) && float.IsFinite(value.Rotation.w) &&
        float.IsFinite(value.Scale.x) && float.IsFinite(value.Scale.y) && float.IsFinite(value.Scale.z);
}
