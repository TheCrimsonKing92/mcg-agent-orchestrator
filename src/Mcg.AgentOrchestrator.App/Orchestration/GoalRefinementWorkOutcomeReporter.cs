using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalRefinementWorkOutcomeReporter
{
    public static void Report(
        GoalRefinementWorkProcessResult result,
        OrchestratorWorkspace workspace,
        IOrchestratorStateOutboxRepository outboxRepository)
    {
        if (!result.Claimed)
        {
            var goalId = new GoalId(result.GoalId);
            Console.Error.WriteLine(
                $"SPEC_REFINEMENT_WORK_FAILED goal={result.GoalId} reason=claim-miss " +
                $"message_id={GoalRefinementWorkCoordinator.MessageId(goalId)} " +
                $"store_path={Path.GetFullPath(workspace.SqliteStatePath)} " +
                GoalRefinementClaimMissDiagnostic.Describe(
                    outboxRepository, GoalRefinementWorkCoordinator.MessageId(goalId)));
            throw new CliExitException(1);
        }

        Console.WriteLine(
            $"SPEC_REFINEMENT_WORK_COMPLETE goal={result.GoalId} " +
            $"claimed=true attached={result.Attached.ToString().ToLowerInvariant()}");
    }
}
