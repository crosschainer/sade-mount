using Sandbox;

namespace Sade.Mount;

/// <summary>Host-side gang combat for the transient mount pedestrian controller.</summary>
internal sealed partial class NativePedestrian
{
    private GameObject? combatTarget;
    private GameObject? heldWeapon;
    private PointLight? muzzleLight;
    private SoundFile? fireSound;
    private SoundFile? reloadSound;
    private SoundFile? reloadFinishSound;
    private SoundHandle? reloadHandle;
    private SoundHandle? continuousSound;
    private SoundFile? impactSound;
    private SoundFile? fireStartSound;
    private SoundFile? fireStopSound;
    private SoundHandle? continuousStartHandle;
    private float targetSearchRemaining;
    private float targetMemoryRemaining;
    private float pursuitRemaining;
    private float fireRemaining;
    private float shotDelayRemaining;
    private float reloadRemaining;
    private float reloadElapsed;
    private float muzzleRemaining;
    private float continuousSoundRemaining;
    private int magazine;
    private bool pendingShot;
    private bool reloadStartedSound;
    private bool reloadFinishedSound;
    private bool aiming;
    private bool firingAction;
    private int meleeAttackIndex;
    private PedestrianRuntime.MeleeAttackProfile? activeMeleeAttack;
    private Vector3 lastPursuitPoint;

    partial void InitializeCombat()
    {
        if (Profile.Weapon is not { } weapon) return;
        if (heldWeapon.IsValid()) return;
        magazine = Math.Clamp(weapon.MagazineSize, 1, 250);
        fireRemaining = 0.3f;
        heldWeapon = new GameObject(GameObject, false, weapon.Name)
        {
            Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked
        };
        if (!string.IsNullOrWhiteSpace(weapon.ModelPath)) heldWeapon.AddComponent<ModelRenderer>().Model = Model.Load(weapon.ModelPath);
        var muzzle = new GameObject(heldWeapon, true, "Muzzle flash") { Flags = heldWeapon.Flags };
        muzzle.LocalPosition = weapon.MuzzleOffset;
        muzzleLight = muzzle.AddComponent<PointLight>();
        muzzleLight.Radius = 110;
        muzzleLight.LightColor = new Color(1f, 0.65f, 0.2f) * 3;
        muzzleLight.Shadows = false;
        muzzleLight.Enabled = false;
        fireSound = LoadWeaponSound(weapon.SoundPath);
        reloadSound = LoadWeaponSound(weapon.ReloadSoundPath);
        reloadFinishSound = LoadWeaponSound(weapon.ReloadFinishSoundPath);
        impactSound = LoadWeaponSound(weapon.ImpactSoundPath);
        fireStartSound = LoadWeaponSound(weapon.FireStartSoundPath);
        fireStopSound = LoadWeaponSound(weapon.FireStopSoundPath);
        UpdateHeldWeapon();
    }

