using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGoalDetailFormatter
{
    internal static async Task ComposeAsync(IOrchestratorStateQueries state, IGoalEventTail tail,
        string id, IOwnerConsoleOutput output, CancellationToken token)
    {
        var kernel = await state.LoadGoalsAsync([new GoalId(id)], token);
        var goal = kernel.Goals.SingleOrDefault(goal => goal.Id.Value == id);
        if (goal is null) { output.WriteLine("goal state unavailable"); return; }
        output.WriteLine("Goal: " + Plain(goal.Id.Value));
        output.WriteLine("Title: " + Plain(OwnerGoalTitle.From(goal.Objective)));
        output.WriteLine("Status: " + goal.Status);
        output.WriteLine("Stage: " + OwnerConsoleGoalDetail.Stage(goal));
        output.WriteLine("Role: " + OwnerConsoleGoalDetail.Stage(goal));
        output.WriteLine("Tasks:");
        foreach (var task in goal.Tasks) output.WriteLine($"  {task.RequiredRole}: {task.Status}");
        if (goal.CurrentHold is { } hold)
            output.WriteLine("Hold: " + Plain((hold.State + ": " + hold.Blocker).Split(" evidence=[", 2)[0]).Replace('_', ' ').Replace('-', ' '));
        var roles = goal.Tasks.ToDictionary(task => task.Id.Value, task => task.RequiredRole);
        var events = tail.ReadLast(id, 10).TakeLast(10)
            .Select(line => OwnerGoalLifecycleEvent.TryParse(line, id, out var item) ? item : null)
            .Where(item => item is not null).Select(item => OwnerGoalLifecycleEvent.WithRole(item!, roles)).ToArray();
        output.WriteLine(events.Length == 0 ? "Recent events: none" : "Recent events:");
        foreach (var item in events)
            output.WriteLine($"  {item.Timestamp.ToLocalTime():HH:mm:ss} {OwnerConsoleActivityPresentation.Phrase(item, OwnerConsoleActivityPresentation.Classify(item) ?? "")}");
    }

    private static string Plain(string text) => text.Replace('{', ' ').Replace('}', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
