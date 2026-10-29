using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Sade.Mount;

/// <summary>Persistent identities for generated prefab objects, components and their references.</summary>
internal static class StablePrefabIds
{
    internal static void Apply(JsonObject root, string resourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        var seed = "sade-prefab:v1\n" + resourcePath.Replace('\\', '/').Trim().ToLowerInvariant() + "\n";
        var replacements = new Dictionary<Guid, Guid>();
        Collect(root, "root");
        Rewrite(root);

        void AddIdentity(JsonObject node, string key)
        {
            if (!TryGuid(node["__guid"], out var old) || old == Guid.Empty)
                throw new InvalidDataException($"{resourcePath}: prefab node {key} has no valid identity.");
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed + key));
            var stable = new Guid(hash.AsSpan(0, 16));
            if (!replacements.TryAdd(old, stable))
                throw new InvalidDataException($"{resourcePath}: duplicate prefab identity {old}.");
        }

        void Collect(JsonObject gameObject, string key)
        {
            AddIdentity(gameObject, key);
            if (gameObject["Components"] is JsonArray components)
            {
                var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var component in components.OfType<JsonObject>())
                {
                    var type = component["__type"]?.GetValue<string>() ?? throw new InvalidDataException("Prefab component has no type.");
                    var number = occurrences.GetValueOrDefault(type);
                    occurrences[type] = number + 1;
                    AddIdentity(component, key + "/component/" + Part(type) + "/" + number);
                }
            }
            if (gameObject["Children"] is not JsonArray children) return;
            var siblings = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in children.OfType<JsonObject>())
            {
                var name = child["Name"]?.GetValue<string>() ?? "";
                var tags = child["Tags"]?.GetValue<string>() ?? "";
                tags = string.Join(',', tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
                var identity = Part(name) + "/" + Part(tags);
                var number = siblings.GetValueOrDefault(identity);
                siblings[identity] = number + 1;
                Collect(child, key + "/child/" + identity + "/" + number);
            }
        }

        JsonNode? Rewrite(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var property in obj.ToArray())
                {
                    var value = Rewrite(property.Value);
                    if (!ReferenceEquals(value, property.Value)) obj[property.Key] = value;
                }
            }
            else if (node is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    var value = Rewrite(array[i]);
                    if (!ReferenceEquals(value, array[i])) array[i] = value;
                }
            }
            else if (TryGuid(node, out var old) && replacements.TryGetValue(old, out var stable))
                return JsonValue.Create(stable);
            return node;
        }
    }

    private static string Part(string value) => value.Length + ":" + value;

    private static bool TryGuid(JsonNode? node, out Guid guid)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<Guid>(out guid)) return true;
            if (value.TryGetValue<string>(out var text) && Guid.TryParse(text, out guid)) return true;
        }
        guid = default;
        return false;
    }
}
