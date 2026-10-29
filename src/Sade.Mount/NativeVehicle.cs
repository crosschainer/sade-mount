using Sandbox;

namespace Sade.Mount;

/// <summary>Arcade rigid-body vehicle simulation using installed handling and attachment data.</summary>
internal sealed class NativeVehicle : Component
{
    internal VehicleRuntime.Profile Profile { get; private set; } = null!;
    internal Rigidbody Body { get; private set; } = null!;
    internal SkinnedModelRenderer Renderer { get; private set; } = null!;
    internal PlayerController? Driver { get; set; }
    internal float Speed => Body.IsValid ? Body.Velocity.Length : 0;
    internal bool Grounded { get; private set; }
    private readonly List<Wheel> wheels = [];
    private float throttle, steering, lift, pitch, roll;
    private bool brake, initialized, shuttingDown;
    private float wheelSpin;
    private float rotorSpeed, rotorAngle, aircraftThrottle;
    private float bikeSteering, bikeLean;
    private float bikeWheelAngle;
    private Vector3 groundNormal = Vector3.Up;
    private NativeVehicleAudio? audio;
    private Component[] waterVolumes = [];
    private float waterScanRemaining;
    private Vector3 Forward => WorldRotation * new Vector3(0, 1, 0);
    private Vector3 Right => WorldRotation * new Vector3(1, 0, 0);
    private Vector3 Up => WorldRotation * Vector3.Up;
    public NativeVehicle() => Flags = ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotCloned | ComponentFlags.NotNetworked;

    internal void Initialize(VehicleRuntime.Profile profile, Rigidbody body, SkinnedModelRenderer renderer)
    {
        Profile = profile; Body = body; Renderer = renderer;
        body.MassOverride = Math.Clamp(profile.Data.MassKg, 30, 100000);
        body.OverrideMassCenter = true;
        body.MassCenterOverride = CoordinateSpace.Position(profile.Data.CenterOfMass);
        body.LinearDamping = .02f; body.AngularDamping = .5f; body.Gravity = true; body.MotionEnabled = true;
        foreach (var socket in profile.Data.Sockets.Where(s => VehicleRuntime.IsWheel(s.Name)))
        {
            var front = VehicleRuntime.IsFrontWheel(socket.Name);
            var radius = (front ? profile.Data.WheelRadiusFront : profile.Data.WheelRadiusRear) * CoordinateSpace.UnitsPerCentimeter;
            var visual = GameObject.Children.FirstOrDefault(c => c.Tags.Has("sade_" + socket.Name, false));
            wheels.Add(new Wheel(socket.Name, CoordinateSpace.Transform(socket.Transform), Math.Max(radius, 3), front, visual));
        }
        initialized = true;
        audio = new NativeVehicleAudio(profile.Data.ModelNumber, profile.Data.Family, profile.Data.GearCount, profile.Data.EngineInertia);
        VehicleSeats.Attach(this);
    }

