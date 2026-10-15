namespace Mcg.AgentOrchestrator.Core;

internal static class FindingEvidenceSuppression
{
    internal static string Message(GoalId goalId, TaskId taskId, string candidateSha,
        IReadOnlyList<string> blockerIds, string evidenceRequestId, AgentRole chosenOwner,
        string reason, string suppressionIdentity)
    {
        ArgumentNullException.ThrowIfNull(blockerIds);
        var normalizedBlockerIds = blockerIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (normalizedBlockerIds.Length == 0)
            throw new ArgumentException("At least one writable blocker id is required.", nameof(blockerIds));
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(suppressionIdentity);
        return $"finding-evidence-suppressed goal_id={goalId}; task_id={taskId}; candidate_sha={candidateSha.Trim()}; " +
               $"blocker_ids={string.Join(',', normalizedBlockerIds)}; evidence_request_id={evidenceRequestId.Trim()}; " +
               $"chosen_owner={chosenOwner}; reason={reason.Trim()}; suppression_identity={suppressionIdentity.Trim()}";
    }
}
