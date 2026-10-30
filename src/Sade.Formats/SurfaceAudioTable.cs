using Sade.Contracts;

namespace Sade.Formats;

public sealed class SurfaceAudioTable
{
    public string[] Names { get; }
    private readonly SurfaceAudio[] groups;
    public SurfaceAudio this[byte id] => id < groups.Length ? groups[id] : SurfaceAudio.Unknown;

    private SurfaceAudioTable(string[] names, SurfaceAudio[] audio) { Names = names; groups = audio; }

    public static SurfaceAudioTable Parse(string text)
    {
        var names = new List<string>(); var groups = new List<SurfaceAudio>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            if (fields.Length != 10 || names.Count == 256 || !seen.Add(fields[0])) throw new InvalidDataException("Invalid surface audio row: " + fields[0]);
            var selected = SurfaceAudio.Unknown;
            for (var i = 1; i < fields.Length; i++)
            {
                if (fields[i] is not ("0" or "1")) throw new InvalidDataException("Invalid surface audio flag: " + fields[0]);
                if (fields[i] == "1")
                {
                    if (selected != SurfaceAudio.Unknown) throw new InvalidDataException("Ambiguous surface audio group: " + fields[0]);
                    selected = (SurfaceAudio)(i - 1);
                }
            }
            names.Add(fields[0]); groups.Add(selected);
        }
        if (names.Count == 0 || names[0] != "DEFAULT") throw new InvalidDataException("Surface audio table must start with DEFAULT.");
        return new(names.ToArray(), groups.ToArray());
    }
}