    internal void Tick(float delta)
    {
        if (!initialized || shuttingDown || !Body.IsValid) return;
        VehicleSeats.Tick(this);
        var localDriver = Driver is { IsValid: true, IsProxy: false };
        // Read the game input context, not the seated player's zero WishVelocity. Physical
        // WASD remains usable in games with different walking bindings; analog input is a fallback.
        throttle = localDriver ? VehicleDriving.Axis(Key("w"), Key("s"), Input.AnalogMove.x) : 0;
        steering = localDriver ? VehicleDriving.Axis(Key("a"), Key("d"), Input.AnalogMove.y) : 0;
        brake = localDriver && (Key("space") || Input.Down("Jump"));
        lift = throttle; // GTA aircraft: W increases collective/throttle, S decreases it.
        pitch = localDriver ? VehicleDriving.Axis(Key("DOWNARROW") || Key("KP_5"), Key("UPARROW") || Key("KP_8")) : 0;
        roll = localDriver ? VehicleDriving.Axis(Key("RIGHTARROW") || Key("KP_6"), Key("LEFTARROW") || Key("KP_4")) : 0;
        var occupied = Driver is { IsValid: true };
        rotorSpeed = VehicleDriving.Approach(rotorSpeed, occupied ? 1 : 0, delta / (occupied ? 2.5f : 4));
        if (Profile.Data.Family == "plane") aircraftThrottle = Math.Clamp(aircraftThrottle + lift * delta * .4f, 0, 1);
        if (!occupied) aircraftThrottle = 0;
        UpdateRotors(delta);
        var audioThrottle = Profile.Data.Family == "plane" ? aircraftThrottle : throttle;
        audio?.Update(WorldPosition, occupied, Vector3.Dot(Body.Velocity, Forward) / Math.Max(Profile.Data.MaxSpeed * CoordinateSpace.UnitsPerCentimeter, 1), audioThrottle, rotorSpeed, delta, Grounded);
        wheelSpin += Vector3.Dot(Body.Velocity, Forward) * delta;
        foreach (var wheel in wheels)
        {
            var position = wheel.Position + Vector3.Down * wheel.Extension;
            var side = wheel.Position.x < 0 ? 180f : 0;
            var spin = Rotation.FromAxis(Vector3.Forward, wheelSpin / wheel.Radius * 180 / MathF.PI);
            if (wheel.Visual is { IsValid: true } visual)
            {
                visual.LocalPosition = position;
                visual.LocalRotation = Rotation.FromYaw(side + (wheel.Front ? VisualSteering() : 0)) * spin;
            }
            else if (Renderer.Model.Bones.GetBone(wheel.Name) is { } bone)
                Renderer.SetBoneTransform(bone, new Transform(position, Rotation.FromYaw(wheel.Front ? VisualSteering() : 0) * wheel.Bind.Rotation * spin));
        }
    }

    private static bool Key(string key) => Input.Keyboard.Down(key);
    private float VisualSteering() => Profile.Data.Family is "bike" or "bicycle" ? bikeWheelAngle * 180 / MathF.PI : steering * Profile.Data.SteeringLockDegrees;

    private void UpdateRotors(float delta)
    {
        if (Profile.Data.Family is not ("helicopter" or "plane")) return;
        rotorAngle = (rotorAngle + delta * rotorSpeed * (Profile.Data.Family == "helicopter" ? 1500 : 2400)) % 360;
        foreach (var socket in Profile.Data.Sockets.Where(s => s.Name.Contains("rotor", StringComparison.OrdinalIgnoreCase)))
        {
            var bone = Renderer.Model.Bones.GetBone(socket.Name);
            if (bone is null) continue;
            var bind = CoordinateSpace.Transform(socket.Transform);
            // DE keeps legacy static/moving names, but most solid rotor geometry is
            // skinned to moving_rotor while static_rotor is an empty dummy. Preserve
            // every authored rotor's scale: hiding by name collapses the actual blades.
            var axis = Profile.Data.Family == "plane" ? new Vector3(0, 1, 0)
                : socket.Name.EndsWith("2", StringComparison.Ordinal) ? Vector3.Forward : Vector3.Up;
            Renderer.SetBoneTransform(bone, bind.WithRotation(Rotation.FromAxis(axis, rotorAngle) * bind.Rotation));
        }
    }

    internal void FixedTick(float delta)
    {
        if (!initialized || shuttingDown || !Body.IsValid || Body.IsProxy || delta <= 0) return;
        if (Driver is { IsValid: true, IsProxy: true }) return;
        var data = Profile.Data;
        // ModelCollider applies the model's default mass when its shapes are built, after the
        // Rigidbody properties were restored. Reapply installed handling once those shapes exist.
        var targetMass = Math.Clamp(data.MassKg, 30, 100000);
        if (Body.PhysicsBody is { IsValid: true } physics && Math.Abs(physics.Mass - targetMass) > .1f)
        {
            physics.Mass = targetMass;
            physics.OverrideMassCenter = true;
            physics.LocalMassCenter = CoordinateSpace.Position(data.CenterOfMass);
        }
        var mass = Math.Max(Body.Mass, 1);
        var gravity = Scene.PhysicsWorld.Gravity.Length;
        waterScanRemaining -= delta;
        switch (data.Family)
        {
            case "boat": SimulateBoat(delta, mass, gravity, false); break;
            case "hovercraft": SimulateBoat(delta, mass, gravity, true); break;
            case "helicopter": SimulateHelicopter(delta, mass, gravity); break;
            case "plane": SimulatePlane(delta, mass, gravity); break;
            case "train" or "trailer": break; // Retain source assets without pretending to implement rail networks or articulated towing.
            default: SimulateGround(delta, mass, gravity); break;
        }
        var velocity = Body.Velocity;
        if (!float.IsFinite(velocity.x) || !float.IsFinite(velocity.y) || !float.IsFinite(velocity.z)) Body.Velocity = Vector3.Zero;
    }

