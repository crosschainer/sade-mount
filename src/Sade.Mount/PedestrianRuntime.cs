using System.Collections.Concurrent;
using System.Threading;
using Sandbox;

namespace Sade.Mount;

/// <summary>
/// Native mount-owned NPC discovery. Prefabs serialize only built-in engine components and tags;
/// the controller is recreated after cloning, scene loading, and mounting again.
/// </summary>
internal static class PedestrianRuntime
{
    internal const string Marker = "sade_npc";

    internal sealed record Profile(string Name, string Idle, string Walk, string Run,
        float WalkSpeed, float RunSpeed, string Faction)
    {
        public WeaponProfile? Weapon { get; init; }
        public float Health { get; init; } = 100;
        public float WanderRadius { get; init; } = 512;
        public string HitSequence { get; init; } = "HIT_front";
        public string DeathSequence { get; init; } = "KO_shot_front";
        public string[] EnemyFactions { get; init; } = [];
    }

    internal sealed record WeaponProfile(string Name, string ModelPath, string SoundPath,
        string? ReloadSoundPath, string FireSequence, string ReloadSequence, float Damage,
        float Range, float FireInterval, float ReloadTime, int MagazineSize, float Pitch = 1)
    {
        public Vector3 MuzzleOffset { get; init; } = new(12, 0, 0);
        public string? ReloadFinishSoundPath { get; init; }
        public float ReloadSoundTime { get; init; }
        public float ReloadFinishSoundTime { get; init; }
        public float Accuracy { get; init; } = 0.75f;
        public bool AimWithArm { get; init; }
        public string WeaponBone { get; init; } = "R_Hand";
        public string AimSequence { get; init; } = "sade_aim";
        public float FireDelay { get; init; }
        public string Kind { get; init; } = "hitscan";
        public bool CombatEnabled { get; init; } = true;
        public int PelletCount { get; init; } = 1;
        public float SpreadDegrees { get; init; }
        public float ProjectileSpeed { get; init; } = 800;
        public float ProjectileGravity { get; init; } = 600;
        public float FuseSeconds { get; init; } = 3;
        public float BlastRadius { get; init; } = 350;
        public float EffectSeconds { get; init; } = 8;
        public string? ProjectileModelPath { get; init; }
        public string? ImpactSoundPath { get; init; }
        public string? ExplosionSoundPath { get; init; }
        public bool LoopingFireSound { get; init; }
        public string? FireStartSoundPath { get; init; }
        public string? FireStopSoundPath { get; init; }
        public MeleeAttackProfile[] MeleeAttacks { get; init; } = [];
    }

    internal sealed record MeleeAttackProfile(string Sequence, float HitTime, float ChainTime, float Damage, float Radius, string HitLevel);

    private static readonly ConcurrentDictionary<string, Profile> profiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Sandbox.Diagnostics.Logger Log = new("SADE pedestrians");
    private static Session? current;

    internal static void Register(string modelPath, Profile profile)
    {
        if (!string.IsNullOrWhiteSpace(modelPath)) profiles[Normalize(modelPath)] = profile;
    }

    internal static void Start()
    {
        if (current is not null) return;
        var session = new Session();
        current = session;
        session.Start();
    }

    internal static void Stop()
    {
        var session = Interlocked.Exchange(ref current, null);
        session?.Stop();
        profiles.Clear();
    }

    internal static bool TryGetProfile(Model model, out Profile profile) =>
        profiles.TryGetValue(Normalize(model.Name), out profile!);

    private static string Normalize(string path) => path.Replace('\\', '/').Trim().ToLowerInvariant();

    private sealed class Session
    {
        private readonly Dictionary<Scene, SceneState> scenes = new();
        private Timer? timer;
        private volatile bool stopped;
        private long observation;
        private int reportedError;

        internal void Start()
        {
            // Mounting can run in menu/asset-preview context. Do not retain that context in a
            // process-lifetime observer. Only QueueObservation runs on the timer thread.
            using (ExecutionContext.SuppressFlow())
                timer = new Timer(_ => QueueObservation(), null, 0, 350);
        }

        private void QueueObservation()
        {
            if (stopped) return;
            var version = Interlocked.Increment(ref observation);
            try
            {
                // Public engine dispatcher; a fresh dispatch each time survives game session
                // changes without retaining an expired game's TaskSource or TypeLibrary.
                GameTask.MainThread().GetAwaiter().OnCompleted(() =>
                {
                    if (stopped || version != Volatile.Read(ref observation)) return;
                    try { Observe(); }
                    catch (Exception e)
                    {
                        if (Interlocked.Exchange(ref reportedError, 1) == 0)
                            Log.Warning(e, "SADE pedestrian discovery failed");
                    }
                });
            }
            catch (Exception)
            {
                // The engine's dispatcher may be unavailable during early startup/shutdown.
                // The next observation uses the current dispatcher, never an old scene.
            }
        }

