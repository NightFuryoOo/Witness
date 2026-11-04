using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;

namespace TheWitness;

internal enum FileCheckStatus
{
    Match,
    Mismatch,
    FileNotFound,
    ReadError
}

internal sealed record FileCheckResult(
    DateTimeOffset TimestampUtc,
    string RelativePath,
    FileCheckStatus Status,
    string? ObservedSha256,
    IReadOnlyList<string> ExpectedSha256s);

internal sealed class GameFileIntegrityMonitor
{
    private readonly GameLayout layout;

    internal GameFileIntegrityMonitor(GameLayout layout)
    {
        this.layout = layout;
    }

    internal IReadOnlyList<FileCheckResult> CheckAll(Action<int, int>? onProgress = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ConcurrentBag<FileCheckResult> results = new();
        int totalFiles = GameFileManifest.Files.Count;
        int completedFiles = 0;

        int degreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8);
        Parallel.ForEach(
            GameFileManifest.Files,
            new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism },
            entry =>
            {
                results.Add(CheckOne(entry.Key, entry.Value, now));
                onProgress?.Invoke(Interlocked.Increment(ref completedFiles), totalFiles);
            });

        return results.ToList();
    }

    internal FileCheckResult CheckOne(string relativePath)
    {
        if (!GameFileManifest.Files.TryGetValue(relativePath, out IReadOnlyList<string>? acceptedHashes))
        {
            throw new ArgumentException($"'{relativePath}' is not in the manifest.", nameof(relativePath));
        }

        return CheckOne(relativePath, acceptedHashes, DateTimeOffset.UtcNow);
    }

    private FileCheckResult CheckOne(string relativePath, IReadOnlyList<string> acceptedHashes, DateTimeOffset now)
    {
        string fullPath = layout.ResolveManifestPath(relativePath);
        bool mayBeAbsent = acceptedHashes.Contains(GameFileManifest.Absent, StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(fullPath))
        {
            return new FileCheckResult(
                now, relativePath,
                mayBeAbsent ? FileCheckStatus.Match : FileCheckStatus.FileNotFound,
                null, acceptedHashes);
        }

        try
        {
            string actualHash = HashFile(fullPath);
            FileCheckStatus status = acceptedHashes.Any(h => string.Equals(h, actualHash, StringComparison.OrdinalIgnoreCase))
                ? FileCheckStatus.Match
                : FileCheckStatus.Mismatch;
            return new FileCheckResult(now, relativePath, status, actualHash, acceptedHashes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileCheckResult(now, relativePath, FileCheckStatus.ReadError, null, acceptedHashes);
        }
    }

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
