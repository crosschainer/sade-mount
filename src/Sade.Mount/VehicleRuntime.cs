using System.Collections.Concurrent;
using System.Threading;
using Sandbox;
using Sade.Contracts;

namespace Sade.Mount;

/// <summary>Restores mount-owned driving controllers around ordinary serialized engine components.</summary>
internal static class VehicleRuntime
{
    internal const string Marker = "sade_vehicle";
    internal sealed record Profile(VehicleData Data, string ModelPath, string? WheelModelPath);
    private static readonly ConcurrentDictionary<string, Profile> profiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Sandbox.Diagnostics.Logger Log = new("SADE vehicles");
    private static Session? current;
    internal static void Register(string modelPath, Profile profile) => profiles[Normalize(modelPath)] = profile;
    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
    internal static void Start() { if (current is null) { current = new Session(); current.Start(); } }
    internal static void Stop() { Interlocked.Exchange(ref current, null)?.Stop(); profiles.Clear(); }

    internal static void BuildPrefabWheels(GameObject root, Profile profile)
    {
        if (profile.WheelModelPath is null) return;
        var model = Model.Load(profile.WheelModelPath);
        if (model is null || model.IsError) return;
        var originalRadius = Math.Max(model.Bounds.Size.y, model.Bounds.Size.z) / 2;
        foreach (var socket in profile.Data.Sockets.Where(s => IsWheel(s.Name)))
        {
            var wheel = new GameObject(root, true, "Wheel " + socket.Name);
            wheel.Tags.Add("sade_" + socket.Name);
            wheel.LocalPosition = CoordinateSpace.Position(socket.Transform.Position);
            wheel.LocalRotation = socket.Name.StartsWith("wheel_l", StringComparison.OrdinalIgnoreCase) ? Rotation.FromYaw(180) : Rotation.Identity;
            var radius = (IsFrontWheel(socket.Name) ? profile.Data.WheelRadiusFront : profile.Data.WheelRadiusRear) * CoordinateSpace.UnitsPerCentimeter;
            wheel.LocalScale = Vector3.One * (radius > 0 && originalRadius > 0 ? radius / originalRadius : 1);
            wheel.Components.Create<ModelRenderer>().Model = model;
        }
    }
    internal static bool IsWheel(string name) => name.StartsWith("wheel_", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("_dummy", StringComparison.OrdinalIgnoreCase);
    internal static bool IsFrontWheel(string name) => name.EndsWith("f", StringComparison.OrdinalIgnoreCase) || name.Contains("front", StringComparison.OrdinalIgnoreCase);

    private sealed class Session
    {
        private readonly Dictionary<Scene, SceneState> scenes = new();
        private Timer? timer;
        private volatile bool stopped;
        private long observation;
        private int reportedError;
        internal void Start()
        {
            using (ExecutionContext.SuppressFlow()) timer = new Timer(_ => Queue(), null, 0, 350);
        }
        private void Queue()
        {
            if (stopped) return;
            var version = Interlocked.Increment(ref observation);
            try
            {
                GameTask.MainThread().GetAwaiter().OnCompleted(() =>
                {
                    if (stopped || version != Volatile.Read(ref observation)) return;
                    try { Observe(); }
                    catch (Exception e) { if (Interlocked.Exchange(ref reportedError, 1) == 0) Log.Warning(e, "Vehicle discovery failed"); }
                });
            }
            catch (Exception) { /* The main dispatcher can be unavailable during engine startup/shutdown. */ }
        }
        private void Observe()
        {
            foreach (var pair in scenes.Where(p => !p.Key.IsValid).ToArray()) { pair.Value.Dispose(); scenes.Remove(pair.Key); }
            var scene = Game.ActiveScene;
            if (scene is not { IsValid: true, IsEditor: false } || scene.IsLoading) return;
            if (!scenes.TryGetValue(scene, out var state)) { state = new SceneState(scene, () => stopped); scenes.Add(scene, state); }
            state.Discover();
        }
        internal void Stop()
        {
            stopped = true; timer?.Dispose(); timer = null;
            void Cleanup() { foreach (var state in scenes.Values) state.Dispose(); scenes.Clear(); }
            var awaiter = GameTask.MainThread().GetAwaiter();
            if (awaiter.IsCompleted) Cleanup(); else awaiter.OnCompleted(Cleanup);
        }
    }

    private sealed class SceneState : IDisposable
    {
        private readonly Scene scene;
        private readonly Func<bool> stopped;
        private readonly List<NativeVehicle> vehicles = [];
        private readonly IDisposable updateHook, fixedHook, loadHook;
        private bool disposed;
        internal SceneState(Scene scene, Func<bool> stopped)
        {
            this.scene = scene; this.stopped = stopped;
            updateHook = scene.AddHook(GameObjectSystem.Stage.StartUpdate, 15, Update, "SADE", "Vehicle seats and wheels");
            fixedHook = scene.AddHook(GameObjectSystem.Stage.StartFixedUpdate, 15, FixedUpdate, "SADE", "Vehicle suspension and propulsion");
            loadHook = scene.AddHook(GameObjectSystem.Stage.SceneLoaded, 15, Discover, "SADE", "Restore mounted vehicles");
        }
        private bool Available => !disposed && !stopped() && scene.IsValid && !scene.IsLoading && !scene.IsEditor;
        internal void Discover()
        {
            if (!Available) return;
            vehicles.RemoveAll(v => !v.IsValid);
            PromoteSpawnedModels();
            foreach (var body in scene.GetAllComponents<Rigidbody>().ToArray())
            {
                var root = body.GameObject;
                if (!root.Tags.Has(Marker, false)) continue;
                var existing = root.Components.Get<NativeVehicle>(FindMode.EverythingInSelf);
                if (existing is { IsValid: true }) { if (!vehicles.Contains(existing)) vehicles.Add(existing); continue; }
                var renderer = root.Components.Get<SkinnedModelRenderer>(FindMode.EnabledInSelfAndDescendants);
                if (renderer?.Model is not { IsValid: true } model || !profiles.TryGetValue(Normalize(model.Name), out var profile)) continue;
                var controller = root.Components.Create<NativeVehicle>(false);
                controller.Flags = ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotCloned | ComponentFlags.NotNetworked;
                controller.Initialize(profile, body, renderer); controller.Enabled = true; vehicles.Add(controller);
            }
        }

        private void PromoteSpawnedModels()
        {
            foreach (var original in scene.GetAllComponents<ModelRenderer>().ToArray())
            {
                if (!original.IsValid || !original.Active || original.IsProxy) continue;
                var root = original.GameObject;
                if (root.Tags.Has(Marker) || original.Model is not { IsValid: true } model ||
                    !profiles.TryGetValue(Normalize(model.Name), out var profile)) continue;
                var tint = original.Tint;
                var groups = original.BodyGroups;
                var materials = original.MaterialGroup;
                var materialOverride = original.MaterialOverride;
                // Thumbnails show the complete assembled model. Driving uses the original body
                // plus independently rotating wheel objects, so the preview wheels are replaced.
                var bodyModel = Model.Load(profile.ModelPath);
                if (bodyModel is null || bodyModel.IsError) continue;
                var oldBody = root.Components.Get<Rigidbody>(FindMode.EnabledInSelf);
                var velocity = oldBody is { IsValid: true } ? oldBody.Velocity : Vector3.Zero;
                var angularVelocity = oldBody is { IsValid: true } ? oldBody.AngularVelocity : Vector3.Zero;
                SpawnedProp.Release(root);
                foreach (var component in root.Components.GetAll<Component>(FindMode.EverythingInSelf).ToArray())
                {
                    if (component is not (ModelRenderer or Collider or Rigidbody or ModelPhysics)) continue;
                    component.Enabled = false;
                    component.Destroy();
                }
                root.Tags.Add(Marker);
                root.Tags.Add("sade_" + profile.Data.Family);
                var renderer = root.Components.Create<SkinnedModelRenderer>();
                renderer.Model = bodyModel;
                renderer.Tint = tint;
                renderer.BodyGroups = groups;
                renderer.MaterialGroup = materials;
                renderer.MaterialOverride = materialOverride;
                renderer.UseAnimGraph = false;
                root.Components.Create<ModelCollider>().Model = bodyModel;
                var body = root.Components.Create<Rigidbody>();
                body.MassOverride = Math.Max(1, profile.Data.MassKg);
                body.OverrideMassCenter = true;
                body.MassCenterOverride = CoordinateSpace.Position(profile.Data.CenterOfMass);
                body.LinearDamping = .05f;
                body.AngularDamping = .15f;
                body.Velocity = velocity;
                body.AngularVelocity = angularVelocity;
                BuildPrefabWheels(root, profile);
                VehicleSeats.BuildPrefab(root, profile.Data);
                if (root.Network.Active) root.Network.Refresh();
            }
        }
        private void Update()
        {
            if (!Available || !Game.IsPlaying) return;
            vehicles.RemoveAll(v => !v.IsValid);
            foreach (var vehicle in vehicles) if (vehicle.Active) vehicle.Tick(Math.Clamp(Time.Delta, 0, .1f));
        }
        private void FixedUpdate()
        {
            if (!Available || !Game.IsPlaying) return;
            foreach (var vehicle in vehicles) if (vehicle.IsValid && vehicle.Active) vehicle.FixedTick(Math.Clamp(Time.Delta, 0, .05f));
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            updateHook.Dispose(); fixedHook.Dispose(); loadHook.Dispose();
            foreach (var vehicle in vehicles) if (vehicle.IsValid) vehicle.Shutdown();
            vehicles.Clear();
        }
    }
}
