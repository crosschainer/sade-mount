using System.Globalization;
using Sandbox;
using Sandbox.Navigation;

namespace Sade.Mount;

/// <summary>
/// Instantiated directly by the native mount, never serialized as a project component.
/// Built-in components, model references, and marker tags make the prefab portable.
/// </summary>
internal sealed partial class NativePedestrian : Component, Component.IDamageable
{
    internal PedestrianRuntime.Profile Profile { get; private set; } = null!;
    internal NavMeshAgent Agent { get; private set; } = null!;
    internal SkinnedModelRenderer Renderer { get; private set; } = null!;
    internal IReadOnlyList<NativePedestrian> Peers { get; set; } = [];
    internal GameObject? Aggressor { get; private set; }
    internal float Health { get; private set; }
    internal bool IsDead => Health <= 0;
    internal bool HasMovementTarget => hasTarget;

    private Vector3 origin;
    private Vector3 lastPosition;
    private Vector3 progressPosition;
    private Vector3 target;
    private float wait;
    private float stuck;
    private float targetAge;
    private float actionTime;
    private string? action;
    private string healthTag = "";
    private bool hasTarget;
    private bool running;
    private readonly LocomotionPlayback locomotion = new();
    private bool shuttingDown;
    private bool initialized;
    private bool usedNavigation;
    private readonly Dictionary<string, string> sequences = new(StringComparer.OrdinalIgnoreCase);

    public NativePedestrian() => Flags = ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotCloned | ComponentFlags.NotNetworked;

    internal void Initialize(PedestrianRuntime.Profile profile, NavMeshAgent agent, SkinnedModelRenderer renderer)
    {
        Profile = profile;
        Agent = agent;
        Renderer = renderer;
        origin = lastPosition = progressPosition = WorldPosition;
        Health = Math.Clamp(profile.Health, 1, 10000);
        foreach (var tag in GameObject.Tags.TryGetAll(false))
        {
            if (!tag.StartsWith("sade_health_", StringComparison.Ordinal)) continue;
            if (float.TryParse(tag.AsSpan(12), NumberStyles.Float, CultureInfo.InvariantCulture, out var saved) && float.IsFinite(saved))
                Health = Math.Clamp(saved, 0, Health);
            healthTag = tag;
        }
        if (Tags.Has("sade_dead")) Health = 0;
        foreach (var name in renderer.Model.AnimationNames) sequences.TryAdd(name, name);
        wait = Game.Random.Float(0.5f, 2.5f);
        initialized = true;
        EnsureCollider();
        InitializePosing();
        if (UpdatePosing()) return;
        if (IsDead) ShowDeath(true);
        else UpdateAnimation(0);
    }

    internal void Tick(float delta)
    {
        if (!initialized || shuttingDown || !Agent.IsValid || !Renderer.IsValid) return;
        if (UpdatePosing()) return;
        if (Tags.Has("sade_dead") && !IsDead)
        {
            Health = 0;
            ShowDeath(false);
        }
        if (IsDead) return;

        var motion = (WorldPosition - lastPosition).WithZ(0);
        lastPosition = WorldPosition;
        var speed = delta > 0 && motion.Length < 128 ? motion.Length / delta : 0;
        Agent.UpdatePosition = !IsProxy && Scene.NavMesh.IsEnabled;
        Agent.UpdateRotation = !IsProxy && Scene.NavMesh.IsEnabled;
        actionTime = Math.Max(0, actionTime - delta);
        if (actionTime <= 0) action = null;
        if (IsProxy)
        {
            var ownsProxyMovement = false;
            UpdateCombat(delta, ref ownsProxyMovement);
            UpdateAnimation(speed, delta);
            return;
        }

        var navigation = Scene.NavMesh.IsEnabled;
        if (usedNavigation != navigation)
        {
            StopMoving(0.5f);
            if (navigation) Agent.SetAgentPosition(WorldPosition);
            usedNavigation = navigation;
        }
        wait = Math.Max(0, wait - delta);
        var ownsMovement = false;
        UpdateCombat(delta, ref ownsMovement);
        if (!ownsMovement) Wander(delta);
        AdvanceMovement(delta);
        UpdateAnimation(speed, delta);
    }

