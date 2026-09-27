namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private void RecordDeferredNoChangeOutcome(
        Goal goal,
        TaskSpec task,
        TaskId taskId,
        TaskVerificationRecord verification,
        string? outcomeRule)
    {
        if (!string.Equals(outcomeRule, TaskOutcomeRules.DeferredNoChangeRound.Token,
                StringComparison.Ordinal)) return;
        if (task.LastDispatch is not { BaseCommit: { Length: > 0 } candidate } ||
            !DeferredNoChangeOutcome.TryParse(verification.StandardError, out var marker) ||
            !string.Equals(marker.CandidateSha, candidate, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Deferred no-change completion lost its candidate-bound outcome.");

        task.SetDispatchResultCommit(candidate);
        Append(goal, taskId, ProgressKind.TaskNote,
            $"DEFERRED_NO_CHANGE_OUTCOME task={taskId.Value} candidate_sha={candidate} " +
            $"classes={string.Join(',', marker.TestClasses)} rationale={marker.Rationale}");
    }
}
