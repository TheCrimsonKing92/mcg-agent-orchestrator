using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintTerminalGoalSweep(
        TerminalGoalSweepResult result,
        bool includeRepairs = true,
        bool includeBlockers = true)
    {
        if (result.ExcludedGoalCount > 0)
        {
            Console.WriteLine($"SWEEP_SUMMARY kind=stale-terminal-excluded count={result.ExcludedGoalCount}");
        }

        foreach (var goal in result.Goals)
        {
            if (includeRepairs)
            {
                foreach (var repair in goal.Repairs)
                {
                    Console.WriteLine($"SWEEP_REPAIR goal={goal.GoalPrefix} kind={repair.Kind} evidence=\"{repair.Evidence}\" command=\"{repair.Command}\"");
                }
            }

            if (includeBlockers)
            {
                foreach (var blocker in goal.Blockers)
                {
                    if (blocker.Kind == "stale-terminal-excluded")
                    {
                        continue;
                    }

                    Console.WriteLine($"SWEEP_BLOCKER goal={goal.GoalPrefix} kind={blocker.Kind} evidence=\"{blocker.Evidence}\" command=\"{blocker.Command}\"");
                }
            }
        }
    }
}
