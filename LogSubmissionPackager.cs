using System.Diagnostics;

namespace TheWitness;

internal static class LogSubmissionPackager
{
    internal static void CreatePackage(
        ObservedLogFile log,
        GameFileIntegrityMonitor integrityMonitor,
        string sessionId,
        Process gameProcess,
        int candidateGameProcessCount,
        DateTimeOffset sessionStartUtc,
        IReadOnlyList<GameFolderEvent> folderEventsSoFar,
        IReadOnlyList<UnexpectedModuleResult> unexpectedModulesSoFar,
        IReadOnlyList<ExternalHandleResult> externalHandlesSoFar)
    {
        string? runDirectory = Path.GetDirectoryName(log.FullPath);
        if (runDirectory == null)
        {
            return;
        }

        IReadOnlyList<FileCheckResult> freshBaseline = integrityMonitor.CheckAll();

        WitnessReport report = new(sessionId, sessionStartUtc);
        report.SetHardwareInfo(HardwareInfo.Capture());
        WitnessMonitor.RecordGameProcessInfo(report, gameProcess, candidateGameProcessCount);
        report.AddBaselineChecks(freshBaseline);
        report.AddFolderEvents(folderEventsSoFar);
        report.AddUnexpectedModules(unexpectedModulesSoFar);
        report.AddExternalHandles(externalHandlesSoFar);
        report.AddObservedLogFiles(new[] { log });

        string logFileNameNoExt = Path.GetFileNameWithoutExtension(log.FullPath);
        report.SaveEncrypted(runDirectory, DateTimeOffset.UtcNow, fileNameOverride: $"{logFileNameNoExt}.witness");
    }
}
