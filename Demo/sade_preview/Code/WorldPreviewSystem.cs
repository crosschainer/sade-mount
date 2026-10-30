using Sandbox;
using System.Linq;

namespace SadePreview;

/// <summary>A walkable preview when opening the mounted map directly in this demo.</summary>
public sealed class WorldPreviewSystem : GameObjectSystem<WorldPreviewSystem>
{
    private PlayerController? player;
    public WorldPreviewSystem(Scene scene) : base(scene)
    {
        Listen(Stage.StartUpdate, -50, EnsurePlayer, "World preview player");
    }

    private void EnsurePlayer()
    {
        if (Scene.IsEditor || Scene.IsLoading || !Game.IsPlaying || player.IsValid()) return;
        if (!Scene.GetAllObjects(false).Any(o => o.Tags.Has("sade_world", false))) return;
        player = Scene.GetAllComponents<PlayerController>().FirstOrDefault(p => p.Active && !p.IsProxy);
        if (player.IsValid()) return;
        var camera = Scene.GetAllComponents<CameraComponent>().FirstOrDefault(c => c.IsMainCamera && c.Active);
        if (camera is null)
        {
            camera = Scene.CreateObject().AddComponent<CameraComponent>();
            camera.GameObject.Name = "Preview game camera"; camera.IsMainCamera = true;
        }
        var spawn = Scene.GetAllComponents<SpawnPoint>().FirstOrDefault();
        var obj = Scene.CreateObject(); obj.Name = "Map preview player";
        obj.Tags.Add("player");
        obj.WorldPosition = spawn?.WorldPosition ?? Vector3.Up * 64;
        var body = new GameObject(obj, true, "Body").AddComponent<SkinnedModelRenderer>();
        body.Model = Model.Load("models/citizen_human/human.vmdl");
        player = obj.AddComponent<PlayerController>(); player.Renderer = body;
        player.ThirdPerson = false; player.EyeAngles = new Angles(0, -90, 0);
    }
}
