using System.Globalization;
using Sade.Contracts;

namespace Sade.Formats;

public sealed record PedDefinition(int Id, string Model, string TextureDictionary, string Group)
{
    public string PedType { get; init; } = "CIVMALE";
}
public sealed record PedAnimationGroup(string Name, string Bank, string Type, string[] Clips);

public sealed class PedMetadata
{
    public IReadOnlyDictionary<string, PedDefinition> Definitions { get; }
    public IReadOnlyDictionary<string, PedAnimationGroup> WalkGroups { get; }
    public IReadOnlyDictionary<string, string[]> Hostilities { get; }
    private PedMetadata(Dictionary<string, PedDefinition> definitions, Dictionary<string, PedAnimationGroup> groups, Dictionary<string, string[]> hostilities)
    { Definitions = definitions; WalkGroups = groups; Hostilities = hostilities; }

    public static PedMetadata Parse(string pedsIde, string animGroups, string relationships = "")
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
            var definition = new PedDefinition(id, fields[1], fields[2], fields[5]) { PedType = fields[3].ToUpperInvariant() };
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
        var hostilities = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var line in Lines(relationships))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 1) { current = fields[0].ToUpperInvariant(); continue; }
            if (current is not null && fields[0].Equals("Hate", StringComparison.OrdinalIgnoreCase))
            {
                var enemies = fields.Skip(1).Where(f => !f.StartsWith("PLAYER", StringComparison.OrdinalIgnoreCase)).Select(FactionForPedType);
                hostilities[current] = hostilities.GetValueOrDefault(current, []).Concat(enemies).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        return new PedMetadata(definitions, groups, hostilities);
    }

    /// <summary>Resolves source locomotion metadata, with an explicit heavy-gait preset for Big Smoke.</summary>
    public PedestrianPreset? GetPreset(string modelId)
    {
        var name = modelId.Replace('\\', '/').Split('/')[^1].Split('.')[0];
        if (name.StartsWith("SK_", StringComparison.OrdinalIgnoreCase)) name = name[3..];
        if (name.Equals("Player_Combined", StringComparison.OrdinalIgnoreCase))
            return new PedestrianPreset("CJ", "ifp:ped", "IDLE_stance", "WALK_player", "run_player", "player")
            {
                Faction = "Grove", EnemyFactions = Hostilities.GetValueOrDefault("GANG2", [])
            };
        string group, pedType;
        if (NamedPresets.TryGetValue(name, out var named)) { group = named.Group; pedType = named.PedType; }
        else if (Definitions.TryGetValue(name, out var definition)) { group = definition.Group; pedType = definition.PedType; }
        else return null;
        if (!WalkGroups.TryGetValue(group, out var animationGroup) || animationGroup.Clips.Length < 4) return null;
        return new PedestrianPreset(name.Equals("smoke", StringComparison.OrdinalIgnoreCase) ? "Big Smoke" : name,
            $"ifp:{animationGroup.Bank.ToLowerInvariant()}", animationGroup.Clips[3], animationGroup.Clips[0], animationGroup.Clips[1], group)
        {
            Faction = FactionForPedType(pedType),
            EnemyFactions = Hostilities.GetValueOrDefault(pedType, [])
        };
    }

    // Gang numbers are the original ePedType values, independently represented from:
    // https://github.com/gta-reversed/gta-reversed/blob/e80afe41b7fed5c2f9f8bb2d87e0cc2466d5e1c1/source/game_sa/Enums/ePedType.h
    public static string FactionForPedType(string type) => type.ToUpperInvariant() switch
    {
        "GANG1" => "Ballas", "GANG2" => "Grove", "GANG3" => "Vagos", "GANG4" => "Rifa",
        "GANG5" => "DaNang", "GANG6" => "Mafia", "GANG7" => "Triads", "GANG8" => "Aztecas",
        "GANG9" => "Gang9", "GANG10" => "Gang10", "COP" => "Police", "CRIMINAL" => "Criminal",
        "DEALER" => "Dealer", "MEDIC" => "Medic", "FIREMAN" => "Firefighter", "PROSTITUTE" => "Prostitute",
        _ => "Civilian"
    };

    // Explicit early-story affiliation for named NPC presets; their special models are not in peds.ide.
    private static readonly Dictionary<string, (string Group, string PedType)> NamedPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["smoke"] = ("fatman", "GANG2"), ["smokev"] = ("fatman", "GANG2"), ["sweet"] = ("man", "GANG2"),
        ["ryder"] = ("gang1", "GANG2"), ["ryder2"] = ("gang1", "GANG2"), ["ryder3"] = ("gang1", "GANG2"),
        ["cesar"] = ("man", "GANG8"), ["wuzimu"] = ("blindman", "GANG7")
    };

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
