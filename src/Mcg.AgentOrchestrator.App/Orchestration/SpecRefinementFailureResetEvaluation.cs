using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SpecRefinementFailureResetEvaluation
{
    public static bool Evaluate(OrchestratorWorkspace workspace, Goal goal)
    {
        var store = SpecRefinementLaunchAttemptStore.ForWorkspace(workspace);
        try
        {
            if (store.Get(goal.Id)?.EscalatedAt is not null)
                return true;
        }
        catch (InvalidDataException)
        {
            // A malformed launch record also prevents automatic recovery.
            return true;
        }

        if (goal.AuthoritativeRefinedSpecVersion is { } version &&
            goal.CriterionEvidenceObligations.Any(item => item.CriterionVersion == version.Version &&
                item.State == CriterionEvidenceState.Pending &&
                item.RequiredScope == "lifecycle declaration repair required"))
            return true;

        if (!File.Exists(workspace.SqliteStatePath))
            return false;
        var state = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
            .GetOutboxStateAsync(GoalRefinementWorkCoordinator.MessageId(goal.Id)).GetAwaiter().GetResult();
        return state?.Status == OrchestratorStateOutboxStatus.Failed &&
            state.Detail?.StartsWith("SPEC_REFINEMENT_FAILED", StringComparison.Ordinal) == true;
    }
}