    partial void UpdateCombat(float delta, ref bool ownsMovement)
    {
        ClearAimOverrides();
        UpdateHeldWeapon();
        muzzleRemaining = Math.Max(0, muzzleRemaining - delta);
        continuousSoundRemaining -= delta;
        if (continuousSoundRemaining <= 0 && continuousSound.IsValid()) StopContinuousAudio();
        if (muzzleLight.IsValid()) muzzleLight.Enabled = muzzleRemaining > 0;
        if (Profile.Weapon is not { CombatEnabled: true } weapon || weapon.Kind == "tool" || IsDead || IsProxy || !Networking.IsHost) return;

        fireRemaining -= delta;
        targetSearchRemaining -= delta;
        pursuitRemaining -= delta;
        targetMemoryRemaining -= delta;
        if (targetSearchRemaining <= 0)
        {
            targetSearchRemaining = 0.3f;
            SelectCombatTarget();
        }
        if (!ValidCombatTarget(combatTarget))
        {
            ReleaseCombatTarget();
            // Finish an already-started reload while idle; never leave an empty weapon stuck.
            if (reloadRemaining > 0) UpdateReload(delta);
            return;
        }

        ownsMovement = true;
        var targetPosition = TargetPosition(combatTarget!);
        var distance = Vector3.DistanceBetween(WorldPosition, combatTarget!.WorldPosition);
        var visible = HasLineOfSight(combatTarget!, targetPosition);
        if (visible) targetMemoryRemaining = 6;
        if (targetMemoryRemaining <= 0 || distance > Math.Max(weapon.Range * 2, 1600))
        {
            ReleaseCombatTarget();
            StopMoving(1);
            return;
        }

        Face(targetPosition, delta);
        if (reloadRemaining > 0)
        {
            StopMoving();
            UpdateReload(delta);
            return;
        }
        if (magazine <= 0 && weapon.Kind is not ("melee" or "chainsaw"))
        {
            BeginReload();
            return;
        }

        if (!visible || distance > weapon.Range * 0.85f)
        {
            CancelShot();
            if (aiming || firingAction) { ClearAction(); aiming = firingAction = false; }
            if (pursuitRemaining <= 0 || Vector3.DistanceBetween(lastPursuitPoint, combatTarget.WorldPosition) > 64)
            {
                pursuitRemaining = 0.7f;
                lastPursuitPoint = combatTarget.WorldPosition;
                if (!NavigateTo(lastPursuitPoint, run: true)) StopMoving(1);
            }
            return;
        }

        StopMoving();
        if (pendingShot)
        {
            shotDelayRemaining -= delta;
            if (shotDelayRemaining <= 0) FireWeapon(targetPosition);
            return;
        }
        if (fireRemaining > 0)
        {
            if (!aiming && !firingAction) { PlayAction(weapon.AimSequence, loop: true, seconds: float.MaxValue); aiming = true; }
            return;
        }

        activeMeleeAttack = weapon.Kind == "melee" && weapon.MeleeAttacks.Length > 0
            ? weapon.MeleeAttacks[meleeAttackIndex++ % weapon.MeleeAttacks.Length] : null;
        fireRemaining = Math.Clamp(activeMeleeAttack?.ChainTime ?? weapon.FireInterval, 0.04f, 5);
        shotDelayRemaining = Math.Clamp(activeMeleeAttack?.HitTime ?? weapon.FireDelay, 0, fireRemaining);
        pendingShot = true;
        aiming = false;
        var sustained = Continuous(weapon.Kind) || weapon.LoopingFireSound;
        if (!sustained || !firingAction)
            firingAction = PlayAction(activeMeleeAttack?.Sequence ?? weapon.FireSequence, loop: sustained, seconds: sustained ? float.MaxValue : fireRemaining);
        if (shotDelayRemaining <= 0) FireWeapon(targetPosition);
    }

    partial void ReceiveCombatDamage(DamageInfo damage)
    {
        var attacker = damage.Attacker;
        if (!ValidCombatTarget(attacker)) return;
        var pedestrian = attacker.GetComponentInParent<NativePedestrian>();
        // An ally crossing a bullet does not start a same-gang war.
        if (pedestrian.IsValid() && string.Equals(pedestrian.Profile.Faction, Profile.Faction, StringComparison.OrdinalIgnoreCase))
        {
            Aggressor = null;
            return;
        }
        combatTarget = attacker;
        Aggressor = attacker;
        targetMemoryRemaining = 8;
        pursuitRemaining = 0;
        fireRemaining = Math.Max(fireRemaining, 0.2f);
    }

    private void SelectCombatTarget()
    {
        if (ValidCombatTarget(Aggressor))
        {
            if (combatTarget != Aggressor) { combatTarget = Aggressor; targetMemoryRemaining = 8; fireRemaining = 0.3f; }
            return;
        }
        if (ValidCombatTarget(combatTarget)) return;
        var range = Math.Min(Math.Max(Profile.Weapon!.Range, 600), 1800);
        NativePedestrian? nearest = null;
        foreach (var peer in Peers)
        {
            if (!peer.IsValid() || !peer.Active || peer == this || peer.IsDead || peer.IsPosed || !WeaponMechanics.AreRivals(
                Profile.Faction, Profile.EnemyFactions, peer.Profile.Faction, peer.Profile.EnemyFactions)) continue;
            var distance = Vector3.DistanceBetween(WorldPosition, peer.WorldPosition);
            if (distance >= range || !HasLineOfSight(peer.GameObject, TargetPosition(peer.GameObject))) continue;
            range = distance;
            nearest = peer;
        }
        if (!nearest.IsValid()) return;
        combatTarget = nearest.GameObject;
        SpeakFirst("EYEING_PED_THREAT", "FIGHT");
        targetMemoryRemaining = 6;
        fireRemaining = 0.35f;
        pursuitRemaining = 0;
    }

    private bool ValidCombatTarget(GameObject? target)
    {
        if (!target.IsValid() || !target.Active || target.Scene != Scene || InHierarchy(target, GameObject)) return false;
        var pedestrian = target.GetComponentInParent<NativePedestrian>();
        return pedestrian is not { IsDead: true } && pedestrian is not { IsPosed: true };
    }

    private static bool InHierarchy(GameObject? child, GameObject parent) => child.IsValid() && (child == parent || child.IsAncestor(parent));

    private static Vector3 TargetPosition(GameObject target)
    {
        var pedestrian = target.GetComponentInParent<NativePedestrian>();
        if (pedestrian.IsValid()) return pedestrian.WorldPosition + Vector3.Up * 42;
        // DamageInfo.Attacker is normally a player root. Its visible bounds also support other game actors.
        var bounds = target.GetBounds();
        return new Vector3(bounds.Center.x, bounds.Center.y, Math.Clamp(bounds.Center.z, target.WorldPosition.z + 24, target.WorldPosition.z + 64));
    }

