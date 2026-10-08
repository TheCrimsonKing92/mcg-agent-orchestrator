using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Passing evidence returns to its Tester owner once per finding and candidate before source repair.
internal static class PassingFindingEvidenceRouter
{
    internal static FailedGoalFindingObservation BuildDecision(
        Goal goal,
        TaskSpec requestingTask,
        TaskSpec? developer,
        string candidateSha,
        IReadOnlyList<string> receiptIds,
        IReadOnlyList<ReviewFinding> findings,
        FindingReceiptClosureDiagnosis diagnosis,
        Func<TaskSpec, string> getAttemptIdentity)
    {
        var findingIds = findings.Select(finding => finding.StableId).Distinct(StringComparer.Ordinal).ToArray();
        var evidence = $"candidate_sha={candidateSha}; " +
            $"receipt_ids={string.Join(',', receiptIds.Distinct(StringComparer.Ordinal))}; " +
            $"finding_ids={string.Join(',', findingIds)}; diagnosis={diagnosis.Describe()}";
        if (!HasTesterReceiptRouteForAll(goal, requestingTask, candidateSha, findingIds))
        {
            return FailedGoalFindingObservation.Routed(
                FailedGoalFindingObservationKind.FindingRouteObserved,
                requestingTask.Id,
                getAttemptIdentity(requestingTask),
                $"PASSING_EVIDENCE_OPEN_FINDING route=tester; task={requestingTask.Id.Value[..8]}; {evidence} " +
                "The supplied receipts passed on the current candidate. Resolve each finding they satisfy, or keep it open and name the source or test location that still needs a Developer change.",
                null, RetryRoundKind.Mechanical, RetryCause.CriterionEvidenceOwnerMismatch);
        }

        var message = $"PASSING_EVIDENCE_OPEN_FINDING route=developer; {evidence}; " +
            $"tester_reason={string.Join(" | ", findings.Select(finding => $"{finding.StableId}: {finding.Description}"))}. " +
            "The Tester re-raised the finding after receiving the passing receipts; repair the Developer-owned source or test location it names. ";
        return developer is null
            ? FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingActionableRedRouteUnavailable,
                message + "No upstream Developer task exists.")
            : FailedGoalFindingObservation.Routed(
                FailedGoalFindingObservationKind.FindingActionableRed,
                developer.Id,
                getAttemptIdentity(developer),
                message,
                null);
    }

    private static bool HasTesterReceiptRouteForAll(
        Goal goal, TaskSpec task, string candidateSha, IReadOnlyList<string> findingIds)
    {
        // Timeline order proves a worker round happened after receipt delivery. Receipt reattachment
        // itself records verification too, so only dispatch execution can establish a re-raise.
        var delivered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in goal.Timeline)
        {
            if (item.TaskId != task.Id) continue;
            if (item.Kind == ProgressKind.TaskVerificationRecorded &&
                item.Message.StartsWith("Dispatch execution", StringComparison.Ordinal) &&
                findingIds.All(delivered.Contains)) return true;
            if (item.Kind != ProgressKind.TaskRetried ||
                !item.Message.StartsWith("PASSING_EVIDENCE_OPEN_FINDING route=tester;", StringComparison.Ordinal)) continue;
            var fields = item.Message.Split(';');
            var candidate = fields.FirstOrDefault(field => field.TrimStart().StartsWith("candidate_sha=", StringComparison.Ordinal));
            if (candidate is null || !string.Equals(candidate.Trim()["candidate_sha=".Length..],
                    candidateSha.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var findings = fields.FirstOrDefault(field => field.TrimStart().StartsWith("finding_ids=", StringComparison.Ordinal));
            if (findings is not null)
                delivered.UnionWith(findings.Trim()["finding_ids=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries));
        }
        return false;
    }
}
