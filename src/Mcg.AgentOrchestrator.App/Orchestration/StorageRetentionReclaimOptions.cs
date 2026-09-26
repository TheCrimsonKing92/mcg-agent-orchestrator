namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record StorageRetentionReclaimOptions(
    TimeSpan? OwnerlessAttemptMaxAge,
    TimeSpan? TerminalGoalAttemptMaxAge,
    TimeSpan? OperatorLogMaxAge,
    TimeSpan? TerminalJournalCompressionAge,
    Func<IReadOnlyCollection<string>> ProtectedOperatorLogPaths)
{
    internal static StorageRetentionReclaimOptions Default => new(
        StorageRetentionMaintenance.AcceptanceArtifactMaxAge,
        StorageRetentionMaintenance.AcceptanceArtifactMaxAge,
        StorageRetentionMaintenance.WorkerDeletionAge,
        StorageRetentionMaintenance.WorkerDeletionAge,
        () => new[]
        {
            Environment.GetEnvironmentVariable(ConductorContinuitySupervisor.StdoutLogPathEnvironmentVariable),
            Environment.GetEnvironmentVariable(ConductorContinuitySupervisor.StderrLogPathEnvironmentVariable)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).ToArray());
}
