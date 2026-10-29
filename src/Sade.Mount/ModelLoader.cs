using System.Runtime.InteropServices;
using Sandbox;
using Sandbox.Mounting;
using Sade.Contracts;
using N = System.Numerics;

namespace Sade.Mount;

internal sealed class ModelLoader(string id, string? animationId = null, string? weaponId = null, bool previewWheels = true) : ResourceLoader<SadeMount>
{
    protected override object Load()
    {
        var data = Host.GetModelData(id);
        if (id.Equals(PlayerOutfit.ModelId, StringComparison.OrdinalIgnoreCase))
            data = PlayerOutfit.Dress(data, Host.GetModelData(PlayerOutfit.VestId), Host.GetModelData(PlayerOutfit.JeansId));
        var vehicle = Host.Reader.GetVehicles().FirstOrDefault(v => v.ModelId.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (previewWheels && vehicle?.WheelModelId is { } wheelId)
            data = VehiclePreviewGeometry.Assemble(data, Host.GetModelData(wheelId), vehicle);
        if (data.Parts.Length == 0) throw new InvalidDataException($"{id} contains no render geometry.");
        if (data.Bones.Length > 256) throw new NotSupportedException($"{id}: {data.Bones.Length} bones exceed the engine vertex palette limit.");
        var builder = Model.Builder.WithName(Path).WithMass(80);
        var boneBounds = new BBox?[data.Bones.Length];
        foreach (var part in data.Parts)
        {
            if (part.Lod > 7 || part.Indices.Length == 0 || part.Vertices.Length == 0) continue;
            var materialPath = Host.PathFor(AssetKind.Material, part.MaterialId);
            var material = materialPath is null ? Material.Load("materials/dev/gray.vmat") : Material.Load(materialPath);
            var mesh = new Mesh(material);
            var positions = new Vector3[part.Vertices.Length];
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            if (data.Bones.Length > 0)
            {
                var vertices = new SkinnedVertex[part.Vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    var source = part.Vertices[i];
                    var geometry = ConvertVertex(source);
                    positions[i] = geometry.Position;
                    min = Vector3.Min(min, geometry.Position); max = Vector3.Max(max, geometry.Position);
                    var indices = new[] { source.Bones.X, source.Bones.Y, source.Bones.Z, source.Bones.W };
                    var weights = new[] { source.Weights.X, source.Weights.Y, source.Weights.Z, source.Weights.W };
                    var total = weights.Sum();
                    if (total <= 0) { indices[0] = 0; weights[0] = 1; total = 1; }
                    var bytes = new byte[4];
                    int sum = 0;
                    for (int j = 0; j < 4; j++)
                    {
                        if (weights[j] <= 0) { indices[j] = 0; continue; }
                        if (indices[j] < 0 || indices[j] >= data.Bones.Length) throw new InvalidDataException($"{id}: invalid bone index {indices[j]}.");
                        bytes[j] = (byte)Math.Clamp((int)MathF.Round(weights[j] / total * 255), 0, 255);
                        sum += bytes[j];
                        var bone = data.Bones[indices[j]];
                        var local = N.Vector3.Transform(source.Position - bone.WorldBind.Position, N.Quaternion.Inverse(bone.WorldBind.Rotation));
                        var point = CoordinateSpace.Position(local);
                        var old = boneBounds[indices[j]];
                        boneBounds[indices[j]] = old.HasValue ? new BBox(Vector3.Min(old.Value.Mins, point), Vector3.Max(old.Value.Maxs, point)) : new BBox(point, point);
                    }
                    var largest = Array.IndexOf(bytes, bytes.Max());
                    bytes[largest] = (byte)Math.Clamp(bytes[largest] + 255 - sum, 0, 255);
                    vertices[i] = new SkinnedVertex(geometry, new Color32((byte)indices[0], (byte)indices[1], (byte)indices[2], (byte)indices[3]), new Color32(bytes[0], bytes[1], bytes[2], bytes[3]));
                }
                mesh.CreateVertexBuffer(vertices.Length, vertices);
            }
            else
            {
                var vertices = new StaticVertex[part.Vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] = ConvertVertex(part.Vertices[i]);
                    positions[i] = vertices[i].Position;
                    min = Vector3.Min(min, positions[i]); max = Vector3.Max(max, positions[i]);
                }
                mesh.CreateVertexBuffer(vertices.Length, vertices);
            }
            var triangles = CoordinateSpace.Indices(part.Indices);
            if (triangles.Any(x => x < 0 || x >= positions.Length)) throw new InvalidDataException($"{id}: mesh index is outside vertex buffer.");
            mesh.CreateIndexBuffer(triangles.Length, triangles);
            mesh.Bounds = new BBox(min, max);
            builder.AddMesh(mesh, part.Lod).WithLodDistance(part.Lod, part.Lod * Host.Settings.LodDistance);
            if (part.Lod == 0) builder.AddTraceMesh(positions, triangles);
        }
        for (int i = 0; i < data.Bones.Length; i++)
        {
            var bone = data.Bones[i];
            if (bone.Parent >= i || bone.Parent < -1) throw new InvalidDataException($"{id}: bone hierarchy is not parent-first.");
            var transform = CoordinateSpace.Transform(bone.WorldBind);
            var bounds = boneBounds[i] ?? new BBox(new Vector3(-0.5f), new Vector3(0.5f));
            builder.AddBone(bone.Name, transform.Position, transform.Rotation, bounds, bone.Parent < 0 ? null : data.Bones[bone.Parent].Name);
        }
        var pedestrianPreset = Host.Reader.GetPedestrianPreset(id);
        var posingBodies = pedestrianPreset is not null ? PedestrianPhysicsBuilder.Add(builder, data) : 0;
        if (posingBodies == 0) AddCollision(builder, data.Collision);
        else Host.Report(new(id, $"Generated {posingBodies}-body articulated posing rig from reference bones and skin weights; constraints and shapes are mount-generated, not an authored GTA PhysicsAsset."));
        if (data.Collision.IsEmpty && data.Bones.Length == 0) Host.Report(new(id, "No authored collision was found."));
        AnimationClipData[] actorClips = [];
        var actorPreset = animationId is null ? pedestrianPreset : null;
        var actorWeapon = weaponId is null ? Host.DefaultWeapon(id) : Host.GetWeapon(weaponId);
        if (data.Bones.Length > 0)
        {
            AnimationClipData[] clips = [];
            var preset = pedestrianPreset;
            if (animationId is not null) clips = Host.Reader.LoadAnimations(animationId, data);
            else if (preset is not null)
            {
                clips = Host.Reader.LoadAnimations(preset.BankId, data, inPlace: true, clips: [preset.Idle, preset.Walk, preset.Run, "HIT_front", "KO_shot_front"]);
                var walk = clips.FirstOrDefault(x => x.Name.Equals(preset.Walk, StringComparison.OrdinalIgnoreCase));
                if (walk is not null && walk.RootSpeed > 0.1f) Host.SetWalkSpeed(id, walk.RootSpeed * CoordinateSpace.UnitsPerCentimeter);
            }
            actorClips = clips;
            if (actorPreset is not null && actorWeapon is not null && !string.IsNullOrEmpty(actorWeapon.AnimationBankId))
            {
                var selected = new[] { actorWeapon.FireClip, actorWeapon.ReloadClip, actorWeapon.AimClip ?? "" }
                    .Concat(actorWeapon.AdditionalAnimationClips).Concat(actorWeapon.MeleeAttacks.Select(x => x.Clip))
                    .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var weaponClips = Host.Reader.LoadAnimations(actorWeapon.AnimationBankId, data, inPlace: true, clips: selected);
                var fire = weaponClips.FirstOrDefault(x => x.Name.Equals(actorWeapon.FireClip, StringComparison.OrdinalIgnoreCase));
                var reload = weaponClips.FirstOrDefault(x => x.Name.Equals(actorWeapon.ReloadClip, StringComparison.OrdinalIgnoreCase));
                var additions = new List<AnimationClipData>(weaponClips.Select(x => x with { Looping = false }));
                if (fire is not null)
                {
                    var hasLoop = actorWeapon.FireLoopEnd > actorWeapon.FireLoopStart;
                    var start = hasLoop ? Math.Clamp((int)MathF.Round(actorWeapon.FireLoopStart * fire.FrameRate), 0, fire.Frames.Length - 1) : 0;
                    var end = hasLoop ? Math.Clamp((int)MathF.Round(actorWeapon.FireLoopEnd * fire.FrameRate), start + 1, fire.Frames.Length) : fire.Frames.Length;
                    var aim = weaponClips.FirstOrDefault(x => x.Name.Equals(actorWeapon.AimClip, StringComparison.OrdinalIgnoreCase));
                    var aimFrame = aim?.Frames[0] ?? fire.Frames[start];
                    additions.Add(fire with { Name = "sade_aim", Frames = [aimFrame, aimFrame], FrameRate = 30, Looping = true });
                    additions.Add(fire with { Name = "sade_fire", Frames = fire.Frames[start..end], Looping = false });
                }
                if (reload is not null) additions.Add(reload with { Name = "sade_reload", Looping = false });
                if (actorWeapon.AimWithArm && actorClips.FirstOrDefault(c => c.Name.Equals(actorPreset.Idle, StringComparison.OrdinalIgnoreCase)) is { } idle)
                    for (var i = 0; i < additions.Count; i++)
                        if (additions[i].Name is "sade_aim" or "sade_fire")
                            additions[i] = PedestrianAnimationLayers.RelaxFreeArm(additions[i], idle, data);
                clips = clips.Concat(additions).DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            foreach (var clip in clips)
            {
                var animation = builder.AddAnimation(clip.Name, clip.FrameRate).WithLooping(clip.Looping).WithDelta(clip.Additive);
                foreach (var frame in clip.Frames)
                {
                    if (frame.Length != data.Bones.Length) throw new InvalidDataException($"{id}/{clip.Name}: animation frame does not match skeleton.");
                    var transforms = frame.Select(CoordinateSpace.Transform).ToArray();
                    animation.AddFrame(transforms.AsSpan());
                }
            }
        }
        var built = builder.Create();
        if (posingBodies > 0) PedestrianPhysicsBuilder.Validate(built, posingBodies);
        if (vehicle is not null) Host.RegisterVehicleModel(built, Path, vehicle);
        if (actorPreset is not null) Host.RegisterActor(built, Path, id, actorPreset, actorClips, actorWeapon);
        return built;
    }

    private static StaticVertex ConvertVertex(VertexData source)
    {
        var position = CoordinateSpace.Position(source.Position);
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) throw new InvalidDataException("Non-finite mesh vertex.");
        var tangent = CoordinateSpace.Direction(new N.Vector3(source.Tangent.X, source.Tangent.Y, source.Tangent.Z));
        return new StaticVertex(position, CoordinateSpace.Direction(source.Normal), new Vector4(tangent.x, tangent.y, tangent.z, -source.Tangent.W), new Vector2(source.UV.X, source.UV.Y), new Color32(ToByte(source.Color.X), ToByte(source.Color.Y), ToByte(source.Color.Z), ToByte(source.Color.W)));
    }
    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
    private static void AddCollision(ModelBuilder builder, CollisionData data)
    {
        if (data.Indices.Length > 0) builder.AddCollisionMesh(data.Vertices.Select(CoordinateSpace.Position).ToArray().AsSpan(), CoordinateSpace.Indices(data.Indices).AsSpan());
        foreach (var hull in data.Hulls) if (hull.Length >= 4) builder.AddCollisionHull(hull.Select(CoordinateSpace.Position).ToArray().AsSpan());
        foreach (var box in data.Boxes) builder.AddCollisionBox(new Vector3(box.Extents.X, box.Extents.Y, box.Extents.Z) * CoordinateSpace.UnitsPerCentimeter, CoordinateSpace.Position(box.Center), CoordinateSpace.Rotation(box.Rotation));
        foreach (var sphere in data.Spheres) builder.AddCollisionSphere(sphere.Radius * CoordinateSpace.UnitsPerCentimeter, CoordinateSpace.Position(sphere.Center));
        foreach (var capsule in data.Capsules) builder.AddCollisionCapsule(CoordinateSpace.Position(capsule.Start), CoordinateSpace.Position(capsule.End), capsule.Radius * CoordinateSpace.UnitsPerCentimeter);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct StaticVertex(Vector3 position, Vector3 normal, Vector4 tangent, Vector2 uv, Color32 color)
    {
        [VertexLayout.Position] public Vector3 Position = position;
        [VertexLayout.Normal] public Vector3 Normal = normal;
        [VertexLayout.Tangent] public Vector4 Tangent = tangent;
        [VertexLayout.TexCoord] public Vector2 UV = uv;
        [VertexLayout.Color] public Color32 Color = color;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SkinnedVertex(StaticVertex vertex, Color32 indices, Color32 weights)
    {
        [VertexLayout.Position] public Vector3 Position = vertex.Position;
        [VertexLayout.Normal] public Vector3 Normal = vertex.Normal;
        [VertexLayout.Tangent] public Vector4 Tangent = vertex.Tangent;
        [VertexLayout.TexCoord] public Vector2 UV = vertex.UV;
        [VertexLayout.Color] public Color32 Color = vertex.Color;
        [VertexLayout.BlendIndices] public Color32 Indices = indices;
        [VertexLayout.BlendWeight] public Color32 Weights = weights;
    }
}
