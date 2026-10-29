using System.Globalization;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using Sade.Contracts;
using Sade.Formats;

namespace Sade.Reader;

public sealed partial class GameReader
{
    private VehicleData[]? vehicles;

    public IReadOnlyList<VehicleData> GetVehicles()
    {
        lock (gate)
        {
            if (vehicles is not null) return vehicles;
            var handling = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var flying = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            var boats = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            var bikes = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in ReadOriginalData("handling.cfg").Split('\n'))
            {
                var line = raw.Split(';')[0].Trim();
                if (line.Length == 0) continue;
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields[0] is "$" or "%" or "!")
                {
                    var table = fields[0] == "$" ? flying : fields[0] == "%" ? boats : bikes;
                    table[fields[1]] = fields.Skip(2).Select(Number).ToArray();
                }
                else if (fields[0] != "^" && fields.Length >= 36) handling[fields[0]] = fields;
            }
            var result = new List<VehicleData>();
            foreach (var raw in ReadOriginalData("vehicles.ide").Split('\n'))
            {
                // Original rows mix comma delimiters and whitespace (three shipped rows omit a comma).
                var fields = raw.Split('#')[0].Split([' ', '\t', '\r', ','], StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 11 || !int.TryParse(fields[0], out var number)) continue;
                var name = fields[1];
                var model = entries.FirstOrDefault(e => e.Kind == AssetKind.SkeletalMesh && e.Id.Equals($"/Game/SanAndreas/Vehicles/SKR_{name}.SKR_{name}", StringComparison.OrdinalIgnoreCase));
                if (model is null) { report?.Invoke(new Diagnostic(name, "vehicles.ide model has no mounted skeletal mesh.")); continue; }
                if (!handling.TryGetValue(fields[4], out var h)) throw new InvalidDataException($"Vehicle {name} has unknown handling ID {fields[4]}.");
                var family = name.Equals("vortex", StringComparison.OrdinalIgnoreCase) ? "hovercraft" : fields[3].ToLowerInvariant() switch
                { "car" or "mtruck" or "quad" => "road", "bike" => "bike", "bmx" => "bicycle", "heli" => "helicopter", var type => type };
                var sockets = ReadVehicleSockets(model.SkeletonId ?? $"/Game/SanAndreas/Vehicles/SKR_{name}_skel");
                var wheel = entries.FirstOrDefault(e => e.Kind == AssetKind.StaticMesh && e.Id.Equals($"/Game/SanAndreas/Vehicles/SM_{name}_wheel.SM_{name}_wheel", StringComparison.OrdinalIgnoreCase));
                result.Add(new VehicleData(name, name, model.Id, family, fields[4])
                {
                    ModelNumber = number, AnimationGroup = fields[6], MassKg = Number(h[1]), TurnMass = Number(h[2]), Drag = Number(h[3]),
                    CenterOfMass = GtaCoordinates.ToUnreal(new System.Numerics.Vector3(Number(h[4]), Number(h[5]), Number(h[6]))),
                    SubmergedPercent = Integer(h[7]), Traction = Number(h[8]), TractionLoss = Number(h[9]),
                    MaxSpeed = Number(h[12]) * 100 / 3.6f, EngineAcceleration = Number(h[13]), DriveType = h[15],
                    BrakeDeceleration = Number(h[17]), SteeringLockDegrees = Number(h[20]), SuspensionForce = Number(h[21]),
                    SuspensionDamping = Number(h[22]), SuspensionUpper = Number(h[24]) * 100, SuspensionLower = Number(h[25]) * 100,
                    SuspensionBias = Number(h[26]), ModelFlags = ulong.Parse(h[31], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    HandlingFlags = ulong.Parse(h[32], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    WheelRadiusFront = fields.Length > 12 ? Number(fields[12]) * 50 : 0,
                    WheelRadiusRear = fields.Length > 13 ? Number(fields[13]) * 50 : 0,
                    Sockets = sockets, WheelModelId = wheel?.Id, FlyingHandling = flying.GetValueOrDefault(h[0], []),
                    BoatHandling = boats.GetValueOrDefault(h[0], []), BikeHandling = bikes.GetValueOrDefault(h[0], []),
                    RemoteControlled = name.StartsWith("rc", StringComparison.OrdinalIgnoreCase)
                });
            }
            vehicles = result.ToArray();
            report?.Invoke(new Diagnostic("vehicles", $"Read {vehicles.Length} installed vehicle profiles, {vehicles.Sum(v => v.Sockets.Length)} original sockets, and {vehicles.Count(v => v.WheelModelId is not null)} separate wheel models."));
            return vehicles;
        }
    }

    private VehicleSocketData[] ReadVehicleSockets(string id)
    {
        var skeleton = Provider.LoadPackageObject<USkeleton>(id);
        var reference = skeleton.ReferenceSkeleton;
        var world = new FTransform[reference.FinalRefBoneInfo.Length];
        for (var i = 0; i < world.Length; i++)
        {
            var parent = reference.FinalRefBoneInfo[i].ParentIndex;
            if (parent >= i) throw new InvalidDataException($"Vehicle skeleton {id} has a non-topological bone parent.");
            world[i] = parent < 0 ? reference.FinalRefBonePose[i] : reference.FinalRefBonePose[i] * world[parent];
        }
        var result = new Dictionary<string, VehicleSocketData>(StringComparer.OrdinalIgnoreCase);
        // Door/rotor bones supplement the positional dummies serialized as sockets.
        for (var i = 0; i < world.Length; i++)
        {
            var name = reference.FinalRefBoneInfo[i].Name.Text;
            if (name.StartsWith("door_", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("_ok") && !name.EndsWith("_dam") || name.Contains("rotor", StringComparison.OrdinalIgnoreCase) || name.StartsWith("wheel_", StringComparison.OrdinalIgnoreCase))
                result[name] = new VehicleSocketData(name, Transform(world[i]));
        }
        foreach (var index in skeleton.GetOrDefault<FPackageIndex[]>("Sockets", []))
        {
            var socket = index.Load();
            if (socket is null) continue;
            var name = socket.GetOrDefault<FName>("SocketName").Text;
            var boneName = socket.GetOrDefault<FName>("BoneName").Text;
            var bone = Array.FindIndex(reference.FinalRefBoneInfo, b => b.Name.Text.Equals(boneName, StringComparison.OrdinalIgnoreCase));
            var transform = new FTransform(socket.GetOrDefault<FRotator>("RelativeRotation").Quaternion(), socket.GetOrDefault<FVector>("RelativeLocation"), socket.GetOrDefault("RelativeScale", FVector.OneVector));
            if (bone >= 0) transform *= world[bone];
            result[name] = new VehicleSocketData(name, Transform(transform));
        }
        return result.Values.ToArray();
    }
}
