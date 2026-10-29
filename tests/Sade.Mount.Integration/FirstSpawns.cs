// Run through sbox-cli run_csharp in the official Sandbox project while playing.
// Force discovery before the first OnStart, reproducing the old placeholder race.
var player = Player.FindLocalPlayer() ?? throw new Exception("Join Sandbox before running this check.");
var mount = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
var results = new System.Collections.Generic.List<object>();
foreach (var (path, runtimeName) in new[] {
    ("mount://sade/00_featured/00_cj.vmdl", "PedestrianRuntime"),
    ("mount://sade/00_featured/vehicles/infernus.vmdl", "VehicleRuntime"),
    ("mount://sade/00_featured/props/sm_bin1.vmdl", "") })
{
    var spawner = new MountSpawner(path);
    await spawner.Loading;
    if (!spawner.HasModel) throw new Exception("The installed model failed to load: " + path);
    var objects = await spawner.Spawn(new Transform(player.WorldPosition + player.WorldRotation.Forward * 180), player);
    try
    {
        if (runtimeName.Length > 0)
        {
            var runtime = mount.GetType("Sade.Mount." + runtimeName)!;
            var session = runtime.GetField("current", flags)!.GetValue(null)!;
            session.GetType().GetMethod("Observe", flags)!.Invoke(session, null);
        }
        await Task.Delay(500);
        foreach (var root in objects)
        {
            var metadata = root.GetComponent<MountMetadata>(true);
            var fallback = metadata is not null && (bool)typeof(MountMetadata).GetField("_isFallback", flags)!.GetValue(metadata)!;
            var renderers = root.GetComponentsInChildren<ModelRenderer>(true).ToArray();
            if (fallback || renderers.Length == 0 || renderers.Any(r => !r.Enabled || r.Model.IsError))
                throw new Exception("First spawn showed the missing-game fallback: " + path);
            results.Add(new { path, Fallback = fallback, VisibleRenderers = renderers.Length });
        }
    }
    finally { foreach (var root in objects) if (root.IsValid) root.Destroy(); }
}
return results;
