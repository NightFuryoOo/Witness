using System.Diagnostics;
using System.Linq;

namespace TheWitness;

internal static class WitnessMonitor
{
    private const int FolderEventDrainIntervalMs = 3000;
    private const int ProcessSearchRetryDelayMs = 2000;
    private const string ReplayLoggerRelativeFolder = "Managed/Mods/ReplayLogger";

    private const int ModuleInjectionCheckIntervalMs = 20_000;

    private const int ExternalHandleCheckIntervalMs = 30_000;

    internal static void Run()
    {
        while (!ShutdownSignal.IsRequested)
        {
            try
            {
                (Process? gameProcess, int candidateCount) = FindGameProcess();
                if (gameProcess == null)
                {
                    Thread.Sleep(ProcessSearchRetryDelayMs);
                    continue;
                }

                try
                {
                    RunSession(gameProcess, candidateCount);
                }
                finally
                {
                    gameProcess.Dispose();
                }
            }
            catch (Exception)
            {
                Thread.Sleep(ProcessSearchRetryDelayMs);
            }
        }

        ShutdownSignal.SignalFinished();
    }

    private static (Process? Process, int CandidateCount) FindGameProcess()
    {
        List<Process> live = new();
        foreach (string name in GameLayout.ProcessNames)
        {
            foreach (Process candidate in Process.GetProcessesByName(name))
            {
                if (IsLiveGameProcess(candidate))
                {
                    live.Add(candidate);
                }
                else
                {
                    candidate.Dispose();
                }
            }
        }

        if (live.Count == 0)
        {
            return (null, 0);
        }

        for (int i = 1; i < live.Count; i++)
        {
            live[i].Dispose();
        }

        return (live[0], live.Count);
    }

