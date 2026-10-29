using Sandbox;
using Sandbox.Mounting;
using Sade.Contracts;

namespace Sade.Mount;

public sealed partial class SadeMount : BaseGameMount
{
    public override string Ident => "sade";
    public override string Title => "GTA San Andreas — The Definitive Edition";
    public override long? SteamAppId => 1547000;
    internal IGameReader Reader { get; private set; } = null!;
    internal SadeSettings Settings { get; private set; } = new();
    private ReaderContext? readerContext;
    private string? gameDirectory;
    // Keep value types from the private reader contract out of fields: the host calls
    // Assembly.GetTypes before our module initializer can install dependency probing.
    private readonly Dictionary<(int, string), string> paths = new();
    private readonly Dictionary<string, AssetEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelData> modelData = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> walkSpeeds = new(StringComparer.OrdinalIgnoreCase);

    protected override void Initialize(InitializeContext context)
    {
        try
        {
            Settings = SadeSettings.Load();
            gameDirectory = Settings.GameDirectory ?? context.GetAppDirectory(1547000);
            IsInstalled = gameDirectory is not null && System.IO.Directory.Exists(System.IO.Path.Combine(gameDirectory, "Gameface", "Content", "Paks"));
        }
        catch (Exception e) { Log.Warning($"SADE setup: {e.Message}"); IsInstalled = false; }
    }

    protected override Task Mount(MountContext context)
    {
        try
        {
            (Reader, readerContext) = Bootstrap.CreateReader();
            var catalog = Reader.Initialize(new(gameDirectory!, Settings.CacheDirectory), Report);
            weapons = Reader.GetWeapons().ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            var pending = new List<(ResourceType Type, string Relative, ResourceLoader Loader)>();
            foreach (var entry in catalog)
            {
                entries[entry.Id] = entry;
                var category = Category(entry.Kind);
                if (category is null) continue;
                var relative = $"{category}/{CleanPath(entry.Path)}";
                ResourceLoader loader = entry.Kind switch
                {
                    AssetKind.StaticMesh or AssetKind.SkeletalMesh => new ModelLoader(entry.Id),
                    AssetKind.Texture => new TextureLoader(entry.Id),
                    AssetKind.Material => new MaterialLoader(entry.Id),
                    AssetKind.Sound => new SoundLoader(entry.Id),
                    _ => throw new InvalidOperationException()
                };
                var type = TypeFor(entry.Kind);
                loader.Tags.Add("sade");
                if (!entry.Id.StartsWith("/Game/SanAndreas/", StringComparison.OrdinalIgnoreCase)) loader.Flags |= ResourceFlags.DeveloperOnly;
                paths[((int)entry.Kind, entry.Id.ToLowerInvariant())] = $"mount://sade/{relative}{Extension(entry.Kind)}";
                pending.Add((type, relative, loader));
            }

            // Sandbox preserves registration order in its Games tab. Put recognizable,
            // usable assets first; dependent resources can still be registered lazily below.
            var representatives = RegisterFeatured(context, catalog);
            RegisterVehicles(context);
            foreach (var item in pending) context.Add(item.Type, item.Relative, item.Loader);
            RegisterLoadouts(context, representatives);

            var banks = catalog.Where(x => x.Kind == AssetKind.AnimationBank).ToArray();
            var nativeAnimations = catalog.Where(x => x.Kind == AssetKind.Animation).ToArray();
            foreach (var entry in catalog.Where(x => x.Kind == AssetKind.SkeletalMesh))
            {
                var preset = Reader.GetPedestrianPreset(entry.Id);
                if (preset is not null)
                {
                    var prefab = new PedestrianLoader(entry.Id, preset);
                    context.Add(ResourceType.PrefabFile, $"pedestrians/{entry.Name.ToLowerInvariant()}", prefab);
                    prefab.Tags.UnionWith(["pedestrian", "animated", preset.Group]);
                    if (entry.Name.Equals("SK_smoke", StringComparison.OrdinalIgnoreCase)) prefab.Tags.Add("big smoke");
                    if (Settings.RegisterAnimationVariants)
                        foreach (var bank in banks)
                            context.Add(ResourceType.Model, $"animations/{CleanPath(entry.Path)}/{CleanPath(bank.Path)}", new ModelLoader(entry.Id, bank.Id));
                }
                if (!Settings.RegisterAnimationVariants || string.IsNullOrEmpty(entry.SkeletonId)) continue;
                foreach (var clip in nativeAnimations.Where(x => string.Equals(x.SkeletonId, entry.SkeletonId, StringComparison.OrdinalIgnoreCase)))
                    context.Add(ResourceType.Model, $"animations/{CleanPath(entry.Path)}/{CleanPath(clip.Path)}", new ModelLoader(entry.Id, clip.Id));
            }
            InvalidateOldPreviews();
            PedestrianRuntime.Start();
            VehicleRuntime.Start();
            IsMounted = true;
            Log.Info($"Mounted {catalog.Count:N0} GTA assets as {Resources.Count:N0} s&box resources.");
        }
        catch (Exception e)
        {
            Log.Warning(e, "SADE mount failed");
            Reader?.Dispose();
            readerContext?.Unload();
            readerContext = null;
            IsMounted = false;
        }
        return Task.CompletedTask;
    }

