using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// One instance belongs to one acceptance attempt; its lane claims are atomic across concurrent shards.
internal sealed class AcceptanceLaneTestFailureRerun
{
    private static readonly ConditionalWeakTable<AcceptancePartitionVerdictCache, AcceptanceLaneTestFailureRerun> Attempts = new();
    private readonly ConcurrentDictionary<string, byte> _reranLanes = new(StringComparer.Ordinal);
    private readonly AcceptanceLaneFlakeLedger? _ledger;
    private readonly string _goalId;
    private readonly string _attemptId;
    private readonly string _candidateTreeSha;
    private readonly Action<string> _emit;

    internal AcceptanceLaneTestFailureRerun(
        AcceptanceLaneFlakeLedger? ledger = null,
        string goalId = "",
        string attemptId = "",
        string candidateTreeSha = "",
        Action<string>? emit = null)
    {
        _ledger = ledger;
        _goalId = goalId;
        _attemptId = attemptId;
        _candidateTreeSha = candidateTreeSha;
        _emit = emit ?? (line => { Console.WriteLine(line); Console.Out.Flush(); });
    }

    internal static AcceptanceLaneTestFailureRerun ForAttempt(AcceptancePartitionVerdictCache cache, string worktreePath) =>
        Attempts.GetValue(cache, owner => new AcceptanceLaneTestFailureRerun(
            new AcceptanceLaneFlakeLedger(worktreePath), owner.GoalId, owner.AttemptId, owner.CandidateTreeSha));

    internal async Task<AcceptanceCheckResult> RunAsync(
        AcceptanceManifestCheck check,
        AcceptanceCheckResult firstRun,
        Func<Task<AcceptanceCheckResult>> rerunLane,
        Func<AcceptanceCheckResult, string> invocationId,
        bool sharedApparatusInvalidated = false,
        CancellationToken cancellationToken = default)
    {
        if (firstRun.Passed ||
            firstRun.CompletionDecision?.FailedPredicate != AcceptanceShardCompletionPredicates.FailingTrx ||
            firstRun.FailingTestIdentities is not { Count: > 0 } ||
            !GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out _, out _) ||
            sharedApparatusInvalidated || cancellationToken.IsCancellationRequested ||
            !_reranLanes.TryAdd(check.Name, 0))
        {
            return firstRun;
        }

        var rerun = await rerunLane().ConfigureAwait(false);
        var outcome = rerun.Passed ? AcceptanceLaneRerunEvidence.Flake : AcceptanceLaneRerunEvidence.ConfirmedFailure;
        var evidence = new AcceptanceLaneRerunEvidence(
            invocationId(firstRun), firstRun.TestResultPaths?.ToArray() ?? [],
            firstRun.FailingTestIdentities.ToArray(), firstRun.CompletionDecision.FailedPredicate!,
            invocationId(rerun), rerun.TestResultPaths?.ToArray() ?? [],
            rerun.FailingTestIdentities?.ToArray() ?? [], rerun.CompletionDecision?.FailedPredicate, outcome);
        var result = rerun with
        {
            LaneRerun = evidence,
            ResultSummary = AcceptanceDotnetBuildPhase.PrefixResultSummary(
                $"lane rerun: first_run=failed({evidence.FirstPredicate}) rerun={(rerun.Passed ? "passed" : "failed")} outcome={outcome}",
                rerun.ResultSummary)
        };

        try
        {
            _ledger?.Append(AcceptanceLaneFlakeRow.FromEvidence(
                _goalId, _attemptId, check.Name, _candidateTreeSha, evidence, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _emit($"LANE_FLAKE_LEDGER_WRITE_FAILED lane=\"{check.Name}\" error={ex.Message.ReplaceLineEndings(" ")}");
        }

        _emit($"LANE_RERUN lane=\"{check.Name}\" outcome={outcome} first_invocation={evidence.FirstInvocationId} rerun_invocation={evidence.RerunInvocationId}");
        return result;
    }
}
