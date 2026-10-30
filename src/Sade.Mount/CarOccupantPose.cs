using Sandbox;
using N = System.Numerics;

namespace Sade.Mount;

/// <summary>Fits seated occupants to the car's wheel and footwell without moving their controller or camera.</summary>
internal sealed class CarOccupantPose : IDisposable
{
    private readonly NativeVehicle vehicle;
    private readonly BaseChair chair;
    private readonly IDisposable hook;
    private readonly Vector3 seat, leftFoot, rightFoot;
    private readonly SteeringGripData? steering;
    private SkinnedModelRenderer? renderer;
    private Model? model;
    private BoneCollection.Bone[] bones = [];
    private BoneCollection.Bone? pelvis, spine, spine1, spine2, neck;
    private Hand[] hands = [];
    private Leg[] legs = [];
    private Dictionary<int, Transform> fingerPose = [];
    private float blend, lean, shoulders, fitRemaining;
    private bool supported, fitHands, fitted, applied;

    internal CarOccupantPose(NativeVehicle vehicle, BaseChair chair, CarSeatData seat, SteeringGripData? steering)
    {
        this.vehicle = vehicle; this.chair = chair; this.steering = steering;
        this.seat = CoordinateSpace.Position(seat.Pelvis);
        leftFoot = CoordinateSpace.Position(seat.LeftFoot);
        rightFoot = CoordinateSpace.Position(seat.RightFoot);
        // Animation is evaluated at 0; hitboxes consume the finished pose at 100.
        hook = vehicle.Scene.AddHook(GameObjectSystem.Stage.UpdateBones, 15, Update, "SADE", "Car occupant posture and grip");
    }

    private void Update()
    {
        var player = vehicle.IsValid && vehicle.Active && chair.IsValid && chair.Active ? chair.GetOccupant() : null;
        var body = player is { IsValid: true, Active: true } ? player.Renderer : null;
        if (!body.IsValid() || !body.Active || !body.SceneModel.IsValid() || body.Model is null
            || body.SceneModel.HasBoneOverrides() || body.GetComponent<ModelPhysics>() is { Active: true })
        { Release(); return; }
        if (renderer != body || model != body.Model) Bind(body);
        if (!supported) return;
        var pose = body.GetBoneTransforms(true);
        if (pose.Length != bones.Length) return;

        var delta = Math.Clamp(Time.Delta, 0, .1f);
        blend = Math.Min(1, blend + delta * 4);
        var offset = vehicle.WorldTransform.PointToWorld(seat) - pose[pelvis!.Index].Position;
        // Human's chair animation leaves its hips behind the chair origin. Fit the
        // visual pelvis to the authored cushion while retaining all controller transforms.
        var scale = Math.Max(body.WorldScale.Length / MathF.Sqrt(3), .1f);
        if (offset.Length > 18 * scale) { Release(); return; }
        for (var i = 0; i < pose.Length; i++) pose[i] = pose[i].WithPosition(pose[i].Position + offset * blend);

        var up = vehicle.WorldRotation.Up;
        var forward = vehicle.WorldRotation * Vector3.Left; // GTA cars face local +Y.
        var right = vehicle.WorldRotation * Vector3.Forward;
        FitLegs(pose, forward, right, up);
        if (fitHands) FitHands(pose, body, forward, right, up, delta);

        // These are render transforms, not persistent physics bone overrides. The
        // next animation evaluation starts from the ordinary seated animation again.
        foreach (var bone in bones) body.SceneModel.SetBoneWorldTransform(bone.Index, pose[bone.Index]);
        SyncBoneObjects(body);
        MergeClothing(body);
        applied = true;
    }

    private void Bind(SkinnedModelRenderer body)
    {
        Release(); renderer = body; model = body.Model;
        bones = model.Bones.AllBones.ToArray();
        pelvis = model.Bones.GetBone("pelvis");
        spine = model.Bones.GetBone("spine_0"); spine1 = model.Bones.GetBone("spine_1");
        spine2 = model.Bones.GetBone("spine_2"); neck = model.Bones.GetBone("neck_0");
        legs = new[] { "L", "R" }.Select(s => new Leg(model.Bones, s)).ToArray();
        hands = new[] { "L", "R" }.Select(s => new Hand(model.Bones, s)).ToArray();
        supported = pelvis is not null && legs.All(l => l.Valid);
        fitHands = supported && steering is not null && spine is not null && spine1 is not null
            && spine2 is not null && neck is not null && hands.All(h => h.Valid);
        if (fitHands) RiderHandPose.TrySample(model, vehicle.Scene.SceneWorld, out fingerPose);
    }

