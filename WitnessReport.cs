using System.Text;

namespace TheWitness;

internal sealed class WitnessReport
{
    private readonly string sessionId;
    private readonly DateTimeOffset sessionStartUtc;
    private readonly List<FileCheckResult> baselineChecks = new();
    private int baselinePassCount;
    private readonly List<GameFolderEvent> folderEvents = new();
    private readonly List<ObservedLogFile> observedLogFiles = new();
    private readonly HashSet<string> recordedLogFilePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UnexpectedModuleResult> unexpectedModules = new();
    private readonly HashSet<string> recordedUnexpectedModulePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ExternalHandleResult> externalHandles = new();
    private readonly HashSet<(int OwnerPid, uint GrantedAccess)> recordedExternalHandles = new();
    private readonly List<string> notes = new();
    private string gameExecutablePath = "-";
    private int gameProcessId;
    private DateTimeOffset gameProcessStartUtc = DateTimeOffset.MinValue;
    private int candidateGameProcessCount = 1;
    private HardwareInfo? hardwareInfo;

    internal WitnessReport(string sessionId, DateTimeOffset sessionStartUtc)
    {
        this.sessionId = sessionId;
        this.sessionStartUtc = sessionStartUtc;
    }

    internal void SetGameProcessInfo(string executablePath, int processId, DateTimeOffset processStartUtc, int candidateCount)
    {
        gameExecutablePath = executablePath;
        gameProcessId = processId;
        gameProcessStartUtc = processStartUtc;
        candidateGameProcessCount = candidateCount;
    }

    internal void SetHardwareInfo(HardwareInfo info) => hardwareInfo = info;

    internal void AddBaselineChecks(IEnumerable<FileCheckResult> results)
    {
        baselineChecks.AddRange(results);
        baselinePassCount++;
    }

    internal void AddFolderEvents(IEnumerable<GameFolderEvent> events) => folderEvents.AddRange(events);

    internal void AddObservedLogFiles(IEnumerable<ObservedLogFile> files)
    {
        foreach (ObservedLogFile file in files)
        {
            if (recordedLogFilePaths.Add(file.RelativePath))
            {
                observedLogFiles.Add(file);
            }
        }
    }

    internal void AddUnexpectedModules(IEnumerable<UnexpectedModuleResult> results)
    {
        foreach (UnexpectedModuleResult result in results)
        {
            if (recordedUnexpectedModulePaths.Add(result.ModulePath))
            {
                unexpectedModules.Add(result);
            }
        }
    }

    internal void AddExternalHandles(IEnumerable<ExternalHandleResult> results)
    {
        foreach (ExternalHandleResult result in results)
        {
            if (recordedExternalHandles.Add((result.OwnerProcessId, result.GrantedAccess)))
            {
                externalHandles.Add(result);
            }
        }
    }

    internal void AddNote(string note) => notes.Add($"{DateTimeOffset.UtcNow:O}|{note}");

    internal IReadOnlyList<GameFolderEvent> FolderEvents => folderEvents;
    internal IReadOnlyList<UnexpectedModuleResult> UnexpectedModules => unexpectedModules;
    internal IReadOnlyList<ExternalHandleResult> ExternalHandles => externalHandles;

    internal bool HasAnyMismatch =>
        baselineChecks.Any(r => r.Status != FileCheckStatus.Match) ||
        folderEvents.Any(e => e.Kind != GameFolderEventKind.BenignFileActivity) ||
        externalHandles.Count > 0;