    private void SimulateGround(float delta, float mass, float gravity)
    {
        var data = Profile.Data;
        var aircraft = data.Family == "plane";
        var bike = data.Family is "bike" or "bicycle";
        var road = data.Family == "road";
        var groundSteering = steering;
        var bikeTurn = default(VehicleDriving.BikeTurn);
        if (bike)
        {
            bikeSteering = VehicleDriving.Approach(bikeSteering, steering, delta * 2.5f);
            var maximumLean = data.BikeHandling.Length > 4 ? data.BikeHandling[4] : 38;
            bikeTurn = VehicleDriving.BikeSteering(bikeSteering, Vector3.Dot(Body.Velocity, Forward), WheelBase(), data.SteeringLockDegrees, maximumLean, gravity);
            bikeWheelAngle = bikeTurn.WheelSteerRadians;
            groundSteering = bikeWheelAngle * 180 / MathF.PI / Math.Max(data.SteeringLockDegrees, 1);
        }
        // A leaned bike still supports its weight along gravity, rather than casting its
        // suspension sideways and fighting its own inward lean at each contact.
        var up = bike ? Vector3.Up : Up;
        var travelUp = Math.Clamp(data.SuspensionUpper * CoordinateSpace.UnitsPerCentimeter, 4, 36);
        var travelDown = Math.Clamp(-data.SuspensionLower * CoordinateSpace.UnitsPerCentimeter, 3, 30);
        var weight = mass * gravity / Math.Max(wheels.Count, 1);
        var contacts = 0;
        var normals = Vector3.Zero;
        foreach (var wheel in wheels)
        {
            var center = WorldTransform.PointToWorld(wheel.Position);
            var start = center + up * travelUp;
            var length = travelUp + travelDown + wheel.Radius;
            var trace = Scene.Trace.Ray(start, start - up * length).IgnoreGameObjectHierarchy(GameObject).WithoutTags("trigger", "player", "sade_npc").Run();
            if (!trace.Hit || trace.StartedSolid || Vector3.Dot(trace.Normal, up) < .15f) { wheel.Extension = travelDown; continue; }
            contacts++;
            normals += trace.Normal;
            var distance = (trace.EndPosition - start).Length;
            var compression = travelUp + wheel.Radius - distance;
            wheel.Extension = road ? Math.Clamp(-compression, -travelUp, travelDown) : -compression;
            var pointVelocity = Body.GetVelocityAtPoint(center);
            var spring = weight * (1 + compression / Math.Max(travelUp + travelDown, 1) * Math.Clamp(data.SuspensionForce, .3f, 5) * 3);
            var damping = Vector3.Dot(pointVelocity, up) * mass / Math.Max(wheels.Count, 1) * (3 + Math.Clamp(data.SuspensionDamping, 0, 1) * 20);
            if (bike)
            {
                var springRate = weight / Math.Max(travelUp + travelDown, 1) * Math.Clamp(data.SuspensionForce, .3f, 5) * 3;
                var critical = 2 * MathF.Sqrt(springRate * mass / Math.Max(wheels.Count, 1));
                damping = Vector3.Dot(pointVelocity, up) * critical * Math.Clamp(.65f + data.SuspensionDamping, .7f, 1.1f);
            }
            else if (road)
            {
                var axleCount = Math.Max(1, wheels.Count(w => w.Front == wheel.Front));
                var axleShare = VehicleDriving.BrakeAxleShare(data.SuspensionBias, wheel.Front);
                var supportedMass = mass * axleShare / axleCount;
                var springRate = supportedMass * gravity * Math.Clamp(data.SuspensionForce, .3f, 5) * 4 / Math.Max(travelUp + travelDown, 1);
                spring = supportedMass * gravity * VehicleDriving.RoadSuspensionLoad(compression, travelUp, travelDown, data.SuspensionForce);
                damping = Vector3.Dot(pointVelocity, up) * 2 * MathF.Sqrt(springRate * supportedMass) * Math.Clamp(.65f + data.SuspensionDamping, .7f, 1.1f);
            }
            var load = Math.Clamp(spring - damping, 0, weight * 4);
            // The two-wheel adapter explicitly balances the chassis into its turn below.
            // Applying its suspension/tire forces at the offset wheel bones adds a second,
            // unbalanced roll torque (outward during cornering), defeating that balance.
            // Use the mass center for bikes; terrain pitch/roll comes from the contact plane.
            if (bike) Body.ApplyForce(up * load);
            else Body.ApplyForceAt(center, up * load);
            var wheelRotation = Rotation.FromAxis(up, wheel.Front ? groundSteering * data.SteeringLockDegrees : 0);
            var forward = wheelRotation * Forward;
            forward = (forward - trace.Normal * Vector3.Dot(forward, trace.Normal)).Normal;
            var right = Vector3.Cross(forward, trace.Normal).Normal;
            var longitudinal = Vector3.Dot(pointVelocity, forward);
            var lateral = Vector3.Dot(pointVelocity, right);
            var traction = load * Math.Clamp(data.Traction * 1.8f, .3f, 3);
            var lateralForce = Math.Clamp(-lateral * mass / Math.Max(wheels.Count, 1) * 6, -traction, traction);
            var driven = data.DriveType == "4" || (data.DriveType == "F" ? wheel.Front : !wheel.Front);
            var topSpeed = Math.Max(100, data.MaxSpeed * CoordinateSpace.UnitsPerCentimeter);
            var drivenWheels = Math.Max(1, wheels.Count(w => data.DriveType == "4" || (data.DriveType == "F" ? w.Front : !w.Front)));
            var drive = driven && !aircraft ? throttle * VehicleDriving.DriveAcceleration(data.EngineAcceleration, longitudinal, topSpeed, throttle) * mass / drivenWheels : 0;
            var braking = (!aircraft && (brake || throttle * longitudinal < -20)) || Driver is not { IsValid: true };
            if (braking) drive = -Math.Clamp(longitudinal / delta, -data.BrakeDeceleration * 39.37008f, data.BrakeDeceleration * 39.37008f) * mass / Math.Max(wheels.Count, 1);
            if (road)
            {
                var parking = Driver is not { IsValid: true };
                var pedal = throttle * longitudinal < -20;
                var handbrake = brake && (data.HandlingFlags & 0x10) == 0;
                if (pedal || parking)
                {
                    var axleCount = Math.Max(1, wheels.Count(w => w.Front == wheel.Front));
                    var share = VehicleDriving.BrakeAxleShare(data.BrakeBias, wheel.Front) / axleCount;
                    var deceleration = VehicleDriving.RoadBrakeAcceleration(data.BrakeDeceleration);
                    drive = -Math.Clamp(longitudinal / delta, -deceleration, deceleration) * mass * share;
                }
                else if (brake) drive = 0; // Holding the brake cuts drive torque, including on a driven front axle.
                if (handbrake && !wheel.Front)
                {
                    // Original ProcessCarWheelPair locks the rear axle independently of
                    // brake-pedal strength; the tire's traction limit determines its force.
                    drive = -longitudinal / delta * mass / Math.Max(wheels.Count, 1);
                }
            }
            var longitudinalForce = Math.Clamp(drive, -traction, traction);
            var tireForce = forward * longitudinalForce + right * lateralForce * (brake && !wheel.Front ? .35f : 1);
            if (bike) Body.ApplyForce(tireForce);
            else Body.ApplyForceAt(center, tireForce);
        }
        Grounded = contacts > 0;
        if (Grounded) groundNormal = normals.Normal;
        if (Grounded)
        {
            var desiredYaw = bike ? bikeTurn.YawRate : groundSteering * Vector3.Dot(Body.Velocity, Forward) / Math.Max(WheelBase(), 40) * MathF.Tan(data.SteeringLockDegrees * MathF.PI / 180);
            var velocity = Body.AngularVelocity;
            var yaw = Vector3.Dot(velocity, Vector3.Up);
            Body.AngularVelocity = velocity + Vector3.Up * (desiredYaw - yaw) * Math.Clamp(delta * 3, 0, 1);
        }
        if (bike) SimulateBikeBalance(delta, bikeTurn.LeanRadians);
        if (!aircraft) Body.ApplyForce(-Body.Velocity * mass * Math.Clamp(data.Drag * .015f, .005f, .2f));
        else if (Grounded) Body.ApplyForce(-Body.Velocity * mass * .015f);
    }

