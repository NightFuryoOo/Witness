using System.Diagnostics;

namespace TheWitness;

internal sealed record UnexpectedModuleResult(
    DateTimeOffset TimestampUtc,
    string ModuleName,
    string ModulePath);

internal static class ModuleInjectionMonitor
{
    private static readonly string[] TrustedSteamModuleNames =
    {
        "gameoverlayrenderer64.dll",
        "gameoverlayrenderer.dll",
        "steamclient64.dll",
        "steamclient.dll",
        "tier0_s64.dll",
        "tier0_s.dll",
        "vstdlib_s64.dll",
        "vstdlib_s.dll"
    };

    internal static IReadOnlyList<UnexpectedModuleResult> CheckOnce(Process gameProcess, GameLayout layout)
    {
        List<UnexpectedModuleResult> results = new();
        if (gameProcess.HasExited)
        {
            return results;
        }

        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string? steamRoot = TryGetSteamRoot();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        try
        {
            gameProcess.Refresh();
            foreach (ProcessModule module in gameProcess.Modules)
            {
                using (module)
                {
                    string? modulePath = module.FileName;
                    if (string.IsNullOrEmpty(modulePath))
                    {
                        continue;
                    }

                    if (IsUnderDirectory(modulePath, windowsDirectory) ||
                        IsKnownGameFile(modulePath, layout) ||
                        IsTrustedSteamModule(module.ModuleName, modulePath, steamRoot) ||
                        IsUnderWindowsDefender(modulePath) ||
                        AuthenticodeVerifier.IsSignedByTrustedPublisher(modulePath))
                    {
                        continue;
                    }

                    results.Add(new UnexpectedModuleResult(now, module.ModuleName, modulePath));
                }
            }
        }
        catch (Exception)
        {
        }

        return results;
    }

    private static bool IsKnownGameFile(string modulePath, GameLayout layout)
    {
        string relativePath = Path.GetRelativePath(layout.DataDirectory, modulePath).Replace('\\', '/');
        return GameFileManifest.Files.ContainsKey(layout.NormalizeEventKey(relativePath));
    }

    private static bool IsUnderWindowsDefender(string modulePath)
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return IsUnderDirectory(modulePath, Path.Combine(programData, "Microsoft", "Windows Defender")) ||
               IsUnderDirectory(modulePath, Path.Combine(programFiles, "Windows Defender")) ||
               (programFilesX86.Length > 0 &&
                IsUnderDirectory(modulePath, Path.Combine(programFilesX86, "Windows Defender")));
    }

    private static bool IsTrustedSteamModule(string moduleName, string modulePath, string? steamRoot)
    {
        if (steamRoot == null || !IsUnderDirectory(modulePath, steamRoot))
        {
            return false;
        }

        foreach (string trustedName in TrustedSteamModuleNames)
        {
            if (string.Equals(moduleName, trustedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryGetSteamRoot()
    {
        Process[] steamProcesses = Process.GetProcessesByName("steam");
        try
        {
            foreach (Process steamProcess in steamProcesses)
            {
                try
                {
                    string? steamExePath = steamProcess.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(steamExePath))
                    {
                        return Path.GetDirectoryName(steamExePath);
                    }
                }
                catch (Exception)
                {
                }
            }

            return null;
        }
        finally
        {
            foreach (Process steamProcess in steamProcesses)
            {
                steamProcess.Dispose();
            }
        }
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        string normalizedDirectory = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
