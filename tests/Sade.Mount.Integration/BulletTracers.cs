// Trusted engine probe. All objects and renders belong to a disposable scene.
var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var effects = assembly.GetType("Sade.Mount.NativeWeaponEffects")!;
var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
var emit = effects.GetMethod("Tracer", flags)!;
var stop = effects.GetMethod("Stop", flags)!;
var originalScene = Game.ActiveScene;
var originalCamera = originalScene?.Camera;
var scene = new Scene();
using (scene.Push())
try
{
    var wall = scene.CreateObject(); wall.WorldPosition = new Vector3(300, 0, 60);
    var collider = wall.AddComponent<BoxCollider>(); collider.Scale = new Vector3(2, 200, 120);
    var start = new Vector3(0, 0, 60);
    var hit = scene.Trace.Ray(start, new Vector3(1000, 0, 60)).Run();
    if (!hit.Hit) throw new Exception("Tracer wall fixture did not collide.");
    emit.Invoke(null, [scene, start, hit.EndPosition, false]);
    var line = scene.GetAllComponents<LineRenderer>().Single();
    var lastHead = start.x;
    var frames = 0;
    while (line.IsValid && frames < 30)
    {
        foreach (var point in line.VectorPoints)
            if (point.x < start.x - .001f || point.x > hit.EndPosition.x + .001f || Math.Abs(point.y) > .001f || Math.Abs(point.z - 60) > .001f)
                throw new Exception("Bullet streak escaped the damage ray or crossed the wall.");
        if (line.VectorPoints[^1].x < lastHead) throw new Exception("Tracer moved backwards.");
        lastHead = line.VectorPoints[^1].x;
        scene.GameTick(.005);
        frames++;
    }
    if (line.IsValid || frames < 2) throw new Exception("Tracer lifetime did not advance and clean up.");
    emit.Invoke(null, [scene, start, start + Vector3.Forward, false]);
    if (scene.GetAllComponents<LineRenderer>().Any()) throw new Exception("Degenerate close hit created a tracer.");

    // Compare a real engine render with and without the additive streak.
    var backdrop = scene.CreateObject();
    backdrop.WorldPosition = new Vector3(150, 100, 60);
    backdrop.WorldScale = new Vector3(10, .1f, 6);
    backdrop.AddComponent<ModelRenderer>().Model = Model.Cube;
    var camera = scene.CreateObject().AddComponent<CameraComponent>();
    camera.WorldPosition = new Vector3(150, -320, 80);
    camera.WorldRotation = Rotation.LookAt(new Vector3(150, 0, 60) - camera.WorldPosition);
    camera.FieldOfView = 58;
    scene.GameTick(.01);
    using var baseline = new Bitmap(800, 450);
    camera.RenderToBitmap(baseline, false);
    scene.GameTick(.001);
    camera.RenderToBitmap(baseline, false);
    var before = baseline.GetPixels();
    emit.Invoke(null, [scene, start, hit.EndPosition, false]);
    // GameTick invokes component OnPreRender; RenderToBitmap only prepares cameras.
    scene.GameTick(.001);
    using var rendered = new Bitmap(800, 450);
    camera.RenderToBitmap(rendered, false);
    var after = rendered.GetPixels();
    var changed = 0;
    for (var i = 0; i < before.Length; i++)
        if (Math.Abs(before[i].r - after[i].r) + Math.Abs(before[i].g - after[i].g) + Math.Abs(before[i].b - after[i].b) > .1f) changed++;
    var capture = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sade-bullet-tracer.png");
    System.IO.File.WriteAllBytes(capture, rendered.ToPng());
    if (changed < 30 || changed > before.Length / 20)
        throw new Exception($"Tracer did not produce a distinct visible streak ({changed} changed pixels, background {before[0]} -> {after[0]}).");

    for (var i = 0; i < 300; i++) emit.Invoke(null, [scene, start, hit.EndPosition, i % 2 == 0]);
    scene.GameTick(.001);
    var crowdCount = scene.GetAllComponents<LineRenderer>().Count();
    if (crowdCount > 256) throw new Exception("Crowd tracer limit failed.");
    stop.Invoke(null, [scene]);
    scene.GameTick(.01);
    if (scene.GetAllComponents<LineRenderer>().Any()) throw new Exception("Scene shutdown left tracers alive.");
    if (originalScene?.Camera != originalCamera) throw new Exception("Probe altered the active camera.");
    return new { Passed = true, Frames = frames, HitDistance = hit.Distance, ChangedPixels = changed,
        CrowdCount = crowdCount, Capture = capture, HitBounds = true, Cleanup = true, ActiveSceneUnchanged = true };
}
finally { stop.Invoke(null, [scene]); scene.Destroy(); }
