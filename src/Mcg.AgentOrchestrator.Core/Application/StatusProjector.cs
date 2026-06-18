using System.Text;

namespace Mcg.AgentOrchestrator.Core;

public sealed record StatusProjectionGoal(
    string Id,
    string Objective,
    GoalStatus Status,
    IReadOnlyList<StatusProjectionTask> Tasks,
    DateTimeOffset? LastEventAt);

public sealed record StatusProjectionTask(
    AgentRole Role,
    WorkTaskStatus Status);

public sealed record StatusProjectionInput(
    IReadOnlyList<StatusProjectionGoal> Goals,
    int OpenEscalationCount,
    string? EscalationThreadUrl,
    DateTimeOffset Now,
    string? PreviousRenderedContent = null);

public sealed record StatusProjection(
    string RenderedContent,
    bool Unchanged,
    StatusProjectionBuckets Buckets,
    int OpenEscalationCount);

public sealed record StatusProjectionBuckets(
    IReadOnlyList<StatusProjectionGoalItem> Active,
    IReadOnlyList<StatusProjectionGoalItem> AlmostDone,
    IReadOnlyList<StatusProjectionGoalItem> Landed);

public sealed record StatusProjectionGoalItem(
    string Id,
    string Objective,
    string Stage,
    int CompletedTaskCount,
    int TotalTaskCount,
    int PercentComplete,
    string Health);

public static class StatusProjector
{
    public static StatusProjection Project(StatusProjectionInput input)
    {
        var landedCutoff = input.Now.AddHours(-24);
        var active = new List<StatusProjectionGoalItem>();
        var almostDone = new List<StatusProjectionGoalItem>();
        var landed = new List<StatusProjectionGoalItem>();

        foreach (var goal in input.Goals.OrderByDescending(goal => goal.LastEventAt ?? DateTimeOffset.MinValue))
        {
            if (goal.Status is GoalStatus.Cancelled or GoalStatus.Superseded or GoalStatus.Failed)
                continue;

            var item = BuildItem(goal);
            if (goal.Status == GoalStatus.Completed && goal.LastEventAt >= landedCutoff)
            {
                landed.Add(item);
                continue;
            }

            if (goal.Status == GoalStatus.WaitingForHuman ||
                item.PercentComplete >= 80 ||
                item.Stage.Equals("accepting", StringComparison.Ordinal))
            {
                almostDone.Add(item);
                continue;
            }

            if (goal.Status is GoalStatus.Active or GoalStatus.Draft)
                active.Add(item);
        }

        var buckets = new StatusProjectionBuckets(active, almostDone, landed);
        var rendered = Render(buckets, input.OpenEscalationCount, input.EscalationThreadUrl, input.Now);
        return new StatusProjection(
            rendered,
            string.Equals(rendered, input.PreviousRenderedContent, StringComparison.Ordinal),
            buckets,
            input.OpenEscalationCount);
    }

    public static StatusProjectionInput BuildInput(
        IReadOnlyCollection<Goal> goals,
        int openEscalationCount,
        string? escalationThreadUrl,
        DateTimeOffset now,
        string? previousRenderedContent = null)
    {
        return new StatusProjectionInput(
            goals.Select(goal => new StatusProjectionGoal(
                    goal.Id.Value,
                    goal.Objective,
                    goal.Status,
                    goal.Tasks.Select(task => new StatusProjectionTask(task.RequiredRole, task.Status)).ToList(),
                    goal.Timeline.OrderByDescending(evt => evt.OccurredAt).FirstOrDefault()?.OccurredAt))
                .ToList(),
            openEscalationCount,
            escalationThreadUrl,
            now,
            previousRenderedContent);
    }

    private static StatusProjectionGoalItem BuildItem(StatusProjectionGoal goal)
    {
        var total = goal.Tasks.Count;
        var completed = goal.Tasks.Count(task => task.Status == WorkTaskStatus.Completed);
        var percent = total == 0 ? 0 : (int)Math.Round(completed * 100d / total);
        return new StatusProjectionGoalItem(
            goal.Id,
            goal.Objective,
            DetermineStage(goal),
            completed,
            total,
            percent,
            DetermineHealth(goal));
    }

    private static string DetermineStage(StatusProjectionGoal goal)
    {
        if (goal.Status == GoalStatus.Completed)
            return "accepting";
        if (goal.Status == GoalStatus.WaitingForHuman ||
            goal.Tasks.Any(task => task.Status == WorkTaskStatus.WaitingForHuman))
            return "waiting";

        var next = goal.Tasks.FirstOrDefault(task => task.Status != WorkTaskStatus.Completed);
        return next?.Role switch
        {
            AgentRole.Planner or AgentRole.Ideation => "planning",
            AgentRole.Researcher or AgentRole.Developer => "developing",
            AgentRole.Tester or AgentRole.Reviewer => "testing",
            _ => "accepting"
        };
    }

    private static string DetermineHealth(StatusProjectionGoal goal)
    {
        if (goal.Status == GoalStatus.WaitingForHuman ||
            goal.Tasks.Any(task => task.Status == WorkTaskStatus.WaitingForHuman))
            return "waiting for human";
        if (goal.Tasks.Any(task => task.Status == WorkTaskStatus.Failed))
            return "needs recovery";
        if (goal.Tasks.Any(task => task.Status == WorkTaskStatus.Running))
            return "running";
        if (goal.Tasks.Any(task => task.Status == WorkTaskStatus.Assigned))
            return "ready";
        return goal.Status == GoalStatus.Completed ? "complete" : "queued";
    }

    private static string Render(
        StatusProjectionBuckets buckets,
        int openEscalationCount,
        string? escalationThreadUrl,
        DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Progress**");
        sb.AppendLine($"Updated: {now:yyyy-MM-dd HH:mm} UTC");
        sb.Append("Open escalations: ").Append(openEscalationCount);
        if (!string.IsNullOrWhiteSpace(escalationThreadUrl))
            sb.Append(" (").Append(escalationThreadUrl).Append(')');
        sb.AppendLine();
        sb.AppendLine();
        AppendBucket(sb, "Active", buckets.Active);
        AppendBucket(sb, "Almost done", buckets.AlmostDone);
        AppendBucket(sb, "Landed (24h)", buckets.Landed);
        return sb.ToString().TrimEnd();
    }

    private static void AppendBucket(StringBuilder sb, string title, IReadOnlyList<StatusProjectionGoalItem> items)
    {
        sb.AppendLine($"**{title}**");
        if (items.Count == 0)
        {
            sb.AppendLine("- none");
            sb.AppendLine();
            return;
        }

        foreach (var item in items.Take(5))
        {
            sb.Append("- ")
                .Append(item.Id[..Math.Min(8, item.Id.Length)])
                .Append(' ')
                .Append(item.PercentComplete)
                .Append("% ")
                .Append(item.Stage)
                .Append(" / ")
                .Append(item.Health)
                .Append(": ")
                .Append(TrimObjective(item.Objective))
                .AppendLine();
        }

        sb.AppendLine();
    }

    private static string TrimObjective(string objective)
    {
        var trimmed = objective.Trim();
        return trimmed.Length <= 96 ? trimmed : trimmed[..93] + "...";
    }
}
