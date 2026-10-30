using Sandbox;
using Sandbox.Mounting;
using Sade.Contracts;

namespace Sade.Mount;

internal sealed class VehicleLoader(VehicleData data) : ResourceLoader<SadeMount>
{
    private PrefabFile? prefab;

    protected override object Load()
    {
        var path = SadeMount.VehicleBodyPath(data);
        var model = Model.Load(path);
        if (model is null || model.IsError) throw new InvalidDataException($"Could not build vehicle {data.Id}.");
        var profile = new VehicleRuntime.Profile(data, path, data.WheelModelId is null ? null : Host.ModelPath(data.WheelModelId));
        VehicleRuntime.Register(path, profile);
        VehicleRuntime.Register(model.Name, profile);
        var builder = new PrefabBuilder().WithName(Path);
        using (builder.Scope())
        {
            var root = new GameObject { Name = data.Name };
            root.Tags.Add(VehicleRuntime.Marker);
            root.Tags.Add("sade_" + data.Family);
            var renderer = root.Components.Create<SkinnedModelRenderer>();
            renderer.Model = model;
            renderer.UseAnimGraph = false;
            root.Components.Create<ModelCollider>().Model = model;
            var body = root.Components.Create<Rigidbody>();
            body.MassOverride = Math.Max(1, data.MassKg);
            body.OverrideMassCenter = true;
            body.MassCenterOverride = CoordinateSpace.Position(data.CenterOfMass);
            body.LinearDamping = 0.05f;
            body.AngularDamping = 0.15f;
            VehicleRuntime.BuildPrefabWheels(root, profile);
            if (data.Family is not ("train" or "trailer") && !data.RemoteControlled)
                VehicleSeats.BuildPrefab(root, data);
        }
        prefab = StablePrefab.Create(builder, Path);
        return prefab;
    }

    protected override void Shutdown() { PrefabBuilder.Destroy(prefab); prefab = null; }
}

public sealed partial class SadeMount
{
    internal static string VehicleBodyPath(VehicleData data) => $"mount://sade/vehicle_bodies/{data.Id}.vmdl";
    internal void RegisterVehicleModel(Model model, string path, VehicleData data)
    {
        var profile = new VehicleRuntime.Profile(data, VehicleBodyPath(data), data.WheelModelId is null ? null : ModelPath(data.WheelModelId));
        VehicleRuntime.Register(path, profile);
        VehicleRuntime.Register(model.Name, profile);
    }

    private void RegisterVehicles(MountContext context)
    {
        var featured = new HashSet<string>(["infernus", "savanna", "bmx", "nrg500", "squalo", "dodo", "maverick"], StringComparer.OrdinalIgnoreCase);
        foreach (var data in Reader.GetVehicles().OrderByDescending(v => featured.Contains(v.Id))
                     .ThenBy(v => SpawnCatalog.GetVehiclePath(v) ?? v.Id, StringComparer.OrdinalIgnoreCase))
        {
            var modelPath = ModelPath(data.ModelId);
            var profile = new VehicleRuntime.Profile(data, VehicleBodyPath(data), data.WheelModelId is null ? null : ModelPath(data.WheelModelId));
            VehicleRuntime.Register(modelPath, profile);
            VehicleRuntime.Register(VehicleBodyPath(data), profile);
            var bodyLoader = new ModelLoader(data.ModelId, previewWheels: false) { Flags = ResourceFlags.DeveloperOnly };
            context.Add(ResourceType.Model, $"vehicle_bodies/{data.Id}", bodyLoader);
            // The folder tree retains the casing of its first registered path.
            if (SpawnCatalog.GetVehiclePath(data) is { } displayPath)
            {
                context.Add(ResourceType.PrefabFile, displayPath, new VehicleLoader(data));
                context.Add(ResourceType.Model, displayPath, new AliasLoader(modelPath));
            }
            var loader = new VehicleLoader(data) { Flags = ResourceFlags.DeveloperOnly };
            context.Add(ResourceType.PrefabFile, $"vehicles/{data.Family}/{data.Id}", loader);
            context.Add(ResourceType.Model, $"vehicles/{data.Family}/{data.Id}", new AliasLoader(modelPath) { Flags = ResourceFlags.DeveloperOnly });
            loader.Tags.UnionWith(["sade", "vehicle", data.Family, data.Name]);
            if (data.Family is not ("train" or "trailer") && !data.RemoteControlled) loader.Tags.Add("driveable");
            if (featured.Contains(data.Id))
            {
                context.Add(ResourceType.PrefabFile, $"00_featured/vehicles/{data.Id}", new VehicleLoader(data) { Flags = ResourceFlags.DeveloperOnly });
                context.Add(ResourceType.Model, $"00_featured/vehicles/{data.Id}", new AliasLoader(modelPath) { Flags = ResourceFlags.DeveloperOnly });
            }
        }
    }
}
