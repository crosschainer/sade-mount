using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Versions;
using Sade.Contracts;
using Sade.Reader.Unreal;

namespace Sade.Reader;

/// <summary>Reads the installed game's PAKs. The host loads this assembly in its own dependency context.</summary>
public sealed partial class GameReader : IGameReader
{
    private const string CacheVersion = "sade-reader-6-cue4parse-1.2.2.202610";
    private const long MaximumCatalogBytes = 128L * 1024 * 1024;
    private readonly object gate = new();
    private DefaultFileProvider? provider;
    private Action<Diagnostic>? report;
    private ReaderOptions? options;
    private List<AssetEntry> entries = [];
    private Dictionary<string, BankSource> banks = new(StringComparer.OrdinalIgnoreCase);
    private string[] containers = [];
    private readonly Dictionary<string, UIMGFile> loadedContainers = new(StringComparer.OrdinalIgnoreCase);
    public string Fingerprint { get; private set; } = "";
    private DefaultFileProvider Provider => provider ?? throw new InvalidOperationException("Initialize the game reader first.");

    public IReadOnlyList<AssetEntry> Initialize(ReaderOptions options, Action<Diagnostic>? report = null)
    {
        lock (gate)
        {
            Dispose();
            this.options = options;
            this.report = report;
            var pakDirectory = Path.Combine(Path.GetFullPath(options.GameDirectory), "Gameface", "Content", "Paks");
            if (!Directory.Exists(pakDirectory)) throw new DirectoryNotFoundException($"GTA SA Definitive Edition PAK directory was not found: {pakDirectory}");
            var paks = Directory.GetFiles(pakDirectory, "*.pak").Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paks.Length == 0) throw new FileNotFoundException("The GTA SA Definitive Edition installation contains no PAK archives.");
            Fingerprint = ComputeFingerprint(paks);
            var oodlePath = Path.Combine(options.GameDirectory, "Engine", "Binaries", "ThirdParty", "Oodle", "Win64", "oo2core_8_win64.dll");
            if (!File.Exists(oodlePath)) throw new FileNotFoundException("The installed game's Oodle decoder is missing.", oodlePath);
            // The instance overload cannot download a decoder. The game's own DLL stays in its installation.
            OodleHelper.Initialize(new OodleDotNet.Oodle(oodlePath));
            ObjectTypeRegistry.RegisterClass("IMGFile", typeof(UIMGFile));
            provider = new DefaultFileProvider(pakDirectory, SearchOption.TopDirectoryOnly,
                new VersionContainer(EGame.GAME_GTATheTrilogyDefinitiveEdition), StringComparer.OrdinalIgnoreCase);
            provider.ReadShaderMaps = false;
            provider.ReadScriptData = false;
            provider.Initialize();
            provider.Mount();
            if (provider.MountedVfs.Count != paks.Length)
                throw new InvalidDataException($"Only {provider.MountedVfs.Count}/{paks.Length} archives could be mounted. This game build may require different archive support.");
            if (!ReadCatalogCache()) BuildCatalog();
            InitializePedMetadata();
            report?.Invoke(new Diagnostic("mount", $"Indexed {entries.Count:N0} assets from {provider.MountedVfs.Count} PAKs; fingerprint {Fingerprint[..12]}."));
            return entries;
        }
    }

    private void BuildCatalog()
    {
        var registryPath = Provider.Files.Keys.FirstOrDefault(p => p.Equals("Gameface/AssetRegistry.bin", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("The game's AssetRegistry.bin is missing.");
        using var stream = Provider.CreateReader(registryPath);
        var registry = new FAssetRegistryState(stream);
        var containerIds = new List<string>();
        foreach (var item in registry.PreallocatedAssetDataBuffers)
        {
            var id = CanonicalId(item.ObjectPath);
            var kind = item.AssetClass.Text switch
            {
                "StaticMesh" => AssetKind.StaticMesh,
                "SkeletalMesh" => AssetKind.SkeletalMesh,
                "Texture2D" => AssetKind.Texture,
                "MaterialInstanceConstant" or "Material" => AssetKind.Material,
                "SoundWave" => AssetKind.Sound,
                "AnimSequence" => AssetKind.Animation,
                _ => (AssetKind?)null
            };
            if (item.AssetClass.Text == "IMGFile") containerIds.Add(id);
            if (kind is null) continue;
            var skeletonTag = item.TagsAndValues.FirstOrDefault(t => t.Key.Text == "Skeleton").Value;
            entries.Add(new AssetEntry(id, ResourcePath(id), kind.Value, item.AssetName.Text)
            {
                SkeletonId = string.IsNullOrEmpty(skeletonTag) || skeletonTag == "None" ? null : CanonicalId(skeletonTag)
            });
        }
        containers = containerIds.ToArray();
        foreach (var path in Provider.Files.Keys.Where(p => p.EndsWith(".ifp", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            AddBank(path, null, Path.GetFileName(path));
        foreach (var container in containers.Where(p => p.EndsWith("/anim.anim", StringComparison.OrdinalIgnoreCase)))
            foreach (var child in GetContainer(container).Entries.Keys.Where(p => p.EndsWith(".ifp", StringComparison.OrdinalIgnoreCase)))
                AddBank(container, child, child);
        entries = entries.DistinctBy(e => e.Id, StringComparer.OrdinalIgnoreCase).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        WriteCatalogCache();
    }

    private void AddBank(string source, string? child, string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
        var id = $"ifp:{name}";
        if (banks.ContainsKey(id)) return;
        banks[id] = new BankSource(source, child);
        entries.Add(new AssetEntry(id, $"animations/ifp/{name}", AssetKind.AnimationBank, name));
    }

    internal UIMGFile GetContainer(string id)
    {
        if (loadedContainers.TryGetValue(id, out var container)) return container;
        return loadedContainers[id] = Provider.LoadPackageObject<UIMGFile>(id);
    }

    private static string ComputeFingerprint(IEnumerable<string> paks)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(CacheVersion));
        foreach (var path in paks)
        {
            var file = new FileInfo(path);
            hash.AppendData(Encoding.UTF8.GetBytes($"{file.FullName.ToLowerInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}"));
            using var stream = File.OpenRead(path);
            stream.Position = Math.Max(0, stream.Length - 512);
            var footer = new byte[stream.Length - stream.Position];
            stream.ReadExactly(footer);
            hash.AppendData(footer);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private string CachePath => Path.Combine(options!.CacheDirectory, $"catalog-{Fingerprint}.json");
    private bool ReadCatalogCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return false;
            using var stream = File.OpenRead(CachePath);
            if (stream.Length > MaximumCatalogBytes) throw new InvalidDataException("Metadata cache exceeds the 128 MiB limit.");
            var cache = JsonSerializer.Deserialize<CatalogCache>(stream, new JsonSerializerOptions { MaxDepth = 16 });
            if (cache is null || cache.Version != CacheVersion || cache.Fingerprint != Fingerprint) return false;
            if (cache.Entries is not { Count: > 0 and <= 1_000_000 } || cache.Banks is null || cache.Banks.Count > 10_000 || cache.Containers is null || cache.Containers.Length > 10_000)
                throw new InvalidDataException("Metadata cache has missing or invalid collections.");
            var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in cache.Entries)
                if (entry is null || !Enum.IsDefined(entry.Kind) || !ValidCacheText(entry.Id) || !ValidCacheText(entry.Path) || !ValidCacheText(entry.Name) ||
                    entry.Path.Split('/').Any(p => p == "..") || !identifiers.Add(entry.Id))
                    throw new InvalidDataException("Metadata cache contains an invalid or duplicate asset record.");
            foreach (var (id, bank) in cache.Banks)
                if (!id.StartsWith("ifp:", StringComparison.OrdinalIgnoreCase) || !identifiers.Contains(id) || bank is null || !ValidCacheText(bank.Source) || (bank.Child is not null && !ValidCacheText(bank.Child)))
                    throw new InvalidDataException("Metadata cache contains an invalid animation bank record.");
            if (cache.Containers.Any(id => !ValidCacheText(id))) throw new InvalidDataException("Metadata cache contains an invalid container record.");
            entries = cache.Entries;
            banks = new(cache.Banks, StringComparer.OrdinalIgnoreCase);
            containers = cache.Containers;
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            report?.Invoke(new Diagnostic("cache", $"Ignoring unreadable metadata cache: {e.Message}"));
            return false;
        }
    }
    private static bool ValidCacheText(string? value) => value is { Length: > 0 and <= 4096 } && !value.Contains('\0');
    private void WriteCatalogCache()
    {
        try
        {
            Directory.CreateDirectory(options!.CacheDirectory);
            var temporary = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new CatalogCache(CacheVersion, Fingerprint, entries, banks, containers)));
            File.Move(temporary, CachePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report?.Invoke(new Diagnostic("cache", $"Metadata cache could not be written: {e.Message}"));
        }
    }

    internal static string CanonicalId(string path)
    {
        path = path.Replace('\\', '/');
        var quote = path.IndexOf('\'');
        if (quote >= 0) path = path[(quote + 1)..].TrimEnd('\'');
        if (path.StartsWith("Gameface/Content/", StringComparison.OrdinalIgnoreCase)) path = "/Game/" + path[17..];
        if (path.StartsWith("Engine/Content/", StringComparison.OrdinalIgnoreCase)) path = "/Engine/" + path[15..];
        return path;
    }
    private static string ResourcePath(string id)
    {
        var dot = id.LastIndexOf('.');
        var package = dot >= 0 ? id[..dot] : id;
        if (package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) package = package[6..];
        return package.TrimStart('/').ToLowerInvariant();
    }
    private static string? ObjectId(UObject? value) => value is null ? null : CanonicalId(value.GetPathName());

    public void Dispose()
    {
        lock (gate)
        {
            provider?.Dispose();
            provider = null;
            loadedContainers.Clear();
            loadedBanks.Clear();
            legacyCollision = null;
            pedestrianMetadata = null;
            weapons = null;
            vehicles = null;
            entries = [];
            banks.Clear();
            containers = [];
        }
    }
    private sealed record BankSource(string Source, string? Child);
    private sealed record CatalogCache(string Version, string Fingerprint, List<AssetEntry> Entries, Dictionary<string, BankSource> Banks, string[] Containers);
}