    private bool HasLineOfSight(GameObject target, Vector3 point)
    {
        var hit = Scene.Trace.Ray(WorldPosition + Vector3.Up * 48, point)
            .IgnoreGameObjectHierarchy(GameObject).UseHitboxes().Run();
        return !hit.Hit || InHierarchy(hit.GameObject, target);
    }

    private void FireWeapon(Vector3 point)
    {
        pendingShot = false;
        var weapon = Profile.Weapon!;
        if (!weapon.CombatEnabled || !ValidCombatTarget(combatTarget)) return;
        if (weapon.Kind == "satchel" && NativeWeaponEffects.DetonateSatchels(Scene, GameObject)) return;
        if (weapon.Kind is not ("melee" or "chainsaw"))
        {
            if (magazine <= 0) return;
            magazine--;
        }
        var origin = heldWeapon.IsValid() ? heldWeapon.WorldTransform.PointToWorld(weapon.MuzzleOffset) : WorldPosition + Vector3.Up * 48;
        var direction = (point - origin).Normal;
        if (weapon.Kind is "melee" or "chainsaw") SpeakFirst("FIGHT");
        else SpeakCombat(combatTarget?.GetComponentInParent<NativePedestrian>()?.Profile.Faction);
        switch (weapon.Kind)
        {
            case "melee":
            case "chainsaw":
            {
                origin = WorldPosition + Vector3.Up * 40;
                direction = (point - origin).Normal;
                var swing = Scene.Trace.Ray(origin, origin + direction * Math.Clamp(weapon.Range, 16, 160))
                    .Radius(Math.Clamp(activeMeleeAttack?.Radius ?? 8, 2, 32)).IgnoreGameObjectHierarchy(GameObject).UseHitboxes().Run();
                ApplyWeaponHit(swing, origin, direction, activeMeleeAttack?.Damage ?? weapon.Damage, weapon.Kind);
                if (swing.Hit) PlayWeaponSound(impactSound, swing.HitPosition, 1);
                break;
            }
            case "rocket": case "homing_rocket": case "grenade": case "molotov": case "teargas": case "satchel": case "projectile":
                NativeWeaponEffects.Launch(Scene, GameObject, weapon, origin, point, combatTarget);
                break;
            case "flame": case "spray": case "extinguisher":
                NativeWeaponEffects.Stream(Scene, GameObject, heldWeapon ?? GameObject, weapon, origin, direction);
                break;
            case "hitscan": case "shotgun":
            {
                var spread = weapon.SpreadDegrees > 0 ? weapon.SpreadDegrees : Math.Clamp((1 - weapon.Accuracy) * 4, 0.2f, 4);
                var rays = WeaponMechanics.PelletDirections(new System.Numerics.Vector3(direction.x, direction.y, direction.z),
                    weapon.Kind == "shotgun" ? weapon.PelletCount : 1, spread, Random.Shared);
                foreach (var ray in rays)
                {
                    var shot = new Vector3(ray.X, ray.Y, ray.Z);
                    var hit = Scene.Trace.Ray(origin, origin + shot * Math.Clamp(weapon.Range, 16, 12000))
                        .IgnoreGameObjectHierarchy(GameObject).UseHitboxes().Run();
                    ApplyWeaponHit(hit, origin, shot, weapon.Damage, "bullet");
                    NativeWeaponEffects.Tracer(Scene, origin, hit.EndPosition, weapon.Kind == "shotgun");
                }
                muzzleRemaining = 0.045f;
                if (muzzleLight.IsValid()) muzzleLight.Enabled = true;
                break;
            }
            default: return; // Equipment and unknown families never silently turn into firearms.
        }
        if (Continuous(weapon.Kind) || weapon.LoopingFireSound)
        {
            if (!continuousSound.IsValid() || !continuousSound.IsPlaying)
            {
                continuousStartHandle = PlayWeaponSound(fireStartSound, origin, weapon.Pitch);
                // Mounted audio can play before VSound_t metadata is available.
                var startDuration = fireStartSound is { IsLoaded: true } ? fireStartSound.Duration : 0;
                continuousSound = PlayWeaponSound(fireSound, origin, weapon.Pitch, Math.Clamp(startDuration / Math.Max(0.25f, weapon.Pitch), 0, 2));
            }
            if (continuousSound.IsValid()) continuousSound.Position = origin;
            continuousSoundRemaining = Math.Max(weapon.FireInterval * 1.5f, 0.15f);
        }
        else PlayWeaponSound(fireSound, origin, weapon.Pitch);
    }

