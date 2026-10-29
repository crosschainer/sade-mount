using Sandbox;
using N = System.Numerics;

namespace Sade.Mount;

internal sealed partial class NativePedestrian
{
    private float aimBlend;
    private bool aimOverrides;

    private void ClearAimOverrides()
    {
        if (!aimOverrides || !Renderer.IsValid() || !Renderer.SceneModel.IsValid()) return;
        Renderer.SceneModel.ClearBoneOverrides();
        aimOverrides = false;
    }

    private void UpdateAimingPose()
    {
        if (!Renderer.IsValid() || !Renderer.SceneModel.IsValid() || Profile.Weapon is not { } weapon) return;
        var shouldAim = !IsDead && ValidCombatTarget(combatTarget) && reloadRemaining <= 0 &&
            (aiming || firingAction || pendingShot) && weapon.Kind is "hitscan" or "shotgun" or "rocket" or "homing_rocket" or "flame" or "spray" or "extinguisher";
        aimBlend = Math.Clamp(aimBlend + (shouldAim ? 1 : -1) * Math.Clamp(Time.Delta, 0, .1f) * 5, 0, 1);
        if (aimBlend <= 0 || !ValidCombatTarget(combatTarget)) return;
        var bones = Renderer.Model.Bones;
        var hand = bones.GetBone(weapon.WeaponBone);
        if (hand is null) return;
        var pose = Renderer.GetBoneTransforms(true);
        var target = TargetPosition(combatTarget!);
        var modelTransform = Renderer.WorldTransform;
        var sceneModel = Renderer.SceneModel;

        // TaskSimpleUseGun::AimGun uses right-arm IK for bAimWithArm; rifles aim with the torso,
        // preserving the authored two-handed hold. The firearm itself stays attached at identity.
        if (weapon.AimWithArm)
        {
            var upper = bones.GetBone("R_UpperArm");
            var fore = bones.GetBone("R_ForeArm");
            if (upper is null || fore is null) return;
            var shoulder = pose[upper.Index];
            var elbow = pose[fore.Index];
            var wrist = pose[hand.Index];
            var direction = (target - shoulder.Position).Normal;
            var armLength = Vector3.DistanceBetween(shoulder.Position, elbow.Position) + Vector3.DistanceBetween(elbow.Position, wrist.Position);
            // Preserve a small elbow bend and the authored elbow's side of the arm plane.
            var pole = elbow.Position - shoulder.Position;
            var goal = shoulder.Position + direction * (armLength * .96f);
            if (!ArmAimSolver.Solve(Vec(shoulder.Position), Vec(elbow.Position), Vec(wrist.Position), Vec(goal), Vec(pole), out var solvedElbow, out var solvedWrist)) return;
            var shoulderDelta = Rot(ArmAimSolver.Between(Vec(elbow.Position - shoulder.Position), solvedElbow - Vec(shoulder.Position)));
            var elbowDelta = Rot(ArmAimSolver.Between(Vec(wrist.Position - elbow.Position), solvedWrist - solvedElbow));
            var newShoulder = shoulder.WithRotation(Rotation.Slerp(shoulder.Rotation, shoulderDelta * shoulder.Rotation, aimBlend));
            var newElbow = elbow.WithPosition(newShoulder.PointToWorld(shoulder.PointToLocal(elbow.Position)))
                .WithRotation(Rotation.Slerp(elbow.Rotation, elbowDelta * elbow.Rotation, aimBlend));
            var aimedRotation = ArmAimSolver.AimMuzzle(Quat(wrist.Rotation), solvedWrist, Vec(weapon.MuzzleOffset * wrist.Scale), Vec(target));
            var newWrist = wrist.WithPosition(newElbow.PointToWorld(elbow.PointToLocal(wrist.Position)))
                .WithRotation(Rotation.Slerp(wrist.Rotation, Rot(aimedRotation), aimBlend));
            foreach (var bone in bones.AllBones)
            {
                Transform corrected;
                if (DescendsFrom(bone, hand)) corrected = newWrist.ToWorld(wrist.ToLocal(pose[bone.Index]));
                else if (DescendsFrom(bone, fore)) corrected = newElbow.ToWorld(elbow.ToLocal(pose[bone.Index]));
                else if (DescendsFrom(bone, upper)) corrected = newShoulder.ToWorld(shoulder.ToLocal(pose[bone.Index]));
                else continue;
                sceneModel.SetBoneOverride(bone.Index, modelTransform.ToLocal(corrected));
            }
        }
        else
        {
            var spine = bones.GetBone("Spine1") ?? bones.GetBone("Spine_2") ?? bones.GetBone("Spine");
            if (spine is null) return;
            var pivot = pose[spine.Index].Position;
            var wrist = pose[hand.Index];
            var correction = Rot(ArmAimSolver.AimTorso(Vec(pivot), Vec(wrist.Position), Quat(wrist.Rotation), Vec(weapon.MuzzleOffset * wrist.Scale), Vec(target)));
            correction = Rotation.Slerp(Rotation.Identity, correction, aimBlend);
            foreach (var bone in bones.AllBones)
            {
                if (!DescendsFrom(bone, spine)) continue;
                var current = pose[bone.Index];
                var corrected = current.WithPosition(pivot + correction * (current.Position - pivot)).WithRotation(correction * current.Rotation);
                sceneModel.SetBoneOverride(bone.Index, modelTransform.ToLocal(corrected));
            }
        }
        aimOverrides = true;
        // Public Update(0) resolves newly submitted model-space overrides in this frame without
        // advancing source clip time. No private engine hooks or reflection are required.
        sceneModel.Update(0);
    }

    private static bool DescendsFrom(BoneCollection.Bone bone, BoneCollection.Bone ancestor)
    {
        for (var current = bone; current is not null; current = current.Parent)
            if (current.Index == ancestor.Index) return true;
        return false;
    }

    private static N.Vector3 Vec(Vector3 v) => new(v.x, v.y, v.z);
    private static Vector3 Vec(N.Vector3 v) => new(v.X, v.Y, v.Z);
    private static N.Quaternion Quat(Rotation r) => new(r.x, r.y, r.z, r.w);
    private static Rotation Rot(N.Quaternion q) => new(q.X, q.Y, q.Z, q.W);
}
