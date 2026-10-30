using Sandbox;
using Sade.Contracts;

namespace Sade.Mount;

/// <summary>
/// A generated posing rig for legacy pedestrians whose DE mesh has no authored PhysicsAsset.
/// Shapes follow the reference skeleton and the dominant skin weights, never world-axis guesses.
/// This is a mount adapter, not a reconstruction of Rockstar's original ragdoll constraints.
/// </summary>
internal static class PedestrianPhysicsBuilder
{
    private sealed record Segment(int Bone, int End, string Kind, float Mass, float MinimumRadius, float MaximumRadius);

    internal static int Add(ModelBuilder builder, ModelData model)
    {
        var bones = model.Bones;
        int Find(params string[] names)
        {
            foreach (var name in names)
            {
                var index = Array.FindIndex(bones, b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) return index;
            }
            return -1;
        }
        var pelvis = Find("Pelvis");
        if (pelvis < 0) throw new InvalidDataException($"{model.Id}: pedestrian posing requires a pelvis bone.");
        var classicSpine = Find("Spine");
        var abdomen = classicSpine >= 0 ? classicSpine : Find("Spine_1");
        var chest = classicSpine >= 0 ? Find("Spine1", "Spine_2") : Find("Spine_2");
        var segments = new List<Segment>();
        void AddSegment(int bone, int end, string kind, float mass, float minRadius, float maxRadius)
        {
            if (bone >= 0 && segments.All(s => s.Bone != bone)) segments.Add(new(bone, end, kind, mass, minRadius, maxRadius));
        }
        AddSegment(pelvis, abdomen, "pelvis", 14, .055f, .11f);
        AddSegment(abdomen, chest, "abdomen", 8, .055f, .12f);
        AddSegment(chest, Find("Neck"), "chest", 16, .065f, .14f);
        AddSegment(Find("Head"), Find("HeadNub", "HeadNubNub"), "head", 6, .04f, .075f);
        foreach (var side in new[] { "L", "R" })
        {
            AddSegment(Find(side + "_UpperArm"), Find(side + "_ForeArm"), "upperarm", 2.2f, .022f, .055f);
            AddSegment(Find(side + "_ForeArm"), Find(side + "_Hand"), "forearm", 1.4f, .018f, .045f);
            AddSegment(Find(side + "_Hand"), Find(side + "_Finger", side + "_Finger1", side + "_Finger10"), "hand", .6f, .013f, .035f);
            AddSegment(Find(side + "_Thigh"), Find(side + "_Calf"), "thigh", 8, .032f, .085f);
            AddSegment(Find(side + "_Calf"), Find(side + "_Foot"), "calf", 4.4f, .022f, .06f);
            AddSegment(Find(side + "_Foot"), Find(side + "_Toe0", side + "_Toe"), "foot", 1.4f, .018f, .04f);
        }
        if (segments.Count < 10) throw new InvalidDataException($"{model.Id}: incomplete pedestrian skeleton for articulated posing ({segments.Count} bodies).");
        // Skeleton indices are parent-first, including CJ's different leg-before-spine ordering.
        segments.Sort((a, b) => a.Bone.CompareTo(b.Bone));
        var bodyForBone = segments.Select((s, i) => (s.Bone, Body: i)).ToDictionary(x => x.Bone, x => x.Body);
        var points = segments.Select(_ => new List<Vector3>()).ToArray();
        var bind = segments.Select(s => CoordinateSpace.Transform(bones[s.Bone].WorldBind).WithScale(1)).ToArray();
        for (var i = 0; i < bind.Length; i++)
            if (!bind[i].Position.IsFinite || !Finite(bind[i].Rotation)) throw new InvalidDataException($"{model.Id}: invalid posing bind transform.");

        // Fold face/finger/toe vertices into their closest physical ancestor. Dominant weights
        // avoid distant low-weight vertices inflating an arm shape into the torso.
        foreach (var vertex in model.Parts.Where(p => p.Lod == 0).SelectMany(p => p.Vertices))
        {
            var weights = vertex.Weights;
            var slot = weights.X >= weights.Y && weights.X >= weights.Z && weights.X >= weights.W ? 0
                : weights.Y >= weights.Z && weights.Y >= weights.W ? 1 : weights.Z >= weights.W ? 2 : 3;
            var bone = slot switch { 0 => vertex.Bones.X, 1 => vertex.Bones.Y, 2 => vertex.Bones.Z, _ => vertex.Bones.W };
            while (bone >= 0 && bone < bones.Length && !bodyForBone.ContainsKey(bone)) bone = bones[bone].Parent;
            if (bone < 0 || !bodyForBone.TryGetValue(bone, out var body)) continue;
            var point = bind[body].PointToLocal(CoordinateSpace.Position(vertex.Position));
            if (!point.IsFinite) throw new InvalidDataException($"{model.Id}: invalid posing mesh vertex.");
            points[body].Add(point);
        }
        var height = (model.BoundsMax.Z - model.BoundsMin.Z) * CoordinateSpace.UnitsPerCentimeter;
        if (!float.IsFinite(height) || height < 8) throw new InvalidDataException($"{model.Id}: invalid pedestrian height.");
        var massScale = 80 / segments.Sum(s => s.Mass);
        var flesh = Surface.FindByName("flesh");
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            // Sandbox chooses both melee audio and blood effects from the trace's
            // physical surface. A default surface makes these limbs behave like props.
            var body = builder.AddBody(segment.Mass * massScale, surface: flesh, boneName: bones[segment.Bone].Name)
                .SetBindPose(bind[i]).SetDamping(.6f, 3f);
            var cloud = points[i];
            if (segment.Kind is "pelvis" or "abdomen" or "chest")
            {
                AddCore(body, cloud, height, segment.Kind);
                continue;
            }
            var end = segment.End >= 0 ? bind[i].PointToLocal(CoordinateSpace.Position(bones[segment.End].WorldBind.Position)) : Vector3.Zero;
            AddLimb(body, cloud, end, height, segment);
        }
        for (var child = 0; child < segments.Count; child++)
        {
            if (segments[child].Bone == pelvis) continue;
            var parentBone = bones[segments[child].Bone].Parent;
            while (parentBone >= 0 && !bodyForBone.ContainsKey(parentBone)) parentBone = bones[parentBone].Parent;
            if (parentBone < 0) throw new InvalidDataException($"{model.Id}: disconnected posing body {bones[segments[child].Bone].Name}.");
            var parent = bodyForBone[parentBone];
            var (swing, twist) = segments[child].Kind switch
            {
                "abdomen" => (30f, 30f), "chest" => (35f, 35f), "head" => (55f, 65f),
                "hand" => (55f, 70f), "foot" => (40f, 35f), "forearm" or "calf" => (85f, 20f),
                _ => (85f, 75f)
            };
            // Both anchors coincide at the child's bind origin. The parent frame includes
            // the authored bone rotation; neutral constraints therefore start unstressed.
            builder.AddBallJoint(child, parent, Transform.Zero, bind[parent].ToLocal(bind[child]), collision: false)
                .WithSwingLimit(swing).WithTwistLimit(-twist, twist).WithFriction(1.5f);
        }
        builder.WithMass(80);
        return segments.Count;
    }

    private static void AddCore(PhysicsBodyBuilder body, List<Vector3> points, float height, string kind)
    {
        var min = points.Count > 0 ? Quantile(points, .05f) : new Vector3(-height * .06f);
        var max = points.Count > 0 ? Quantile(points, .95f) : new Vector3(height * .06f);
        var center = (min + max) * .5f;
        var extents = (max - min) * .44f;
        var lower = height * (kind == "pelvis" ? .025f : .02f);
        var upper = height * (kind == "chest" ? .15f : .13f);
        extents = new Vector3(Math.Clamp(extents.x, lower, upper), Math.Clamp(extents.y, lower, upper), Math.Clamp(extents.z, lower, upper));
        body.AddBox(extents, new Transform(center));
    }

    private static void AddLimb(PhysicsBodyBuilder body, List<Vector3> points, Vector3 end, float height, Segment segment)
    {
        var length = end.Length;
        var axis = length > .1f ? end / length : Vector3.Forward;
        if (length <= .1f && points.Count > 0)
        {
            // Missing head-tip/finger helper bones: derive the major extent from their skin.
            var min = Quantile(points, .05f); var max = Quantile(points, .95f); var size = max - min;
            axis = size.x >= size.y && size.x >= size.z ? Vector3.Forward : size.y >= size.z ? Vector3.Left : Vector3.Up;
            length = Math.Max(Vector3.Dot(size, axis), height * .04f);
        }
        length = Math.Max(length, height * .025f);
        var distances = points.Select(p => (p - axis * Vector3.Dot(p, axis)).Length).Order().ToArray();
        var radius = distances.Length > 0 ? distances[(int)((distances.Length - 1) * .65f)] * .85f : height * segment.MinimumRadius;
        radius = Math.Clamp(radius, height * segment.MinimumRadius, height * segment.MaximumRadius);
        if (segment.Kind is "upperarm" or "forearm" or "thigh" or "calf") radius = Math.Min(radius, length * .32f);
        var low = 0f; var high = length;
        if (segment.Kind is "hand" or "head" or "foot" && points.Count > 0)
        {
            var projections = points.Select(p => Vector3.Dot(p, axis)).Order().ToArray();
            low = Math.Max(-height * .025f, projections[(int)((projections.Length - 1) * .05f)]);
            high = Math.Max(length, projections[(int)((projections.Length - 1) * .95f)]);
        }
        // Keep capsule tips inside the joint-to-joint span; linked bodies do not self-collide.
        var inset = Math.Min(radius * 1.05f, (high - low) * .45f);
        var a = axis * (low + inset); var b = axis * (high - inset);
        if (!a.IsFinite || !b.IsFinite || !float.IsFinite(radius) || radius <= 0) throw new InvalidDataException("Invalid generated pedestrian shape.");
        if ((b - a).Length < .05f) body.AddSphere(new Sphere((a + b) * .5f, radius));
        else body.AddCapsule(new Capsule(a, b, radius));
    }

    private static Vector3 Quantile(List<Vector3> points, float q)
    {
        var index = (int)((points.Count - 1) * q);
        return new(points.Select(p => p.x).Order().ElementAt(index), points.Select(p => p.y).Order().ElementAt(index), points.Select(p => p.z).Order().ElementAt(index));
    }

    internal static void Validate(Model model, int expectedBodies)
    {
        var physics = model.Physics ?? throw new InvalidDataException($"{model.Name}: generated posing physics is missing.");
        if (physics.Parts.Count != expectedBodies || physics.Joints.Count != expectedBodies - 1)
            throw new InvalidDataException($"{model.Name}: generated posing rig lost bodies or joints.");
        if (Math.Abs(physics.Parts.Sum(p => p.Mass) - 80) > .02f) throw new InvalidDataException($"{model.Name}: invalid posing rig mass.");
        foreach (var part in physics.Parts)
            if (string.IsNullOrEmpty(part.BoneName) || !part.Transform.Position.IsFinite || !Finite(part.Transform.Rotation) || !float.IsFinite(part.Mass) || part.Mass <= 0)
                throw new InvalidDataException($"{model.Name}: invalid posing body.");
        foreach (var joint in physics.Joints)
            if (joint.Body1 < 0 || joint.Body1 >= expectedBodies || joint.Body2 < 0 || joint.Body2 >= expectedBodies || joint.Body1 == joint.Body2 ||
                !joint.Frame1.Position.IsFinite || !joint.Frame2.Position.IsFinite || !Finite(joint.Frame1.Rotation) || !Finite(joint.Frame2.Rotation))
                throw new InvalidDataException($"{model.Name}: invalid posing joint.");
    }

    private static bool Finite(Rotation r) => float.IsFinite(r.x) && float.IsFinite(r.y) && float.IsFinite(r.z) && float.IsFinite(r.w);
}
