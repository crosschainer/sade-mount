using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.PhysicsEngine;
using CUE4Parse.UE4.Objects.Meshes;
using Sade.Contracts;
using Sade.Formats;

namespace Sade.Reader;

public sealed partial class GameReader
{
    private Dictionary<string, CollisionData>? legacyCollision;

    public ModelData LoadModel(string id)
    {
        lock (gate)
        {
            var asset = Provider.LoadPackageObject(id);
            ModelData model;
            if (asset is UStaticMesh mesh)
            {
                var bounds = mesh.RenderData?.Bounds?.GetBox() ?? throw new InvalidDataException($"Mesh {id} has no render bounds.");
                model = new ModelData { Id = id, Parts = ReadStaticParts(mesh), BoundsMin = Vec(bounds.Min), BoundsMax = Vec(bounds.Max),
                    Collision = ReadBody(mesh.BodySetup?.Load<UBodySetup>(), TransformData.Identity) };
            }
            else if (asset is USkinnedAsset skeletal)
            {
                var reference = skeletal.ReferenceSkeleton;
                var bones = new BoneData[reference.FinalRefBoneInfo.Length];
                var world = new FTransform[bones.Length];
                for (var i = 0; i < bones.Length; i++)
                {
                    var bone = reference.FinalRefBoneInfo[i];
                    var pose = reference.FinalRefBonePose[i];
                    if (bone.ParentIndex >= i) throw new InvalidDataException($"Bone {bone.Name} has a non-topological parent.");
                    world[i] = bone.ParentIndex >= 0 ? pose * world[bone.ParentIndex] : pose;
                    bones[i] = new BoneData(bone.Name.Text, bone.ParentIndex, Transform(pose), Transform(world[i]));
                }
                var collision = new List<CollisionData>();
                var physics = skeletal.PhysicsAsset?.Load<UPhysicsAsset>();
                foreach (var bodyIndex in physics?.SkeletalBodySetups ?? [])
                {
                    var body = bodyIndex.Load<USkeletalBodySetup>();
                    if (body is null) continue;
                    var bone = Array.FindIndex(bones, b => b.Name.Equals(body.BoneName.Text, StringComparison.OrdinalIgnoreCase));
                    if (bone >= 0) collision.Add(ReadBody(body, bones[bone].WorldBind));
                }
                var parts = ReadSkeletalParts(skeletal);
                var positions = parts.Where(p => p.Lod == 0).SelectMany(p => p.Vertices).Select(v => v.Position).ToArray();
                model = new ModelData { Id = id, Parts = parts, Bones = bones, SkeletonId = ObjectId(skeletal.Skeleton.Load()),
                    BoundsMin = positions.Aggregate(Vector3.Min), BoundsMax = positions.Aggregate(Vector3.Max), Collision = MergeCollision(collision, "Unreal PhysicsAsset") };
            }
            else throw new NotSupportedException($"{id} is {asset.ExportType}, not a static or skeletal mesh.");
            if (model.Parts.Length == 0) throw new InvalidDataException($"{id} has no readable mesh sections.");
            if (model.Collision.IsEmpty && FindLegacyCollision(id) is { } fallback)
            {
                var collision = BakedMeshCollision.Resolve(model, fallback);
                if (!ReferenceEquals(collision, fallback)) report?.Invoke(new(id, collision.Source));
                model = model with { Collision = collision };
            }
            if (model.Collision.IsEmpty && id.StartsWith("/Game/SanAndreas/Vehicles/", StringComparison.OrdinalIgnoreCase))
            {
                // Several aircraft ship an empty PhysicsAsset and no matching legacy COL record.
                // Build small convex pieces from the intact rigid mesh bones instead of a whole-plane box.
                var hulls = new List<Vector3[]>();
                foreach (var group in model.Parts.Where(p => p.Lod == 0).SelectMany(p => p.Vertices).GroupBy(v => v.Bones.X))
                {
                    var vertices = group.Select(v => v.Position).Distinct().ToArray();
                    if (vertices.Length < 4) continue;
                    var min = vertices.Aggregate(Vector3.Min); var max = vertices.Aggregate(Vector3.Max);
                    if ((max - min).X < .1f || (max - min).Y < .1f || (max - min).Z < .1f) continue;
                    var support = new HashSet<Vector3>();
                    for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++) for (var z = -1; z <= 1; z++)
                    {
                        if (x == 0 && y == 0 && z == 0) continue;
                        var direction = new Vector3(x, y, z);
                        support.Add(vertices.MaxBy(v => Vector3.Dot(v, direction)));
                    }
                    if (support.Count >= 4) hulls.Add(support.ToArray());
                }
                model = model with { Collision = new CollisionData { Hulls = hulls.ToArray(), Source = "Derived convex vehicle mesh (no authored collision)" } };
                report?.Invoke(new Diagnostic(id, $"No authored vehicle collision was available; generated {hulls.Count} convex pieces from intact mesh support points."));
            }
            return model;
        }
    }

    private MeshPartData[] ReadStaticParts(UStaticMesh mesh)
    {
        var parts = new List<MeshPartData>();
        for (var level = 0; level < (mesh.RenderData?.LODs?.Length ?? 0); level++)
        {
            var lod = mesh.RenderData!.LODs![level];
            if (lod.SkipLod) continue;
            if (lod.PositionVertexBuffer is null || lod.VertexBuffer is null || lod.IndexBuffer?.Buffer is null) throw new InvalidDataException($"{mesh.Name} LOD{level} has missing buffers.");
            var vertices = new VertexData[lod.PositionVertexBuffer.Verts.Length];
            for (var i = 0; i < vertices.Length; i++)
            {
                var uv = lod.VertexBuffer.UV[i];
                vertices[i] = MakeVertex(lod.PositionVertexBuffer.Verts[i], uv.Normal[2], uv.Normal[0], uv.UV[0],
                    lod.ColorVertexBuffer?.Data is { } colors && i < colors.Length ? colors[i] : new FColor(255, 255, 255, 255));
            }
            foreach (var section in lod.Sections)
                AddPart(parts, level, section.MaterialIndex, ObjectId(mesh.Materials.ElementAtOrDefault(section.MaterialIndex)?.Load()), vertices, lod.IndexBuffer.Buffer, section.FirstIndex, section.NumTriangles);
        }
        return parts.ToArray();
    }

    private MeshPartData[] ReadSkeletalParts(USkinnedAsset mesh)
    {
        var parts = new List<MeshPartData>();
        var vehicleDamage = mesh.AssetUserData?.Select(p => p.Load()).FirstOrDefault(p => p?.ExportType == "GTAVehicleUserData")
            ?.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("SectionDamageData", []);
        for (var level = 0; level < (mesh.LODModels?.Length ?? 0); level++)
        {
            var lod = mesh.LODModels![level];
            if (lod.SkipLod) continue;
            if (lod.Indices?.Buffer is null) throw new InvalidDataException($"{mesh.Name} LOD{level} has no index buffer.");
            var count = lod.VertexBufferGPUSkin.GetVertexCount();
            var useSoft = count == 0;
            if (useSoft) count = lod.Sections.Sum(s => s.SoftVertices.Length);
            var vertices = new VertexData[count];
            var sectionNumber = 0;
            foreach (var section in lod.Sections)
            {
                for (var offset = 0; offset < section.NumVertices; offset++)
                {
                    var i = checked((int)section.BaseVertexIndex + offset);
                    FSkelMeshVertexBase vertex = useSoft ? section.SoftVertices[offset] : lod.VertexBufferGPUSkin.bUseFullPrecisionUVs ? lod.VertexBufferGPUSkin.VertsFloat[i] : lod.VertexBufferGPUSkin.VertsHalf[i];
                    var color = lod.ColorVertexBuffer?.Data is { } colors && i < colors.Length ? colors[i] : useSoft ? section.SoftVertices[offset].Color : new FColor(255, 255, 255, 255);
                    var converted = MakeVertex(vertex.Pos, vertex.Normal[2], vertex.Normal[0], vertex.UVs[0], color);
                    var bones = new int[4];
                    var weights = new float[4];
                    if (vertex.Infs is { } skin)
                    {
                        var influences = Enumerable.Range(0, skin.BoneWeight.Length).Where(j => skin.BoneWeight[j] > 0).OrderByDescending(j => skin.BoneWeight[j]).Take(4).ToArray();
                        var total = influences.Sum(j => (float)skin.BoneWeight[j]);
                        for (var j = 0; j < influences.Length; j++) { bones[j] = section.BoneMap[skin.BoneIndex[influences[j]]]; weights[j] = skin.BoneWeight[influences[j]] / total; }
                    }
                    vertices[i] = converted with { Bones = new(bones[0], bones[1], bones[2], bones[3]), Weights = new(weights[0], weights[1], weights[2], weights[3]) };
                }
                // DE stores intact triangles first, followed by replacement damaged panels in each section.
                // Rendering both at once causes overlapping doors, windows and bodywork.
                var intact = vehicleDamage?.ElementAtOrDefault(level)?.GetOrDefault<int[]>("NumTrianglesOk", []);
                var triangles = intact is not null && sectionNumber < intact.Length ? intact[sectionNumber] : section.NumTriangles;
                if (triangles < 0 || triangles > section.NumTriangles) throw new InvalidDataException($"{mesh.Name}: invalid intact vehicle section count.");
                AddPart(parts, level, section.MaterialIndex, ObjectId(mesh.Materials.ElementAtOrDefault(section.MaterialIndex)?.Load()), vertices, lod.Indices.Buffer, section.BaseIndex, triangles);
                sectionNumber++;
            }
        }
        return parts.ToArray();
    }

    private static VertexData MakeVertex(FVector position, FVector4 normal, FVector4 tangent, FMeshUVFloat uv, FColor color) =>
        new(Vec(position), new(normal.X, normal.Y, normal.Z), new(tangent.X, tangent.Y, tangent.Z, normal.W), new(uv.U, uv.V), new Vector4(color.R, color.G, color.B, color.A) / 255, default, default);

    private static void AddPart(List<MeshPartData> parts, int level, int material, string? materialId, VertexData[] source, uint[] sourceIndices, int first, int triangles)
    {
        if (triangles == 0) return;
        var indices = new int[checked(triangles * 3)];
        var vertices = new List<VertexData>();
        var map = new Dictionary<uint, int>();
        for (var i = 0; i < indices.Length; i++)
        {
            var original = sourceIndices[first + i];
            if (!map.TryGetValue(original, out var target)) { target = vertices.Count; map[original] = target; vertices.Add(source[original]); }
            indices[i] = target;
        }
        parts.Add(new MeshPartData($"lod{level}_{material}_{parts.Count}", materialId, vertices.ToArray(), indices, level));
    }

    private static CollisionData ReadBody(UBodySetup? body, TransformData parent)
    {
        var geom = body?.AggGeom;
        if (geom is null) return new();
        var spheres = geom.SphereElems.Select(s => new CollisionSphere(Point(Vec(s.Center), parent), s.Radius * MaxScale(parent))).ToArray();
        var boxes = geom.BoxElems.Select(b => new CollisionBox(Point(Vec(b.Center), parent), new Vector3(b.X, b.Y, b.Z) * Vector3.Abs(parent.Scale) / 2, Quaternion.Normalize(parent.Rotation * Quat(b.Rotation.Quaternion())))).ToArray();
        var capsules = geom.SphylElems.Select(c =>
        {
            var offset = Vector3.Transform(Vector3.UnitZ * c.Length / 2, Quat(c.Rotation.Quaternion()));
            return new CollisionCapsule(Point(Vec(c.Center) - offset, parent), Point(Vec(c.Center) + offset, parent), c.Radius * MaxScale(parent));
        }).ToArray();
        var hulls = geom.ConvexElems.Where(c => c.VertexData.Length >= 4).Select(c => c.VertexData.Select(v => Point(Point(Vec(v), Transform(c.Transform)), parent)).ToArray()).ToArray();
        return new CollisionData { Boxes = boxes, Spheres = spheres, Capsules = capsules, Hulls = hulls, Source = "Unreal BodySetup" };
    }

    private CollisionData? FindLegacyCollision(string id)
    {
        if (legacyCollision is null)
        {
            legacyCollision = new(StringComparer.OrdinalIgnoreCase);
            foreach (var containerId in containers)
            {
                foreach (var (name, bytes) in GetContainer(containerId).Entries.Where(e => e.Key.EndsWith(".col", StringComparison.OrdinalIgnoreCase)))
                {
                    try { foreach (var col in ColParser.Parse(bytes, $"{containerId}:{name}", report, skipInvalidRecords: true)) legacyCollision.TryAdd(col.Name, col.Collision); }
                    catch (InvalidDataException e) { report?.Invoke(new Diagnostic(name, $"Legacy collision could not be read: {e.Message}")); }
                }
            }
            foreach (var path in Provider.Files.Keys.Where(p => p.EndsWith(".col", StringComparison.OrdinalIgnoreCase)))
            {
                try { foreach (var col in ColParser.Parse(Provider.SaveAsset(path), path, report, skipInvalidRecords: true)) legacyCollision.TryAdd(col.Name, col.Collision); }
                catch (InvalidDataException e) { report?.Invoke(new Diagnostic(path, $"Legacy collision could not be read: {e.Message}")); }
            }
        }
        var nameOnly = id.Split('/')[^1].Split('.')[0];
        if (nameOnly.StartsWith("SKR_", StringComparison.OrdinalIgnoreCase)) nameOnly = nameOnly[4..];
        if (nameOnly.StartsWith("SM_", StringComparison.OrdinalIgnoreCase) || nameOnly.StartsWith("SK_", StringComparison.OrdinalIgnoreCase)) nameOnly = nameOnly[3..];
        return legacyCollision.GetValueOrDefault(nameOnly);
    }

    private static CollisionData MergeCollision(IEnumerable<CollisionData> parts, string source)
    {
        var data = parts.ToArray();
        return new CollisionData { Boxes = data.SelectMany(c => c.Boxes).ToArray(), Spheres = data.SelectMany(c => c.Spheres).ToArray(),
            Capsules = data.SelectMany(c => c.Capsules).ToArray(), Hulls = data.SelectMany(c => c.Hulls).ToArray(), Source = source };
    }
    private static float MaxScale(TransformData t) => Math.Max(Math.Abs(t.Scale.X), Math.Max(Math.Abs(t.Scale.Y), Math.Abs(t.Scale.Z)));
    private static Vector3 Point(Vector3 v, TransformData t) => Vector3.Transform(v * t.Scale, t.Rotation) + t.Position;
    private static Vector3 Vec(FVector v) => new(v.X, v.Y, v.Z);
    private static Quaternion Quat(FQuat q) => Quaternion.Normalize(new(q.X, q.Y, q.Z, q.W));
    private static TransformData Transform(FTransform t) => new(Vec(t.Translation), Quat(t.Rotation), Vec(t.Scale3D));
}
