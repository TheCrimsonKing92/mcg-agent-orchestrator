using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static bool TrySkipGoalLeftWorkingSet(
        AgentOrchestratorKernel kernel,
        Goal goal,
        int tick,
        string stage,
        List<string> tickLines,
        Action<string> finishGoalWalk,
        out ConductorAdvanceResult result)
    {
        result = null!;
        return TrySkipGoalLeftWorkingSet(kernel, goal, tick, stage, tickLines, finishGoalWalk);
    }

    private static bool TrySkipGoalLeftWorkingSet(
        AgentOrchestratorKernel kernel,
        Goal goal,
        int tick,
        string stage,
        List<string> tickLines,
        Action<string> finishGoalWalk)
    {
        if (!TrySkipGoalLeftWorkingSet(kernel, goal, tick, stage, tickLines))
        {
            return false;
        }

        finishGoalWalk("goal-left-working-set");
        return true;
    }

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

    private static string ClassifyGoalEvent(string line)
    {
        if (line.Contains("result=done", StringComparison.Ordinal) ||
            line.Contains("result=landed", StringComparison.Ordinal))
            return "goal-landing";
        if (line.Contains("result=escalated", StringComparison.Ordinal) ||
            line.Contains("escalated", StringComparison.Ordinal))
            return "goal-escalation";
        if (line.Contains("result=", StringComparison.Ordinal))
            return "goal";
        return string.Empty;
    }
}
