// Run after the Grove Street neighbourhood loads. Does not move the player.
var scene = Game.ActiveScene;
var found = new System.Collections.Generic.Dictionary<string, (Surface Surface, Vector3 Position)>();
for (float x = -1150; x <= 1800; x += 64)
for (float y = 650; y <= 2375; y += 64)
{
    var at = new Vector3(x, y, 220);
    var hit = scene.Trace.Ray(at, at - Vector3.Up * 1200).WithTag("sade_world_geometry").Run();
    if (!hit.Hit || hit.Normal.z < .6f || hit.Surface is null) continue;
    found.TryAdd(hit.Surface.ResourceName, (hit.Surface, hit.EndPosition));
}
foreach (var name in new[] { "default", "grass", "metal" })
    if (!found.ContainsKey(name)) throw new Exception("Expected a loaded " + name + " surface around Grove Street.");
var footsteps = found.Values.Select(p => p.Surface.SoundCollection.FootLeft?.ResourcePath).Where(p => p is not null).Distinct().ToArray();
if (footsteps.Length < 3) throw new Exception("Concrete, grass and metal must resolve different footstep sounds.");
var statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
System.Collections.Generic.HashSet<SoundHandle> Sounds() => ((System.Collections.IEnumerable)typeof(SoundHandle).GetField("active", statics)!.GetValue(null)!).Cast<SoundHandle>().ToHashSet();
var obj = scene.CreateObject(); obj.Name = "Temporary surface-footstep probe";
var played = new System.Collections.Generic.List<SoundHandle>();
try
{
    var controller = obj.Components.Create<PlayerController>(false);
    foreach (var name in new[] { "default", "grass", "metal" })
    {
        var sample = found[name]; controller.GroundSurface = sample.Surface; obj.WorldPosition = sample.Position + Vector3.Up * 32;
        var before = Sounds();
        controller.PlayFootstepSound(sample.Position, .25f, 0);
        var newSounds = Sounds().Except(before).ToArray(); played.AddRange(newSounds);
        if (!newSounds.Any(s => s.IsPlaying)) throw new Exception(name + " footstep did not start through PlayerController.");
    }
    return new { Passed = true, Groups = found.Select(p => new { Name = p.Key, Surface = p.Value.Surface.ResourcePath, Position = p.Value.Position.ToString(), Footstep = p.Value.Surface.SoundCollection.FootLeft?.ResourcePath }).ToArray(), NativeFootstepPlayback = true };
}
finally { foreach (var sound in played) sound.Stop(); obj.Destroy(); }
