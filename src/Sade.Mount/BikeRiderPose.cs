using Sandbox;
using N = System.Numerics;

namespace Sade.Mount;

/// <summary>Fits the seated player's visual skeleton to the authored saddle and handlebar grips.</summary>
internal sealed class BikeRiderPose : IDisposable
{
    private readonly NativeVehicle vehicle;
    private readonly IDisposable hook;
    private readonly Vector3 seat, leftGrip, rightGrip;
    private SkinnedModelRenderer? renderer;
    private Model? model;
    private BoneCollection.Bone[] bones = [];
    private BoneCollection.Bone? pelvis, spine, spine1, spine2, neck;
    private Hand[] hands = [];
    private Dictionary<int, Transform> fingerPose = [];
    private float blend, lean, shoulders, fitRemaining;
    private bool fitted, applied, supported;

    internal static BikeRiderPose? Create(NativeVehicle vehicle)
    {
        if (vehicle.Profile.Data.Family is not ("bike" or "bicycle")) return null;
        var sockets = vehicle.Profile.Data.Sockets;
        var seat = sockets.FirstOrDefault(s => s.Name == "ped_frontseat");
        var left = sockets.FirstOrDefault(s => s.Name == "left_handle");
        var right = sockets.FirstOrDefault(s => s.Name == "right_handle");
        if (seat is null || left is null || right is null) return null;
        return new(vehicle, CoordinateSpace.Position(seat.Transform.Position),
            CoordinateSpace.Position(left.Transform.Position), CoordinateSpace.Position(right.Transform.Position));
    }

    private BikeRiderPose(NativeVehicle vehicle, Vector3 seat, Vector3 leftGrip, Vector3 rightGrip)
    {
        this.vehicle = vehicle; this.seat = seat; this.leftGrip = leftGrip; this.rightGrip = rightGrip;
        // The engine evaluates animation at 0 and invalidates hitboxes at 100.
        hook = vehicle.Scene.AddHook(GameObjectSystem.Stage.UpdateBones, 15, Update, "SADE", "Bike rider posture and grip");
    }

