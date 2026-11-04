using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;

namespace TheWitness;

internal sealed record ObservedLogFile(
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset FinalizedUtc,
    string RelativePath,
    string FullPath,
    string Sha256,
    int ObservedChangeCount,
    TimeSpan ObservedWriteSpan);

internal sealed class LogFileMonitor : IDisposable
{
    private const long DeleteToCreateCorrelationWindowMs = 3000;

    private const long ReconcileIntervalMs = 30_000;

    private readonly string watchRoot;
    private readonly FileSystemWatcher watcher;
    private readonly FileSystemWatcher folderCreationWatcher;

    private readonly ConcurrentDictionary<string, long> observedFolderCreatedTicks = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, FileLifecycle> pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<ObservedLogFile> finalizedFiles = new();
    private readonly ConcurrentDictionary<string, byte> finalizedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object recentDeleteSync = new();
    private (FileLifecycle Lifecycle, long DeletedAtTicks)? mostRecentlyDeleted;
    private int watcherBufferOverflowCount;
    private readonly HashSet<string> preExistingPaths;
    private long lastReconcileTicks = long.MinValue;

    internal LogFileMonitor(string replayLoggerFolder)
    {
        watchRoot = replayLoggerFolder;

        Directory.CreateDirectory(replayLoggerFolder);

        preExistingPaths = new HashSet<string>(
            SafeEnumerateLogFiles(replayLoggerFolder),
            StringComparer.OrdinalIgnoreCase);

        watcher = new FileSystemWatcher(replayLoggerFolder)
        {
            IncludeSubdirectories = true,
            Filter = "*.log",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Created += (_, e) => TrackCreated(e.FullPath);
        watcher.Changed += (_, e) => TrackCreatedOrTouched(e.FullPath, isChange: true);
        watcher.Deleted += (_, e) => TrackDeleted(e.FullPath);
        watcher.Renamed += (_, e) => TrackRenamed(e.OldFullPath, e.FullPath);
        watcher.Error += (_, _) => Interlocked.Increment(ref watcherBufferOverflowCount);
        watcher.EnableRaisingEvents = true;

        folderCreationWatcher = new FileSystemWatcher(replayLoggerFolder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.DirectoryName
        };
        folderCreationWatcher.Created += (_, e) => observedFolderCreatedTicks.TryAdd(e.FullPath, Environment.TickCount64);
        folderCreationWatcher.Error += (_, _) => Interlocked.Increment(ref watcherBufferOverflowCount);
        folderCreationWatcher.EnableRaisingEvents = true;
    }

    internal IReadOnlyList<ObservedLogFile> FinalizedFiles => finalizedFiles.ToList();

    internal int PendingCount => pendingFiles.Count;

    internal int WatcherBufferOverflowCount => watcherBufferOverflowCount;

    internal IReadOnlyList<ObservedLogFile> PollPendingFiles()
    {
        long nowTicks = Environment.TickCount64;
        if (nowTicks - lastReconcileTicks >= ReconcileIntervalMs)
        {
            ReconcileMissedFiles();
            lastReconcileTicks = nowTicks;
        }

        if (pendingFiles.IsEmpty)
        {
            return Array.Empty<ObservedLogFile>();
        }

        List<ObservedLogFile> newlyFinalized = new();

        foreach (string path in pendingFiles.Keys.ToArray())
        {
            if (!File.Exists(path))
            {
                pendingFiles.TryRemove(path, out _);
                continue;
            }

            if (!TryHashIfUnlocked(path, out string? hash))
            {
                continue;
            }

            pendingFiles.TryRemove(path, out FileLifecycle? lifecycle);
            string relativePath = Path.GetRelativePath(watchRoot, path);
            DateTimeOffset firstSeen = lifecycle?.FirstSeenUtc ?? DateTimeOffset.UtcNow;
            int changeCount = lifecycle?.ChangeCount ?? 0;
            TimeSpan writeSpan = lifecycle?.ObservedWriteSpan ?? TimeSpan.Zero;

            ObservedLogFile observed = new(firstSeen, DateTimeOffset.UtcNow, relativePath, path, hash!, changeCount, writeSpan);
            finalizedFiles.Add(observed);
            finalizedPaths.TryAdd(path, 0);
            newlyFinalized.Add(observed);
        }

        return newlyFinalized;
    }

    private void TrackCreated(string fullPath)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long nowTicks = Environment.TickCount64;
        FileLifecycle? inherited = null;

        lock (recentDeleteSync)
        {
            if (mostRecentlyDeleted is { } recent &&
                nowTicks - recent.DeletedAtTicks <= DeleteToCreateCorrelationWindowMs &&
                IsPlausibleContinuation(recent.Lifecycle, fullPath))
            {
                inherited = recent.Lifecycle;
            }

            mostRecentlyDeleted = null;
        }

        if (inherited != null)
        {
            inherited.RecordChange();
            pendingFiles[fullPath] = inherited;
        }
        else
        {
            FileLifecycle fresh = new(now);
            fresh.UpdateLength(TryGetLength(fullPath));
            pendingFiles.TryAdd(fullPath, fresh);
        }
    }

