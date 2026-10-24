using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PreTesterOutOfDiffRerun(
    string worktree, string? sourceRoot, Func<Goal, IReadOnlyList<string>> changedPaths,
    Func<Goal, IReadOnlyList<string>, IReadOnlyDictionary<string, IReadOnlyList<string>>> historyReader,
    PreTesterOutOfDiffRerun.ReconcileAttempt reconcile,
    Action<GoalId, TaskId, string> recordRequest, Action<GoalId, TaskId, string> recordRun,
    Func<string, string, string, string, string> batchId, Func<string, string, string, string> receiptId)
{
    internal delegate bool ReconcileAttempt(Goal goal, ConductorAutonomyPolicy policy, string request,
        string? candidateSha, string source, ConductorFocusedEvidenceRequestContext? requestContext,
        out FocusedEvidenceRunResult evidence, out ConductorParallelAcceptanceAttempt? attempt,
        out FailedGoalFindingObservation decision, out ConductorParallelAcceptanceAttemptDecisionKind? kind,
        ConductorParallelAcceptanceAttempt? existingAttempt = null);

    internal bool TryRun(Goal goal, TaskSpec tester, PreTesterEvidenceEntry entry, ConductorAutonomyPolicy policy,
        out FailedGoalFindingObservation decision, out ConductorParallelAcceptanceAttemptDecisionKind? kind)
    {
        const string reason = "pre-tester-out-of-diff-rerun";
        decision = FailedGoalFindingObservation.None;
        kind = null;
        if (PreTesterOutOfDiffRerunMarker.Latest(goal, tester.Id, entry.CandidateSha, entry.ReceiptId) is not null) return false;
        var started = PreTesterOutOfDiffRerunMarker.LatestRequest(goal, tester.Id, entry.CandidateSha, entry.ReceiptId);
        var selections = PreTesterOutOfDiffRerunEligibility.Evaluate(entry, changedPaths(goal), worktree, sourceRoot);
        if (selections is null)
        {
            if (started is not null) throw new InvalidDataException("Started out-of-diff rerun lost its eligible source receipt.");
            return false;
        }
        // Preserve the history that authorized an in-flight run when a store becomes temporarily unavailable.
        var history = started?.FailureHistory ?? historyReader(goal, entry.FailingTests);
        var keys = entry.FailingTests.Select(CrossGoalTestFailureHistory.TestKey).OfType<string>().ToArray();
        if (keys.Length != entry.FailingTests.Count || PreTesterOutOfDiffRerunMarker.DecideVerdict("green", keys, history) != "non-blocking") return false;
        var identity = entry.ReceiptId + FindingEvidenceExecutionClassifier.BuildRequestIdentity(new(selections));
        var rerunReceipt = receiptId(entry.CandidateSha, reason, identity);
        var context = new ConductorFocusedEvidenceRequestContext(reason, batchId(entry.CandidateSha, reason, reason, identity), []);
        var request = string.Join("; ", selections.Select(selection => selection.TestProject + ":" + selection.TestClass));
        var receiptHistory = keys.Distinct(StringComparer.Ordinal).ToDictionary(key => key,
            key => (IReadOnlyList<string>)history[key].Select(id => id[..Math.Min(8, id.Length)]).Distinct().ToArray(), StringComparer.Ordinal);
        var requestMarker = new PreTesterOutOfDiffRerunMarker(entry.CandidateSha, entry.ReceiptId, rerunReceipt,
            "started", entry.FailingTests, receiptHistory, "blocking").FormatRequest();
        var completed = reconcile(goal, policy, request, entry.CandidateSha, reason, context,
            out var evidence, out var attempt, out decision, out kind);
        if ((kind == ConductorParallelAcceptanceAttemptDecisionKind.Started || completed) &&
            started is null)
            recordRequest(goal.Id, tester.Id, requestMarker);
        if (!completed && (kind is not (ConductorParallelAcceptanceAttemptDecisionKind.Completed or
                ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun) ||
            PreTesterApparatusReceiptClassifier.IsTransientAttemptOutcome(attempt?.Outcome) ||
            kind == ConductorParallelAcceptanceAttemptDecisionKind.Completed && decision.Kind == FailedGoalFindingObservationKind.FindingEvidencePending)) return true;
        var (outcome, _, _) = PreTesterApparatusReceiptClassifier.ClassifyOutcome(evidence, completed, entry.CandidateSha, selections, _ => false);
        var marker = new PreTesterOutOfDiffRerunMarker(entry.CandidateSha, entry.ReceiptId, rerunReceipt, outcome,
            entry.FailingTests, receiptHistory,
            PreTesterOutOfDiffRerunMarker.DecideVerdict(outcome, keys, history));
        recordRun(goal.Id, tester.Id, marker.Format());
        return false;
    }
}