    private void SimulateBikeBalance(float delta, float targetLean)
    {
        if (!Grounded) return; // Airborne bikes retain momentum instead of snapping upright.
        bikeLean = VehicleDriving.Approach(bikeLean, Driver is { IsValid: true } ? targetLean : 0, delta * 1.8f);
        var surfaceRight = Vector3.Cross(Forward, groundNormal).Normal;
        var desiredUp = (groundNormal * MathF.Cos(bikeLean) - surfaceRight * MathF.Sin(bikeLean)).Normal;
        var correction = Vector3.Cross(Up, desiredUp) * 7;
        var angular = Body.AngularVelocity;
        var yaw = Vector3.Up * Vector3.Dot(angular, Vector3.Up);
        Body.AngularVelocity = yaw + Vector3.Lerp(angular - yaw, correction.WithZ(0), 1 - MathF.Exp(-delta * 9));
    }

    private float WheelBase() => wheels.Count < 2 ? 100 : wheels.Max(w => w.Position.y) - wheels.Min(w => w.Position.y);

    private bool WaterSurface(out float height)
    {
        // Official Sandbox supplies a global WaterVolume in the game's isolated context,
        // distinct from the engine's Sandbox.WaterVolume.
        // Core Component/Collider remain shared across contexts.
        if (waterScanRemaining <= 0)
        {
            // Scene's type index deliberately excludes the base Component class. Water
            // requires a collider, whose indexed owners expose their actual component list.
            waterVolumes = Scene.GetAllComponents<Collider>()
                .SelectMany(c => c.GameObject.Components.GetAll<Component>())
                .Where(c => c.GetType().FullName is "Sandbox.WaterVolume" or "WaterVolume")
                .Distinct().ToArray();
            waterScanRemaining = 1;
        }
        foreach (var water in waterVolumes)
        {
            if (!water.IsValid || !water.Active) continue;
            var collider = water.GetComponent<Collider>();
            if (!collider.IsValid || !collider.Enabled) continue;
            var local = water.WorldTransform.PointToLocal(WorldPosition);
            var box = collider.LocalBounds;
            if (local.x < box.Mins.x || local.x > box.Maxs.x || local.y < box.Mins.y || local.y > box.Maxs.y) continue;
            var surfaceOffset = water.GetType().GetProperty("SurfaceOffset")?.GetValue(water) as float?;
            // Engine water uses origin + SurfaceOffset; Sandbox's game component uses
            // the top of its trigger box instead (GameManager.Water.cs).
            height = surfaceOffset.HasValue ? water.WorldPosition.z + surfaceOffset.Value
                : water.WorldTransform.PointToWorld(box.Center.WithZ(box.Maxs.z)).z;
            return true;
        }
        height = 0;
        return false;
    }