    internal string BuildPlainText(DateTimeOffset sessionEndUtc)
    {
        StringBuilder sb = new();
        sb.AppendLine("TheWitness-Report-v3");
        sb.AppendLine($"SessionId: {sessionId}");
        sb.AppendLine($"SessionStartUtc: {sessionStartUtc:O}");
        sb.AppendLine($"SessionEndUtc: {sessionEndUtc:O}");
        sb.AppendLine($"ManifestFileCount: {GameFileManifest.Files.Count}");
        sb.AppendLine($"WitnessBinarySha256: {WitnessBinaryInfo.ComputeSelfSha256() ?? "unavailable"}");
        sb.AppendLine(
            $"GameProcess: Path={gameExecutablePath}|Pid={gameProcessId}|StartedUtc={gameProcessStartUtc:O}" +
            $"|CandidatesFound={candidateGameProcessCount}");
        sb.AppendLine($"GameRunningBeforeSessionStart: {DescribePreSessionRuntime()}");
        sb.AppendLine();

        sb.AppendLine("---- BaselineChecks ----");
        int baselineMismatchCount = baselineChecks.Count(r => r.Status != FileCheckStatus.Match);
        sb.AppendLine($"Full passes: {baselinePassCount} | Files per pass: {GameFileManifest.Files.Count} | Mismatches: {baselineMismatchCount}");
        foreach (FileCheckResult r in baselineChecks.Where(r => r.Status != FileCheckStatus.Match))
        {
            sb.AppendLine($"{r.TimestampUtc:O}|{r.RelativePath}|{r.Status}|{r.ObservedSha256 ?? "-"}|expected={string.Join(",", r.ExpectedSha256s)}");
        }

        sb.AppendLine();
        sb.AppendLine("---- FolderEvents ----");
        foreach (GameFolderEvent e in folderEvents)
        {
            string detail = e.TrackedCheck is { } check
                ? $"{check.Status}|{check.ObservedSha256 ?? "-"}|expected={string.Join(",", check.ExpectedSha256s)}"
                : "-";
            sb.AppendLine($"{e.TimestampUtc:O}|{e.Kind}|{e.ChangeType}|{e.RelativePath}|{detail}");
        }

        sb.AppendLine();
        sb.AppendLine("---- ObservedLogFiles ----");
        foreach (ObservedLogFile f in observedLogFiles)
        {
            sb.AppendLine(
                $"FirstSeenUtc={f.FirstSeenUtc:O}|FinalizedUtc={f.FinalizedUtc:O}|Path={f.RelativePath}|Sha256={f.Sha256}" +
                $"|ObservedChangeCount={f.ObservedChangeCount}|ObservedWriteSpanMs={f.ObservedWriteSpan.TotalMilliseconds:F0}");
        }

        sb.AppendLine();
        sb.AppendLine("---- UnexpectedModules ----");
        foreach (UnexpectedModuleResult m in unexpectedModules)
        {
            sb.AppendLine($"{m.TimestampUtc:O}|{m.ModuleName}|{m.ModulePath}");
        }

        sb.AppendLine();
        sb.AppendLine("---- ExternalHandles ----");
        foreach (ExternalHandleResult h in externalHandles)
        {
            string relation = h.OwnerLaunchedGame ? "|relation=launcher" : string.Empty;
            string image = string.IsNullOrEmpty(h.OwnerImagePath) ? string.Empty : $"|image={h.OwnerImagePath}";
            string imageHash = string.IsNullOrEmpty(h.OwnerImageSha256) ? string.Empty : $"|imageSha256={h.OwnerImageSha256}";
            sb.AppendLine($"{h.TimestampUtc:O}|{h.OwnerProcessName}|pid={h.OwnerProcessId}|grantedAccess=0x{h.GrantedAccess:X8}{relation}{image}{imageHash}");
        }

        sb.AppendLine();
        sb.AppendLine("---- Notes ----");
        foreach (string note in notes)
        {
            sb.AppendLine(note);
        }

        sb.AppendLine();
        sb.AppendLine("---- HardwareHashes ----");
        if (hardwareInfo is { } hw)
        {
            sb.AppendLine($"HardwareHash-CPU: {hw.CpuHash}");
            sb.AppendLine($"HardwareHash-GPU: {hw.GpuHash}");
            sb.AppendLine($"HardwareHash-RAM: {hw.RamHash}");
            sb.AppendLine($"HardwareHash-Motherboard: {hw.MotherboardHash}");
            sb.AppendLine($"HardwareHash-All: {hw.AllHash}");
        }
        else
        {
            sb.AppendLine("(not captured)");
        }

        sb.AppendLine();
        sb.AppendLine($"Summary: {(HasAnyMismatch ? "MISMATCH DETECTED" : "all checks matched reference hashes")}");

        return sb.ToString();
    }

    private string DescribePreSessionRuntime()
    {
        if (gameProcessStartUtc == DateTimeOffset.MinValue)
        {
            return "unknown";
        }

        if (gameProcessStartUtc >= sessionStartUtc)
        {
            return "00:00:00";
        }

        return (sessionStartUtc - gameProcessStartUtc).ToString();
    }

    internal string SaveEncrypted(string outputDirectory, DateTimeOffset sessionEndUtc, string? fileNameOverride = null)
    {
        Directory.CreateDirectory(outputDirectory);
        string plainText = BuildPlainText(sessionEndUtc);
        string encrypted = WitnessReportEncryption.Encrypt(plainText);

        string fileName = fileNameOverride ?? $"Witness_{sessionStartUtc.ToLocalTime():yyyy-MM-dd_HH-mm-ss}.witness";
        string fullPath = Path.Combine(outputDirectory, fileName);

        string tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, encrypted);
        File.Move(tempPath, fullPath, overwrite: true);
        return fullPath;
    }
}
