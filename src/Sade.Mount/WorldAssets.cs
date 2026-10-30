using System.Diagnostics;
using Sandbox;
using Sade.Contracts;

namespace Sade.Mount;

/// <summary>One background decode and incremental GPU uploads, with shared textures and an idle LRU budget.</summary>
internal sealed class WorldAssets(SadeMount mount) : IDisposable
{
    internal sealed class Asset(Model model, string[] materials, long bytes, bool collision, WorldPropData? prop)
    {
        internal readonly Model Model = model;
        internal readonly string[] Materials = materials;
        internal readonly long Bytes = bytes;
        internal readonly bool HasCollision = collision;
        internal readonly WorldPropData? Prop = prop;
        internal int Users;
        internal long LastUse;
        internal double ProtectedUntil;
    }
    private sealed class Shared<T>(T value, long bytes, string[] dependencies) where T : class
    {
        internal readonly T Value = value;
        internal readonly long Bytes = bytes;
        internal readonly string[] Dependencies = dependencies;
        internal int Users;
    }
    private sealed record Prepared(ModelData Model, string?[] Overrides, Dictionary<string, MaterialData> Materials, Dictionary<string, TextureData> Textures);
    private readonly Dictionary<string, Asset> models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Shared<Material>> materials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Shared<Texture>> textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> failed = new(StringComparer.Ordinal);
    private readonly string prefix = "sade_world/" + Guid.NewGuid().ToString("N") + "/";
    private Task<Prepared>? pending;
    private CancellationTokenSource? cancellation;
    private Prepared? prepared;
    private string? pendingKey;
    private Queue<string> textureQueue = [], materialQueue = [];
    private readonly List<string> heldMaterials = [];
    private readonly List<string> buildTextures = [];
    private long serial, bytes;
    private bool disposed;
    internal long ResidentBytes => bytes;
    internal int ModelCount => models.Count;
    internal bool Busy => pending is not null || prepared is not null;
    internal Action<ModelData>? ModelDecoded;
    internal Func<string, bool>? Needed;

    internal static string Key(WorldPlacementData p) => p.Materials.Length == 0 ? p.ModelId : p.ModelId + "|" + string.Join('|', p.Materials);
    internal bool Failed(string key) => failed.Contains(key);
    internal bool TryAcquire(string key, out Asset asset)
    {
        if (!models.TryGetValue(key, out asset!)) return false;
        asset.Users++; asset.LastUse = ++serial; return true;
    }
    internal void Release(Asset asset) { asset.Users = Math.Max(0, asset.Users - 1); asset.LastUse = ++serial; }

    internal void Request(WorldPlacementData placement)
    {
        var key = Key(placement);
        if (disposed || Busy || models.ContainsKey(key) || failed.Contains(key)) return;
        pendingKey = key;
        cancellation = new();
        var token = cancellation.Token;
        var cachedTextures = textures.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The reader is isolated and serialized by its own gate. No engine/GPU APIs run here.
        pending = Task.Run(() => Decode(placement, cachedTextures, token), token);
    }

    private Prepared Decode(WorldPlacementData placement, HashSet<string> cachedTextures, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var model = mount.Reader.LoadWorldModel(placement.ModelId);
        var mats = new Dictionary<string, MaterialData>(StringComparer.OrdinalIgnoreCase);
        var tex = new Dictionary<string, TextureData>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in model.Parts.Where(p => p.Lod <= 3).Select(p => WorldModelBuilder.MaterialId(p, placement.Materials)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            MaterialData material;
            try { material = mount.Reader.LoadMaterial(id); mats[id] = material; }
            catch (Exception e) { mount.Report(new(id, "World material unavailable: " + e.Message, true)); continue; }
            foreach (var texture in TextureIds(material))
            {
                token.ThrowIfCancellationRequested();
                if (tex.ContainsKey(texture) || cachedTextures.Contains(texture)) continue;
                try
                {
                    var data = mount.Reader.LoadTexture(texture);
                    var level = 0; var limit = Math.Clamp(mount.Settings.WorldTextureMaxSize, 128, 2048);
                    while (level + 1 < data.Mips.Length && Math.Max(data.Mips[level].Width, data.Mips[level].Height) > limit) level++;
                    tex[texture] = data with { Mips = data.Mips[level..] };
                }
                catch (Exception e) { mount.Report(new(texture, "World texture unavailable: " + e.Message, true)); }
            }
        }
        return new(model, placement.Materials, mats, tex);
    }