    private void Wander(float delta)
    {
        if (hasTarget)
        {
            if ((WorldPosition - target).WithZ(0).Length < 10)
                StopMoving(Game.Random.Float(2, 5));
            return;
        }
        if (wait > 0 || !sequences.ContainsKey(Profile.Walk)) return;
        if (!Scene.NavMesh.IsEnabled) { wait = 2; return; }
        var radius = Math.Clamp(Profile.WanderRadius, 32, 4096);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = Scene.NavMesh.GetRandomPoint(origin, radius);
            if (candidate is not { } destination || (destination - WorldPosition).Length < 32) continue;
            if (NavigateTo(destination)) return;
        }
        wait = 2;
    }

    internal bool NavigateTo(Vector3 destination, bool run = false)
    {
        if (IsDead || IsPosed || IsProxy || !Scene.NavMesh.IsEnabled) return false;
        if (!float.IsFinite(destination.x) || !float.IsFinite(destination.y) || !float.IsFinite(destination.z)) return false;
        running = run && sequences.ContainsKey(Profile.Run);
        Agent.MaxSpeed = running ? Positive(Profile.RunSpeed, 100) : Positive(Profile.WalkSpeed, 45);
        Agent.Acceleration = Agent.MaxSpeed * 3;
        Agent.AutoTraverseLinks = false;
        Agent.UpdatePosition = Scene.NavMesh.IsEnabled;
        Agent.UpdateRotation = Scene.NavMesh.IsEnabled;
        if (Scene.NavMesh.IsEnabled)
        {
            // Reuse a current pursuit path when the target barely moved.
            if (hasTarget && (target - destination).Length < 24 && Agent.IsNavigating) return true;
            var path = Scene.NavMesh.CalculatePath(new CalculatePathRequest
            {
                Start = Agent.AgentPosition, Target = destination, Agent = Agent
            });
            if (path.Status != NavMeshPathStatus.Complete) return false;
            Agent.SetPath(path);
        }
        if (!hasTarget || (target - destination).Length > 24)
        {
            progressPosition = WorldPosition;
            stuck = targetAge = 0;
        }
        target = destination;
        hasTarget = true;
        return true;
    }

    private void AdvanceMovement(float delta)
    {
        if (!Scene.NavMesh.IsEnabled) { StopMoving(2); return; }
        if (!hasTarget) return;
        stuck += delta;
        targetAge += delta;
        if ((WorldPosition - progressPosition).WithZ(0).Length >= 2)
        {
            progressPosition = WorldPosition;
            stuck = 0;
        }
        if (stuck >= 3 || (Scene.NavMesh.IsEnabled && !Agent.IsNavigating && targetAge > 0.75f)) StopMoving(2);
    }

    internal void StopMoving(float pause = 0)
    {
        if (Agent is { IsValid: true }) Agent.Stop();
        hasTarget = false;
        running = false;
        stuck = targetAge = 0;
        wait = Math.Max(0, pause);
    }

    internal void Face(Vector3 position, float delta)
    {
        if (IsPosed) return;
        var direction = (position - WorldPosition).WithZ(0);
        if (direction.Length < 0.01f) return;
        WorldRotation = Rotation.Lerp(WorldRotation, Rotation.LookAt(direction.Normal), Math.Clamp(delta * 9, 0, 1));
    }

    internal bool PlayAction(string clip, bool loop = false, float seconds = 0)
    {
        if (IsPosed || !Renderer.IsValid || !sequences.TryGetValue(clip, out var resolved)) return false;
        action = resolved;
        Renderer.UseAnimGraph = false;
        Renderer.Sequence.Looping = loop;
        Renderer.Sequence.Blending = true;
        Renderer.Sequence.Name = resolved;
        Renderer.PlaybackRate = 1;
        if (Renderer.SceneModel.IsValid()) Renderer.Sequence.Time = 0;
        actionTime = seconds > 0 ? seconds : Renderer.SceneModel.IsValid()
            ? Math.Max(0.05f, Renderer.Sequence.Duration) : 0.5f;
        return true;
    }

    internal void ClearAction()
    {
        action = null;
        actionTime = 0;
    }

    private void UpdateAnimation(float speed, float delta = 0)
    {
        locomotion.Update(speed, delta, Positive(Profile.WalkSpeed, 45), running);
        if (action is not null && actionTime > 0) return;
        var walking = locomotion.Walking;
        var run = locomotion.Running && sequences.ContainsKey(Profile.Run);
        var desired = walking ? run ? Profile.Run : Profile.Walk : Profile.Idle;
        if (!sequences.TryGetValue(desired, out var clip)) return;
        Renderer.UseAnimGraph = false;
        if (Renderer.Sequence.Name != clip)
        {
            Renderer.Sequence.Looping = true;
            Renderer.Sequence.Blending = true;
            Renderer.Sequence.Name = clip;
        }
        var reference = run ? Positive(Profile.RunSpeed, 100) : Positive(Profile.WalkSpeed, 45);
        Renderer.PlaybackRate = walking ? Math.Clamp(locomotion.Speed / reference, 0.15f, 3) : 1;
    }

    public void OnDamage(in DamageInfo damage)
    {
        if (!initialized || IsDead || IsProxy || damage is null || !float.IsFinite(damage.Damage) || damage.Damage <= 0) return;
        Health = Math.Max(0, Health - damage.Damage);
        if (damage.Attacker is { IsValid: true } attacker && attacker != GameObject) Aggressor = attacker;
        if (healthTag.Length > 0) Tags.Remove(healthTag);
        healthTag = "sade_health_" + Health.ToString("0.###", CultureInfo.InvariantCulture);
        Tags.Add(healthTag);
        if (!IsPosed) ReceiveCombatDamage(damage);
        if (IsDead)
        {
            Tags.Add("sade_dead");
            ShowDeath(false);
        }
        else if (!IsPosed) PlayAction(Profile.HitSequence, seconds: 0.3f);
    }

    private void ShowDeath(bool restoring)
    {
        StopMoving();
        StopCombat();
        if (IsPosed) return;
        if (PlayAction(Profile.DeathSequence, seconds: float.MaxValue))
        {
            if (restoring && Renderer.SceneModel.IsValid()) Renderer.Sequence.TimeNormalized = 1;
        }
        else
        {
            // Imported NPCs normally have KO_shot_front. A missing optional death clip
            // keeps their last pose, rather than returning a dead character to an idle loop.
            Renderer.PlaybackRate = 0;
        }
        var collider = Components.Get<CapsuleCollider>(FindMode.EverythingInSelf);
        if (collider is not null) collider.Enabled = false;
    }

    private void EnsureCollider()
    {
        if (Components.Get<CapsuleCollider>(FindMode.EverythingInSelf) is not null) return;
        var collider = Components.Create<CapsuleCollider>();
        collider.Flags = ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked | ComponentFlags.NotCloned;
        collider.Radius = Math.Clamp(Agent.Radius, 6, 24);
        collider.Start = Vector3.Up * collider.Radius;
        collider.End = Vector3.Up * Math.Max(collider.Radius, Agent.Height - collider.Radius);
    }

    internal void Shutdown()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        StopMoving();
        StopCombat();
        Destroy();
    }

    protected override void OnDisabled()
    {
        if (!initialized) return;
        StopMoving();
        StopCombat();
    }

    protected override void OnEnabled()
    {
        if (initialized && !IsDead && !shuttingDown) InitializeCombat();
    }

    protected override void OnDestroy()
    {
        if (!initialized) return;
        StopCombat();
    }

    private static float Positive(float value, float fallback) => float.IsFinite(value) && value > 0 ? value : fallback;
    partial void InitializeCombat();
    partial void UpdateCombat(float delta, ref bool ownsMovement);
    partial void ReceiveCombatDamage(DamageInfo damage);
    partial void StopCombat();
}
