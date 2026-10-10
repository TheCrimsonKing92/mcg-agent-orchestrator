using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGoalDetailFormatter
{
    internal static async Task<Goal?> ComposeAsync(IOrchestratorStateQueries state, IGoalEventTail tail,
        string id, IOwnerConsoleOutput output, CancellationToken token, TimeZoneInfo? zone = null)
    {
        var kernel = await state.LoadGoalsAsync([new GoalId(id)], token);
        var goal = kernel.Goals.SingleOrDefault(goal => goal.Id.Value == id);
        if (goal is null) { output.WriteLine("goal state unavailable"); return null; }
        output.WriteLine(goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)] + "  " + OwnerGoalTitle.From(goal.Objective));
        output.WriteLine("Status: " + goal.Status);
        var stage = GoalLifecycle.ResolveState(goal, new(IsBlocked: goal.CurrentHold is not null)).ToString();
        if (stage != goal.Status.ToString()) output.WriteLine("Stage: " + stage);
        var role = OwnerConsoleGoalDetail.Stage(goal);
        if (role != "-") output.WriteLine("Role: " + role);
        int? position = null;
        if (goal.Status == GoalStatus.Verified && goal.CurrentHold is null)
        {
            var metadata = await state.ListGoalMetadataAsync(token);
            var ids = metadata.Where(item => item.Status == nameof(GoalStatus.Verified)).Select(item => new GoalId(item.Id)).ToArray();
            var queued = await state.LoadGoalsAsync(ids, token);
            var ordered = ConductorBatchLoop.OrderParallelAcceptanceEligibleGoals(queued.Goals
                .Where(item => item.Status == GoalStatus.Verified && item.CurrentHold is null).ToArray());
            var index = ordered.ToList().FindIndex(item => item.Id == goal.Id);
            if (index >= 0) position = index + 1;
        }
        if (OwnerConsoleGoalLanding.Line(goal, position, zone ?? TimeZoneInfo.Local) is { } landing)
            output.WriteLine("Landing: " + landing);
        output.WriteLine("Tasks:");
        foreach (var task in goal.Tasks) output.WriteLine($"  {task.RequiredRole}: {task.Status}");
        if (goal.CurrentHold is { } hold)
            output.WriteLine("Waiting on: " + OwnerActivityNarrator.WaitingOn(hold.Blocker));
        var roles = goal.Tasks.ToDictionary(task => task.Id.Value, task => task.RequiredRole);
        var events = tail.ReadLast(id, 100)
            .Select(line => OwnerGoalLifecycleEvent.TryParse(line, id, out var item) ? item : null)
            .Where(item => item is not null).Select(item => OwnerGoalLifecycleEvent.WithRole(item!, roles)).ToArray();
        var lines = new List<(DateTimeOffset Time, string Phrase)>();
        foreach (var item in events)
            if (OwnerActivityNarrator.StagePhrase(item, Finding(goal, item)) is { } phrase &&
                (lines.Count == 0 || lines[^1].Phrase != phrase)) lines.Add((item.Timestamp, phrase));
        output.WriteLine(lines.Count == 0 ? "Recent events: none" : "Recent events:");
        foreach (var line in lines.TakeLast(10)) output.WriteLine($"  {line.Time.ToLocalTime():HH:mm:ss} {Plain(line.Phrase)}");
        return goal;
    }

    private static string Plain(string text) => text.Replace('{', ' ').Replace('}', ' ').Replace('\r', ' ').Replace('\n', ' ');

    internal static string? Finding(Goal goal, OwnerConductEvent item)
    {
        if (!item.Detail.StartsWith("TaskFailed", StringComparison.Ordinal)) return null;
        var id = OwnerActivityNarrator.Field(item, "task");
        var task = goal.Tasks.FirstOrDefault(task => task.Id.Value == id);
        if (task?.RequiredRole is not (AgentRole.Reviewer or AgentRole.Tester)) return null;
        var result = task.VerificationHistory.Where(record => record.CompletedAt <= item.Timestamp)
            .OrderByDescending(record => record.CompletedAt).FirstOrDefault();
        if (result is null) return null;
        var findings = WorkerResultBlockers.TryFindReviewFindingRound(result, out var round, out _) ?
            round.Findings : result.MergedReviewFindings;
        return OwnerHoldReason.FirstLine(findings?.FirstOrDefault()?.Description);
    }
}
