using System.Collections.Concurrent;

namespace TheWitness;

internal enum GameFolderEventKind
{
    TrackedFileChanged,

    UnlistedFileActivity,

    BenignFileActivity,

    WatcherBufferOverflow
}

internal sealed record GameFolderEvent(
    DateTimeOffset TimestampUtc,
    GameFolderEventKind Kind,
    string RelativePath,
    WatcherChangeTypes ChangeType,
    FileCheckResult? TrackedCheck);

internal sealed class GameFolderWatcher : IDisposable
{
    private static readonly HashSet<string> BenignUnlistedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "Config.ini",
        "boot.config"
    };

    private readonly GameLayout layout;
    private readonly string dataDirectory;
    private readonly string ignoredRelativeDirectory;
    private readonly string ignoredRelativeDirectoryNoSlash;
    private readonly GameFileIntegrityMonitor integrityMonitor;
    private readonly FileSystemWatcher watcher;
    private readonly FileSystemWatcher rootFilesWatcher;
    private readonly FileSystemWatcher? monoRuntimeWatcher;
    private readonly ConcurrentQueue<GameFolderEvent> pendingEvents = new();

    internal GameFolderWatcher(GameLayout layout, string ignoredRelativeDirectory)
    {
        this.layout = layout;
        dataDirectory = layout.DataDirectory;
        this.ignoredRelativeDirectory = NormalizeRelativeDirectory(ignoredRelativeDirectory);
        ignoredRelativeDirectoryNoSlash = this.ignoredRelativeDirectory.TrimEnd('/');
        integrityMonitor = new GameFileIntegrityMonitor(layout);

        watcher = new FileSystemWatcher(dataDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };
        watcher.Created += (_, e) => Enqueue(e.FullPath, e.ChangeType);
        watcher.Changed += (_, e) => Enqueue(e.FullPath, e.ChangeType);
        watcher.Deleted += (_, e) => Enqueue(e.FullPath, e.ChangeType);
        watcher.Renamed += (_, e) => Enqueue(e.FullPath, e.ChangeType);
        watcher.Error += (_, e) => EnqueueOverflow(e.GetException());
        watcher.EnableRaisingEvents = true;

        rootFilesWatcher = new FileSystemWatcher(layout.GameDirectory) { IncludeSubdirectories = false };
        rootFilesWatcher.Filters.Add(layout.ExeName);
        rootFilesWatcher.Filters.Add("UnityPlayer.dll");
        rootFilesWatcher.Filters.Add("Galaxy64.dll");
        rootFilesWatcher.Filters.Add("GalaxyCSharpGlue.dll");
        rootFilesWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
        rootFilesWatcher.Created += (_, e) => Enqueue(e.FullPath, e.ChangeType);
        rootFilesWatcher.Changed += (_, e) => Enqueue(e.FullPath, e.ChangeType);
        rootFilesWatcher.Deleted += (_, e) => Enqueue(e.FullPath, e.ChangeType);
        rootFilesWatcher.Renamed += (_, e) => Enqueue(e.FullPath, e.ChangeType);
        rootFilesWatcher.Error += (_, e) => EnqueueOverflow(e.GetException());
        rootFilesWatcher.EnableRaisingEvents = true;

        string monoRuntimeDirectory = Path.Combine(layout.GameDirectory, "MonoBleedingEdge", "EmbedRuntime");
        if (Directory.Exists(monoRuntimeDirectory))
        {
            monoRuntimeWatcher = new FileSystemWatcher(monoRuntimeDirectory) { IncludeSubdirectories = false };
            monoRuntimeWatcher.Filters.Add("mono-2.0-bdwgc.dll");
            monoRuntimeWatcher.Filters.Add("MonoPosixHelper.dll");
            monoRuntimeWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
            monoRuntimeWatcher.Created += (_, e) => Enqueue(e.FullPath, e.ChangeType);
            monoRuntimeWatcher.Changed += (_, e) => Enqueue(e.FullPath, e.ChangeType);
            monoRuntimeWatcher.Deleted += (_, e) => Enqueue(e.FullPath, e.ChangeType);
            monoRuntimeWatcher.Renamed += (_, e) => Enqueue(e.FullPath, e.ChangeType);
            monoRuntimeWatcher.Error += (_, e) => EnqueueOverflow(e.GetException());
            monoRuntimeWatcher.EnableRaisingEvents = true;
        }
    }

    internal IReadOnlyList<GameFolderEvent> DrainPendingEvents()
    {
        List<GameFolderEvent> drained = new();
        while (pendingEvents.TryDequeue(out GameFolderEvent? evt))
        {
            drained.Add(evt);
        }

        return drained;
    }

    private void Enqueue(string fullPath, WatcherChangeTypes changeType)
    {
        string relativePath = layout.NormalizeEventKey(
            Path.GetRelativePath(dataDirectory, fullPath).Replace('\\', '/'));

        if (relativePath.StartsWith(ignoredRelativeDirectory, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(relativePath, ignoredRelativeDirectoryNoSlash, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (GameFileManifest.Files.ContainsKey(relativePath))
        {
            FileCheckResult check = integrityMonitor.CheckOne(relativePath);
            pendingEvents.Enqueue(new GameFolderEvent(now, GameFolderEventKind.TrackedFileChanged, relativePath, changeType, check));
        }
        else
        {
            GameFolderEventKind kind = BenignUnlistedPaths.Contains(relativePath)
                ? GameFolderEventKind.BenignFileActivity
                : GameFolderEventKind.UnlistedFileActivity;
            pendingEvents.Enqueue(new GameFolderEvent(now, kind, relativePath, changeType, null));
        }
    }

    private void EnqueueOverflow(Exception error)
    {
        pendingEvents.Enqueue(new GameFolderEvent(
            DateTimeOffset.UtcNow,
            GameFolderEventKind.WatcherBufferOverflow,
            error.Message,
            WatcherChangeTypes.All,
            null));
    }

    private static string NormalizeRelativeDirectory(string relativeDirectory) =>
        relativeDirectory.Replace('\\', '/').TrimEnd('/') + "/";

    public void Dispose()
    {
        watcher.Dispose();
        rootFilesWatcher.Dispose();
        monoRuntimeWatcher?.Dispose();
    }
}
