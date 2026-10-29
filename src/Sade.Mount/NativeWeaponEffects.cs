using Sandbox;
using NVector3 = System.Numerics.Vector3;

namespace Sade.Mount;

/// <summary>
/// Scene-owned projectile and area simulation. These effects outlive their firing pedestrian.
/// Trajectory/fuse values are adapter settings, not a claim to reproduce GTA's unreversed physics.
/// </summary>
internal static class NativeWeaponEffects
{
    private static readonly Dictionary<Scene, State> scenes = new();
    private const GameObjectFlags EffectFlags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

    internal static void Launch(Scene scene, GameObject attacker, PedestrianRuntime.WeaponProfile weapon, Vector3 origin, Vector3 target, GameObject? homingTarget)
    {
        if (!Networking.IsHost || !weapon.CombatEnabled) return;
        Get(scene).Launch(attacker, weapon, origin, target, homingTarget);
    }

    internal static bool DetonateSatchels(Scene scene, GameObject owner)
    {
        if (!scenes.TryGetValue(scene, out var state)) return false;
        var charges = state.Projectiles.Where(p => p.Owner == owner && p.Weapon.Kind == "satchel" && p.Age >= Math.Max(0.8f, p.Weapon.FuseSeconds)).ToArray();
        foreach (var charge in charges) { state.Detonate(charge); state.Projectiles.Remove(charge); }
        return charges.Length > 0;
    }

    internal static void Stream(Scene scene, GameObject attacker, GameObject weaponObject, PedestrianRuntime.WeaponProfile weapon, Vector3 origin, Vector3 direction)
    {
        if (!Networking.IsHost || !weapon.CombatEnabled) return;
        Get(scene).Stream(attacker, weaponObject, weapon, origin, direction);
    }

    internal static void Tracer(Scene scene, Vector3 origin, Vector3 hitPosition, bool shotgun)
    {
        if (!scene.IsValid || scene.IsEditor || Vector3.DistanceBetween(origin, hitPosition) < 2) return;
        Get(scene).Tracer(origin, hitPosition, shotgun);
    }

    internal static void Stop(Scene? scene = null)
    {
        foreach (var pair in scenes.ToArray())
        {
            if (scene is not null && pair.Key != scene) continue;
            pair.Value.Dispose();
            scenes.Remove(pair.Key);
        }
    }

    private static State Get(Scene scene)
    {
        foreach (var old in scenes.Keys.Where(s => !s.IsValid).ToArray()) Stop(old);
        if (!scenes.TryGetValue(scene, out var state)) scenes[scene] = state = new State(scene);
        return state;
    }

    private sealed class Projectile
    {
        internal required GameObject Object;
        internal required GameObject Owner;
        internal required PedestrianRuntime.WeaponProfile Weapon;
        internal GameObject? Target;
        internal Vector3 Velocity;
        internal float Age;
        internal bool Resting;
    }

    private sealed class Area
    {
        internal required GameObject Object;
        internal required GameObject Owner;
        internal required PedestrianRuntime.WeaponProfile Weapon;
        internal required ParticleEffect Particles;
        internal float Remaining;
        internal float DamageTimer;
    }

    private sealed record Visual(GameObject Object, float Expires);

    private sealed class BulletTrail
    {
        internal required LineRenderer Line;
        internal Vector3 Origin;
        internal Vector3 Direction;
        internal float Distance;
        internal float Length;
        internal float Duration;
        internal float Age;
        internal bool Fresh = true;

        internal void UpdatePoints()
        {
            // Damage already used this exact ray. The visible streak travels along it and
            // contracts at the hit point, including close walls and shotgun pellet hits.
            var head = Length + Distance * Math.Clamp(Age / Duration, 0, 1);
            var tail = Math.Clamp(head - Length, 0, Distance);
            var tip = Math.Min(head, Distance);
            for (var i = 0; i < Line.VectorPoints.Count; i++)
                Line.VectorPoints[i] = Origin + Direction * (tail + (tip - tail) * i / (Line.VectorPoints.Count - 1));
        }
    }

    private sealed class State : IDisposable
    {
        private readonly Scene scene;
        private readonly IDisposable hook;
        internal readonly List<Projectile> Projectiles = [];
        private readonly List<Area> areas = [];
        private readonly List<Visual> visuals = [];
        private readonly List<BulletTrail> tracers = [];
        private readonly Dictionary<GameObject, (ParticleEffect Effect, float Until)> streams = [];
        private float age;