    private void SimulateBoat(float delta, float mass, float gravity, bool hover)
    {
        var floating = WaterSurface(out var height);
        if (hover)
        {
            var ground = Scene.Trace.Ray(WorldPosition + Vector3.Up * 20, WorldPosition + Vector3.Down * 70).IgnoreGameObjectHierarchy(GameObject).WithoutTags("trigger").Run();
            if (ground.Hit && (!floating || ground.EndPosition.z > height)) { floating = true; height = ground.EndPosition.z + 12; }
        }
        Grounded = floating && WorldPosition.z < height + 50;
        if (!Grounded) return;
        var target = height + (hover ? 10 : 0);
        // WaterVolume already applies native submerged-hull buoyancy to touching bodies.
        // Hovercraft additionally support themselves above solid terrain.
        if (hover)
        {
            var acceleration = gravity + Math.Clamp((target - WorldPosition.z) * 12 - Body.Velocity.z * 4, -gravity, gravity * 2);
            Body.ApplyForce(Vector3.Up * (mass * acceleration));
        }
        Stabilize(delta, 2);
        var forward = Forward.WithZ(0).Normal;
        var side = Right.WithZ(0).Normal;
        var speed = Vector3.Dot(Body.Velocity, forward);
        var max = Math.Max(Profile.Data.MaxSpeed * CoordinateSpace.UnitsPerCentimeter, 300);
        // Boat handling acceleration uses a much smaller source range than road vehicles.
        var thrust = hover ? Math.Clamp(Profile.Data.EngineAcceleration * 4, 70, 450) : Math.Clamp(Profile.Data.EngineAcceleration * 120, 40, 600);
        Body.ApplyForce(forward * mass * throttle * thrust * Math.Clamp(1 - Math.Abs(speed) / max, 0, 1));
        Body.ApplyForce(-side * Vector3.Dot(Body.Velocity, side) * mass * (hover ? .8f : 2));
        Body.ApplyForce(-forward * speed * mass * .1f);
        var yaw = steering * Math.Clamp(Math.Abs(speed) / 180, .15f, 1) * (speed < -10 ? -1 : 1);
        Body.AngularVelocity = Body.AngularVelocity.WithZ(MathX.Lerp(Body.AngularVelocity.z, yaw, delta * 3));
    }

