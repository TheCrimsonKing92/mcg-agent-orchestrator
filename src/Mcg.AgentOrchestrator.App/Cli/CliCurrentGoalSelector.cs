using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliCurrentGoalSelector
{
    internal static GoalId Select(IOrchestratorStateQueries stateQueries, GoalId? sessionGoalId)
    {
        var candidates = stateQueries.ListGoalMetadataAsync(includeTerminalCreatedAt: true)
            .GetAwaiter().GetResult()
            .Select((goal, index) => (Goal: goal, Index: index))
            .OrderByDescending(candidate => candidate.Goal.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Goal)
            .ToList();
        if (sessionGoalId is not null)
        {
            var currentGoalId = sessionGoalId.Value;
            var current = candidates.FirstOrDefault(goal =>
                goal.Id.Equals(currentGoalId, StringComparison.OrdinalIgnoreCase));
            if (current is not null)
            {
                candidates.Remove(current);
                candidates.Insert(0, current);
            }
        }
        var latest = candidates.FirstOrDefault()
            ?? throw new InvalidOperationException("Create a goal first with: goal <objective>");
        return new GoalId(latest.Id);
    }
}
