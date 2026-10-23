using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceTimedOutRunProgress(string State, long? SilenceSeconds);

internal static class AcceptanceTimedOutProbeEvidence
{
    // The 2026-10-23 census measured budget-stop silence at 0-227 s and stall stops at >=600 s.
    // 300 s (60 heartbeat intervals) separates those cases, conservatively keeping longer quiet runs RED.
    internal const int ProgressWindowSeconds = 300;

    internal static AcceptanceTimedOutRunProgress ClassifyFirstRunProgress(string? path) =>
        ClassifyFirstRunProgress(GateHeartbeatArtifacts.TryReadSnapshot(path));

    internal static AcceptanceTimedOutRunProgress ClassifyFirstRunProgress(GateHeartbeatSnapshot? snapshot)
    {
        if (snapshot is not { State: "timed-out", PreStopObservedAt: { } observedAt, PreStopProgressAt: { } progressAt })
            return new("unknown", null);

        var silence = observedAt - progressAt;
        return new(silence < TimeSpan.FromSeconds(ProgressWindowSeconds) ? "growing" : "stalled",
            (long)silence.TotalSeconds);
    }

    internal static AcceptanceCheckResult? TryComposeRerunPass(
        AcceptanceCheckResult firstRun,
        AcceptanceCheckResult probe,
        string firstInvocationId,
        string rerunInvocationId,
        AcceptanceTimedOutRunProgress progress)
    {
        if (firstRun.CompletionDecision?.FailedPredicate != AcceptanceShardCompletionPredicates.TimedOut ||
            firstRun.FailingTestIdentities?.Any(identity => !string.IsNullOrWhiteSpace(identity)) == true ||
            progress.State != "growing" ||
            !probe.Passed || probe.CompletionDecision is not { Passed: true } ||
            (probe.CompletionDecision.ExecutedTestCount ?? probe.ExecutedTestCount ?? 0) <= 0 ||
            probe.FailingTestIdentities?.Any(identity => !string.IsNullOrWhiteSpace(identity)) == true ||
            probe.TestResultPaths is not { Count: > 0 } paths || !paths.All(File.Exists))
            return null;

        return probe with
        {
            LaneRerun = new AcceptanceLaneRerunEvidence(
                firstInvocationId, firstRun.TestResultPaths?.Where(File.Exists).ToArray() ?? [], [],
                AcceptanceShardCompletionPredicates.TimedOut,
                rerunInvocationId, paths, [], null, AcceptanceLaneRerunEvidence.Flake),
            WithinAttemptRerun = null,
            ResultSummary = AcceptanceDotnetBuildPhase.PrefixResultSummary(
                "lane rerun: first_run=failed(timed-out) first_trx=none first_run_progress=growing rerun=passed outcome=flake",
                probe.ResultSummary)
        };
    }

    internal static AcceptanceWithinAttemptRerunEvidence? TryBuild(
        string partitionId,
        AcceptanceCheckResult firstRun,
        AcceptanceCheckResult probe,
        string originalInvocationId,
        string retryInvocationId)
    {
        if (firstRun.CompletionDecision?.FailedPredicate != AcceptanceShardCompletionPredicates.TimedOut ||
            probe.TestResultPaths is not { Count: > 0 } paths || !paths.All(File.Exists))
            return null;

        return new AcceptanceWithinAttemptRerunEvidence(
            partitionId,
            AcceptanceShardCompletionPredicates.TimedOut,
            originalInvocationId,
            retryInvocationId,
            probe.CompletionDecision?.ExecutedTestCount ?? probe.ExecutedTestCount ?? 0,
            paths,
            probe.Passed,
            probe.FailingTestIdentities?.Where(identity => !string.IsNullOrWhiteSpace(identity)).ToArray() ?? []);
    }
}
