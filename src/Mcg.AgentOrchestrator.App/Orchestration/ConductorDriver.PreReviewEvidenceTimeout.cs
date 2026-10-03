using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private bool TryHoldPreReviewEvidenceTimeout(
        Goal goal,
        TaskSpec reviewerTask,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        PreReviewEvidenceContext context,
        int round,
        FocusedEvidenceRunResult evidence,
        IReadOnlyList<string> failingTests,
        PreReviewEvidenceReceipt? priorReceipt,
        string? evidencePointer,
        out ConductorAdvanceResult result)
    {
        result = default!;
        if (evidence.Passed || failingTests.Count > 0)
        {
            return false;
        }

        var timedOutChecks = evidence.Checks.Where(check => !check.Passed)
            .Select(check => check.Name).ToArray();
        if (timedOutChecks.Length == 0 || !timedOutChecks.All(IsBlockingTimeoutCheck))
        {
            return false;
        }

        // The immediately preceding durable receipt is the sequence memory. Any other outcome,
        // candidate, or selection resets it; ordering and duplicate selection entries do not.
        var consecutiveTimeout = priorReceipt is { EvidenceTimeoutChecks.Count: > 0 } &&
            string.Equals(priorReceipt.GoalId, goal.Id.Value, StringComparison.Ordinal) &&
            string.Equals(priorReceipt.CandidateSha, context.CandidateSha, StringComparison.OrdinalIgnoreCase) &&
            priorReceipt.SelectedFocusedTests.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .SequenceEqual(context.SelectedFocusedTests.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                    StringComparer.Ordinal);

        PreReviewEvidenceReceipts.Record(_recordPreReviewEvidence,
            goal, reviewerTask, context, round, PreReviewEvidenceDisposition.Red,
            evidence.Checks, [], evidencePointer, evidenceTimeoutChecks: timedOutChecks);

        var checks = string.Join(", ", timedOutChecks);
        result = consecutiveTimeout
            ? Escalate(goal, goalPrefix, policy, fromState,
                $"PRE_REVIEW_EVIDENCE_TIMEOUT: candidate {context.CandidateSha} focused evidence hit its time budget on consecutive runs without failing test identities; " +
                $"timed-out checks: {checks}; selected focused tests: {string.Join(", ", context.SelectedFocusedTests)}; " +
                $"pointer={evidencePointer ?? "none"}. Run the selected tests without the cap, then retry or adjudicate.")
            : MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Held(fromState,
                $"Pre-review focused evidence hit its time budget for candidate {context.CandidateSha} ({checks}); no worker dispatched; re-running on next conduct tick."));
        return true;
    }
}
