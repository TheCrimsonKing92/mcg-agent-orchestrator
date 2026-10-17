namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceAdmissionPathSet
{
    // Full gates: median 17.1 min, p90 23.3 min, failed-then-rerun about 40 min.
    // Allow 1.5 times that rerun duration after admission; waiting admission gets 5 min.
    internal static readonly TimeSpan AdmissionTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(60);

    private static readonly string[] Prefixes =
    [
        "AcceptanceHolder", "LegacyAcceptanceHolder", "ConductorParallelAcceptanceAttempt",
        "ConductorGroupedGateAttempt", "ConductorLiveGroupedGateCount", "ConductorOrphanGateAttemptAdoption",
        "OwnedChildAttemptCoordinator", "ConductorBatchLoop.LiveAcceptanceAdmission",
        "ConductorBatchLoop.ParallelAcceptanceAdmission", "ConductorBatchLoop.FallbackAcceptance",
        "ConductorBatchLoop.FollowerGateAdmission", "AcceptanceJobCustodian", "DurableAcceptanceJob",
        "ConductorDriver.AcceptanceCohortExecution", "ConductorDriver.FollowerGates",
        "ConductorDriver.GroupedGateAttempts", "ConductorDriver.MergeTrains",
        "StorageRetentionMaintenance.AcceptanceHolders"
    ];

    internal static bool Touches(IEnumerable<string> changedPaths) => changedPaths.Any(path =>
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (!normalized.StartsWith("src/Mcg.AgentOrchestrator.App/Orchestration/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("src/Mcg.AgentOrchestrator.Execution/Processes/", StringComparison.OrdinalIgnoreCase))
            return false;
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        return Prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    });
}