    private void Update()
    {
        var player = vehicle.IsValid && vehicle.Active ? vehicle.Driver : null;
        var body = player is { IsValid: true, Active: true } ? player.Renderer : null;
        if (!body.IsValid() || !body.Active || !body.SceneModel.IsValid() || body.Model is null
            || body.SceneModel.HasBoneOverrides() || body.GetComponent<ModelPhysics>() is { Active: true })
        { Release(); return; }
        if (renderer != body || model != body.Model)
        {
            Release(); renderer = body; model = body.Model;
            bones = model.Bones.AllBones.ToArray();
            pelvis = model.Bones.GetBone("pelvis"); spine = model.Bones.GetBone("spine_0");
            spine1 = model.Bones.GetBone("spine_1"); spine2 = model.Bones.GetBone("spine_2"); neck = model.Bones.GetBone("neck_0");
            hands = new[] { "L", "R" }.Select(s => new Hand(model.Bones, s)).ToArray();
            supported = pelvis is not null && spine is not null && spine1 is not null && spine2 is not null && neck is not null && hands.All(h => h.Valid);
            if (supported) RiderHandPose.TrySample(model, vehicle.Scene.SceneWorld, out fingerPose);
        }
        if (!supported) return;
        var pose = body.GetBoneTransforms(true);
        if (pose.Length != bones.Length) return;
        var delta = Math.Clamp(Time.Delta, 0, .1f);
        blend = Math.Min(1, blend + delta * 4);
        var saddle = vehicle.WorldTransform.PointToWorld(seat);
        var left = vehicle.WorldTransform.PointToWorld(leftGrip);
        var right = vehicle.WorldTransform.PointToWorld(rightGrip);
        // ChairForward puts Human's animated hips 4.5 units behind its root. Align the
        // actual pelvis to GTA's socket without moving the player, chair or camera.
        var offset = saddle - pose[pelvis!.Index].Position;
        if (offset.Length > 18 * Math.Max(body.WorldScale.Length / MathF.Sqrt(3), .1f)) return;
        for (var i = 0; i < pose.Length; i++) pose[i] = pose[i].WithPosition(pose[i].Position + offset * blend);
        var bar = (right - left).Normal;
        var up = vehicle.WorldRotation.Up;
        var forward = vehicle.WorldRotation * Vector3.Left;
        forward = (forward - bar * Vector3.Dot(forward, bar)).Normal;
        var bendAxis = Vector3.Cross(up, forward).Normal;
        foreach (var hand in hands)
        {
            hand.Grip = hand.Left ? left : right;
            hand.Rotation = Rotation.LookAt(forward, Vector3.Cross(forward, bar) * (hand.Left ? 1 : -1));
            var palm = fingerPose.TryGetValue(hand.Hold!.Index, out var local) ? local.Position
                : pose[hand.Wrist!.Index].PointToLocal(pose[hand.Hold.Index].Position);
            hand.Target = hand.Grip - hand.Rotation * (palm * pose[hand.Wrist!.Index].Scale);
            hand.Length = pose[hand.Upper!.Index].Position.Distance(pose[hand.Lower!.Index].Position)
                + pose[hand.Lower.Index].Position.Distance(pose[hand.Wrist.Index].Position);
        }
        fitRemaining -= delta;
        if (!fitted || fitRemaining <= 0)
        {
            (var desiredLean, var desiredShoulders) = Fit(pose, bendAxis, up);
            lean = fitted ? MathX.Lerp(lean, desiredLean, .35f) : desiredLean;
            shoulders = fitted ? MathX.Lerp(shoulders, desiredShoulders, .35f) : desiredShoulders;
            fitted = true; fitRemaining = .15f;
        }
        Bend(pose, bendAxis, lean * blend);
        Rotate(pose, neck!, pose[neck!.Index].Position, Rotation.FromAxis(bendAxis, -lean * blend * .85f));
        foreach (var hand in hands)
        {
            Rotate(pose, hand.Clavicle!, pose[hand.Clavicle!.Index].Position,
                Rotation.FromAxis(up, shoulders * blend * (hand.Left ? -1 : 1)));
            var a = pose[hand.Upper!.Index]; var e = pose[hand.Lower!.Index]; var w = pose[hand.Wrist!.Index];
            var goal = Vector3.Lerp(w.Position, hand.Target, blend);
            var pole = bar * (hand.Left ? -1 : 1) - up * .7f;
            if (!ArmAimSolver.Solve(Vec(a.Position), Vec(e.Position), Vec(w.Position), Vec(goal), Vec(pole), out var elbow, out var wrist)) continue;
            Rotate(pose, hand.Upper, a.Position, Between(e.Position - a.Position, Vec(elbow) - a.Position));
            e = pose[hand.Lower.Index]; w = pose[hand.Wrist.Index];
            Rotate(pose, hand.Lower, e.Position, Between(w.Position - e.Position, Vec(wrist) - e.Position));
            w = pose[hand.Wrist.Index];
            var rotation = Rotation.Slerp(w.Rotation, hand.Rotation, blend);
            Rotate(pose, hand.Wrist, w.Position, rotation * w.Rotation.Inverse);
        }
        foreach (var bone in bones)
        {
            if (bone.Parent is null || !fingerPose.TryGetValue(bone.Index, out var target)) continue;
            var current = body.SceneModel.GetParentSpaceBone(bone.Index);
            var local = current.WithRotation(Rotation.Slerp(current.Rotation, target.Rotation, blend))
                .WithPosition(Vector3.Lerp(current.Position, target.Position, blend));
            pose[bone.Index] = pose[bone.Parent.Index].ToWorld(local);
        }
        // Final render transforms expire at the next animation evaluation; no persistent
        // physics overrides, graph replacement or changes to the chair are needed.
        foreach (var bone in bones) body.SceneModel.SetBoneWorldTransform(bone.Index, pose[bone.Index]);
        SyncBoneObjects(body);
        MergeClothing(body);
        applied = true;
    }

    private (float Lean, float Shoulders) Fit(Transform[] pose, Vector3 axis, Vector3 up)
    {
        var best = float.MaxValue; var bestLean = 0f; var bestShoulders = 0f;
        // Fit only the two shoulder chains. Do not copy/solve the entire skeleton for
        // each candidate, and retain a small elbow bend rather than stretching limbs.
        for (var angle = 0; angle <= 60; angle += 2)
        {
            var rootTurn = Rotation.FromAxis(axis, angle * .7f);
            var midTurn = Rotation.FromAxis(axis, angle * .15f);
            Vector3 Root(Vector3 point) => pose[pelvis!.Index].Position + rootTurn * (point - pose[pelvis.Index].Position);
            var mid = Root(pose[spine1!.Index].Position);
            Vector3 Mid(Vector3 point) => mid + midTurn * (Root(point) - mid);
            var top = Mid(pose[spine2!.Index].Position);
            Vector3 Top(Vector3 point) => top + midTurn * (Mid(point) - top);
            for (var protract = 0; protract <= 30; protract += 2)
            {
                var error = 0f;
                foreach (var hand in hands)
                {
                    var clavicle = Top(pose[hand.Clavicle!.Index].Position);
                    var shoulder = clavicle + Rotation.FromAxis(up, protract * (hand.Left ? -1 : 1)) * (Top(pose[hand.Upper!.Index].Position) - clavicle);
                    error = Math.Max(error, shoulder.Distance(hand.Target) - hand.Length * .95f);
                }
                var cost = angle + protract * 1.3f + Math.Max(0, error) * 1000;
                if (cost >= best) continue;
                best = cost; bestLean = angle; bestShoulders = protract;
            }
        }
        return (bestLean, bestShoulders);
    }

