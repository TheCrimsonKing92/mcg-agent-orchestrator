using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGoalDetailFormatter
{
    internal static async Task<Goal?> ComposeAsync(IOrchestratorStateQueries state, IGoalEventTail tail,
        string id, IOwnerConsoleOutput output, CancellationToken token, TimeZoneInfo? zone = null,
        Func<OwnerConsoleGateProgress?>? gate = null)
    {
        var detail = await new OwnerGoalDetailQuery(state, tail, gate).LoadAsync(id, token);
        if (detail is null) { output.WriteLine("goal state unavailable"); return null; }
        Render(detail, output, zone ?? TimeZoneInfo.Local);
        return detail.Goal;
    }

    internal static void Render(OwnerGoalDetailSnapshot detail, IOwnerConsoleOutput output, TimeZoneInfo zone)
    {
        var goal = detail.Goal;
        output.WriteLine(goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)] + "  " + OwnerGoalTitle.From(goal.Objective));
        output.WriteLine("Status: " + goal.Status);
        var stage = GoalLifecycle.ResolveState(goal, new(IsBlocked: goal.CurrentHold is not null)).ToString();
        if (stage != goal.Status.ToString()) output.WriteLine("Stage: " + stage);
        var role = OwnerConsoleGoalDetail.Stage(goal);
        if (role != "-") output.WriteLine("Role: " + role);
        if (OwnerConsoleGoalLanding.Line(goal, detail.LandingQueuePosition, zone) is { } landing)
            output.WriteLine("Landing: " + landing);
        if (detail.Gate is { } liveGate) output.WriteLine("Gate: " + OwnerGoalDetailGateLine.Text(liveGate));
        output.WriteLine("Tasks:");
        foreach (var task in goal.Tasks) output.WriteLine($"  {task.RequiredRole}: {task.Status}");
        if (goal.CurrentHold is { } hold)
            output.WriteLine("Waiting on: " + OwnerActivityNarrator.WaitingOn(hold.Blocker));
        var lines = new List<(DateTimeOffset Time, string Phrase)>();
        foreach (var item in detail.RecentEvents)
            if (OwnerActivityNarrator.StagePhrase(item, Finding(goal, item)) is { } phrase &&
                (lines.Count == 0 || lines[^1].Phrase != phrase)) lines.Add((item.Timestamp, phrase));
        output.WriteLine(lines.Count == 0 ? "Recent events: none" : "Recent events:");
        foreach (var line in lines.TakeLast(10)) output.WriteLine($"  {line.Time.ToLocalTime():HH:mm:ss} {Plain(line.Phrase)}");
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