    private void SimulateHelicopter(float delta, float mass, float gravity)
    {
        Grounded = false;
        if (Driver is not { IsValid: true }) return;
        var authority = Profile.Data.FlyingHandling.FirstOrDefault();
        var power = Math.Clamp(authority * 2, .7f, 1.6f);
        // Collective controls a vertical climb/descent target; rotor spool is visible and
        // affects lift. Neutral collective holds altitude once the rotor reaches speed.
        var acceleration = VehicleDriving.HelicopterLift(gravity, Body.Velocity.z, lift, rotorSpeed, power);
        Body.ApplyForce(Vector3.Up * (mass * acceleration));
        var forward = Forward.WithZ(0).Normal;
        var right = Right.WithZ(0).Normal;
        Body.ApplyForce((forward * -pitch + right * roll) * mass * 320 * power * rotorSpeed);
        Body.ApplyForce(-Body.Velocity.WithZ(0) * mass * .4f);
        // Cyclic tilt reaches a bounded angle and levels when released; yaw remains independent.
        var desiredUp = (Vector3.Up + forward * -pitch * .32f + right * roll * .32f).Normal;
        var correction = Vector3.Cross(Up, desiredUp) * 3;
        var desired = correction + Vector3.Up * steering * .9f * rotorSpeed;
        Body.AngularVelocity = Vector3.Lerp(Body.AngularVelocity, desired, Math.Clamp(delta * 3, 0, 1));
    }

