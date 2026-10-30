using Sandbox.Mounting;
using Sade.Contracts;

namespace Sade.Mount;

public sealed partial class SadeMount
{
    private void RegisterCuratedPedestrians(MountContext context, IReadOnlyList<AssetEntry> catalog)
    {
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Where(e => e.Kind == AssetKind.SkeletalMesh)
                     .OrderBy(SpawnCatalog.GetPedestrianOrder).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (Reader.GetPedestrianPreset(entry.Id) is not { } preset) continue;
            if (SpawnCatalog.GetPedestrianPath(entry, preset) is not { } path || !usedPaths.Add(path)) continue;
            var model = new AliasLoader(ModelPath(entry.Id));
            model.Tags.UnionWith(["sade", "npc", preset.Faction]);
            context.Add(ResourceType.Model, path, model);
            context.Add(ResourceType.PrefabFile, path, new PedestrianLoader(entry.Id, preset));
        }
    }

    private void RegisterCuratedProps(MountContext context)
    {
        foreach (var pick in SpawnCatalog.StaticAssets)
        {
            if (!entries.TryGetValue(pick.SourceId, out var entry) || entry.Kind != AssetKind.StaticMesh)
            {
                Report(new(pick.SourceId, $"The curated prop '{pick.Path}' is missing from this game build."));
                continue;
            }
            var loader = new AliasLoader(ModelPath(entry.Id));
            loader.Tags.UnionWith(["sade", "prop"]);
            context.Add(ResourceType.Model, pick.Path, loader);
        }
    }

    private void RegisterAllModels(MountContext context, IReadOnlyList<AssetEntry> catalog)
    {
        // Keep the complete source model library one folder away from the curated
        // choices. Aliases share their converted model with every other reference.
        foreach (var entry in catalog.Where(e => e.Kind is AssetKind.StaticMesh or AssetKind.SkeletalMesh))
        {
            var loader = new AliasLoader(ModelPath(entry.Id));
            loader.Tags.UnionWith(["sade", "all"]);
            context.Add(ResourceType.Model, $"All/{CleanPath(entry.Path)}", loader);
        }
    }
}
