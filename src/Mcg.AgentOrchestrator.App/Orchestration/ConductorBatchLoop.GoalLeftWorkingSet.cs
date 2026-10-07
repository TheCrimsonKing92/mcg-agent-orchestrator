using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static bool TrySkipGoalLeftWorkingSet(
        AgentOrchestratorKernel kernel,
        Goal goal,
        int tick,
        string stage,
        List<string> tickLines)
    {
        if (kernel.Goals.FirstOrDefault(candidate => candidate.Id == goal.Id) is not null)
        {
            return false;
        }

        EmitProgress($"GOAL_LEFT_WORKING_SET tick={tick} goal={goal.Id.Value[..8]} stage={stage}", tickLines);
        return true;
    }
}