    private void FitLegs(Transform[] pose, Vector3 forward, Vector3 right, Vector3 up)
    {
        var facing = Rotation.LookAt(forward, up);
        foreach (var leg in legs)
        {
            var target = vehicle.WorldTransform.PointToWorld(leg.Left ? leftFoot : rightFoot);
            var hip = pose[leg.Upper!.Index]; var knee = pose[leg.Lower!.Index]; var ankle = pose[leg.Ankle!.Index];
            var goal = Vector3.Lerp(ankle.Position, target, blend);
            var pole = forward + up * .2f + right * (leg.Left ? -.12f : .12f);
            if (!ArmAimSolver.Solve(Vec(hip.Position), Vec(knee.Position), Vec(ankle.Position), Vec(goal), Vec(pole), out var solvedKnee, out var solvedAnkle)) continue;
            Rotate(pose, leg.Upper, hip.Position, Between(knee.Position - hip.Position, Vec(solvedKnee) - hip.Position));
            knee = pose[leg.Lower.Index]; ankle = pose[leg.Ankle.Index];
            Rotate(pose, leg.Lower, knee.Position, Between(ankle.Position - knee.Position, Vec(solvedAnkle) - knee.Position));
            ankle = pose[leg.Ankle.Index];
            var rotation = Rotation.Slerp(ankle.Rotation, facing * leg.ForwardRotation, blend);
            Rotate(pose, leg.Ankle, ankle.Position, rotation * ankle.Rotation.Inverse);
            if (leg.Ball is not null)
            {
                // Keep the authored ankle-to-sole slope. A raw Euler rotation on the
                // ankle points Human's toes down because its foot bone axes are tilted.
                var current = pose[leg.Ball.Index];
                var desired = pose[leg.Ankle.Index].ToWorld(leg.BallLocal);
                var turn = Rotation.Slerp(current.Rotation, desired.Rotation, blend) * current.Rotation.Inverse;
                Rotate(pose, leg.Ball, current.Position, turn);
            }
        }
    }

    private void FitHands(Transform[] pose, SkinnedModelRenderer body, Vector3 forward, Vector3 right, Vector3 up, float delta)
    {
        var wheel = steering!;
        var normal = (vehicle.WorldRotation * CoordinateSpace.Direction(wheel.Normal)).Normal;
        var tangent = (vehicle.WorldRotation * CoordinateSpace.Direction(wheel.Up)).Normal;
        var reach = -normal;
        var bendAxis = Vector3.Cross(up, forward).Normal;
        foreach (var hand in hands)
        {
            var grip = vehicle.WorldTransform.PointToWorld(CoordinateSpace.Position(hand.Left ? wheel.Left : wheel.Right));
            hand.Rotation = Rotation.LookAt(reach, Vector3.Cross(reach, tangent) * (hand.Left ? 1 : -1));
            var palm = fingerPose.TryGetValue(hand.Hold!.Index, out var local) ? local.Position
                : pose[hand.Wrist!.Index].PointToLocal(pose[hand.Hold.Index].Position);
            hand.Target = grip - hand.Rotation * (palm * pose[hand.Wrist!.Index].Scale);
            hand.Length = pose[hand.Upper!.Index].Position.Distance(pose[hand.Lower!.Index].Position)
                + pose[hand.Lower.Index].Position.Distance(pose[hand.Wrist.Index].Position);
        }
        fitRemaining -= delta;
        if (!fitted || fitRemaining <= 0)
        {
            (var desiredLean, var desiredShoulders) = FitTorso(pose, bendAxis, up);
            lean = fitted ? MathX.Lerp(lean, desiredLean, .35f) : desiredLean;
            shoulders = fitted ? MathX.Lerp(shoulders, desiredShoulders, .35f) : desiredShoulders;
            fitted = true; fitRemaining = .15f;
        }
        Rotate(pose, spine!, pose[pelvis!.Index].Position, Rotation.FromAxis(bendAxis, lean * blend * .7f));
        Rotate(pose, spine1!, pose[spine1!.Index].Position, Rotation.FromAxis(bendAxis, lean * blend * .15f));
        Rotate(pose, spine2!, pose[spine2!.Index].Position, Rotation.FromAxis(bendAxis, lean * blend * .15f));
        Rotate(pose, neck!, pose[neck!.Index].Position, Rotation.FromAxis(bendAxis, -lean * blend * .85f));
        foreach (var hand in hands)
        {
            Rotate(pose, hand.Clavicle!, pose[hand.Clavicle!.Index].Position,
                Rotation.FromAxis(up, shoulders * blend * (hand.Left ? -1 : 1)));
            var shoulder = pose[hand.Upper!.Index]; var elbow = pose[hand.Lower!.Index]; var wrist = pose[hand.Wrist!.Index];
            var goal = Vector3.Lerp(wrist.Position, hand.Target, blend);
            var pole = right * (hand.Left ? -1 : 1) - up * .7f;
            if (!ArmAimSolver.Solve(Vec(shoulder.Position), Vec(elbow.Position), Vec(wrist.Position), Vec(goal), Vec(pole), out var solvedElbow, out var solvedWrist)) continue;
            Rotate(pose, hand.Upper, shoulder.Position, Between(elbow.Position - shoulder.Position, Vec(solvedElbow) - shoulder.Position));
            elbow = pose[hand.Lower.Index]; wrist = pose[hand.Wrist.Index];
            Rotate(pose, hand.Lower, elbow.Position, Between(wrist.Position - elbow.Position, Vec(solvedWrist) - elbow.Position));
            wrist = pose[hand.Wrist.Index];
            var rotation = Rotation.Slerp(wrist.Rotation, hand.Rotation, blend);
            Rotate(pose, hand.Wrist, wrist.Position, rotation * wrist.Rotation.Inverse);
        }
        foreach (var bone in bones)
        {
            if (bone.Parent is null || !fingerPose.TryGetValue(bone.Index, out var target)) continue;
            var current = body.SceneModel.GetParentSpaceBone(bone.Index);
            var local = current.WithRotation(Rotation.Slerp(current.Rotation, target.Rotation, blend))
                .WithPosition(Vector3.Lerp(current.Position, target.Position, blend));
            pose[bone.Index] = pose[bone.Parent.Index].ToWorld(local);
        }
    }