        internal State(Scene scene)
        {
            this.scene = scene;
            hook = scene.AddHook(GameObjectSystem.Stage.FinishUpdate, -20, Update, "SADE", "Weapon projectiles and effects");
        }

        internal void Tracer(Vector3 origin, Vector3 hitPosition, bool shotgun)
        {
            // A large gang fight must not leave an unbounded pile of transient renderers.
            if (tracers.Count >= 256)
            {
                if (tracers[0].Line.IsValid()) tracers[0].Line.GameObject.Destroy();
                tracers.RemoveAt(0);
            }
            var distance = Vector3.DistanceBetween(origin, hitPosition);
            var gameObject = new GameObject(scene, false, "SADE bullet tracer") { Flags = EffectFlags };
            var line = gameObject.AddComponent<LineRenderer>();
            line.UseVectorPoints = true;
            // LineRenderer samples color/width at its points, so the interior points
            // carry the bright center and tapered ends as well as the straight path.
            line.VectorPoints = [origin, origin, origin, origin, origin];
            line.Face = SceneLineObject.FaceMode.Camera;
            line.Color = new Gradient(new Gradient.ColorFrame(0, new Color(1, .65f, .2f, 0)),
                new Gradient.ColorFrame(.7f, new Color(1, .85f, .5f)),
                new Gradient.ColorFrame(1, new Color(1, .97f, .85f)));
            line.Width = new Curve(new Curve.Frame(0, 0),
                new Curve.Frame(.75f, shotgun ? .25f : .45f), new Curve.Frame(1, .1f));
            line.Opaque = false;
            line.Additive = true;
            line.Lighting = false;
            line.CastShadows = false;
            var trail = new BulletTrail
            {
                Line = line, Origin = origin, Direction = (hitPosition - origin) / distance,
                Distance = distance, Length = Math.Min(distance, shotgun ? 64 : 144),
                Duration = Math.Clamp(distance / 16000f, 0.045f, 0.12f)
            };
            trail.UpdatePoints();
            gameObject.Enabled = true;
            tracers.Add(trail);
        }

        internal void Launch(GameObject owner, PedestrianRuntime.WeaponProfile weapon, Vector3 origin, Vector3 target, GameObject? homingTarget)
        {
            // Keep NPC inventories bounded even if a game repeatedly triggers throws without detonating.
            if (Projectiles.Count(p => p.Owner == owner) >= 12) return;
            var gameObject = new GameObject(scene, false, weapon.Name + " projectile") { Flags = EffectFlags, WorldPosition = origin };
            var modelPath = weapon.ProjectileModelPath;
            if (string.IsNullOrWhiteSpace(modelPath) && weapon.Kind is "grenade" or "molotov" or "teargas" or "satchel") modelPath = weapon.ModelPath;
            if (!string.IsNullOrWhiteSpace(modelPath)) gameObject.AddComponent<ModelRenderer>().Model = Model.Load(modelPath);
            var speed = Math.Clamp(weapon.ProjectileSpeed, 50, 10000);
            var velocity = (target - origin).Normal * speed;
            if (weapon.Kind is not ("rocket" or "homing_rocket") && WeaponMechanics.TryBallisticVelocity(ToNumerics(origin), ToNumerics(target), speed, weapon.ProjectileGravity, out var arc))
                velocity = FromNumerics(arc);
            gameObject.WorldRotation = Rotation.LookAt(velocity.Normal);
            gameObject.Enabled = true;
            Projectiles.Add(new Projectile { Object = gameObject, Owner = owner, Weapon = weapon, Velocity = velocity, Target = homingTarget });
        }

