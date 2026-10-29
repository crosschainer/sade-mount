using System.Globalization;
using Sade.Contracts;

namespace Sade.Formats;

public sealed record PedDefinition(int Id, string Model, string TextureDictionary, string Group);
public sealed record PedAnimationGroup(string Name, string Bank, string Type, string[] Clips);

public sealed class PedMetadata
{
    public IReadOnlyDictionary<string, PedDefinition> Definitions { get; }
    public IReadOnlyDictionary<string, PedAnimationGroup> WalkGroups { get; }
    private PedMetadata(Dictionary<string, PedDefinition> definitions, Dictionary<string, PedAnimationGroup> groups)
    { Definitions = definitions; WalkGroups = groups; }

    public static PedMetadata Parse(string pedsIde, string animGroups)
    {
        ArgumentNullException.ThrowIfNull(pedsIde);
        ArgumentNullException.ThrowIfNull(animGroups);
        var definitions = new Dictionary<string, PedDefinition>(StringComparer.OrdinalIgnoreCase);
        var inPeds = false;
        foreach (var line in Lines(pedsIde))
        {
            if (line.Equals("peds", StringComparison.OrdinalIgnoreCase)) { inPeds = true; continue; }
            if (line.Equals("end", StringComparison.OrdinalIgnoreCase)) { inPeds = false; continue; }
            if (!inPeds) continue;
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 6 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                throw new InvalidDataException($"Invalid pedestrian definition: {line}");
            var definition = new PedDefinition(id, fields[1], fields[2], fields[5]);
            if (!definitions.TryAdd(definition.Model, definition)) throw new InvalidDataException($"Duplicate pedestrian '{definition.Model}'.");
        }
        var groups = new Dictionary<string, PedAnimationGroup>(StringComparer.OrdinalIgnoreCase);
        var lines = Lines(animGroups).ToArray();
        for (var i = 0; i < lines.Length; i++)
        {
            var header = lines[i].Split(',', StringSplitOptions.TrimEntries);
            if (header.Length != 4 || !int.TryParse(header[3], out var count) || count < 0 || count > 10_000)
                throw new InvalidDataException($"Invalid animation group header: {lines[i]}");
            var clips = new List<string>();
            while (++i < lines.Length && !lines[i].Equals("end", StringComparison.OrdinalIgnoreCase)) clips.Add(lines[i]);
            if (i == lines.Length || clips.Count != count) throw new InvalidDataException($"Animation group '{header[0]}' has an incorrect clip count or no end marker.");
            if (header[2].Equals("walkcycle", StringComparison.OrdinalIgnoreCase))
                groups[header[0]] = new PedAnimationGroup(header[0], header[1], header[2], clips.ToArray());
        }
        return new PedMetadata(definitions, groups);
    }

    /// <summary>Resolves source locomotion metadata, with an explicit heavy-gait preset for Big Smoke.</summary>
    public PedestrianPreset? GetPreset(string modelId)
    {
        var name = modelId.Replace('\\', '/').Split('/')[^1].Split('.')[0];
        if (name.StartsWith("SK_", StringComparison.OrdinalIgnoreCase)) name = name[3..];
        string group;
        if (name.Equals("smoke", StringComparison.OrdinalIgnoreCase) || name.Equals("smokev", StringComparison.OrdinalIgnoreCase)) group = "fatman";
        else if (Definitions.TryGetValue(name, out var definition)) group = definition.Group;
        else return null;
        if (!WalkGroups.TryGetValue(group, out var animationGroup) || animationGroup.Clips.Length < 4) return null;
        return new PedestrianPreset(name.Equals("smoke", StringComparison.OrdinalIgnoreCase) ? "Big Smoke" : name,
            $"ifp:{animationGroup.Bank.ToLowerInvariant()}", animationGroup.Clips[3], animationGroup.Clips[0], animationGroup.Clips[1], group);
    }

    private static IEnumerable<string> Lines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var comment = raw.IndexOf('#');
            var line = (comment < 0 ? raw : raw[..comment]).Trim();
            if (line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal)) yield return line;
        }
    }
}