    private void ApplyWeaponHit(SceneTraceResult hit, Vector3 origin, Vector3 direction, float damage, string kind)
    {
        if (!hit.Hit || !hit.GameObject.IsValid()) return;
        var tags = new TagSet(); tags.Add(kind);
        if (hit.Hitbox?.Tags is { } hitTags) tags.Add(hitTags);
        hit.GameObject.GetComponentInParent<Component.IDamageable>()?.OnDamage(new DamageInfo(damage, GameObject, heldWeapon ?? GameObject)
            { Origin = origin, Position = hit.HitPosition, Hitbox = hit.Hitbox, Shape = hit.Shape, Tags = tags });
        hit.GameObject.GetComponentInParent<Rigidbody>()?.ApplyImpulseAt(hit.HitPosition, direction * damage * 8);
    }

    private static bool Continuous(string kind) => kind is "chainsaw" or "flame" or "spray" or "extinguisher";

    private void BeginReload()
    {
        var weapon = Profile.Weapon!;
        CancelShot();
        StopContinuousAudio();
        aiming = firingAction = false;
        reloadRemaining = Math.Clamp(weapon.ReloadTime, 0.2f, 15);
        reloadElapsed = 0;
        reloadStartedSound = reloadFinishedSound = false;
        StopMoving();
        PlayAction(weapon.ReloadSequence, seconds: reloadRemaining);
        UpdateReload(0);
    }

    private void UpdateReload(float delta)
    {
        var weapon = Profile.Weapon!;
        reloadRemaining -= delta;
        reloadElapsed += delta;
        if (!reloadStartedSound && reloadElapsed >= weapon.ReloadSoundTime)
        {
            reloadStartedSound = true;
            reloadHandle = PlayWeaponSound(reloadSound, WorldPosition + Vector3.Up * 42, 1);
        }
        if (!reloadFinishedSound && reloadElapsed >= weapon.ReloadFinishSoundTime)
        {
            reloadFinishedSound = true;
            PlayWeaponSound(reloadFinishSound, WorldPosition + Vector3.Up * 42, 1);
        }
        if (reloadRemaining > 0) return;
        reloadRemaining = 0;
        magazine = Math.Clamp(weapon.MagazineSize, 1, 250);
        fireRemaining = 0.15f;
        ClearAction();
    }

    // Called after SceneAnimationSystem finishes this frame's pose evaluation. Early combat
    // still reads the most recently available pose for shot origins; visuals must follow the new pose.
    internal void SyncWeaponPose()
    {
        if (!IsPosed) UpdateAimingPose();
        UpdateHeldWeapon();
    }

    private void UpdateHeldWeapon()
    {
        if (!heldWeapon.IsValid() || Profile.Weapon is not { } weapon || !Renderer.IsValid()) return;
        // GTA attaches ordinary firearms directly to the right-hand bone's world matrix.
        if (!Renderer.TryGetBoneTransform(weapon.WeaponBone, out var transform)) return;
        heldWeapon.WorldTransform = transform;
        heldWeapon.Enabled = true;
    }

    private static SoundFile? LoadWeaponSound(string? path) => string.IsNullOrWhiteSpace(path) ? null : SoundFile.Load(path);
    private static SoundHandle? PlayWeaponSound(SoundFile? sound, Vector3 position, float pitch, float delay = 0)
    {
        if (sound is null) return null;
        var handle = Sound.PlayFile(sound, pitch: Math.Clamp(pitch, 0.25f, 4), delay: delay);
        if (!handle.IsValid()) return null;
        handle.Position = position;
        handle.ListenLocal = false;
        handle.DistanceAttenuation = true;
        handle.Distance = 3000;
        return handle;
    }

    private void CancelShot() { pendingShot = false; shotDelayRemaining = 0; }
    private void StopContinuousAudio()
    {
        var wasPlaying = continuousSound.IsValid();
        continuousSound?.Stop(0.05f); continuousSound = null;
        continuousStartHandle?.Stop(0.05f); continuousStartHandle = null;
        if (wasPlaying) PlayWeaponSound(fireStopSound, WorldPosition + Vector3.Up * 42, 1);
    }
    private void ReleaseCombatTarget()
    {
        combatTarget = null;
        Aggressor = null;
        CancelShot();
        if (aiming || firingAction) { ClearAction(); aiming = firingAction = false; }
    }

    partial void StopCombat()
    {
        ClearAimOverrides();
        aimBlend = 0;
        ReleaseCombatTarget();
        reloadHandle?.Stop(); reloadHandle = null;
        StopContinuousAudio();
        reloadRemaining = muzzleRemaining = 0;
        if (heldWeapon.IsValid()) heldWeapon.Destroy();
        heldWeapon = null; muzzleLight = null;
    }
}