    private static bool IsLiveGameProcess(Process candidate)
    {
        try
        {
            if (candidate.HasExited)
            {
                return false;
            }

            candidate.Refresh();
            return candidate.Threads.Count > 0 && !string.IsNullOrEmpty(candidate.MainModule?.FileName);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void RunSession(Process gameProcess, int candidateGameProcessCount)
    {
        string? gameDirectory = TryGetGameDirectory(gameProcess);
        if (gameDirectory == null)
        {
            WaitForExitOrShutdown(gameProcess);
            NotifyGameSessionEnded();
            return;
        }

        GameLayout layout = GameLayout.Resolve(gameDirectory);
        string replayLoggerFolder = Path.Combine(layout.DataDirectory, ReplayLoggerRelativeFolder.Replace('/', Path.DirectorySeparatorChar));

        string sessionId = Guid.NewGuid().ToString("N");
        DateTimeOffset sessionStartUtc = DateTimeOffset.UtcNow;
        WitnessReport report = new(sessionId, sessionStartUtc);
        report.SetHardwareInfo(HardwareInfo.Capture());
        RecordGameProcessInfo(report, gameProcess, candidateGameProcessCount);

        try
        {
            RunSessionCore(gameProcess, layout, replayLoggerFolder, sessionId, report, candidateGameProcessCount, sessionStartUtc);
        }
        catch (Exception ex)
        {
            report.AddNote($"Monitoring stopped: {ex.GetType().Name}: {ex.Message}");
        }

        NotifyGameSessionEnded();
    }

    private static void NotifyGameSessionEnded()
    {
        if (!ShutdownSignal.IsRequested)
        {
            SessionIndicatorOverlay.OnGameExited();
        }
    }

    internal static void RecordGameProcessInfo(WitnessReport report, Process gameProcess, int candidateCount)
    {
        try
        {
            string? exePath = gameProcess.MainModule?.FileName;
            report.SetGameProcessInfo(exePath ?? "-", gameProcess.Id, gameProcess.StartTime.ToUniversalTime(), candidateCount);
        }
        catch (Exception)
        {
            report.SetGameProcessInfo("-", gameProcess.Id, DateTimeOffset.MinValue, candidateCount);
        }
    }

    private static void RunSessionCore(
        Process gameProcess,
        GameLayout layout,
        string replayLoggerFolder,
        string sessionId,
        WitnessReport report,
        int candidateGameProcessCount,
        DateTimeOffset sessionStartUtc)
    {
        GameFileIntegrityMonitor integrityMonitor = new(layout);

        BaselineProgressToast? progressToast = UiDispatcher.Invoke(BaselineProgressToast.Create);
        IReadOnlyList<FileCheckResult> startBaseline = integrityMonitor.CheckAll(
            (completedFiles, totalFiles) => progressToast?.ReportProgress(completedFiles, totalFiles));
        progressToast?.CompleteAndClose();
        report.AddBaselineChecks(startBaseline);

        using GameFolderWatcher folderWatcher = new(layout, ReplayLoggerRelativeFolder);
        using LogFileMonitor logMonitor = new(replayLoggerFolder);

        long lastModuleInjectionCheck = 0;
        long lastExternalHandleCheck = 0;
        int lastReportedLogWatcherOverflowCount = 0;

        while (!ShutdownSignal.IsRequested && !gameProcess.HasExited)
        {
            IReadOnlyList<GameFolderEvent> folderEvents = folderWatcher.DrainPendingEvents();
            report.AddFolderEvents(folderEvents);
            if (folderEvents.Any(e => e.Kind == GameFolderEventKind.WatcherBufferOverflow))
            {
                report.AddBaselineChecks(integrityMonitor.CheckAll());
            }

            if (Environment.TickCount64 - lastModuleInjectionCheck >= ModuleInjectionCheckIntervalMs)
            {
                report.AddUnexpectedModules(ModuleInjectionMonitor.CheckOnce(gameProcess, layout));
                lastModuleInjectionCheck = Environment.TickCount64;
            }

            if (Environment.TickCount64 - lastExternalHandleCheck >= ExternalHandleCheckIntervalMs)
            {
                report.AddExternalHandles(ExternalHandleMonitor.CheckOnce(gameProcess));
                lastExternalHandleCheck = Environment.TickCount64;
            }

            foreach (ObservedLogFile newlyFinalized in logMonitor.PollPendingFiles())
            {
                LogSubmissionPackager.CreatePackage(
                    newlyFinalized, integrityMonitor, sessionId, gameProcess, candidateGameProcessCount, sessionStartUtc,
                    report.FolderEvents, report.UnexpectedModules, report.ExternalHandles);
            }

            int currentLogWatcherOverflowCount = logMonitor.WatcherBufferOverflowCount;
            if (currentLogWatcherOverflowCount != lastReportedLogWatcherOverflowCount)
            {
                report.AddNote($"Log watcher overflow: {currentLogWatcherOverflowCount}");
                lastReportedLogWatcherOverflowCount = currentLogWatcherOverflowCount;
            }

            Thread.Sleep(FolderEventDrainIntervalMs);
        }

        report.AddFolderEvents(folderWatcher.DrainPendingEvents());
        foreach (ObservedLogFile newlyFinalized in logMonitor.PollPendingFiles())
        {
            LogSubmissionPackager.CreatePackage(
                newlyFinalized, integrityMonitor, sessionId, gameProcess, candidateGameProcessCount, sessionStartUtc,
                report.FolderEvents, report.UnexpectedModules, report.ExternalHandles);
        }

        IReadOnlyList<FileCheckResult> endBaseline = integrityMonitor.CheckAll();
        report.AddBaselineChecks(endBaseline);
    }

    private static void WaitForExitOrShutdown(Process gameProcess)
    {
        while (!ShutdownSignal.IsRequested && !gameProcess.HasExited)
        {
            Thread.Sleep(ProcessSearchRetryDelayMs);
        }
    }

    private static string? TryGetGameDirectory(Process gameProcess)
    {
        try
        {
            string? exePath = gameProcess.MainModule?.FileName;
            return string.IsNullOrEmpty(exePath) ? null : Path.GetDirectoryName(exePath);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
