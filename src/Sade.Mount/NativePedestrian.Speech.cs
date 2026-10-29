using Sandbox;
using Sade.Contracts;

namespace Sade.Mount;

/// <summary>Local original-game speech. Audio is loaded only for a selected phrase.</summary>
internal sealed partial class NativePedestrian
{
    private SpeechVoiceData? speechVoice;
    private SoundHandle? speechHandle;
    private float speechClock;
    private float nextSpeech;
    private float nextAmbientSpeech;
    private readonly Dictionary<string, float> speechCooldowns = new(StringComparer.Ordinal);
    private readonly Queue<string> recentSpeech = new();
    internal string? LastSpeechPath { get; private set; }
    internal string? LastSpeechContext { get; private set; }
    internal string? SpeechVoiceId => speechVoice?.Id;
    internal bool SpeechPlaying => speechHandle.IsValid() && speechHandle.IsPlaying;

    private void InitializeSpeech()
    {
        var voices = Profile.Speech?.Voices;
        if (voices is not { Length: > 0 }) return;
        // Keep a character's selected voice for its lifetime, including a scene reload.
        // Range variation is per spawned pedestrian, never per spoken sentence.
        var bytes = GameObject.Id.ToByteArray();
        var identity = BitConverter.ToUInt32(bytes, 0);
        speechVoice = voices[identity % (uint)voices.Length];
        nextAmbientSpeech = Game.Random.Float(4, 9);
    }

    private void UpdateSpeech(float delta)
    {
        speechClock += Math.Max(0, delta);
        if (speechHandle.IsValid()) speechHandle.Position = SpeechPosition();
        if (speechVoice is null || IsDead || IsProxy || IsPosed || combatTarget.IsValid()) return;
        if (speechClock < nextAmbientSpeech) return;
        nextAmbientSpeech = speechClock + Game.Random.Float(22, 40);
        var neighbour = Peers.FirstOrDefault(p => p.IsValid() && p.Active && p != this && !p.IsDead && !p.IsPosed
            && Vector3.DistanceBetween(WorldPosition, p.WorldPosition) < 250
            && !WeaponMechanics.AreRivals(Profile.Faction, Profile.EnemyFactions, p.Profile.Faction, p.Profile.EnemyFactions));
        // A greeting needs somebody nearby. CHAT is the original free-roam context,
        // used only when that character has no suitable greeting, never mission audio.
        var listenerNearby = Scene == Game.ActiveScene
            && Vector3.DistanceBetween(WorldPosition, Sound.Listener.Position) < 280;
        if (!neighbour.IsValid() && !listenerNearby) return;
        SpeakFirst(neighbour?.Profile.Speech?.Female == true ? "PCONV_GREET_FEM" : "PCONV_GREET_MALE", "CHAT");
    }

    private void SpeakFirst(params string[] contexts)
    {
        foreach (var context in contexts)
        {
            if (speechVoice?.Contexts.Any(x => x.Context == context) != true) continue;
            TrySpeak(context);
            return;
        }
    }

    private void SpeakCombat(string? targetFaction)
    {
        var context = targetFaction switch
        {
            "Ballas" => "SHOOT_BALLAS", "Grove" => "SHOOT_FAMILIES",
            "Vagos" => "SHOOT_LSV", "Aztecas" => "SHOOT_VLA", _ => "SHOOT_GENERIC"
        };
        SpeakFirst(context, "SHOOT_GENERIC", "SHOOT", "FIGHT");
    }

    private bool TrySpeak(string context, bool interrupt = false)
    {
        if (IsProxy || IsPosed || shuttingDown || !Active || Profile.Speech is not { } profile) return false;
        if (IsDead && context != "PAIN_DEATH") return false;
        var voice = context.StartsWith("PAIN_", StringComparison.Ordinal) ? profile.PainVoice : speechVoice;
        var range = voice?.Contexts.FirstOrDefault(x => x.Context == context);
        if (voice is null || range is null || range.Last < range.First) return false;
        if (speechCooldowns.GetValueOrDefault(context) > speechClock) return false;
        if (!interrupt && (speechClock < nextSpeech || SpeechPlaying)) return false;
        // No decoding for distant NPCs, and no crowd of simultaneous voices.
        if (Scene == Game.ActiveScene && Vector3.DistanceBetween(WorldPosition, Sound.Listener.Position) > 1600) return false;
        var speaking = Peers.Count(p => p.IsValid() && p != this && p.SpeechPlaying
            && Vector3.DistanceBetween(WorldPosition, p.WorldPosition) < 1000);
        if (speaking >= (interrupt ? 3 : 2)) return false;

        var candidates = Enumerable.Range(range.First, range.Last - range.First + 1)
            .Where(sample => !recentSpeech.Contains(voice.Bank + "/" + sample)).ToArray();
        while (candidates.Length == 0 && recentSpeech.Count > 0)
        {
            recentSpeech.Dequeue();
            candidates = Enumerable.Range(range.First, range.Last - range.First + 1)
                .Where(sample => !recentSpeech.Contains(voice.Bank + "/" + sample)).ToArray();
        }
        if (candidates.Length == 0) return false;
        var sample = candidates[Random.Shared.Next(candidates.Length)];
        var path = $"mount://sade/sounds/sanandreas/audio/sfx/{voice.Bank.ToLowerInvariant()}/s_{sample:000}.vsnd";
        // SoundFile resolves through the existing lazy reader and engine resource cache.
        var sound = SoundFile.Load(path);
        if (sound is null) return false;
        StopSpeech();
        speechHandle = Sound.PlayFile(sound);
        if (!speechHandle.IsValid()) { speechHandle = null; return false; }
        speechHandle.Name = $"{Profile.Name}: {context}";
        speechHandle.Position = SpeechPosition();
        speechHandle.ListenLocal = false;
        speechHandle.DistanceAttenuation = true;
        speechHandle.Distance = 1400;
        // The engine default loses most volume within 5% of its distance. Speech
        // remains clear at conversational range and fades smoothly across the street.
        speechHandle.Falloff = new Curve(new(0, 1), new(.12f, 1), new(.4f, .45f), new(1, 0));
        speechHandle.Volume = 1;
        LastSpeechPath = path;
        LastSpeechContext = context;
        recentSpeech.Enqueue(voice.Bank + "/" + sample);
        while (recentSpeech.Count > 6) recentSpeech.Dequeue();
        speechCooldowns[context] = speechClock + range.CooldownSeconds + Game.Random.Float(0, 1);
        // Pain contexts share a budget even when alternating light/heavy hits.
        if (context is "PAIN_HIGH" or "PAIN_LOW")
            speechCooldowns["PAIN_HIGH"] = speechCooldowns["PAIN_LOW"] = speechClock + 2;
        // Mounted sounds can have a playable CSfxTable before the engine supplies
        // VSound_t metadata. IsPlaying prevents overlap without querying Duration,
        // whose native accessor throws on those otherwise playable resources.
        nextSpeech = speechClock + 4;
        nextAmbientSpeech = Math.Max(nextAmbientSpeech, speechClock + 20);
        return true;
    }

    private Vector3 SpeechPosition() => Renderer.IsValid() && Renderer.TryGetBoneTransform("Head", out var head)
        ? head.Position : WorldPosition + Vector3.Up * 60;

    private void StopSpeech()
    {
        speechHandle?.Stop(.03f);
        speechHandle = null;
    }
}