    internal string? PathFor(AssetKind kind, string? id)
    {
        if (id is null) return null;
        if (paths.TryGetValue(((int)kind, id.ToLowerInvariant()), out var value)) return value;
        // Package references can omit the export suffix; resolve the catalog identity once.
        // Do not capture the contract enum in a compiler-generated closure field.
        foreach (var entry in entries.Values)
            if (entry.Kind == kind && entry.Id.Split('.')[0].Equals(id.Split('.')[0], StringComparison.OrdinalIgnoreCase))
                return paths.GetValueOrDefault(((int)kind, entry.Id.ToLowerInvariant()));
        return null;
    }

    internal string ModelPath(string id) => PathFor(AssetKind.SkeletalMesh, id) ?? PathFor(AssetKind.StaticMesh, id) ?? throw new KeyNotFoundException(id);
    private void InvalidateOldPreviews()
    {
        // The engine caches mount thumbnails by path alone. Converted geometry can change
        // without changing that stable path, so invalidate only this mount's icons on updates.
        var version = Reader.Fingerprint + "/" + typeof(SadeMount).Module.ModuleVersionId;
        const string key = "sade/preview-version";
        if (FileSystem.Cache.TryGet(key, out var previous) && System.Text.Encoding.UTF8.GetString(previous) == version) return;
        foreach (var loader in Resources)
            if (loader.Type == ResourceType.Model) FileSystem.Cache.Remove($"preview/icons/{loader.Path.Md5()}.png");
        FileSystem.Cache.Set(key, System.Text.Encoding.UTF8.GetBytes(version));
    }
    internal ModelData GetModelData(string id)
    {
        if (!modelData.TryGetValue(id, out var value)) modelData[id] = value = Reader.LoadModel(id);
        return value;
    }
    internal void SetWalkSpeed(string id, float speed) => walkSpeeds[id] = speed;
    internal float WalkSpeed(string id) => walkSpeeds.GetValueOrDefault(id, 45);
    internal void Report(Diagnostic diagnostic)
    {
        if (diagnostic.Error) Log.Warning($"{diagnostic.Asset}: {diagnostic.Message}");
        else Log.Info($"{diagnostic.Asset}: {diagnostic.Message}");
    }
    protected override void Shutdown()
    {
        PedestrianRuntime.Stop();
        VehicleRuntime.Stop();
        weapons.Clear();
        paths.Clear(); entries.Clear(); modelData.Clear(); walkSpeeds.Clear();
        Reader?.Dispose(); Reader = null!;
        readerContext?.Unload(); readerContext = null;
        IsMounted = false;
    }

    private static string CleanPath(string path)
    {
        path = path.Replace('\\', '/').Trim('/').ToLowerInvariant();
        if (path.StartsWith("game/")) path = path[5..];
        foreach (var suffix in new[] { ".uasset", ".ifp" }) if (path.EndsWith(suffix)) path = path[..^suffix.Length];
        return path;
    }
    private static string? Category(AssetKind kind) => kind switch
    {
        AssetKind.StaticMesh or AssetKind.SkeletalMesh => "models", AssetKind.Texture => "textures",
        AssetKind.Material => "materials", AssetKind.Sound => "sounds", _ => null
    };
    private static ResourceType TypeFor(AssetKind kind) => kind switch
    {
        AssetKind.StaticMesh or AssetKind.SkeletalMesh => ResourceType.Model, AssetKind.Texture => ResourceType.Texture,
        AssetKind.Material => ResourceType.Material, AssetKind.Sound => ResourceType.Sound, _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static string Extension(AssetKind kind) => kind switch
    {
        AssetKind.StaticMesh or AssetKind.SkeletalMesh => ".vmdl", AssetKind.Texture => ".vtex",
        AssetKind.Material => ".vmat", AssetKind.Sound => ".vsnd", _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
