// Run in Sandbox after installing the current mount. Uses the game's actual melee
// impact override and built-in bullet presentation, entirely in a disposable scene.
var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var nativeType = assembly.GetType("Sade.Mount.NativePedestrian")!;
var runtimeType = assembly.GetType("Sade.Mount.PedestrianRuntime")!;
var meleeType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("MeleeWeapon", false))
    .FirstOrDefault(t => t is not null) ?? throw new Exception("Run this probe in Sandbox so its real MeleeWeapon is available.");
var members = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
var activeScene = Game.ActiveScene;
var activeCamera = activeScene?.Camera;
var scene = new Scene();
var controllers = new System.Collections.Generic.List<Component>();
var results = new System.Collections.Generic.List<object>();
using (scene.Push())
try
{
    scene.NavMesh.IsEnabled = false;
    var flesh = Surface.FindByName("flesh") ?? throw new Exception("The engine flesh surface is unavailable.");
    Check(flesh.SoundCollection.ImpactHard?.ResourcePath.EndsWith("impact-melee-flesh.sound") == true,
        "Flesh selects the stock melee flesh sound.");
    Check(flesh.PrefabCollection.BulletImpact is not null && flesh.PrefabCollection.BulletImpactDecal is not null,
        "Flesh provides stock blood particles and a wound decal.");
    var attacker = scene.CreateObject(); attacker.Name = "Temporary hit-feedback attacker";
    var weaponObject = scene.CreateObject(); weaponObject.Name = "Temporary crowbar";
    var melee = Create(weaponObject, meleeType, false);
    var impact = meleeType.GetMethod("OnShootImpact", members)!;

    foreach (var path in new[] { "mount://sade/00_featured/01_big_smoke.prefab", "mount://sade/00_featured/00_cj.prefab" })
    {
        Check(PrefabFile.Load(path) is not null, "Mounted prefab loads: " + path);
        var actor = GameObject.Clone(path) ?? throw new Exception("Could not clone " + path);
        var renderer = actor.GetComponentInChildren<SkinnedModelRenderer>();
        var physics = renderer.Components.Create<ModelPhysics>(false);
        physics.Model = renderer.Model; physics.Renderer = renderer;
        physics.IgnoreRoot = true; physics.MotionEnabled = false; physics.Enabled = true;
        scene.GameTick(.02);
        Check(physics.Bodies.Count == 16 && physics.Joints.Count == 15, "Articulated rig is intact.");
        var colliders = physics.Bodies.SelectMany(b => b.Component.Components.GetAll<Collider>(FindMode.EverythingInSelf)).ToArray();
        Check(colliders.Length >= 16 && colliders.All(c => c.Surface == flesh),
            "The generated model itself supplies flesh surfaces before the controller runs.");
        // Mimic colliders deserialized from a scene saved by an earlier mount version.
        foreach (var collider in colliders) collider.Surface = Surface.FindByName("default");
        var bodyIds = physics.Bodies.Select(b => b.Component.Id).ToArray();
        var motion = physics.Bodies.Select(b => b.Component.MotionEnabled).ToArray();
        var positions = physics.Bodies.Select(b => b.Component.WorldPosition).ToArray();
        var native = Attach(actor, renderer);
        Check(colliders.All(c => c.Surface == flesh), "Existing generic colliders are migrated to flesh.");
        Check(bodyIds.SequenceEqual(physics.Bodies.Select(b => b.Component.Id))
            && motion.SequenceEqual(physics.Bodies.Select(b => b.Component.MotionEnabled))
            && physics.Bodies.Select((b, i) => b.Component.WorldPosition.Distance(positions[i])).All(d => d < .001f),
            "Surface migration does not rebuild, move, or unfreeze the body rig.");
        Check(actor.GetComponent<CapsuleCollider>(true).Surface == flesh, "Fallback capsule also reports flesh.");

        var head = physics.Bodies.Single(b => b.Component.GameObject.Name == "Head").Component;
        var origin = head.WorldPosition + Vector3.Up * 40;
        var trace = scene.Trace.Ray(origin, head.WorldPosition).UseHitboxes().Run();
        Check(trace.Hit && trace.Surface == flesh && trace.Component is Rigidbody,
            "The same ray used by a crowbar resolves a grabbable flesh body.");
        Check(trace.GameObject.GetComponentInParent<Component.IDamageable>() == native,
            "Limb damage still reaches the pedestrian controller.");
        var beforeEffects = BloodEffects();
        var beforeSounds = Sounds();
        var shot = new BaseCombatWeapon.ShotEffect(trace.HitPosition, true, trace.Normal, trace.GameObject, trace.Surface, origin);
        impact.Invoke(melee, [shot]);
        Check(BloodEffects() == beforeEffects + 2, "One real melee impact spawns one blood burst and one wound decal.");
        Check(Sounds().Except(beforeSounds).Any(s => s.IsPlaying), "The real melee override starts a native impact sound.");
        var health = (float)nativeType.GetProperty("Health", members)!.GetValue(native)!;
        ((Component.IDamageable)native).OnDamage(new DamageInfo(12, attacker, weaponObject)
            { Position = trace.HitPosition, Origin = origin, Shape = trace.Shape, Hitbox = trace.Hitbox });
        Check((float)nativeType.GetProperty("Health", members)!.GetValue(native)! == health - 12
            && nativeType.GetProperty("Aggressor", members)!.GetValue(native) == attacker,
            "Melee damage and retaliation remain connected.");
        Check(BloodEffects() == beforeEffects + 2, "Damage handling does not duplicate the weapon's blood effects.");

        var hand = physics.Bodies.Single(b => b.Component.GameObject.Name == "R_Hand").Component;
        hand.MotionEnabled = true;
        nativeType.GetMethod("Tick", members)!.Invoke(native, [.02f]);
        Check(actor.Tags.Has("sade_posed") && physics.MotionEnabled
            && !actor.GetComponent<NavMeshAgent>(true).Enabled, "First limb pickup still activates persistent posing.");
        hand.MotionEnabled = false;
        nativeType.GetMethod("Tick", members)!.Invoke(native, [.02f]);
        Check(!hand.MotionEnabled && colliders.All(c => c.Surface == flesh), "Frozen limbs retain flesh surfaces.");
        beforeEffects = BloodEffects();
        BaseCombatWeapon.ImpactEffect(trace);
        Check(BloodEffects() == beforeEffects + 2, "A bullet receives exactly one built-in flesh impact.");
        ((Component.IDamageable)native).OnDamage(new DamageInfo(1, attacker, weaponObject)
            { Position = trace.HitPosition, Origin = origin, Shape = trace.Shape });
        Check(BloodEffects() == beforeEffects + 2 && !hand.MotionEnabled,
            "Posed damage adds no duplicate impact and preserves frozen state.");
        results.Add(new { Path = path, Bodies = physics.Bodies.Count, Surface = trace.Surface.ResourcePath,
            MeleeSound = flesh.SoundCollection.ImpactHard.ResourcePath, BloodBurstAndDecal = true,
            LegacyColliderMigration = true, DamageAndRetaliation = true, NoDuplicateBulletEffects = true, PosingPreserved = true });
        nativeType.GetMethod("Shutdown", members)!.Invoke(native, null);
        actor.Destroy(); scene.GameTick(.02);
    }
    Check(activeScene?.Camera == activeCamera, "Active camera remains unchanged.");
    return new { Passed = true, Cases = results, ActiveSceneUnchanged = true, MultiplayerTested = false };

    Component Create(GameObject obj, Type type, bool enabled)
    {
        var method = obj.Components.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition);
        return (Component)method.MakeGenericMethod(type).Invoke(obj.Components, [enabled])!;
    }
    Component Attach(GameObject obj, SkinnedModelRenderer renderer)
    {
        var profiles = runtimeType.GetField("profiles", statics)!.GetValue(null)!;
        var profile = profiles.GetType().GetProperty("Item")!.GetValue(profiles, [renderer.Model.Name])!;
        var native = Create(obj, nativeType, false);
        controllers.Add(native);
        nativeType.GetMethod("Initialize", members)!.Invoke(native, [profile, obj.GetComponent<NavMeshAgent>(true), renderer]);
        native.Enabled = true;
        return native;
    }
    int BloodEffects() => scene.GetAllComponents<TemporaryEffect>().Count(t => t.GameObject.Name is "flesh_bullet" or "flesh_bullet-decal");
    System.Collections.Generic.HashSet<SoundHandle> Sounds() =>
        ((System.Collections.IEnumerable)typeof(SoundHandle).GetField("active", statics)!.GetValue(null)!).Cast<SoundHandle>().ToHashSet();
    void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
finally
{
    foreach (var native in controllers)
        if (native.IsValid) nativeType.GetMethod("Shutdown", members)!.Invoke(native, null);
    scene.Destroy();
}