    internal void Tick(float budgetMilliseconds)
    {
        if (disposed) return;
        var timer = Stopwatch.StartNew();
        try
        {
            if (pendingKey is not null && Needed?.Invoke(pendingKey) == false)
            {
                cancellation?.Cancel();
                if (prepared is not null) throw new OperationCanceledException();
            }
            if (pending is { IsCompleted: true })
            {
                prepared = pending.GetAwaiter().GetResult(); pending = null;
                ModelDecoded?.Invoke(prepared.Model);
                textureQueue = new(prepared.Textures.Keys.Where(id => !textures.ContainsKey(id)));
                materialQueue = new(prepared.Materials.Keys);
            }
            if (prepared is null) { if (pending is null) Trim(); return; }
            do
            {
                if (textureQueue.TryDequeue(out var textureId))
                {
                    var data = prepared.Textures[textureId];
                    var texture = TextureLoader.Build(data, prefix + "texture/" + ++serial);
                    var size = data.Mips.Sum(m => (long)m.Data.Length);
                    textures[textureId] = new(texture, size, []); bytes += size;
                    buildTextures.Add(textureId);
                }
                else if (materialQueue.TryDequeue(out var materialId))
                {
                    if (!materials.TryGetValue(materialId, out var shared))
                    {
                        var data = prepared.Materials[materialId];
                        var dependencies = TextureIds(data).Where(textures.ContainsKey).ToArray();
                        var material = MaterialLoader.Build(data, prefix + "material/" + ++serial,
                            (id, fallback) => id is not null && textures.TryGetValue(id, out var texture) ? texture.Value : fallback);
                        materials[materialId] = shared = new(material, 0, dependencies);
                        foreach (var id in dependencies) textures[id].Users++;
                    }
                    shared.Users++; heldMaterials.Add(materialId);
                }
                else
                {
                    var data = prepared.Model;
                    var model = WorldModelBuilder.Build(data, prefix + "model/" + ++serial, prepared.Overrides,
                        id => id is not null && materials.TryGetValue(id, out var material) ? material.Value : Material.Load("materials/dev/gray.vmat"),
                        Math.Max(2000, mount.Settings.LodDistance));
                    var geometry = data.Parts.Sum(p => (long)p.Vertices.Length * 64 + (long)p.Indices.Length * 4)
                        + data.Collision.Vertices.LongLength * 12 + data.Collision.Indices.LongLength * 4;
                    models[pendingKey!] = new(model, heldMaterials.ToArray(), geometry, !data.Collision.IsEmpty, data.WorldProp) { LastUse = ++serial, ProtectedUntil = Time.NowDouble + 3 };
                    bytes += geometry; heldMaterials.Clear(); buildTextures.Clear(); prepared = null; pendingKey = null;
                    cancellation?.Dispose(); cancellation = null;
                    Trim(); return;
                }
            } while (timer.Elapsed.TotalMilliseconds < budgetMilliseconds);
        }
        catch (Exception e)
        {
            var source = prepared?.Model.Id ?? pendingKey ?? "world model";
            if (e is not OperationCanceledException)
            {
                if (pendingKey is not null) failed.Add(pendingKey);
                mount.Report(new(source, "World asset failed: " + e.GetBaseException().Message, true));
            }
            foreach (var id in heldMaterials) ReleaseMaterial(id);
            foreach (var id in buildTextures) if (textures.TryGetValue(id, out var texture) && texture.Users == 0) { bytes -= texture.Bytes; textures.Remove(id); }
            heldMaterials.Clear(); buildTextures.Clear(); pending = null; prepared = null; pendingKey = null;
            cancellation?.Dispose(); cancellation = null;
        }
    }

    private void Trim()
    {
        var limit = Math.Clamp(mount.Settings.WorldCacheMegabytes, 64, 4096) * 1024L * 1024;
        // Active geometry stays pinned, even when a crowded area exceeds the idle-cache budget.
        foreach (var pair in models.Where(p => p.Value.Users == 0 && Time.NowDouble >= p.Value.ProtectedUntil).OrderBy(p => p.Value.LastUse).ToArray())
        {
            if (bytes <= limit && models.Count <= 256) break;
            models.Remove(pair.Key); bytes -= pair.Value.Bytes;
            foreach (var id in pair.Value.Materials) ReleaseMaterial(id);
        }
    }
    private void ReleaseMaterial(string id)
    {
        if (!materials.TryGetValue(id, out var material) || --material.Users > 0) return;
        materials.Remove(id);
        foreach (var textureId in material.Dependencies)
        {
            if (!textures.TryGetValue(textureId, out var texture) || --texture.Users > 0) continue;
            textures.Remove(textureId); bytes -= texture.Bytes;
        }
    }
    private static IEnumerable<string> TextureIds(MaterialData m) => new[] { m.ColorTexture, m.NormalTexture, m.RoughnessTexture,
        m.MetallicTexture, m.PackedTexture, m.OpacityTexture, m.EmissiveTexture }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
    public void Dispose()
    {
        disposed = true; ModelDecoded = null; Needed = null; cancellation?.Cancel();
        models.Clear(); materials.Clear(); textures.Clear(); prepared = null;
        heldMaterials.Clear(); buildTextures.Clear(); bytes = 0;
        // Observe failures if a read was still finishing during map teardown.
        if (pending is { } task) _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        pending = null;
    }
}
