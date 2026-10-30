using System.Numerics;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using Sade.Contracts;

namespace Sade.Reader;

public sealed partial class GameReader
{
    private WorldData? world;
    private const string WorldVersion = "unreal-exterior-4";
    private static readonly JsonSerializerOptions WorldJson = new() { IncludeFields = true, MaxDepth = 32 };

    public WorldData GetWorld()
    {
        lock (gate)
        {
            if (world is not null) return world;
            var cache = Path.Combine(options!.CacheDirectory, $"world-{WorldVersion}-{Fingerprint}.json");
            var inputCache = new[] { cache, Path.Combine(options.CacheDirectory, $"world-unreal-exterior-3-{Fingerprint}.json"),
                Path.Combine(options.CacheDirectory, $"world-unreal-exterior-2-{Fingerprint}.json") }.FirstOrDefault(File.Exists) ?? cache;
            try
            {
                if (File.Exists(inputCache) && new FileInfo(inputCache).Length <= 256L * 1024 * 1024)
                {
                    using var input = File.OpenRead(inputCache);
                    var saved = JsonSerializer.Deserialize<WorldData>(input, WorldJson);
                    if (saved is not null && saved.Fingerprint == Fingerprint && saved.Placements is { Length: > 0 and < 2_000_000 }
                        && saved.FailedSections is { Length: < 1000 } && saved.FailedSections.All(ValidCacheText)
                        && saved.Bounds is { Length: < 20_000 } && saved.Bounds.All(ValidBounds) && saved.Placements.All(ValidPlacement)
                        && saved.Bounds.Select(b => b.ModelId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == saved.Bounds.Length
                        && saved.Placements.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == saved.Placements.Length)
                    {
                        world = !saved.BoundsComplete ? IndexWorldBounds(saved) : saved;
                        if (inputCache != cache) SaveWorldCache(cache, world);
                        return world;
                    }
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            { report?.Invoke(new("world cache", $"Rebuilding placement index: {e.Message}")); }

            var models = entries.Where(e => e.Kind is AssetKind.StaticMesh or AssetKind.SkeletalMesh)
                .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
            var files = Provider.Files.Keys.Where(p => p.StartsWith("Gameface/Content/SanAndreas/Maps/SAWorld/", StringComparison.OrdinalIgnoreCase)
                && p.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)
                && !p.Contains("/INT/", StringComparison.OrdinalIgnoreCase) && !p.Contains("/Lighting/", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var placements = new List<WorldPlacementData>(); var failed = new List<string>();
            for (var section = 0; section < files.Length; section++)
            {
                var file = files[section];
                try
                {
                    var package = Provider.LoadPackage(file);
                    for (var index = 0; index < package.ExportsLazy.Length; index++)
                    {
                        try
                        {
                            var obj = package.ExportsLazy[index].Value;
                            if (obj.ExportType is not ("StaticMeshComponent" or "SkeletalMeshComponent")) continue;
                            var reference = Inherited<FPackageIndex>(obj, obj.ExportType == "SkeletalMeshComponent" ? "SkeletalMesh" : "StaticMesh");
                            if (reference?.ResolvedObject is not { } resolved) continue;
                            var modelId = CanonicalId(resolved.GetPathName());
                            if (!models.ContainsKey(modelId)) continue;
                            var actor = obj.Outer?.Load();
                            // Interior instances embedded in an exterior section must not overlap the street.
                            if (actor is not null && (Inherited(actor, "areaCodeAndFlags", 0) & 255) != 0) continue;
                            if (Inherited(obj, "bHiddenInGame", false) || actor is not null && Inherited(actor, "bHidden", false)) continue;
                            var visible = Inherited(obj, "bVisible", true);
                            var transform = ComponentTransform(obj, new HashSet<UObject>(), 0);
                            var overrides = Inherited<FPackageIndex[]>(obj, "OverrideMaterials", [])
                                .Select(p => p?.ResolvedObject is { } material ? CanonicalId(material.GetPathName()) : null).ToArray();
                            var placement = new WorldPlacementData(file + "#" + index, modelId, transform)
                            {
                                Visible = visible, Materials = overrides,
                                Collision = visible,
                                Prominent = actor is not null && Inherited(actor, "bProminent", actor.ExportType == "IPLMapActor"),
                                DrawDistance = Inherited(obj, "LDMaxDrawDistance", 0f)
                            };
                            if (ValidPlacement(placement)) placements.Add(placement);
                        }
                        catch (Exception e) { report?.Invoke(new(file + "#" + index, $"Skipping world component: {e.Message}", true)); }
                    }
                }
                catch (Exception e) { failed.Add(file); report?.Invoke(new(file, $"World section could not be indexed: {e.Message}", true)); }
                if (section % 25 == 0) report?.Invoke(new("world", $"Indexing exterior sections {section + 1}/{files.Length}; {placements.Count:N0} placements."));
            }
            if (placements.Count == 0) throw new InvalidDataException("No exterior world placements could be read from the installed game.");
            world = IndexWorldBounds(new(Fingerprint, placements.ToArray(), failed.ToArray()));
            SaveWorldCache(cache, world);
            report?.Invoke(new("world", $"Indexed {world.Placements.Length:N0} exterior placements from {files.Length} sections; {failed.Count} failed sections."));
            return world;
        }
    }

    private void SaveWorldCache(string cache, WorldData data)
    {
        try
        {
            Directory.CreateDirectory(options!.CacheDirectory);
            var temporary = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var output = File.Create(temporary)) JsonSerializer.Serialize(output, data, WorldJson);
            File.Move(temporary, cache, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { report?.Invoke(new("world cache", e.Message)); }
    }

    private WorldData IndexWorldBounds(WorldData data)
    {
        // Source bounds locate baked-world terrain correctly and let the streamer
        // cull tiny props before allocating their models or textures.
        var result = data.Bounds.ToDictionary(b => b.ModelId, StringComparer.OrdinalIgnoreCase);
        var ids = data.Placements.Select(p => p.ModelId).Distinct(StringComparer.OrdinalIgnoreCase).Where(id => !result.ContainsKey(id)).ToArray();
        var processed = 0;
        foreach (var id in ids)
        {
            try
            {
                var asset = Provider.LoadPackageObject(id);
                var box = asset switch
                {
                    UStaticMesh mesh => mesh.RenderData?.Bounds?.GetBox(),
                    USkeletalMesh mesh => mesh.ImportedBounds.GetBox(),
                    _ => null
                };
                if (box is not { } b) continue;
                result[id] = new(id, Vec(b.Min), Vec(b.Max));
            }
            catch (Exception e) { report?.Invoke(new(id, "World bounds could not be read: " + e.Message, true)); }
            if (++processed % 1000 == 0) report?.Invoke(new("world", $"Reading mesh bounds {processed}/{ids.Length}."));
        }
        report?.Invoke(new("world", $"Indexed source bounds for {result.Count} world meshes."));
        return data with { Bounds = result.Values.ToArray(), BoundsComplete = true };
    }

    private static T Inherited<T>(UObject obj, string name, T fallback = default!)
    {
        var seen = new HashSet<UObject>();
        while (seen.Add(obj) && seen.Count <= 32)
        {
            if (obj.Properties.Any(p => p.Name.Text.Equals(name, StringComparison.Ordinal))) return obj.GetOrDefault(name, fallback);
            if (obj.Template?.Load() is not { } template) break;
            obj = template;
        }
        return fallback;
    }

    private static TransformData ComponentTransform(UObject obj, HashSet<UObject> seen, int depth)
    {
        if (depth > 32 || !seen.Add(obj)) throw new InvalidDataException("Cyclic world attachment hierarchy.");
        var location = Inherited(obj, "RelativeLocation", FVector.ZeroVector);
        var rotation = Inherited(obj, "RelativeRotation", new FRotator()).Quaternion();
        var scale = Inherited(obj, "RelativeScale3D", FVector.OneVector);
        var local = new TransformData(new(location.X, location.Y, location.Z), new(rotation.X, rotation.Y, rotation.Z, rotation.W), new(scale.X, scale.Y, scale.Z));
        var parent = Inherited<FPackageIndex>(obj, "AttachParent")?.Load();
        if (parent is null) return local;
        var outer = ComponentTransform(parent, seen, depth + 1);
        var position = Vector3.Transform(local.Position * outer.Scale, outer.Rotation) + outer.Position;
        return new(position, Quaternion.Normalize(outer.Rotation * local.Rotation), outer.Scale * local.Scale);
    }

    private static bool ValidPlacement(WorldPlacementData p) => p is not null && ValidCacheText(p.Id) && ValidCacheText(p.ModelId)
        && p.Materials is not null && p.Materials.Length < 256 && p.Materials.All(m => m is null || ValidCacheText(m))
        && float.IsFinite(p.Transform.Position.LengthSquared()) && float.IsFinite(p.Transform.Scale.LengthSquared())
        && p.Transform.Scale.LengthSquared() > .000001f && float.IsFinite(p.Transform.Rotation.LengthSquared())
        && p.Transform.Rotation.LengthSquared() > .5f && float.IsFinite(p.DrawDistance);

    private static bool ValidBounds(WorldModelBounds b) => b is not null && ValidCacheText(b.ModelId)
        && float.IsFinite(b.Min.LengthSquared()) && float.IsFinite(b.Max.LengthSquared())
        && b.Min.X <= b.Max.X && b.Min.Y <= b.Max.Y && b.Min.Z <= b.Max.Z;
}
