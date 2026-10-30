using System.Diagnostics;
using System.Threading;
using Sandbox;
using Sade.Contracts;
using N = System.Numerics;

namespace Sade.Mount;

/// <summary>Restores local world streaming around a serialized, component-free map anchor.</summary>
internal static class WorldRuntime
{
    internal const string Marker = "sade_world";
    internal const string LoadingTag = "sade_world_loading";
    internal const string GeometryTag = "sade_world_geometry";
    private static Session? session;
    private static WorldSpatialIndex? index;
    internal static N.Vector3 Spawn(string start) => start switch
    {
        "fierro" => new(-198500, -13800, 3650),
        "venturas" => new(202500, -156000, 1250),
        _ => new(249500, 168800, 1400)
    };
    internal static void SetIndex(WorldSpatialIndex value) => Volatile.Write(ref index, value);
    internal static void Start(SadeMount mount) => session ??= new Session(mount);
    internal static void Stop() { Interlocked.Exchange(ref session, null)?.Dispose(); index = null; }

    private sealed class Session : IDisposable
    {
        private readonly SadeMount mount;
        private readonly Dictionary<GameObject, State> states = [];
        private readonly WorldAssets assets;
        private readonly Timer timer;
        private int queued;
        private bool stopped;
        internal Session(SadeMount mount)
        {
            this.mount = mount; assets = new(mount);
            assets.ModelDecoded = model => Volatile.Read(ref index)?.SetBounds(model);
            assets.Needed = key => states.Values.Any(s => s.Needs(key));
            using (ExecutionContext.SuppressFlow()) timer = new Timer(_ => Queue(), null, 0, 250);
        }
        private void Queue()
        {
            if (stopped || Interlocked.Exchange(ref queued, 1) != 0) return;
            try
            {
                GameTask.MainThread().GetAwaiter().OnCompleted(() =>
                {
                    Interlocked.Exchange(ref queued, 0);
                    if (stopped) return;
                    try { Observe(); }
                    catch (Exception e) { mount.Report(new("world runtime", e.Message, true)); }
                });
            }
            catch { Interlocked.Exchange(ref queued, 0); }
        }
        private void Observe()
        {
            var scene = Game.ActiveScene;
            foreach (var pair in states.Where(p => !p.Key.IsValid() || !p.Key.Scene.IsValid || p.Key.Scene != scene).ToArray())
            { pair.Value.Dispose(); states.Remove(pair.Key); }
            if (scene is not { IsValid: true } || scene.IsLoading) return;
            var cameras = scene.GetAllComponents<CameraComponent>().ToArray();
            var gameCamera = cameras.Any(c => c.Active && c.IsMainCamera && !c.GameObject.Tags.Has("sade_world_preview_camera", false));
            foreach (var camera in cameras.Where(c => c.GameObject.Tags.Has("sade_world_preview_camera", false))) camera.Enabled = !gameCamera;
            foreach (var root in scene.GetAllObjects(false).Where(o => o.Tags.Has(Marker, false)).ToArray())
            {
                if (states.ContainsKey(root)) continue;
                if (Volatile.Read(ref index) is not { } spatial) { _ = mount.PrepareWorld(); continue; }
                states.Add(root, new State(root, mount, assets, spatial));
            }
        }
        public void Dispose()
        {
            stopped = true; timer.Dispose();
            void Cleanup() { foreach (var state in states.Values) state.Dispose(); states.Clear(); assets.Dispose(); }
            var dispatch = GameTask.MainThread().GetAwaiter();
            if (dispatch.IsCompleted) Cleanup(); else dispatch.OnCompleted(Cleanup);
        }
    }

