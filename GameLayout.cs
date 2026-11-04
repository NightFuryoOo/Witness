using System.Linq;

namespace TheWitness;

internal sealed class GameLayout
{
    private static readonly string[] DataFolderNames = { "hollow_knight_Data", "Hollow Knight_Data" };
    private static readonly string[] KnownExeNames = { "hollow_knight.exe", "Hollow Knight.exe" };

    internal static readonly string[] ProcessNames = { "hollow_knight", "Hollow Knight" };

    internal string GameDirectory { get; }
    internal string DataDirectory { get; }

    internal string ExeName { get; }

    private GameLayout(string gameDirectory, string dataDirectory, string exeName)
    {
        GameDirectory = gameDirectory;
        DataDirectory = dataDirectory;
        ExeName = exeName;
    }

    internal static GameLayout Resolve(string gameDirectory)
    {
        string data =
            DataFolderNames.Select(name => Path.Combine(gameDirectory, name)).FirstOrDefault(Directory.Exists)
            ?? Path.Combine(gameDirectory, DataFolderNames[0]);

        string exe =
            KnownExeNames.FirstOrDefault(name => File.Exists(Path.Combine(gameDirectory, name)))
            ?? KnownExeNames[0];

        return new GameLayout(gameDirectory, data, exe);
    }

    internal string ResolveManifestPath(string manifestKey)
    {
        string combined = Path.GetFullPath(Path.Combine(DataDirectory, manifestKey));

        if (!File.Exists(combined)
            && manifestKey.Replace('\\', '/').EndsWith("/hollow_knight.exe", StringComparison.OrdinalIgnoreCase))
        {
            string alternative = Path.Combine(Path.GetDirectoryName(combined) ?? GameDirectory, ExeName);
            if (File.Exists(alternative))
            {
                return alternative;
            }
        }

        return combined;
    }

    internal string NormalizeEventKey(string relativeKey)
    {
        return string.Equals(relativeKey, "../" + ExeName, StringComparison.OrdinalIgnoreCase)
            ? "../hollow_knight.exe"
            : relativeKey;
    }
}
