using System.Security.Cryptography;
using System.Text.Json;

string gameDir = @"C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight";
string? manifestPath = null;
string? outPath = null;
bool write = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--game": gameDir = args[++i]; break;
        case "--manifest": manifestPath = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--write": write = true; break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}

manifestPath ??= FindManifest();
outPath ??= manifestPath;

if (!File.Exists(manifestPath))
{
    Console.Error.WriteLine($"Manifest not found: {manifestPath}");
    return 1;
}

string dataDir = Path.Combine(gameDir, "hollow_knight_Data");
if (!Directory.Exists(dataDir))
{
    Console.Error.WriteLine($"Game data folder not found: {dataDir}");
    return 1;
}

using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
List<string> acceptedBuilds = doc.RootElement.TryGetProperty("acceptedBuilds", out JsonElement buildsElement)
    ? buildsElement.EnumerateArray().Select(e => e.GetString()!).ToList()
    : [];
List<(string Path, List<string> Hashes)> entries = doc.RootElement
    .GetProperty("files")
    .EnumerateObject()
    .Select(p => (p.Name, ReadHashes(p.Value)))
    .ToList();

List<(string Path, string Added)> added = [];
List<string> missing = [];
List<(string Path, List<string> Hashes)> result = new(entries.Count);

foreach ((string relPath, List<string> acceptedHashes) in entries)
{
    string full = Path.GetFullPath(Path.Combine(dataDir, relPath));
    if (!File.Exists(full))
    {
        if (!acceptedHashes.Contains("ABSENT", StringComparer.OrdinalIgnoreCase))
        {
            missing.Add(relPath);
        }

        result.Add((relPath, acceptedHashes));
        continue;
    }

    string liveHash = HashFile(full);
    if (acceptedHashes.Contains(liveHash, StringComparer.OrdinalIgnoreCase))
    {
        result.Add((relPath, acceptedHashes));
        continue;
    }

    added.Add((relPath, liveHash));
    result.Add((relPath, [.. acceptedHashes, liveHash]));
}

Console.WriteLine($"Manifest: {manifestPath}");
Console.WriteLine($"Install:  {dataDir}");
Console.WriteLine($"Files:    {result.Count}  |  new hashes: {added.Count}  |  unexpectedly missing: {missing.Count}");
Console.WriteLine();

foreach ((string path, string hash) in added)
{
    Console.WriteLine($"  NEW HASH  {path}");
    Console.WriteLine($"            + {hash}");
}

foreach (string path in missing)
{
    Console.WriteLine($"  MISSING   {path}");
}

if (!write)
{
    Console.WriteLine();
    Console.WriteLine(added.Count == 0 && missing.Count == 0
        ? "Manifest is already current."
        : "Dry run. Use --write to save.");
    return 0;
}

WriteManifest(outPath, acceptedBuilds, result);
Console.WriteLine();
Console.WriteLine($"Wrote {outPath}");
return 0;

static List<string> ReadHashes(JsonElement value) => value.ValueKind switch
{
    JsonValueKind.String => [value.GetString()!],
    JsonValueKind.Array => value.EnumerateArray().Select(e => e.GetString()!).ToList(),
    _ => throw new InvalidOperationException($"Unexpected value kind: {value.ValueKind}")
};

static string HashFile(string path)
{
    using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static void WriteManifest(string path, List<string> acceptedBuilds, List<(string Path, List<string> Hashes)> files)
{
    const string newline = "\r\n";
    System.Text.StringBuilder sb = new();
    sb.Append('{').Append(newline);
    sb.Append("  \"generatedUtc\": ").Append(JsonSerializer.Serialize(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))).Append(',').Append(newline);
    if (acceptedBuilds.Count > 0)
    {
        sb.Append("  \"acceptedBuilds\": [")
            .Append(string.Join(", ", acceptedBuilds.Select(s => JsonSerializer.Serialize(s))))
            .Append("],").Append(newline);
    }

    sb.Append("  \"fileCount\": ").Append(files.Count).Append(',').Append(newline);
    sb.Append("  \"files\": {").Append(newline);
    for (int i = 0; i < files.Count; i++)
    {
        (string relPath, List<string> hashes) = files[i];
        string hashArray = "[" + string.Join(", ", hashes.Select(s => JsonSerializer.Serialize(s))) + "]";
        sb.Append("    ").Append(JsonSerializer.Serialize(relPath)).Append(": ").Append(hashArray);
        sb.Append(i == files.Count - 1 ? newline : "," + newline);
    }

    sb.Append("  }").Append(newline);
    sb.Append('}').Append(newline);
    File.WriteAllText(path, sb.ToString());
}

static string FindManifest()
{
    DirectoryInfo? dir = new(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GameFileManifest.json")))
    {
        dir = dir.Parent;
    }

    return dir is null
        ? "GameFileManifest.json"
        : Path.Combine(dir.FullName, "GameFileManifest.json");
}
