using System.Numerics;
using Sade.Contracts;
using Sade.Mount;

internal static class CabinGeometryChecks
{
    internal static void Run()
    {
        var vehicle = new VehicleData("test", "Test", "test", "road", "TEST")
        {
            Sockets = [new("ped_frontseat", new(new Vector3(40, 0, 0), Quaternion.Identity, Vector3.One)),
                new("ped_backseat", new(new Vector3(40, 100, 0), Quaternion.Identity, Vector3.One))]
        };
        var wheel = Rim(new(-40, -40, 35), "vehiclesteering128");
        var floor = Plane("floor", -30);
        var model = new ModelData { Id = "test", Parts = [wheel, floor] };
        var baseline = VehicleCabinGeometry.Create(model, vehicle)!;
        Require(baseline.Steering is not null && baseline.Seats.Length == 4, "A supported cabin must expose its rim and all four feet pairs.");
        Require(baseline.Steering!.Left.X < -50 && baseline.Steering.Right.X > -30, "Left/right grips must agree with driver handedness.");
        Require(baseline.Steering.Normal.Y > .99f && baseline.Steering.Up.Z > .99f, "Steering normal must face the driver.");
        Require(baseline.Seats.All(s => Math.Abs(s.LeftFoot.Z + 20) < .001f && Math.Abs(s.RightFoot.Z + 20) < .001f && s.LeftFoot.Y < s.Pelvis.Y),
            "Ankles must have shoe clearance above the actual floor, ahead of the pelvis.");

        // Some installed models use the steering material on remote trim. Do not use
        // the whole material bounds as the wheel center/radius.
        var trim = Plane("vehiclesteering128", 35);
        var combined = wheel with { Vertices = wheel.Vertices.Concat(trim.Vertices).ToArray(),
            Indices = wheel.Indices.Concat(trim.Indices.Select(i => i + wheel.Vertices.Length)).ToArray() };
        var contaminated = VehicleCabinGeometry.Create(model with { Parts = [combined, floor, Plane("MI_TransparentCar", -12)] }, vehicle)!;
        Require(Vector3.Distance(contaminated.Steering!.Left, baseline.Steering.Left) < .001f
            && contaminated.Seats.All(s => Math.Abs(s.LeftFoot.Z + 20) < .001f), "Remote trim and invisible panels must not move hand/floor targets.");

        var alternate = VehicleCabinGeometry.Create(model with { Parts = [wheel with { MaterialId = "broadway92interior128" }, floor] }, vehicle)!;
        Require(alternate.Steering is not null, "Interior-material steering rims must remain supported.");
        var noRim = VehicleCabinGeometry.Create(model with { Parts = [floor] }, vehicle)!;
        Require(noRim.Steering is null && noRim.Seats.Length == 4, "Cabin feet may be fitted without inventing steering grips.");
        Require(VehicleCabinGeometry.Create(model with { Parts = [floor] }, vehicle with { AnimationGroup = "quad" }) is null,
            "An unsupported saddle must not acquire a car footwell.");
        Require(VehicleCabinGeometry.Create(model, vehicle with { Family = "bike" }) is null, "Bike grip fitting remains separate.");
        var malformed = floor with { Indices = [0, 1, int.MaxValue] };
        var missingFloor = VehicleCabinGeometry.Create(model with { Parts = [wheel, malformed] }, vehicle)!;
        Require(missingFloor.Seats.Length == 0, "Malformed floor triangles must be rejected instead of crashing a model load.");
        var entryDummy = vehicle with { Sockets = [vehicle.Sockets[0], new("ped_backseat", new(new Vector3(40, 100, -100), Quaternion.Identity, Vector3.One))] };
        var badRear = VehicleCabinGeometry.Create(model with { Parts = [wheel, floor, Plane("underbody", -130)] }, entryDummy)!;
        Require(badRear.Seats.Length == 2, "A legacy entry dummy below the cabin must not fit its feet to the underbody.");
        Console.WriteLine("PASS cabin rim isolation, alternate materials, source handedness, floor clearance and safe missing geometry.");
    }

    internal static void RunInstalled(IGameReader reader)
    {
        var vehicles = reader.GetVehicles();
        foreach (var id in new[] { "infernus", "premier", "banshee", "landstal", "linerun", "savanna", "broadway", "slamvan", "tornado", "manana", "elegant", "buccanee", "rdtrain" })
        {
            var vehicle = vehicles.Single(v => v.Id == id);
            var cabin = VehicleCabinGeometry.Create(reader.LoadModel(vehicle.ModelId), vehicle);
            Require(cabin?.Steering is not null && cabin.Seats.Length >= 2, id + ": installed rim or floor was not recognized.");
            var wheel = cabin!.Steering!;
            Require(wheel.Left.X < wheel.Right.X && Vector3.Distance(wheel.Left, wheel.Right) is > 20 and < 45
                && wheel.Normal.Y > .3f && wheel.Up.Z > .3f, id + ": invalid installed rim orientation/width.");
            Require(cabin.Seats.All(s => s.LeftFoot.Y <= s.Pelvis.Y - 39 && s.RightFoot.Y <= s.Pelvis.Y - 39),
                id + ": feet did not reach forward into the footwell.");
        }
        var bus = vehicles.Single(v => v.Id == "bus");
        var busCabin = VehicleCabinGeometry.Create(reader.LoadModel(bus.ModelId), bus)!;
        Require(busCabin.Steering is not null && busCabin.Seats.Length == 1 && busCabin.Seats[0].Pelvis.X < 0,
            "Bus driver floor must be supported without inventing a floor over the right entry or below-cabin rear dummies.");
        Console.WriteLine("PASS 13 installed cabin rims/floors and safe bus entry/rear-dummy handling.");
    }

    private static MeshPartData Rim(Vector3 center, string material)
    {
        var vertices = new List<VertexData>(); var indices = new List<int>();
        const int steps = 32;
        for (var i = 0; i < steps; i++)
        {
            var angle = MathF.Tau * i / steps;
            foreach (var radius in new[] { 14f, 16f })
                vertices.Add(Vertex(center + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius)));
        }
        for (var i = 0; i < steps; i++)
        {
            var next = (i + 1) % steps;
            indices.AddRange([i * 2, i * 2 + 1, next * 2, next * 2, i * 2 + 1, next * 2 + 1]);
        }
        return new("rim", material, vertices.ToArray(), indices.ToArray());
    }
    private static MeshPartData Plane(string material, float z) => new("plane", material,
        [Vertex(new(-100, -160, z)), Vertex(new(100, -160, z)), Vertex(new(-100, 160, z)), Vertex(new(100, 160, z))],
        [0, 1, 2, 2, 1, 3]);
    private static VertexData Vertex(Vector3 p) => new(p, Vector3.UnitZ, Vector4.Zero, Vector2.Zero, Vector4.One, default, Vector4.Zero);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