    private sealed class State : IDisposable
    {
        private sealed record Instance(GameObject Object, ModelCollider? Collider, WorldAssets.Asset Asset);
        private readonly GameObject root;
        private readonly SadeMount mount;
        private readonly WorldAssets assets;
        private readonly WorldSpatialIndex spatial;
        private readonly Dictionary<int, Instance> instances = [];
        private readonly HashSet<int> resident = [];
        private List<(int Index, float DistanceSquared)> wanted = [];
        private Queue<(int Index, float DistanceSquared)> creates = [];
        private HashSet<string> needed = new(StringComparer.Ordinal);
        private List<N.Vector3> focus = [];
        private readonly IDisposable hook;
        private GameObject? deck;
        private double planAt;
        private bool disposed;
        internal int ResidentCount => instances.Count;
        internal bool Needs(string key) => needed.Contains(key);
        internal double LastUpdateMilliseconds { get; private set; }
        internal State(GameObject root, SadeMount mount, WorldAssets assets, WorldSpatialIndex spatial)
        {
            this.root = root; this.mount = mount; this.assets = assets; this.spatial = spatial;
            deck = root.Children.FirstOrDefault(o => o.Tags.Has(LoadingTag, false));
            hook = root.Scene.AddHook(GameObjectSystem.Stage.StartUpdate, 5, Update, "SADE", "World streaming");
        }
        private void Update()
        {
            if (disposed || !root.IsValid() || !root.Active || root.Scene.IsLoading) return;
            var watch = Stopwatch.StartNew();
            var budget = Math.Clamp(mount.Settings.WorldFrameBudgetMilliseconds, .5f, 10);
            using var scope = root.Scene.Push();
            if (Time.NowDouble >= planAt || wanted.Count == 0)
            {
                focus = Focus();
                wanted = spatial.Query(focus, mount.Settings.WorldViewDistanceMeters, resident, 1.2f);
                var keep = wanted.Select(p => p.Index).ToHashSet();
                foreach (var id in resident.Where(id => !keep.Contains(id)).ToArray()) Remove(id);
                creates = new(wanted.Where(p => !resident.Contains(p.Index)));
                needed = wanted.Select(p => WorldAssets.Key(spatial.Data.Placements[p.Index])).ToHashSet(StringComparer.Ordinal);
                var collisionRadius = Math.Clamp(mount.Settings.WorldCollisionDistanceMeters, 40, 1000) * 100;
                foreach (var entry in wanted)
                    if (instances.TryGetValue(entry.Index, out var loaded) && loaded.Collider.IsValid())
                        loaded.Collider.Enabled = spatial.Data.Placements[entry.Index].Collision && entry.DistanceSquared <= collisionRadius * collisionRadius;
                planAt = Time.NowDouble + .25;
            }
            assets.Tick(budget * .65f);
            var collisionDistance = Math.Clamp(mount.Settings.WorldCollisionDistanceMeters, 40, 1000) * 100;
            var created = 0;
            var attempts = Math.Min(creates.Count, 64);
            while (attempts-- > 0 && creates.TryDequeue(out var next))
            {
                if (watch.Elapsed.TotalMilliseconds >= budget) { creates.Enqueue(next); break; }
                var (id, distance) = next;
                var placement = spatial.Data.Placements[id];
                var collision = placement.Collision && distance <= collisionDistance * collisionDistance;
                if (instances.ContainsKey(id)) continue;
                var key = WorldAssets.Key(placement);
                if (assets.Failed(key)) continue;
                if (!assets.TryAcquire(key, out var asset)) { assets.Request(placement); creates.Enqueue(next); continue; }
                var obj = new GameObject(root, true, System.IO.Path.GetFileNameWithoutExtension(placement.ModelId.Split('.')[0]))
                {
                    LocalTransform = CoordinateSpace.Transform(placement.Transform),
                    Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked | GameObjectFlags.Hidden
                };
                obj.Tags.Add(GeometryTag);
                if (placement.Visible) obj.AddComponent<ModelRenderer>().Model = asset.Model;
                ModelCollider? collider = null;
                if (asset.HasCollision) { collider = obj.AddComponent<ModelCollider>(); collider.Model = asset.Model; collider.Enabled = collision; }
                instances.Add(id, new(obj, collider, asset)); resident.Add(id);
                if (++created >= 24 || watch.Elapsed.TotalMilliseconds >= budget) break;
            }
            RemoveLoadingDeck();
            LastUpdateMilliseconds = watch.Elapsed.TotalMilliseconds;
        }
        private List<N.Vector3> Focus()
        {
            var result = new List<N.Vector3>();
            if (root.Scene.Camera is { IsValid: true } camera) result.Add(CoordinateSpace.SourcePosition(root.WorldTransform.PointToLocal(camera.WorldPosition)));
            foreach (var player in root.Scene.GetAllComponents<PlayerController>().Where(p => p.IsValid && p.Active))
                result.Add(CoordinateSpace.SourcePosition(root.WorldTransform.PointToLocal(player.WorldPosition)));
            if (result.Count == 0)
            {
                var start = root.Tags.TryGetAll(false).FirstOrDefault(t => t.StartsWith("sade_world_", StringComparison.Ordinal));
                result.Add(Spawn(start?.Replace("sade_world_", "") ?? "grove"));
            }
            return result;
        }
        private void RemoveLoadingDeck()
        {
            if (!deck.IsValid()) return;
            var at = deck.WorldPosition + Vector3.Up * 8;
            var ground = root.Scene.Trace.Ray(at + Vector3.Up * 32, at - Vector3.Up * 192)
                .WithoutTags(LoadingTag, "player", "trigger", "sade_npc").Run();
            if (ground.Hit && ground.GameObject.IsValid() && ground.GameObject.Tags.Has(GeometryTag)) { deck.Destroy(); deck = null; }
        }
        private void Remove(int id)
        {
            if (!instances.Remove(id, out var instance)) return;
            if (instance.Object.IsValid()) instance.Object.Destroy();
            assets.Release(instance.Asset); resident.Remove(id);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; hook.Dispose();
            foreach (var id in resident.ToArray()) Remove(id);
        }
    }
}
