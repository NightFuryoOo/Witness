using System.Security.Cryptography;

namespace TheWitness.Launcher;

internal static class InstalledWitness
{
    internal const string FolderName = "Witness";
    internal const string RuntimesFolderName = "runtimes";

    internal static readonly string[] WitnessFiles =
    [
        "TheWitness.exe",
        "TheWitness.dll",
        "TheWitness.deps.json",
        "TheWitness.runtimeconfig.json",
        "System.Management.dll"
    ];

    private static string launcherDirectory = AppContext.BaseDirectory;

    internal static void UseLauncherDirectory(string directory) => launcherDirectory = directory;

    internal static string InstallDirectory => Path.Combine(launcherDirectory, FolderName);

    internal static string ExePath => Path.Combine(InstallDirectory, "TheWitness.exe");

    internal static string DllPath => Path.Combine(InstallDirectory, "TheWitness.dll");

    internal static string? TryComputeInstalledSha256()
    {
        try
        {
            if (!File.Exists(DllPath))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(DllPath);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
