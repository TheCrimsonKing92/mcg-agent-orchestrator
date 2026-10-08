using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGoalDetail
{
    internal static async Task ComposeAsync(IOrchestratorStateQueries state, IGoalEventTail tail,
        string idOrPrefix, IOwnerConsoleOutput output, CancellationToken cancellationToken)
    {
        var matches = (await state.ListGoalMetadataAsync(cancellationToken))
            .Where(item => item.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        // A board selection supplies an exact id, even when that id prefixes another goal.
        var exact = matches.FirstOrDefault(item => item.Id.Equals(idOrPrefix, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) matches = [exact];
        if (matches.Length != 1)
        {
            output.WriteLine(matches.Length == 0 ? $"no goal matches '{idOrPrefix}'" : "ambiguous goal");
            return;
        }
        var kernel = await state.LoadGoalsAsync([new GoalId(matches[0].Id)], cancellationToken);
        var goal = kernel.Goals.SingleOrDefault(item => item.Id.Value == matches[0].Id);
        if (goal is null) { output.WriteLine("goal state unavailable"); return; }
        output.WriteLine($"{goal.Id.Value} | {OwnerGoalTitle.From(goal.Objective)} | {goal.Status} | stage: {Stage(goal)}");
        foreach (var item in tail.ReadLast(goal.Id.Value, 15)) output.WriteLine(item);
    }

    internal static string Stage(Goal goal) =>
        goal.Tasks.FirstOrDefault(task => task.Status != WorkTaskStatus.Completed)?.RequiredRole.ToString() ?? "-";
}
