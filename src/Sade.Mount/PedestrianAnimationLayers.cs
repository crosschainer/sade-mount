using Sade.Contracts;

namespace Sade.Mount;

internal static class PedestrianAnimationLayers
{
    internal static AnimationClipData RelaxFreeArm(AnimationClipData clip, AnimationClipData idle, ModelData model)
    {
        var clavicle = Array.FindIndex(model.Bones, b => b.Name.Equals("Bip01_L_Clavicle", StringComparison.OrdinalIgnoreCase));
        if (clavicle < 0 || idle.Frames.Length == 0) return clip;
        var freeArm = new bool[model.Bones.Length];
        for (var i = 0; i < freeArm.Length; i++)
            freeArm[i] = i == clavicle || model.Bones[i].Parent >= 0 && freeArm[model.Bones[i].Parent];
        // The one-handed runtime variant keeps the free arm in its authored idle pose.
        // General animation-bank variants and two-handed/reload clips retain every source track.
        return clip with { Frames = clip.Frames.Select(frame => frame.Select((pose, bone) => freeArm[bone] ? idle.Frames[0][bone] : pose).ToArray()).ToArray() };
    }
}
