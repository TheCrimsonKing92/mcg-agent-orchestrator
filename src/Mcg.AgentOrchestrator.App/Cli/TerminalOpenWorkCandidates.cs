using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class TerminalOpenWorkCandidates
{
    internal static IReadOnlyList<GoalId> Read(IOrchestratorStateRepository repository, Action<string> emit)
    {
        try
        {
            if (repository.EnsureTerminalOpenWorkIndexAsync().GetAwaiter().GetResult())
                emit("TERMINAL_OPEN_WORK_INDEX built");
        }
        catch (Exception ex)
        {
            emit($"TERMINAL_OPEN_WORK_INDEX ensure-failed: {ex.GetType().Name}: {ex.Message}");
        }
        return repository.ListTerminalGoalIdsWithNonTerminalTasksAsync().GetAwaiter().GetResult();
    }
}