        private void Observe()
        {
            foreach (var pair in scenes.ToArray())
            {
                if (pair.Key.IsValid) continue;
                pair.Value.Dispose();
                scenes.Remove(pair.Key);
            }
            var scene = Game.ActiveScene;
            if (scene is not { IsValid: true, IsEditor: false } || scene.IsLoading) return;
            if (!scenes.TryGetValue(scene, out var state))
            {
                state = new SceneState(scene, () => stopped);
                scenes.Add(scene, state);
            }
            state.Discover();
        }

        internal void Stop()
        {
            stopped = true;
            timer?.Dispose();
            timer = null;
            // Mount shutdown normally runs on the main thread; the dispatcher also handles
            // a caller unloading from another thread. Generation checks reject stale work.
            var awaiter = GameTask.MainThread().GetAwaiter();
            void Cleanup()
            {
                foreach (var scene in scenes.Values) scene.Dispose();
                scenes.Clear();
                NativeWeaponEffects.Stop();
            }
            if (awaiter.IsCompleted) Cleanup();
            else awaiter.OnCompleted(Cleanup);
        }
    }

    private sealed class SceneState : IDisposable
    {
        private readonly Scene scene;
        private readonly Func<bool> stopped;
        private readonly List<NativePedestrian> pedestrians = new();
        private readonly IDisposable spawnHook;
        private readonly IDisposable updateHook;
        private readonly IDisposable poseHook;
        private readonly IDisposable loadHook;
        private bool disposed;

        internal SceneState(Scene scene, Func<bool> stopped)
        {
            this.scene = scene;
            this.stopped = stopped;
            spawnHook = scene.AddHook(GameObjectSystem.Stage.StartFixedUpdate, -10000, DiscoverNewProps,
                "SADE", "Initialize mounted pedestrians before prop physics");
            updateHook = scene.AddHook(GameObjectSystem.Stage.StartUpdate, 10, Update,
                "SADE", "Pedestrian movement and combat");
            poseHook = scene.AddHook(GameObjectSystem.Stage.FinishUpdate, 10, SyncWeaponPoses,
                "SADE", "Attach held weapons to the final animated hand pose");
            loadHook = scene.AddHook(GameObjectSystem.Stage.SceneLoaded, 10, Discover,
                "SADE", "Restore mounted pedestrians");
        }

        private void DiscoverNewProps()
        {
            if (disposed || stopped() || !scene.IsValid || scene.IsLoading || scene.IsEditor ||
                (Networking.IsActive && !Networking.IsHost)) return;
            // Sandbox creates a dynamic Prop first. Waiting for the coarse scene observer
            // lets its ragdoll simulate before promotion. Check only the active Prop index
            // here; the full renderer discovery still runs only on a match or observation.
            if (scene.GetAllComponents<Prop>().Any(prop => !prop.Tags.Has(Marker) &&
                prop.Model is { IsValid: true } model && TryGetProfile(model, out _))) Discover();
        }

        internal void Discover()
        {
            if (disposed || stopped() || !scene.IsValid || scene.IsLoading || scene.IsEditor) return;
            pedestrians.RemoveAll(p => !p.IsValid);
            PromoteSpawnedModels();
            // Posing disables the crowd agent. Its active ModelPhysics lets us find the
            // saved disabled agent again without scanning every object in a large map.
            var agents = scene.GetAllComponents<NavMeshAgent>().Concat(
                scene.GetAllComponents<ModelPhysics>()
                    .Select(p => p.GetComponentInParent<NavMeshAgent>(includeDisabled: true))
                    .Where(a => a.IsValid())).Distinct().ToArray();
            foreach (var agent in agents)
            {
                var root = agent.GameObject;
                if (!root.Tags.Has(Marker, false)) continue;
                EnsureHostOwnership(root);
                var existing = root.Components.Get<NativePedestrian>(FindMode.EverythingInSelf);
                if (existing is { IsValid: true })
                {
                    if (!pedestrians.Contains(existing)) pedestrians.Add(existing);
                    continue;
                }
                var renderer = root.Components.Get<SkinnedModelRenderer>(FindMode.EnabledInSelfAndDescendants);
                if (renderer?.Model is not { IsValid: true } model || !TryGetProfile(model, out var profile)) continue;
                var controller = root.Components.Create<NativePedestrian>(false);
                controller.Flags = ComponentFlags.Hidden | ComponentFlags.NotSaved |
                    ComponentFlags.NotCloned | ComponentFlags.NotNetworked;
                controller.Initialize(profile, agent, renderer);
                controller.Enabled = true;
                pedestrians.Add(controller);
            }
        }

