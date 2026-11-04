using System.Linq;
using System.Text.Json;

namespace TheWitness;

internal static class GameFileManifest
{
    internal const string Absent = "ABSENT";

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Files { get; } = Load();

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Load()
    {
        using Stream? stream = typeof(GameFileManifest).Assembly
            .GetManifestResourceStream("TheWitness.GameFileManifest.json");

        if (stream == null)
        {
            throw new InvalidOperationException("GameFileManifest.json not found.");
        }

        using JsonDocument doc = JsonDocument.Parse(stream);
        Dictionary<string, IReadOnlyList<string>> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty prop in doc.RootElement.GetProperty("files").EnumerateObject())
        {
            files[prop.Name] = prop.Value.ValueKind == JsonValueKind.Array
                ? prop.Value.EnumerateArray().Select(v => v.GetString()!).ToArray()
                : new[] { prop.Value.GetString()! };
        }

        return files;
    }
}
