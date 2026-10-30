using Sandbox;
using Sandbox.Mounting;
using N = System.Numerics;

namespace Sade.Mount;

internal sealed class WorldMapLoader(string start) : SceneLoader<SadeMount>
{
    protected override Texture GetThumbnail()
    {
        // Metadata is queried from both the game and menu filesystems.
        var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(SadeMount).Assembly.Location)!, "assets", "ui", "sade", "san_andreas.png");
        if (!File.Exists(path)) return null!;
        using var bitmap = Bitmap.CreateFromBytes(File.ReadAllBytes(path));
        return bitmap.ToTexture();
    }

    protected override void BuildScene()
    {
        // Mounted scene requests can be synchronous during editor startup. Never
        // await a main-thread continuation from inside the resource loader's lock.
        _ = Host.PrepareWorld();
        var source = WorldRuntime.Spawn(start);
        var root = new GameObject { Name = "San Andreas", WorldPosition = -CoordinateSpace.Position(source) };
        root.Tags.Add(WorldRuntime.Marker); root.Tags.Add("sade_world_" + start);
        var deck = new GameObject(root, true, "Initial ground") { LocalPosition = CoordinateSpace.Position(source) + Vector3.Down * 8 };
        deck.Tags.Add(WorldRuntime.LoadingTag);
        deck.AddComponent<BoxCollider>().Scale = new Vector3(512, 512, 16);
        var spawn = new GameObject { Name = "Spawn", WorldPosition = Vector3.Up * 64, WorldRotation = Rotation.FromYaw(180) };
        spawn.AddComponent<SpawnPoint>(); spawn.Tags.Add("spawn");
        var preview = new GameObject { Name = "Map preview camera", WorldPosition = new Vector3(0, 100, 64) }.AddComponent<CameraComponent>();
        preview.WorldRotation = Rotation.FromYaw(-90); preview.Priority = -1000; preview.IsMainCamera = true;
        preview.GameObject.Tags.Add("sade_world_preview_camera");
        var sun = new GameObject { Name = "Sun" }.AddComponent<DirectionalLight>();
        sun.WorldRotation = Rotation.From(new Angles(48, -35, 0)); sun.LightColor = Color.White * 2;
        var sky = new GameObject { Name = "Sky" }.AddComponent<SkyBox2D>();
        sky.SkyMaterial = Material.Load("materials/skybox/skybox_day_01.vmat");
        var ambient = new GameObject { Name = "Ambient light" }.AddComponent<AmbientLight>(); ambient.Color = Color.White * .4f;
    }
}

public sealed partial class SadeMount
{
    private Task? worldPreparation;
    private int worldGeneration;
    internal Task PrepareWorld()
    {
        if (worldPreparation is { IsFaulted: false, IsCanceled: false }) return worldPreparation;
        var generation = Volatile.Read(ref worldGeneration);
        return worldPreparation = Task.Run(() =>
        {
            var data = Reader.GetWorld();
            var spatial = new WorldSpatialIndex(data);
            if (generation == Volatile.Read(ref worldGeneration)) WorldRuntime.SetIndex(spatial);
        });
    }

    private void RegisterWorld(Sandbox.Mounting.MountContext context)
    {
        context.Add(ResourceType.Scene, "maps/San Andreas", new WorldMapLoader("grove"));
        foreach (var (start, title, savedPath) in new[]
        {
            ("grove", "San Andreas - Grove Street", "san_andreas"),
            ("fierro", "San Andreas - San Fierro", "san_andreas_san_fierro"),
            ("venturas", "San Andreas - Las Venturas", "san_andreas_las_venturas")
        })
        {
            context.Add(ResourceType.Scene, "maps/" + title, new WorldMapLoader(start) { Flags = ResourceFlags.DeveloperOnly });
            // Keep the original source-build paths working without duplicate map-picker entries.
            context.Add(ResourceType.Scene, "maps/" + savedPath, new WorldMapLoader(start) { Flags = ResourceFlags.DeveloperOnly });
        }
    }
}
