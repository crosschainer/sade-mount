using Sandbox;
using Sandbox.Mounting;
using Sade.Contracts;

namespace Sade.Mount;

internal sealed class PedestrianLoader(string id, PedestrianPreset preset, string? weaponId = null) : ResourceLoader<SadeMount>
{
    private PrefabFile? prefab;
    protected override object Load()
    {
        var model = Model.Load(Host.ActorModelPath(id, weaponId));
        if (model is null || model.IsError) throw new InvalidDataException($"Could not build pedestrian model {id}.");
        var missing = new[] { preset.Idle, preset.Walk }.Where(x => !model.AnimationNames.Contains(x, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"{id}: required locomotion clips were not loaded: {string.Join(", ", missing)}.");
        var builder = new PrefabBuilder().WithName(Path);
        using (builder.Scope())
        {
            var root = new GameObject { Name = preset.Name };
            root.Tags.Add(PedestrianRuntime.Marker);
            root.Tags.Add("sade_" + preset.Faction);
            var visual = new GameObject { Name = "Character", Parent = root };
            visual.LocalPosition = new Vector3(0, 0, -model.Bounds.Mins.z);
            var renderer = visual.Components.Create<SkinnedModelRenderer>();
            renderer.Model = model;
            renderer.UseAnimGraph = false;
            renderer.Sequence.Name = preset.Idle;
            renderer.Sequence.Looping = true;
            renderer.Sequence.Blending = true;
            renderer.PlayAnimationsInEditorScene = true;
            var navigation = root.Components.Create<NavMeshAgent>();
            navigation.UpdatePosition = true; navigation.UpdateRotation = true; navigation.AutoTraverseLinks = false;
            navigation.Height = Math.Clamp(model.Bounds.Size.z, 32, 100);
            navigation.Radius = Math.Clamp(Math.Min(model.Bounds.Size.x, model.Bounds.Size.y) * 0.3f, 10, 20);
            navigation.MaxSpeed = Host.WalkSpeed(id);
            var collider = root.Components.Create<CapsuleCollider>();
            collider.Radius = navigation.Radius;
            collider.Start = Vector3.Up * navigation.Radius;
            collider.End = Vector3.Up * (navigation.Height - navigation.Radius);
        }
        prefab = StablePrefab.Create(builder, Path);
        return prefab;
    }
    protected override void Shutdown() { PrefabBuilder.Destroy(prefab); prefab = null; }
}