        private void Update()
        {
            if (!scene.IsValid || scene.IsEditor || !Game.IsPlaying) return;
            var delta = Math.Clamp(Time.Delta, 0, 0.1f);
            if (delta <= 0) return;
            age += delta;
            foreach (var tracer in tracers.ToArray())
            {
                // Shooting happens earlier in this update. Always allow its first render,
                // even if a long frame exceeds the short tracer lifetime.
                if (tracer.Fresh) { tracer.Fresh = false; continue; }
                tracer.Age += delta;
                if (!tracer.Line.IsValid() || tracer.Age >= tracer.Duration)
                {
                    if (tracer.Line.IsValid()) tracer.Line.GameObject.Destroy();
                    tracers.Remove(tracer);
                    continue;
                }
                tracer.UpdatePoints();
            }
            foreach (var projectile in Projectiles.ToArray())
            {
                if (!projectile.Object.IsValid() || !StepProjectile(projectile, delta))
                {
                    if (projectile.Object.IsValid()) projectile.Object.Destroy();
                    Projectiles.Remove(projectile);
                }
            }
            foreach (var area in areas.ToArray())
            {
                area.Remaining -= delta;
                if (!area.Object.IsValid() || area.Remaining <= 0)
                {
                    if (area.Object.IsValid()) area.Object.Destroy();
                    areas.Remove(area);
                    continue;
                }
                area.DamageTimer -= delta;
                if (area.DamageTimer <= 0)
                {
                    area.DamageTimer = 0.25f;
                    var kind = area.Weapon.Kind == "teargas" ? "gas" : "fire";
                    DamageArea(area.Object.WorldPosition + Vector3.Up * 8, area.Weapon.BlastRadius,
                        area.Weapon.Damage * 0.25f, area.Owner, area.Object, kind, 0);
                    for (var n = 0; n < 5; n++)
                    {
                        var offset = Vector3.Random.WithZ(0) * area.Weapon.BlastRadius * 0.7f;
                        var particle = area.Particles.Emit(area.Object.WorldPosition + offset, Game.Random.Float(0, 1));
                        particle.Velocity = Vector3.Up * Game.Random.Float(12, 42);
                    }
                }
            }
            foreach (var pair in streams.ToArray())
                if (!pair.Key.IsValid() || pair.Value.Until < age)
                {
                    if (pair.Value.Effect.IsValid()) pair.Value.Effect.GameObject.Destroy();
                    streams.Remove(pair.Key);
                }
            foreach (var visual in visuals.ToArray())
                if (!visual.Object.IsValid() || visual.Expires <= age)
                {
                    if (visual.Object.IsValid()) visual.Object.Destroy();
                    visuals.Remove(visual);
                }
        }

        private bool StepProjectile(Projectile projectile, float delta)
        {
            var weapon = projectile.Weapon;
            projectile.Age += delta;
            if (weapon.Kind == "satchel")
            {
                if (projectile.Age > 120) return false;
            }
            else if (weapon.FuseSeconds > 0 && projectile.Age >= Math.Clamp(weapon.FuseSeconds, 0.1f, 30)) { Detonate(projectile); return false; }
            else if (projectile.Age > 30) return false; // Impact-only projectiles never become arbitrary midair blasts.
            if (projectile.Resting) return true;
            var steps = Math.Max(1, (int)MathF.Ceiling(delta / (1f / 60)));
            var step = delta / steps;
            for (var i = 0; i < steps; i++)
            {
                if (weapon.Kind == "homing_rocket" && projectile.Target.IsValid())
                {
                    var desired = (projectile.Target.WorldPosition + Vector3.Up * 36 - projectile.Object.WorldPosition).Normal;
                    projectile.Velocity = Vector3.Lerp(projectile.Velocity.Normal, desired, Math.Clamp(step * 3, 0, 1)).Normal * projectile.Velocity.Length;
                }
                var from = projectile.Object.WorldPosition;
                var acceleration = weapon.Kind is "rocket" or "homing_rocket" ? Vector3.Zero : Vector3.Down * Math.Clamp(weapon.ProjectileGravity, 0, 2000);
                var to = from + projectile.Velocity * step + acceleration * (0.5f * step * step);
                projectile.Velocity += acceleration * step;
                var trace = scene.Trace.Ray(from, to).Radius(2).UseHitboxes();
                if (projectile.Age < 0.2f && projectile.Owner.IsValid()) trace = trace.IgnoreGameObjectHierarchy(projectile.Owner);
                var hit = trace.IgnoreGameObjectHierarchy(projectile.Object).Run();
                projectile.Object.WorldPosition = hit.EndPosition;
                if (!hit.Hit)
                {
                    if (projectile.Velocity.Length > 1) projectile.Object.WorldRotation = Rotation.LookAt(projectile.Velocity.Normal);
                    continue;
                }
                projectile.Object.WorldPosition = hit.HitPosition + hit.Normal * 2.2f;
                if (weapon.Kind is "rocket" or "homing_rocket" or "molotov" or "projectile") { Detonate(projectile); return false; }
                PlaySound(weapon.ImpactSoundPath, hit.HitPosition);
                if (weapon.Kind == "satchel") { projectile.Resting = true; projectile.Velocity = 0; return true; }
                projectile.Velocity = (projectile.Velocity - 2 * Vector3.Dot(projectile.Velocity, hit.Normal) * hit.Normal) * 0.45f;
                if (hit.Normal.z > 0.65f && projectile.Velocity.Length < 35) { projectile.Resting = true; projectile.Velocity = 0; return true; }
            }
            return true;
        }

