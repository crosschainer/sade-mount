using Sade.Contracts;
using Sade.Formats;
using Sade.Reader;

internal static class SpeechTests
{
    internal static void Run(GameReader reader, IReadOnlyList<AssetEntry> entries)
    {
        var ids = entries.Where(x => x.Kind == AssetKind.Sound).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var checkedSamples = 0;
        foreach (var voice in SpeechCatalog.Voices)
        foreach (var range in voice.Contexts)
        {
            Check(range.First >= 0 && range.Last >= range.First && range.CooldownSeconds >= 2, "Valid speech range " + voice.Id);
            for (var sample = range.First; sample <= range.Last; sample++)
            {
                Check(ids.Contains(SpeechCatalog.SoundId(voice.Bank, sample)), "Installed speech sample " + voice.Bank + "/" + sample);
                checkedSamples++;
            }
        }
        var smoke = reader.GetPedestrianPreset("SK_smoke")!.Speech!;
        Check(smoke.Voices.Single().Id == "VOICE_GNG_SMOKE", "Smoke has his own voice.");
        Range(smoke.Voices.Single(), "FIGHT", 73, 79);
        Check(smoke.Voices.Single().Contexts.All(x => x.Context != "SHOOT_GENERIC"), "Unsupported Smoke shooting context stays absent.");
        var fam = reader.GetPedestrianPreset("SK_fam1")!.Speech!;
        Check(fam.Voices.Select(x => x.Id).SequenceEqual(new[] { "VOICE_GNG_FAM1", "VOICE_GNG_FAM2" }), "FAM1 model uses its installed two-voice range.");
        Range(fam.Voices[0], "SHOOT_BALLAS", 310, 318);
        var ballas = reader.GetPedestrianPreset("SK_ballas1")!.Speech!;
        Range(ballas.Voices[0], "SHOOT_FAMILIES", 235, 240);
        var player = reader.GetPedestrianPreset("SK_Player_Combined")!.Speech!;
        Range(player.Voices.Single(), "SHOOT", 309, 322);
        Check(player.PainVoice.Bank == "PAIN_A/CARL", "CJ uses CARL pain recordings.");
        var female = reader.GetPedestrianPreset("SK_bfyri")!.Speech!;
        Check(female.Female && female.PainVoice.Bank == "PAIN_A/FEMALE", "Female pain enum follows installed bank order.");
        var malformedComma = reader.GetPedestrianPreset("SK_bmymoun")!.Speech!;
        Check(malformedComma.Voices.Length > 0, "Shipped missing comma before speech type does not discard the voice.");
        Check(SpeechCatalog.Resolve("VOICE_GNG_SMOKE", "VOICE_GEN_BFYRI") is null, "Cross-family voice ranges are rejected.");

        var decoded = 0;
        foreach (var profile in new[] { smoke, fam, ballas, player, female,
            reader.GetPedestrianPreset("SK_lsv1")!.Speech!, reader.GetPedestrianPreset("SK_lapd1")!.Speech! })
        foreach (var voice in profile.Voices.Take(1).Append(profile.PainVoice))
        {
            foreach (var range in voice.Contexts)
            for (var sample = range.First; sample <= range.Last; sample++)
                Check(ids.Contains(SpeechCatalog.SoundId(voice.Bank, sample)), "Installed pain/voice sample " + voice.Bank + "/" + sample);
            var id = SpeechCatalog.SoundId(voice.Bank, voice.Contexts[0].First);
            var sound = reader.LoadSound(id);
            Check(sound.Format == "OGG" && sound.Data.AsSpan(0, 4).SequenceEqual("OggS"u8), "Real installed OGG decode " + id);
            decoded++;
        }
        Console.WriteLine($"PASS speech: {checkedSamples} contextual sample references exist; {decoded} actual voice/pain OGG files decoded; original model voice ranges and gender/CJ pain mapping verified.");
    }

    private static void Range(SpeechVoiceData voice, string context, int first, int last)
    {
        var range = voice.Contexts.Single(x => x.Context == context);
        Check(range.First == first && range.Last == last, voice.Id + " " + context + " matches source endpoints.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
