using System.Text.Json;

namespace Sade.Mount;

public sealed record SadeSettings
{
    public string? GameDirectory { get; init; }
    public string CacheDirectory { get; init; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sade-mount", "cache");
    public bool RegisterAnimationVariants { get; init; } = true;
    public float LodDistance { get; init; } = 2000;

    internal static SadeSettings Load()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(SadeSettings).Assembly.Location)!, "sade.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<SadeSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"Settings in {path} are empty.");
    }
}