    private void Bend(Transform[] pose, Vector3 axis, float angle)
    {
        Rotate(pose, spine!, pose[pelvis!.Index].Position, Rotation.FromAxis(axis, angle * .7f));
        Rotate(pose, spine1!, pose[spine1!.Index].Position, Rotation.FromAxis(axis, angle * .15f));
        Rotate(pose, spine2!, pose[spine2!.Index].Position, Rotation.FromAxis(axis, angle * .15f));
    }

    private void Rotate(Transform[] pose, BoneCollection.Bone root, Vector3 pivot, Rotation rotation)
    {
        foreach (var bone in bones)
        {
            if (!Descends(bone, root)) continue;
            var current = pose[bone.Index];
            pose[bone.Index] = current.WithPosition(pivot + rotation * (current.Position - pivot)).WithRotation(rotation * current.Rotation);
        }
    }

    private static bool Descends(BoneCollection.Bone bone, BoneCollection.Bone root)
    {
        for (var current = bone; current is not null; current = current.Parent) if (current.Index == root.Index) return true;
        return false;
    }

    private static void MergeClothing(SkinnedModelRenderer parent)
    {
        foreach (var child in parent.GameObject.GetComponentsInChildren<SkinnedModelRenderer>())
        {
            if (child == parent || child.BoneMergeTarget != parent || !child.SceneModel.IsValid()) continue;
            child.SceneModel.Transform = parent.SceneModel.Transform;
            child.SceneModel.MergeBones(parent.SceneModel);
            SyncBoneObjects(child);
            MergeClothing(child);
        }
    }

    private static void SyncBoneObjects(SkinnedModelRenderer body)
    {
        // Attachments and Sandbox's ragdoll handoff read these existing objects. Do not
        // create bones or overwrite explicitly procedural/absolute attachment targets.
        foreach (var bone in body.Model.Bones.AllBones)
        {
            var obj = body.GetBoneObject(bone);
            if (!obj.IsValid() || (obj.Flags & (GameObjectFlags.ProceduralBone | GameObjectFlags.Absolute)) != 0) continue;
            obj.WorldTransform = body.SceneModel.GetBoneWorldTransform(bone.Index);
        }
    }

    private void Release()
    {
        if (applied && renderer.IsValid() && renderer.SceneModel.IsValid() && !renderer.SceneModel.HasBoneOverrides())
        {
            renderer.SceneModel.Update(0);
            SyncBoneObjects(renderer);
            MergeClothing(renderer);
        }
        renderer = null; model = null; blend = lean = shoulders = fitRemaining = 0;
        fitted = applied = supported = false; fingerPose.Clear();
    }

    public void Dispose() { hook.Dispose(); Release(); }
    private static N.Vector3 Vec(Vector3 v) => new(v.x, v.y, v.z);
    private static Vector3 Vec(N.Vector3 v) => new(v.X, v.Y, v.Z);
    private static Rotation Between(Vector3 from, Vector3 to)
    {
        var q = ArmAimSolver.Between(Vec(from), Vec(to)); return new(q.X, q.Y, q.Z, q.W);
    }

    private sealed class Hand(BoneCollection bones, string side)
    {
        internal readonly bool Left = side == "L";
        internal readonly BoneCollection.Bone? Upper = bones.GetBone("arm_upper_" + side), Lower = bones.GetBone("arm_lower_" + side),
            Wrist = bones.GetBone("hand_" + side), Hold = bones.GetBone("hold_" + side), Clavicle = bones.GetBone("clavicle_" + side);
        internal bool Valid => Upper is not null && Lower is not null && Wrist is not null && Hold is not null && Clavicle is not null;
        internal Vector3 Grip, Target;
        internal Rotation Rotation;
        internal float Length;
    }
}