    private void TrackCreatedOrTouched(string fullPath, bool isChange)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long observedLength = TryGetLength(fullPath);
        pendingFiles.AddOrUpdate(
            fullPath,
            _ =>
            {
                FileLifecycle lifecycle = new(now);
                lifecycle.UpdateLength(observedLength);
                return lifecycle;
            },
            (_, existing) =>
            {
                if (isChange)
                {
                    existing.RecordChange();
                }

                existing.UpdateLength(observedLength);
                return existing;
            });
    }

    private bool IsPlausibleContinuation(FileLifecycle deletedLifecycle, string newFullPath)
    {
        if (deletedLifecycle.LastKnownLength < 0)
        {
            return false;
        }

        long newLength = TryGetLength(newFullPath);
        if (newLength < 0 || newLength != deletedLifecycle.LastKnownLength)
        {
            return false;
        }

        string? parentDirectory = Path.GetDirectoryName(newFullPath);
        if (string.IsNullOrEmpty(parentDirectory))
        {
            return false;
        }

        if (!observedFolderCreatedTicks.TryGetValue(parentDirectory, out long observedCreatedTicks))
        {
            return false;
        }

        return Environment.TickCount64 - observedCreatedTicks <= DeleteToCreateCorrelationWindowMs;
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private void ReconcileMissedFiles()
    {
        foreach (string path in SafeEnumerateLogFiles(watchRoot))
        {
            if (preExistingPaths.Contains(path))
            {
                continue;
            }

            if (pendingFiles.ContainsKey(path))
            {
                continue;
            }

            if (finalizedPaths.ContainsKey(path))
            {
                continue;
            }

            FileLifecycle lifecycle = new(DateTimeOffset.UtcNow);
            lifecycle.UpdateLength(TryGetLength(path));
            pendingFiles.TryAdd(path, lifecycle);
        }
    }

    private static IEnumerable<string> SafeEnumerateLogFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private void TrackDeleted(string fullPath)
    {
        if (!pendingFiles.TryRemove(fullPath, out FileLifecycle? lifecycle))
        {
            return;
        }

        lifecycle.RecordChange();
        lock (recentDeleteSync)
        {
            mostRecentlyDeleted = (lifecycle, Environment.TickCount64);
        }
    }

    private void TrackRenamed(string oldFullPath, string newFullPath)
    {
        if (pendingFiles.TryRemove(oldFullPath, out FileLifecycle? lifecycle))
        {
            lifecycle.RecordChange();
            pendingFiles[newFullPath] = lifecycle;
        }
        else
        {
            pendingFiles.TryAdd(newFullPath, new FileLifecycle(DateTimeOffset.UtcNow));
        }
    }

    private static bool TryHashIfUnlocked(string path, out string? sha256)
    {
        sha256 = null;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
            sha256 = Convert.ToHexString(SHA256.HashData(stream));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        watcher.Dispose();
        folderCreationWatcher.Dispose();
    }

    private sealed class FileLifecycle
    {
        private readonly long firstSeenTicks;
        private long lastActivityTicks;

        internal FileLifecycle(DateTimeOffset firstSeenUtc)
        {
            FirstSeenUtc = firstSeenUtc;
            firstSeenTicks = Environment.TickCount64;
            lastActivityTicks = firstSeenTicks;
            LastKnownLength = -1;
        }

        internal DateTimeOffset FirstSeenUtc { get; }

        internal int ChangeCount { get; private set; }

        internal long LastKnownLength { get; private set; }

        internal TimeSpan ObservedWriteSpan => TimeSpan.FromMilliseconds(lastActivityTicks - firstSeenTicks);

        internal void RecordChange()
        {
            ChangeCount++;
            lastActivityTicks = Environment.TickCount64;
        }

        internal void UpdateLength(long length)
        {
            if (length >= 0)
            {
                LastKnownLength = length;
            }
        }
    }
}
