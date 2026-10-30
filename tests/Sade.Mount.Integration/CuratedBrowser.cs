// Metadata-only browser audit. Run before scenery checks to verify browsing does not decode models.
var mount = Sandbox.Mounting.Directory.Get("sade") ?? throw new Exception("SADE is not available.");
var flag = Sandbox.Mounting.ResourceFlags.DeveloperOnly;
var modelType = Sandbox.Mounting.ResourceType.Model;
var visible = mount.GetAll(modelType).Where(x => (x.Flags & flag) == 0).ToArray();
var tree = mount.RootFolder ?? throw new Exception("The browser tree is missing.");
var folders = tree.Folders.Where(f => f.ContainsType(modelType)).Select(f => f.Name).OrderBy(n => n).ToArray();
var expected = new[] { "All", "Common buildings", "Construction props", "NPCs", "Ramps", "Street props", "Vehicles" }.OrderBy(n => n).ToArray();
if (!folders.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase)) throw new Exception("Unexpected top-level folders: " + string.Join(", ", folders));
var allModels = visible.Where(x => x.RelativePath.StartsWith("All/", StringComparison.OrdinalIgnoreCase)).ToArray();
var curated = visible.Except(allModels).ToArray();
if (curated.Length != 322) throw new Exception($"Expected the verified 322 curated choices, got {curated.Length}.");
var entries = (System.Collections.IDictionary)mount.GetType().GetField("entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mount)!;
var sourceModelCount = entries.Values.Cast<object>().Count(entry => entry.GetType().GetProperty("Kind")!.GetValue(entry)!.ToString() is "StaticMesh" or "SkeletalMesh");
if (allModels.Length != sourceModelCount) throw new Exception($"All has {allModels.Length} models but the source catalog contains {sourceModelCount}.");
var treeModels = tree.GetAllFilesRecursive().Where(x => x.Type == modelType).ToArray();
if (treeModels.Length != visible.Length || !treeModels.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(visible.Select(x => x.Path)))
    throw new Exception("Folder traversal differs from the actual Sandbox model list.");
if (tree.GetAllFilesRecursive().Any(x => (x.Flags & flag) != 0)) throw new Exception("Hidden dependencies leaked into the browser tree.");
if (visible[0].Name != "CJ" || visible[1].Name != "Big Smoke") throw new Exception("The featured characters lost priority.");
var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var entry in visible)
{
    if (!seen.Add(entry.Path)) throw new Exception("Duplicate browser entry " + entry.Path);
    if (!entry.RelativePath.StartsWith("All/", StringComparison.OrdinalIgnoreCase) && (entry.Path.Contains("loadouts/", StringComparison.OrdinalIgnoreCase) || entry.Path.Contains("weapons/", StringComparison.OrdinalIgnoreCase)
        || entry.Path.Contains("animations/", StringComparison.OrdinalIgnoreCase))) throw new Exception("Raw asset noise remains outside All: " + entry.Path);
}
var legacyPaths = new[] { "mount://sade/00_featured/00_cj.vmdl", "mount://sade/00_featured/01_big_smoke.prefab",
    "mount://sade/vehicles/road/infernus.prefab", "mount://sade/01_weapons/hitscan/colt45.vmdl" };
foreach (var path in legacyPaths)
{
    var loader = mount.GetByPath(path) ?? throw new Exception("Saved path no longer resolves: " + path);
    if ((loader.Flags & flag) == 0) throw new Exception("Legacy alias remains visible: " + path);
}
var animation = mount.GetAll(modelType).FirstOrDefault(x => x.Path.Contains("animations/", StringComparison.OrdinalIgnoreCase));
if (animation is null || (animation.Flags & flag) == 0) throw new Exception("Legacy animation references must resolve without filling the spawn browser.");
var field = typeof(Sandbox.Mounting.ResourceLoader).GetField("_cachedResult", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
var loaded = mount.GetAll(modelType).Count(x => field.GetValue(x) is Model);
return new { Passed = true, CuratedModels = curated.Length, AllModels = allModels.Length, VisibleModels = visible.Length, Folders = folders,
    Groups = visible.GroupBy(x => x.RelativePath.Split('/')[0]).Select(g => new { Folder = g.Key, Count = g.Count() }).ToArray(),
    TotalRegisteredModels = mount.GetAll(modelType).Count, ModelsAlreadyDecoded = loaded,
    SavedPathsPreserved = true, AnimationReferencesPreserved = true };
