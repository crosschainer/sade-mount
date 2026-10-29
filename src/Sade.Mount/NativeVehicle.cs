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
        VehicleSeats.Attach(this);
    }

    internal void Tick(float delta)
    {
        if (!initialized || shuttingDown || !Body.IsValid) return;
        VehicleSeats.Tick(this);
        var localDriver = Driver is { IsValid: true, IsProxy: false };
        throttle = localDriver ? Math.Clamp(Input.AnalogMove.x, -1, 1) : 0;
        steering = localDriver ? Math.Clamp(Input.AnalogMove.y, -1, 1) : 0;
        brake = localDriver && Input.Down("Jump");
        lift = localDriver ? (Input.Down("Jump") ? 1 : 0) - (Input.Down("Duck") ? 1 : 0) : 0;
        pitch = localDriver ? (Input.Keyboard.Down("s") ? 1 : 0) - (Input.Keyboard.Down("w") ? 1 : 0) : 0;
        roll = localDriver ? (Input.Keyboard.Down("d") ? 1 : 0) - (Input.Keyboard.Down("a") ? 1 : 0) : 0;
        wheelSpin += Vector3.Dot(Body.Velocity, Forward) * delta;
        foreach (var wheel in wheels)
        {
            var position = wheel.Position + Vector3.Down * wheel.Extension;
            var side = wheel.Position.x < 0 ? 180f : 0;
            var spin = Rotation.FromAxis(Vector3.Forward, wheelSpin / wheel.Radius * 180 / MathF.PI);
            if (wheel.Visual is { IsValid: true } visual)
            {
                visual.LocalPosition = position;
                visual.LocalRotation = Rotation.FromYaw(side + (wheel.Front ? steering * Profile.Data.SteeringLockDegrees : 0)) * spin;
            }
            else if (Renderer.Model.Bones.GetBone(wheel.Name) is { } bone)
                Renderer.SetBoneTransform(bone, new Transform(position, Rotation.FromYaw(wheel.Front ? steering * Profile.Data.SteeringLockDegrees : 0) * wheel.Bind.Rotation * spin));
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
        var groundSteering = aircraft ? (Input.Keyboard.Down("q") ? 1 : 0) - (Input.Keyboard.Down("r") ? 1 : 0) : steering;
        var up = Up;
        var travelUp = Math.Clamp(data.SuspensionUpper * CoordinateSpace.UnitsPerCentimeter, 4, 36);
        var travelDown = Math.Clamp(-data.SuspensionLower * CoordinateSpace.UnitsPerCentimeter, 3, 30);
        var weight = mass * gravity / Math.Max(wheels.Count, 1);
        var contacts = 0;
        foreach (var wheel in wheels)
        {
            var center = WorldTransform.PointToWorld(wheel.Position);
            var start = center + up * travelUp;
            var length = travelUp + travelDown + wheel.Radius;
            var trace = Scene.Trace.Ray(start, start - up * length).IgnoreGameObjectHierarchy(GameObject).WithoutTags("trigger", "player", "sade_npc").Run();
            if (!trace.Hit || trace.StartedSolid || Vector3.Dot(trace.Normal, up) < .15f) { wheel.Extension = travelDown; continue; }
            contacts++;
            var distance = (trace.EndPosition - start).Length;
            var compression = travelUp + wheel.Radius - distance;
            wheel.Extension = -compression;
            var pointVelocity = Body.GetVelocityAtPoint(center);
            var spring = weight * (1 + compression / Math.Max(travelUp + travelDown, 1) * Math.Clamp(data.SuspensionForce, .3f, 5) * 3);
            var damping = Vector3.Dot(pointVelocity, up) * mass / Math.Max(wheels.Count, 1) * (3 + Math.Clamp(data.SuspensionDamping, 0, 1) * 20);
            var load = Math.Clamp(spring - damping, 0, weight * 4);
            Body.ApplyForceAt(center, up * load);
            var wheelRotation = Rotation.FromAxis(up, wheel.Front ? groundSteering * data.SteeringLockDegrees : 0);
            var forward = wheelRotation * Forward;
            forward = (forward - trace.Normal * Vector3.Dot(forward, trace.Normal)).Normal;
            var right = Vector3.Cross(forward, trace.Normal).Normal;
            var longitudinal = Vector3.Dot(pointVelocity, forward);
            var lateral = Vector3.Dot(pointVelocity, right);
            var traction = load * Math.Clamp(data.Traction * 1.8f, .3f, 3);
            var lateralForce = Math.Clamp(-lateral * mass / Math.Max(wheels.Count, 1) * 6, -traction, traction);
            var driven = data.DriveType == "4" || (data.DriveType == "F" ? wheel.Front : !wheel.Front);
            var drive = driven && !aircraft ? throttle * Math.Clamp(data.EngineAcceleration * 6.7f, 20, 900) * mass / Math.Max(wheels.Count / 2, 1) : 0;
            var topSpeed = Math.Max(100, data.MaxSpeed * CoordinateSpace.UnitsPerCentimeter);
            if (longitudinal * MathF.Sign(throttle) > (throttle >= 0 ? topSpeed : topSpeed * .3f)) drive = 0;
            var braking = (!aircraft && (brake || throttle * longitudinal < -20)) || Driver is not { IsValid: true };
            if (braking) drive = -Math.Clamp(longitudinal / delta, -data.BrakeDeceleration * 32, data.BrakeDeceleration * 32) * mass / Math.Max(wheels.Count, 1);
            var longitudinalForce = Math.Clamp(drive, -traction, traction);
            Body.ApplyForceAt(center, forward * longitudinalForce + right * lateralForce * (brake && !wheel.Front ? .35f : 1));
        }
        Grounded = contacts > 0;
        if (Grounded)
        {
            var desiredYaw = groundSteering * Vector3.Dot(Body.Velocity, Forward) / Math.Max(WheelBase(), 40) * MathF.Tan(data.SteeringLockDegrees * MathF.PI / 180);
            var velocity = Body.AngularVelocity;
            var yaw = Vector3.Dot(velocity, Vector3.Up);
            Body.AngularVelocity = velocity + Vector3.Up * (desiredYaw - yaw) * Math.Clamp(delta * 3, 0, 1);
        }
        // Two contact points cannot balance a stationary bike by themselves. Keep the arcade
        // rider/stand balance active before touchdown too, so a falling bike can regain contact.
        if (data.Family is "bike" or "bicycle") Stabilize(delta, 10);
        Body.ApplyForce(-Body.Velocity * mass * Math.Clamp(data.Drag * .015f, .005f, .2f));
    }

    private float WheelBase() => wheels.Count < 2 ? 100 : wheels.Max(w => w.Position.y) - wheels.Min(w => w.Position.y);

    private bool WaterSurface(out float height)
    {
        foreach (var water in Scene.GetAllComponents<WaterVolume>())
        {
            var collider = water.GetComponent<Collider>();
            if (!collider.IsValid || !collider.Enabled) continue;
            var local = water.WorldTransform.PointToLocal(WorldPosition);
            var box = collider.LocalBounds;
            if (local.x < box.Mins.x || local.x > box.Maxs.x || local.y < box.Mins.y || local.y > box.Maxs.y) continue;
            height = water.WorldPosition.z + water.SurfaceOffset;
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
        var upward = gravity + lift * gravity * .5f * power - Body.Velocity.z * 1.5f;
        Body.ApplyForce(Vector3.Up * Math.Max(0, mass * upward));
        Body.ApplyForce(Forward.WithZ(0).Normal * throttle * mass * 180 * power - Body.Velocity.WithZ(0) * mass * .15f);
        var desired = Vector3.Up * steering * .8f + Right * -throttle * .12f;
        Body.AngularVelocity = Vector3.Lerp(Body.AngularVelocity, desired, delta * 2);
        Stabilize(delta, 2);
    }

    private void SimulatePlane(float delta, float mass, float gravity)
    {
        if (wheels.Count > 0) SimulateGround(delta, mass, gravity);
        var speed = Vector3.Dot(Body.Velocity, Forward);
        var max = Math.Max(Profile.Data.MaxSpeed * CoordinateSpace.UnitsPerCentimeter, 1500);
        var power = Profile.Data.FlyingHandling.FirstOrDefault();
        if (Driver is { IsValid: true })
        {
            // Space adds throttle; Ctrl reduces it. W/S pitch, A/D roll and Q/E yaw.
            var engine = Math.Clamp(.65f + lift * .35f, 0, 1);
            Body.ApplyForce(Forward * mass * Math.Clamp(power * 600, 150, 900) * engine * Math.Clamp(1 - speed / max, 0, 1));
            var authority = Math.Clamp(Math.Abs(speed) / 500, .05f, 1);
            var yaw = (Input.Keyboard.Down("q") ? 1 : 0) - (Input.Keyboard.Down("r") ? 1 : 0);
            var target = Right * pitch * .65f + Forward * roll * 1.1f + Up * yaw * .35f;
            Body.AngularVelocity = Vector3.Lerp(Body.AngularVelocity, target * authority, delta * 2);
        }
        var stall = Math.Clamp(max * .22f, 350, 900);
        var liftRatio = Math.Clamp(speed * speed / (stall * stall), 0, 1.4f);
        Body.ApplyForce(Up * mass * gravity * liftRatio);
        Body.ApplyForce(-Right * Vector3.Dot(Body.Velocity, Right) * mass * .6f);
        Body.ApplyForce(-Body.Velocity * mass * .025f);
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
