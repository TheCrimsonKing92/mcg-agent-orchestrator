namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class StorageRetentionMaintenance
{
    private static void SweepOperatorLogs(
        string logDirectory, DateTimeOffset now, StorageRetentionReclaimOptions options,
        List<EvidenceRetentionDecision> decisions)
    {
        if (options.OperatorLogMaxAge is null || !Directory.Exists(logDirectory)) return;
        IReadOnlyCollection<string> protectedPaths;
        try { protectedPaths = options.ProtectedOperatorLogPaths(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DispatchLogs,
                EvidenceRetentionAction.Failed, logDirectory, null, EvidenceOwnerResolution.Unrecorded,
                "operator-log-protection-unavailable", FailureExceptionType: ex.GetType().Name));
            return;
        }
        var protectedSet = new HashSet<string>(
            protectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        string[] candidates;
        try { candidates = Directory.EnumerateFiles(logDirectory, "operator-*.log", SearchOption.TopDirectoryOnly).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DispatchLogs,
                EvidenceRetentionAction.DeferredLocked, logDirectory, null, EvidenceOwnerResolution.Unrecorded,
                "operator-log-enumeration-failed", FailureExceptionType: ex.GetType().Name));
            return;
        }
        foreach (var path in candidates)
        {
            var fileName = Path.GetFileName(path);
            if (!fileName.StartsWith("operator-", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("conduct-events", StringComparison.OrdinalIgnoreCase)) continue;
            if (protectedSet.Contains(Path.GetFullPath(path)))
            {
                decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DispatchLogs,
                    EvidenceRetentionAction.Preserved, path, null, EvidenceOwnerResolution.Unrecorded,
                    "running-conductor-log"));
                continue;
            }
            try
            {
                if (now - File.GetLastWriteTimeUtc(path) <= MaxGrace(options.OperatorLogMaxAge.Value))
                {
                    decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DispatchLogs,
                        EvidenceRetentionAction.Preserved, path, null, EvidenceOwnerResolution.Unrecorded,
                        "within-grace-period"));
                    continue;
                }
                var length = SafeLength(path);
                var deletion = TryDeleteExclusive(path);
                decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DispatchLogs,
                    deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
                    path, null, EvidenceOwnerResolution.Unrecorded,
                    deletion.Success ? "operator-log-past-age" : "exclusive-delete-failed",
                    BytesAttempted: length, BytesReclaimed: deletion.Success ? length : 0,
                    FailureExceptionType: deletion.ExceptionType));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DispatchLogs,
                    EvidenceRetentionAction.DeferredLocked, path, null, EvidenceOwnerResolution.Unrecorded,
                    "operator-log-inspection-failed", FailureExceptionType: ex.GetType().Name));
            }
        }
    }
}
