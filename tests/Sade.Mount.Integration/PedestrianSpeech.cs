// Trusted run_csharp probe. Uses real mounted voice clips in a disposable scene;
// never changes the active map, player, listener, or camera.
var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var nativeType = assembly.GetType("Sade.Mount.NativePedestrian")!;
var runtimeType = assembly.GetType("Sade.Mount.PedestrianRuntime")!;
var members = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
var activeScene = Game.ActiveScene;
var activeCamera = activeScene?.Camera;
var scene = new Scene();
var controllers = new System.Collections.Generic.List<Component>();
var paths = new System.Collections.Generic.List<string>();
using (scene.Push())
try
{
    scene.NavMesh.IsEnabled = false;
    var a = Spawn("mount://sade/pedestrians/sk_smoke.prefab", new Vector3(0, 0, 0));
    var b = Spawn("mount://sade/pedestrians/sk_smoke.prefab", new Vector3(80, 0, 0));
    var c = Spawn("mount://sade/pedestrians/sk_smoke.prefab", new Vector3(160, 0, 0));
    var peers = Array.CreateInstance(nativeType, controllers.Count);
    for (var i = 0; i < controllers.Count; i++) peers.SetValue(controllers[i], i);
    foreach (var npc in controllers) nativeType.GetProperty("Peers", members)!.SetValue(npc, peers);
    scene.GameTick(.02);

    // Advance the controller naturally beyond its 4–9 second ambient delay. This
    // proves the event path from Tick, rather than only forcing the audio helper.
    nativeType.GetMethod("Tick", members)!.Invoke(a, [10f]);
    Check(Context(a) == "CHAT" && Playing(a), "Tick automatically starts Smoke's ambient line near a friendly pedestrian.");
    paths.Add(Last(a));
    Stop(a);
    Clock(a, 20);
    Check(Speak(a, "FIGHT"), "Smoke starts a supported original fight line.");
    var first = Last(a);
    Check(first.Contains("/spc_na/smoke/s_07"), "Smoke uses his own FIGHT recording.");
    Check(!Speak(a, "FIGHT"), "A rapid second trigger cannot overlap or repeat.");
    Check(!Speak(a, "SHOOT_GENERIC"), "An unsupported context never becomes an arbitrary voice line.");
    Check(Speak(b, "FIGHT"), "A second nearby speaker is permitted.");
    Check(!Speak(c, "FIGHT"), "A third nearby ordinary speaker is suppressed.");
    Stop(b);
    Stop(a);
    Clock(a, 40);
    Check(Speak(a, "FIGHT") && Last(a) != first, "Recent clip avoidance changes the next phrase.");
    paths.Add(Last(a));
    var handle = (SoundHandle)nativeType.GetField("speechHandle", members)!.GetValue(a)!;
    Check(handle.IsPlaying && handle.DistanceAttenuation && !handle.ListenLocal, "Actual SoundHandle plays positional audio.");
    Check(a.GameObject.GetComponentInChildren<SkinnedModelRenderer>().TryGetBoneTransform("Head", out var head), "The head bone is available.");
    Check(handle.Position.Distance(head.Position) < 1, "Voice originates at the rendered head.");

    ((Component.IDamageable)a).OnDamage(new DamageInfo(1, b.GameObject, b.GameObject));
    Check(Context(a) == "PAIN_LOW" && Last(a).Contains("/pain_a/male/"), "Damage interrupts with the male source pain bank.");
    var pain = Last(a);
    ((Component.IDamageable)a).OnDamage(new DamageInfo(25, b.GameObject, b.GameObject));
    Check(Last(a) == pain, "Alternating light/heavy hits shares the pain cooldown.");
    ((Component.IDamageable)a).OnDamage(new DamageInfo(1000, b.GameObject, b.GameObject));
    Check(Context(a) == "PAIN_DEATH" && Last(a).Contains("/pain_a/male/"), "Lethal damage selects the original death context.");
    paths.Add(Last(a));
    Stop(a);

    Clock(b, 30);
    Check(Speak(b, "CHAT"), "Smoke's free-roam CHAT context is available.");
    b.GameObject.Tags.Add("sade_posed");
    nativeType.GetMethod("Tick", members)!.Invoke(b, [.02f]);
    Check(!Playing(b) && !Speak(b, "FIGHT"), "Posing stops speech and suppresses new speech.");
    Clock(c, 30);
    Check(Speak(c, "CHAT"), "Unposed third NPC still speaks.");
    c.Enabled = false;
    Check(!Playing(c), "Disabling the controller stops its live audio.");
    c.Enabled = true;
    Clock(c, 60);
    Check(Speak(c, "FIGHT"), "Re-enabled NPC can speak again.");
    nativeType.GetMethod("Shutdown", members)!.Invoke(c, null);
    Check(!Playing(c), "Shutdown stops speech.");
    Check(activeScene?.Camera == activeCamera, "Active camera remains unchanged.");
    return new { Passed = true, OriginalVoice = "VOICE_GNG_SMOKE", Samples = paths,
        AutomaticTickAmbient = true, Cooldowns = true, RecentAvoidance = true, NearbyLimit = true, PositionalHeadAudio = true,
        HitAndDeath = true, PoseDisableShutdownCleanup = true, ActiveSceneUnchanged = true, MultiplayerTested = false };

    Component Spawn(string path, Vector3 position)
    {
        if (PrefabFile.Load(path) is null) throw new Exception("Missing mounted prefab " + path);
        var obj = GameObject.Clone(path) ?? throw new Exception("Missing mounted prefab " + path);
        obj.WorldPosition = position;
        var renderer = obj.GetComponentInChildren<SkinnedModelRenderer>();
        var profiles = runtimeType.GetField("profiles", statics)!.GetValue(null)!;
        var profile = profiles.GetType().GetProperty("Item")!.GetValue(profiles, [renderer.Model.Name])!;
        var create = obj.Components.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition);
        var npc = (Component)create.MakeGenericMethod(nativeType).Invoke(obj.Components, [false])!;
        controllers.Add(npc);
        nativeType.GetMethod("Initialize", members)!.Invoke(npc, [profile, obj.GetComponent<NavMeshAgent>(true), renderer]);
        npc.Enabled = true;
        return npc;
    }
    bool Speak(Component npc, string context) => (bool)nativeType.GetMethod("TrySpeak", members)!.Invoke(npc, [context, false])!;
    void Stop(Component npc) => nativeType.GetMethod("StopSpeech", members)!.Invoke(npc, null);
    void Clock(Component npc, float value) => nativeType.GetField("speechClock", members)!.SetValue(npc, value);
    string Last(Component npc) => (string)nativeType.GetProperty("LastSpeechPath", members)!.GetValue(npc)!;
    string Context(Component npc) => (string)nativeType.GetProperty("LastSpeechContext", members)!.GetValue(npc)!;
    bool Playing(Component npc) => (bool)nativeType.GetProperty("SpeechPlaying", members)!.GetValue(npc)!;
    void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
finally
{
    foreach (var npc in controllers)
        if (npc.IsValid) nativeType.GetMethod("Shutdown", members)!.Invoke(npc, null);
    scene.Destroy();
}