        internal void Detonate(Projectile projectile)
        {
            if (!projectile.Object.IsValid()) return;
            var point = projectile.Object.WorldPosition;
            var weapon = projectile.Weapon;
            if (weapon.Kind is "molotov" or "teargas")
            {
                CreateArea(point, projectile.Owner, weapon);
                PlaySound(weapon.ExplosionSoundPath ?? weapon.ImpactSoundPath, point);
            }
            else
            {
                DamageArea(point, weapon.BlastRadius, weapon.Damage, projectile.Owner, projectile.Object, "explosion", weapon.Damage * 25);
                PlaySound(weapon.ExplosionSoundPath, point);
                var fire = MakeParticles(point, true, Math.Clamp(weapon.BlastRadius * 0.15f, 20, 80), 0.7f);
                for (var n = 0; n < 24; n++) fire.Emit(point, Game.Random.Float(0, 1)).Velocity = Vector3.Random * 160 + Vector3.Up * 80;
                visuals.Add(new Visual(fire.GameObject, age + 1.2f));
                var smoke = MakeParticles(point, false, Math.Clamp(weapon.BlastRadius * 0.15f, 24, 90), 2);
                for (var n = 0; n < 14; n++) smoke.Emit(point, Game.Random.Float(0, 1)).Velocity = Vector3.Random * 45 + Vector3.Up * 55;
                visuals.Add(new Visual(smoke.GameObject, age + 2.5f));
            }
            projectile.Object.Destroy();
        }

        private void CreateArea(Vector3 point, GameObject owner, PedestrianRuntime.WeaponProfile weapon)
        {
            var gas = weapon.Kind == "teargas";
            var particles = MakeParticles(point, !gas, gas ? 65 : 30, gas ? 3 : 1.2f);
            areas.Add(new Area { Object = particles.GameObject, Owner = owner, Weapon = weapon, Particles = particles, Remaining = Math.Clamp(weapon.EffectSeconds, 0.5f, 60) });
        }

        internal void Stream(GameObject attacker, GameObject weaponObject, PedestrianRuntime.WeaponProfile weapon, Vector3 origin, Vector3 direction)
        {
            var hit = scene.Trace.Ray(origin, origin + direction * Math.Clamp(weapon.Range, 16, 2000))
                .IgnoreGameObjectHierarchy(attacker).Radius(3).UseHitboxes().Run();
            var end = hit.EndPosition;
            if (weapon.Kind == "extinguisher")
            {
                foreach (var area in areas)
                {
                    if (area.Weapon.Kind == "teargas") continue;
                    var offset = area.Object.WorldPosition - origin;
                    if (offset.Length <= weapon.Range + area.Weapon.BlastRadius && Vector3.Dot(offset.Normal, direction) > 0.85f && Visible(origin, area.Object.WorldPosition + Vector3.Up * 8, area.Object))
                        area.Remaining = Math.Max(0, area.Remaining - 2);
                }
            }
            else
            {
                foreach (var receiver in scene.GetAllComponents<Component.IDamageable>().ToArray())
                {
                    if (receiver is not Component component || !component.Active || component.GameObject == attacker || component.GameObject.IsAncestor(attacker)) continue;
                    var point = component is NativePedestrian ? component.WorldPosition + Vector3.Up * 36 : component.GameObject.GetBounds().Center;
                    var offset = point - origin;
                    if (offset.Length > weapon.Range || Vector3.Dot(offset.Normal, direction) < 0.97f || !Visible(origin, point, component.GameObject)) continue;
                    var tags = new TagSet(); tags.Add(weapon.Kind == "flame" ? "fire" : "chemical");
                    receiver.OnDamage(new DamageInfo(weapon.Damage, attacker, weaponObject) { Origin = origin, Position = point, Tags = tags });
                }
            }
            if (!streams.TryGetValue(attacker, out var current) || !current.Effect.IsValid())
                current = (MakeParticles(origin, weapon.Kind == "flame", 12, 0.65f), age);
            streams[attacker] = (current.Effect, age + 0.8f);
            // The stream visually stops at the same wall as its damage trace.
            var length = Math.Min(Vector3.DistanceBetween(origin, end), weapon.Range);
            for (var i = 0; i < 4; i++)
            {
                var fraction = i / 4f;
                var particle = current.Effect.Emit(origin + direction * length * fraction, Game.Random.Float(0, 1));
                particle.Velocity = direction * Math.Min(80, length * 0.2f) + Vector3.Up * 12;
            }
        }

