// Decode only the curated scenery, verify usable geometry/collision, and capture a private overview.
var mount = Sandbox.Mounting.Directory.Get("sade") ?? throw new Exception("SADE is not available.");
var groups = new[] { "Common buildings", "Ramps", "Street props", "Construction props" };
var loaders = mount.GetAll(Sandbox.Mounting.ResourceType.Model)
    .Where(x => (x.Flags & Sandbox.Mounting.ResourceFlags.DeveloperOnly) == 0 && groups.Contains(x.RelativePath.Split('/')[0])).ToArray();
var active = Game.ActiveScene; var activeCamera = active?.Camera;
var scene = new Scene();
var results = new System.Collections.Generic.List<object>(); var failures = new System.Collections.Generic.List<string>();
using (scene.Push())
try
{
    var ambient = scene.CreateObject().AddComponent<AmbientLight>(); ambient.Color = Color.White * .8f;
    var sun = scene.CreateObject().AddComponent<DirectionalLight>(); sun.WorldRotation = Rotation.From(new Angles(50, 20, 0)); sun.LightColor = Color.White * 2;
    var camera = scene.CreateObject().AddComponent<CameraComponent>(); camera.FieldOfView = 45;
    var cells = 8;
    for (var i = 0; i < loaders.Length; i++)
    {
        var loader = loaders[i];
        var model = await loader.GetOrCreate() as Model;
        if (model is null || model.IsError) { failures.Add(loader.Path + ": failed to load"); continue; }
        var bounds = model.Bounds;
        var size = bounds.Size;
        if (!float.IsFinite(size.Length) || size.Length < 1 || model.MeshCount == 0) failures.Add(loader.Path + ": invalid geometry");
        var collision = model.Physics?.Parts?.Count ?? 0;
        if (collision == 0) failures.Add(loader.Path + ": missing collision");
        var obj = scene.CreateObject(); obj.Name = loader.Name;
        var scale = 80f / Math.Max(size.x, Math.Max(size.y, size.z));
        obj.WorldScale = Vector3.One * scale;
        obj.WorldPosition = new Vector3((i % cells) * 110, (i / cells) * 110, 0) - bounds.Center * scale;
        obj.AddComponent<ModelRenderer>().Model = model;
        results.Add(new { loader.Name, loader.Path, Size = size, Center = bounds.Center, CollisionParts = collision });
        await Task.Yield();
    }
    var center = new Vector3(385, 385, 0);
    camera.WorldPosition = center + new Vector3(-650, -750, 1100); camera.WorldRotation = Rotation.LookAt(center - camera.WorldPosition);
    scene.GameTick(.02);
    using var bitmap = new Bitmap(1800, 1800); camera.RenderToBitmap(bitmap, false);
    var image = System.IO.Path.Combine(exchangeDirectory, "curated-scenery.png"); System.IO.File.WriteAllBytes(image, bitmap.ToPng());
    if (active?.Camera != activeCamera) throw new Exception("Active camera changed.");
    return new { Passed = failures.Count == 0 && results.Count == 64, Loaded = results.Count, Failures = failures, Image = image, Assets = results };
}
finally { scene.Destroy(); }
