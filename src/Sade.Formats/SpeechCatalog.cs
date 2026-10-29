using System.Globalization;
using Sade.Contracts;

namespace Sade.Formats;

/// <summary>Independent lookup of factual GTA bank/context ranges. See Data/SpeechContexts.sources.txt.</summary>
public static class SpeechCatalog
{
    private sealed record Entry(int Index, SpeechVoiceData Voice);
    private static readonly IReadOnlyDictionary<string, Entry> Entries = ReadEntries();
    public static IReadOnlyList<SpeechVoiceData> Voices { get; } = Entries.Values.Select(x => x.Voice).ToArray();

    public static PedestrianSpeechData? Resolve(string first, string last, bool female = false)
    {
        if (!Entries.TryGetValue(first, out var start) || !Entries.TryGetValue(last, out var end)) return null;
        var family = first[..first.IndexOf('_', 6)];
        if (!last.StartsWith(family + "_", StringComparison.OrdinalIgnoreCase) || end.Index < start.Index) return null;
        var voices = Entries.Where(x => x.Key.StartsWith(family + "_", StringComparison.OrdinalIgnoreCase)
            && x.Value.Index >= start.Index && x.Value.Index <= end.Index && x.Value.Voice.Contexts.Length > 0)
            .OrderBy(x => x.Value.Index).Select(x => x.Value.Voice).ToArray();
        if (voices.Length == 0) return null;
        var player = family.Equals("VOICE_PLY", StringComparison.OrdinalIgnoreCase);
        // PAIN_A ordering follows the actual voice enum: CARL, FEMALE, MALE.
        // A comment in the reverse-engineered LUT swaps the latter two; the installed
        // bank counts (169, 131, 101) and sample endpoints confirm this ordering.
        var pain = player
            ? new SpeechVoiceData("VOICE_PAIN_CARL", "PAIN_A/CARL",
                [new("PAIN_HIGH", 87, 100, 2), new("PAIN_LOW", 101, 121, 2), new("PAIN_DEATH", 25, 33, 2)])
            : female
                ? new SpeechVoiceData("VOICE_PAIN_FEMALE", "PAIN_A/FEMALE",
                    [new("PAIN_HIGH", 46, 87, 2), new("PAIN_LOW", 46, 87, 2), new("PAIN_DEATH", 25, 45, 2)])
                : new SpeechVoiceData("VOICE_PAIN_MALE", "PAIN_A/MALE",
                    [new("PAIN_HIGH", 36, 68, 2), new("PAIN_LOW", 36, 68, 2), new("PAIN_DEATH", 7, 35, 2)]);
        return new(voices, pain, female);
    }

    public static string SoundId(string bank, int sample) =>
        $"/Game/SanAndreas/Audio/SFX/{bank}/S_{sample:000}.S_{sample:000}";

    private static IReadOnlyDictionary<string, Entry> ReadEntries()
    {
        using var stream = typeof(SpeechCatalog).Assembly.GetManifestResourceStream("Sade.Formats.Data.SpeechContexts.tsv")
            ?? throw new InvalidOperationException("Speech context metadata is missing.");
        using var reader = new StreamReader(stream);
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('|');
            var contexts = fields[3].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(value =>
            {
                var range = value.Split(':');
                return new SpeechRangeData(range[0], int.Parse(range[1], CultureInfo.InvariantCulture),
                    int.Parse(range[2], CultureInfo.InvariantCulture), float.Parse(range[3], CultureInfo.InvariantCulture));
            }).ToArray();
            entries.Add(fields[0], new(int.Parse(fields[2], CultureInfo.InvariantCulture), new(fields[0], fields[1], contexts)));
        }
        return entries;
    }
}