        private void DamageArea(Vector3 origin, float radius, float damage, GameObject attacker, GameObject weapon, string kind, float impulse)
        {
            radius = Math.Clamp(radius, 1, 4000);
            foreach (var receiver in scene.GetAllComponents<Component.IDamageable>().ToArray())
            {
                if (receiver is not Component component || !component.Active) continue;
                var point = component is NativePedestrian ? component.WorldPosition + Vector3.Up * 32 : component.GameObject.GetBounds().Center;
                var strength = WeaponMechanics.BlastFalloff(Vector3.DistanceBetween(origin, point), radius);
                if (strength <= 0 || !Visible(origin, point, component.GameObject)) continue;
                var tags = new TagSet(); tags.Add(kind);
                receiver.OnDamage(new DamageInfo(damage * strength, attacker.IsValid() ? attacker : weapon, weapon)
                    { Origin = origin, Position = point, Tags = tags });
            }
            if (impulse <= 0) return;
            foreach (var body in scene.GetAllComponents<Rigidbody>().ToArray())
            {
                var point = body.WorldPosition;
                var strength = WeaponMechanics.BlastFalloff(Vector3.DistanceBetween(origin, point), radius);
                if (strength > 0 && Visible(origin, point, body.GameObject)) body.ApplyImpulseAt(point, (point - origin).Normal * impulse * strength);
            }
        }

        private bool Visible(Vector3 origin, Vector3 point, GameObject target)
        {
            var hit = scene.Trace.Ray(origin, point).Run();
            return !hit.Hit || hit.GameObject == target || (hit.GameObject.IsValid() && hit.GameObject.IsAncestor(target));
        }

        private ParticleEffect MakeParticles(Vector3 position, bool fire, float size, float lifetime)
        {
            var gameObject = new GameObject(scene, false, fire ? "SADE fire" : "SADE smoke") { Flags = EffectFlags, WorldPosition = position };
            var effect = gameObject.AddComponent<ParticleEffect>();
            effect.MaxParticles = 160;
            effect.Lifetime = lifetime;
            effect.ApplyShape = true; effect.Scale = size;
            effect.ApplyColor = true; effect.Tint = fire ? new Color(1, 0.7f, 0.2f) : new Color(0.7f, 0.72f, 0.7f);
            effect.ApplyAlpha = true;
            effect.Alpha = new ParticleFloat { Type = ParticleFloat.ValueType.Curve, CurveA = new Curve(new Curve.Frame(0, 0.8f), new Curve.Frame(1, 0)) };
            var renderer = gameObject.AddComponent<ParticleSpriteRenderer>();
            renderer.Sprite = Sprite.FromTexture(Texture.Load(fire ? "textures/particles/flame/fire_embers1.vtex" : "textures/particles/smoke/smoke_wispy_a.vtex"));
            renderer.Additive = fire; renderer.Lighting = !fire; renderer.Shadows = false;
            gameObject.Enabled = true;
            return effect;
        }

        public void Dispose()
        {
            hook.Dispose();
            foreach (var gameObject in Projectiles.Select(p => p.Object).Concat(areas.Select(a => a.Object)).Concat(visuals.Select(v => v.Object)).Concat(streams.Values.Select(s => s.Effect.GameObject)).Concat(tracers.Where(t => t.Line.IsValid()).Select(t => t.Line.GameObject)))
                if (gameObject.IsValid()) gameObject.Destroy();
            Projectiles.Clear(); areas.Clear(); visuals.Clear(); streams.Clear(); tracers.Clear();
        }
    }

    private static void PlaySound(string? path, Vector3 position)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var sound = SoundFile.Load(path);
        if (sound is null) return;
        var handle = Sound.PlayFile(sound);
        if (!handle.IsValid()) return;
        handle.Position = position; handle.ListenLocal = false; handle.Distance = 3500;
    }

    private static NVector3 ToNumerics(Vector3 v) => new(v.x, v.y, v.z);
    private static Vector3 FromNumerics(NVector3 v) => new(v.X, v.Y, v.Z);
}
