using System.IO.Compression;
using System.Security.Cryptography;

namespace TheWitness.Launcher;

internal static class UpdateInstaller
{
    internal static async Task ApplyAsync(LatestReleaseInfo expected, IProgress<double> downloadProgress, CancellationToken cancellationToken)
    {
        string tempZipPath = Path.Combine(Path.GetTempPath(), $"TheWitness-update-{Guid.NewGuid():N}.zip");
        string tempExtractDir = Path.Combine(Path.GetTempPath(), $"TheWitness-update-{Guid.NewGuid():N}");

        try
        {
            await DownloadAsync(ReleaseSource.PackageUrl, tempZipPath, downloadProgress, cancellationToken);

            Directory.CreateDirectory(tempExtractDir);
            ZipFile.ExtractToDirectory(tempZipPath, tempExtractDir, overwriteFiles: true);

            string packageRoot = PackageRoot(tempExtractDir);
            string extractedDllPath = Path.Combine(packageRoot, "TheWitness.dll");
            string actualHash = await ComputeSha256Async(extractedDllPath, cancellationToken);
            if (!string.Equals(actualHash, expected.Sha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Hash mismatch: {actualHash} != {expected.Sha256Hex}");
            }

            InstallExtracted(packageRoot, InstalledWitness.InstallDirectory);
        }
        finally
        {
            TryDelete(tempZipPath);
            TryDeleteDirectory(tempExtractDir);
        }
    }

    private static async Task DownloadAsync(string url, string destinationPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TheWitnessLauncher/1.0");

        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream destination = File.Create(destinationPath);

        byte[] buffer = new byte[81920];
        long readSoFar = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            readSoFar += bytesRead;
            if (totalBytes is > 0)
            {
                progress.Report(Math.Min(100.0, readSoFar * 100.0 / totalBytes.Value));
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("TheWitness.dll not found in the package.", path);
        }

        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    internal static string PackageRoot(string extractedDir) => Path.Combine(extractedDir, InstalledWitness.FolderName);

    internal static void InstallExtracted(string packageRoot, string installDir)
    {
        Directory.CreateDirectory(installDir);

        foreach (string fileName in InstalledWitness.WitnessFiles)
        {
            string sourcePath = Path.Combine(packageRoot, fileName);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            File.Copy(sourcePath, Path.Combine(installDir, fileName), overwrite: true);
        }

        string sourceRuntimesDir = Path.Combine(packageRoot, InstalledWitness.RuntimesFolderName);
        if (Directory.Exists(sourceRuntimesDir))
        {
            CopyDirectoryRecursive(sourceRuntimesDir, Path.Combine(installDir, InstalledWitness.RuntimesFolderName));
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string filePath in Directory.GetFiles(sourceDir))
        {
            File.Copy(filePath, Path.Combine(destinationDir, Path.GetFileName(filePath)), overwrite: true);
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