    private (float Lean, float Shoulders) FitTorso(Transform[] pose, Vector3 axis, Vector3 up)
    {
        var best = float.MaxValue; var bestLean = 0f; var bestShoulders = 0f;
        // Find the smallest lean that leaves a little bend in both elbows. This only
        // evaluates shoulder chains, rather than copying every avatar bone per trial.
        for (var angle = 0; angle <= 40; angle += 2)
        {
            var rootTurn = Rotation.FromAxis(axis, angle * .7f);
            var midTurn = Rotation.FromAxis(axis, angle * .15f);
            Vector3 Root(Vector3 point) => pose[pelvis!.Index].Position + rootTurn * (point - pose[pelvis.Index].Position);
            var mid = Root(pose[spine1!.Index].Position);
            Vector3 Mid(Vector3 point) => mid + midTurn * (Root(point) - mid);
            var top = Mid(pose[spine2!.Index].Position);
            Vector3 Top(Vector3 point) => top + midTurn * (Mid(point) - top);
            for (var protract = 0; protract <= 24; protract += 2)
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
        supported = fitHands = fitted = applied = false; fingerPose.Clear();
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
        internal Vector3 Target;
        internal Rotation Rotation;
        internal float Length;
    }

    private sealed class Leg
    {
        internal readonly bool Left;
        internal readonly BoneCollection.Bone? Upper, Lower, Ankle, Ball;
        internal readonly Rotation ForwardRotation;
        internal readonly Transform BallLocal;
        internal bool Valid => Upper is not null && Lower is not null && Ankle is not null;

        internal Leg(BoneCollection bones, string side)
        {
            Left = side == "L";
            Upper = bones.GetBone("leg_upper_" + side); Lower = bones.GetBone("leg_lower_" + side);
            Ankle = bones.GetBone("ankle_" + side) ?? bones.GetBone("foot_" + side);
            Ball = bones.GetBone("ball_" + side) ?? bones.GetBone("toe_" + side);
            if (Ankle is null) return;
            ForwardRotation = Ankle.LocalTransform.Rotation;
            if (Ball is null || Ball.Parent != Ankle) { Ball = null; return; }
            // Bone.LocalTransform is relative to the model root, not its parent.
            // Retain the reference foot pitch/roll and straighten its outward toe yaw.
            var toe = Ball.LocalTransform.Position - Ankle.LocalTransform.Position;
            var yaw = MathF.Atan2(toe.y, toe.x) * 180 / MathF.PI;
            ForwardRotation = Rotation.FromAxis(Vector3.Up, -yaw) * ForwardRotation;
            BallLocal = Ankle.LocalTransform.ToLocal(Ball.LocalTransform);
        }
    }
}