    private void SimulatePlane(float delta, float mass, float gravity)
    {
        if (wheels.Count > 0) SimulateGround(delta, mass, gravity);
        else Grounded = false;
        var velocity = Body.Velocity;
        var speed = Vector3.Dot(velocity, Forward);
        var max = Math.Max(Profile.Data.MaxSpeed * CoordinateSpace.UnitsPerCentimeter, 1500);
        var handling = Profile.Data.FlyingHandling;
        float Source(int index, float fallback) => index < handling.Length ? handling[index] : fallback;
        var referenceSpeed = Math.Clamp(max * .38f, 450, 1600);
        var authority = VehicleDriving.PlaneControlAuthority(speed, referenceSpeed);
        var angleOfAttack = MathF.Atan2(-Vector3.Dot(velocity, Up), Math.Max(speed, 1));
        if (Driver is { IsValid: true })
        {
            Body.ApplyForce(Forward * mass * Math.Clamp(Source(0, .5f) * 750, 180, 1000) * aircraftThrottle * Math.Clamp(1 - Math.Max(speed, 0) / max, 0, 1));
        }
        // tFlyingHandlingData: 0 thrust,2 yaw,5 roll,7 pitch,9 form lift,10 attack lift.
        // Source coefficients set relative response; aerodynamic scaling is a mount adapter.
        var liftAcceleration = VehicleDriving.PlaneLiftAcceleration(speed, referenceSpeed, angleOfAttack, Source(9, .02f), Source(10, .15f), gravity);
        var liftDirection = Vector3.Cross(Right, velocity.Length > 1 ? velocity.Normal : Forward).Normal;
        Body.ApplyForce(liftDirection * mass * liftAcceleration);
        var coefficient = VehicleDriving.PlaneLiftCoefficient(angleOfAttack, Source(9, .02f), Source(10, .15f));
        var pressure = Math.Max(speed, 0) / referenceSpeed;
        var drag = gravity * pressure * pressure * (.035f + Math.Clamp(Profile.Data.Drag, 0, 30) * .002f + .07f * coefficient * coefficient);
        if (velocity.Length > 1) Body.ApplyForce(-velocity.Normal * mass * drag);
        Body.ApplyForce(-Right * Vector3.Dot(velocity, Right) * mass * authority * .8f);

        var occupied = Driver is { IsValid: true };
        var bank = MathF.Atan2(Vector3.Dot(Up, Right.WithZ(0).Normal), Math.Max(Vector3.Dot(Up, Vector3.Up), .05f));
        var bankYaw = Grounded ? 0 : VehicleDriving.PlaneBankTurn(bank, Math.Max(speed, 0), gravity);
        var pitchRate = (occupied ? pitch * Math.Clamp(Math.Abs(Source(7, .0003f)) * 1600, .2f, .9f) : 0)
            - angleOfAttack * Math.Clamp(Math.Abs(Source(8, .002f)) * 400, .3f, 1.5f);
        var rollRate = occupied ? roll * Math.Clamp(Math.Abs(Source(5, .003f)) * 300, .2f, 1.6f) : 0;
        var yawRate = occupied ? steering * Math.Clamp(Math.Abs(Source(2, -.00015f)) * 2500, .1f, .55f) : 0;
        var target = (Right * pitchRate + Forward * rollRate + Up * yawRate) * authority + Vector3.Up * bankYaw;
        // Rudder/ailerons/elevator lose authority with airspeed; no zero-speed hovering turns.
        if (!Grounded || authority > .25f)
            Body.AngularVelocity = Vector3.Lerp(Body.AngularVelocity, target, 1 - MathF.Exp(-delta * (1.2f + authority)));
    }

    private void Stabilize(float delta, float strength)
    {
        var correction = Vector3.Cross(Up, Vector3.Up) * strength;
        var angular = Body.AngularVelocity;
        Body.AngularVelocity = angular + (correction - angular.WithZ(0)) * Math.Clamp(delta * strength, 0, 1);
    }

    internal void Shutdown()
    {
        if (shuttingDown) return; shuttingDown = true;
        audio?.Dispose(); audio = null;
        VehicleSeats.Detach(this); Driver = null;
    }
    protected override void OnDestroy() => Shutdown();
    private sealed class Wheel(string name, Transform bind, float radius, bool front, GameObject? visual)
    {
        internal string Name = name;
        internal Transform Bind = bind;
        internal Vector3 Position = bind.Position;
        internal float Radius = radius;
        internal bool Front = front;
        internal GameObject? Visual = visual;
        internal float Extension;
    }
}