        private void PromoteSpawnedModels()
        {
            // Sandbox's Games tab spawns mounted models as ordinary Props, not prefabs.
            // Only model paths explicitly registered as actors opt into this conversion.
            foreach (var original in scene.GetAllComponents<ModelRenderer>().ToArray())
            {
                if (!original.IsValid || !original.Active || (Networking.IsActive && !Networking.IsHost)) continue;
                var root = original.GameObject;
                if (root.Tags.Has(Marker) || original.Model is not { IsValid: true } model ||
                    !TryGetProfile(model, out var profile)) continue;
                EnsureHostOwnership(root);
                if (root.IsProxy) continue;
                var tint = original.Tint;
                var groups = original.BodyGroups;
                var materials = original.MaterialGroup;
                var materialOverride = original.MaterialOverride;
                var actorTransform = RecoverActorTransform(root);
                var feet = actorTransform.PointToWorld(Vector3.Up * model.Bounds.Mins.z);

                // Disabling Prop releases the rendering/physics components it owns. Remove
                // its damage handler as well, while retaining the root, ownership and undo.
                SpawnedProp.Release(root);
                foreach (var component in root.Components.GetAll<Component>(FindMode.EverythingInSelf).ToArray())
                {
                    if (component is not (ModelRenderer or Collider or Rigidbody or ModelPhysics)) continue;
                    component.Enabled = false;
                    component.Destroy();
                }
                root.WorldTransform = actorTransform.WithPosition(feet);
                // Promotion can run inside fixed update. This is a change of coordinate
                // frame, not movement to interpolate from the old model-origin position.
                root.Transform.ClearInterpolation();
                root.Tags.Add(Marker);
                root.Tags.Add("sade_" + profile.Faction);
                var visual = new GameObject(root, false, "Character")
                {
                    LocalPosition = Vector3.Up * -model.Bounds.Mins.z
                };
                var renderer = visual.Components.Create<SkinnedModelRenderer>();
                renderer.Model = model;
                renderer.Tint = tint;
                renderer.BodyGroups = groups;
                renderer.MaterialGroup = materials;
                renderer.MaterialOverride = materialOverride;
                renderer.UseAnimGraph = false;
                renderer.Sequence.Name = profile.Idle;
                renderer.Sequence.Looping = true;
                renderer.Sequence.Blending = true;
                visual.Enabled = true;
                var agent = root.Components.Create<NavMeshAgent>();
                agent.UpdatePosition = true;
                agent.UpdateRotation = true;
                agent.AutoTraverseLinks = false;
                agent.Height = Math.Clamp(model.Bounds.Size.z, 32, 100);
                agent.Radius = Math.Clamp(Math.Min(model.Bounds.Size.x, model.Bounds.Size.y) * .3f, 10, 20);
                agent.MaxSpeed = profile.WalkSpeed;
                var collider = root.Components.Create<CapsuleCollider>();
                collider.Radius = agent.Radius;
                collider.Start = Vector3.Up * agent.Radius;
                collider.End = Vector3.Up * (agent.Height - agent.Radius);
                if (root.Network.Active) root.Network.Refresh();
            }
        }

        private static Transform RecoverActorTransform(GameObject root)
        {
            var transform = root.WorldTransform;
            var physics = root.GetComponent<ModelPhysics>();
            if (physics is { IgnoreRoot: false, MotionEnabled: true } && physics.Bodies.Count > 0)
            {
                // ModelPhysics drives a Prop root from its first physical bone (Pelvis).
                // That bone has a different local basis from the model; retaining it would
                // put the new animated NPC sideways and offset its feet horizontally.
                var body = physics.Bodies[0];
                if (body.Component.IsValid())
                {
                    var rotation = body.Component.WorldRotation * body.LocalTransform.Rotation.Inverse;
                    transform.Rotation = rotation;
                    transform.Position = body.Component.WorldPosition - rotation * (body.LocalTransform.Position * transform.Scale);
                }
            }
            var forward = transform.Rotation.Forward.WithZ(0);
            transform.Rotation = Rotation.LookAt(forward.Length > .001f ? forward.Normal : Vector3.Forward);
            return transform;
        }

        private static void EnsureHostOwnership(GameObject root)
        {
            // Sandbox records the spawning player for undo separately from network ownership.
            // NPC decisions and damage run on the host, including NPCs spawned by a remote player.
            if (!Networking.IsHost || !root.Network.Active) return;
            if (root.IsProxy) root.Network.TakeOwnership();
            root.Network.SetOwnerTransfer(OwnerTransfer.Fixed);
        }

        private void Update()
        {
            if (disposed || stopped() || !scene.IsValid || scene.IsEditor || scene.IsLoading || !Game.IsPlaying) return;
            DiscoverNewProps(); // Also covers render frames without a fixed physics step.
            pedestrians.RemoveAll(p => !p.IsValid);
            foreach (var pedestrian in pedestrians)
            {
                if (!pedestrian.Active) continue;
                pedestrian.Peers = pedestrians;
                pedestrian.Tick(Math.Clamp(Time.Delta, 0, 0.1f));
            }
        }

        private void SyncWeaponPoses()
        {
            if (disposed || stopped() || !scene.IsValid || scene.IsEditor || scene.IsLoading || !Game.IsPlaying) return;
            foreach (var pedestrian in pedestrians)
                if (pedestrian.IsValid && pedestrian.Active) pedestrian.SyncWeaponPose();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            spawnHook.Dispose();
            updateHook.Dispose();
            poseHook.Dispose();
            loadHook.Dispose();
            NativeWeaponEffects.Stop(scene);
            foreach (var pedestrian in pedestrians)
                if (pedestrian.IsValid) pedestrian.Shutdown();
            pedestrians.Clear();
        }
    }
}
