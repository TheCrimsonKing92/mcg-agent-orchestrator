using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Each goal membership belongs to exactly one bucket, including absent goal rows.
internal static class EpicProgressReadModel
{
    internal static EpicProgressBucket BucketOf(GoalStatus status) => status switch
    {
        GoalStatus.Active or GoalStatus.WaitingForHuman or GoalStatus.Draft => EpicProgressBucket.Active,
        GoalStatus.Verifying => EpicProgressBucket.Verifying,
        GoalStatus.Verified => EpicProgressBucket.Verified,
        GoalStatus.Failed or GoalStatus.AcceptanceFailed => EpicProgressBucket.Failed,
        GoalStatus.Parked => EpicProgressBucket.Parked,
        GoalStatus.Completed => EpicProgressBucket.Landed,
        GoalStatus.Cancelled or GoalStatus.Superseded => EpicProgressBucket.Closed,
        _ => EpicProgressBucket.Active
    };

    internal static IReadOnlyList<EpicProgressRollup> Build(
        IReadOnlyList<PortfolioEpic> epics,
        IReadOnlyList<PortfolioProject> projects,
        IReadOnlyList<PortfolioEpicMember> members,
        IReadOnlyList<GoalSummary> goals,
        IReadOnlyList<BacklogItem> backlog)
    {
        var projectsById = projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var goalsById = goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
        var backlogById = backlog.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var membersByEpic = members.ToLookup(member => member.EpicId, StringComparer.Ordinal);
        return epics.Select(epic =>
        {
            var epicMembers = membersByEpic[epic.Id].ToArray();
            var memberGoals = epicMembers.Where(member => member.Kind == PortfolioMemberKind.Goal)
                .Select(member => ToMember(member.MemberId, goalsById.GetValueOrDefault(member.MemberId)))
                .OrderBy(member => GroupOf(member.Bucket))
                .ThenByDescending(member => member.UpdatedAt)
                .ThenBy(member => member.Id, StringComparer.Ordinal).ToArray();
            var counts = memberGoals.ToLookup(member => member.Bucket);
            var backlogMembers = epicMembers.Where(member => member.Kind == PortfolioMemberKind.BacklogItem).ToArray();
            var done = backlogMembers.Count(member => backlogById.TryGetValue(member.MemberId, out var item)
                && item.Status is BacklogItemStatus.Done or BacklogItemStatus.Superseded);
            return new EpicProgressRollup(epic,
                epic.ProjectId is null ? null : projectsById.GetValueOrDefault(epic.ProjectId),
                epicMembers, memberGoals, memberGoals.Length, backlogMembers.Length,
                counts[EpicProgressBucket.Active].Count(), counts[EpicProgressBucket.Verified].Count(),
                counts[EpicProgressBucket.Parked].Count(), counts[EpicProgressBucket.Landed].Count(),
                // Metadata UpdatedAt replaces the old hydrated timeline-event source.
                memberGoals.Select(member => member.UpdatedAt).Max(),
                counts[EpicProgressBucket.Verifying].Count(), counts[EpicProgressBucket.Failed].Count(),
                counts[EpicProgressBucket.Closed].Count(), counts[EpicProgressBucket.Missing].Count(),
                backlogMembers.Length - done, done);
        }).OrderBy(row => row.Project?.Title ?? "~")
            .ThenBy(row => row.Epic.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool IsEpicGoalsListing(IReadOnlyList<string> args) =>
        args.Count >= 2 && args[0].Equals("goals", StringComparison.OrdinalIgnoreCase)
        && !args[1].Equals("subscribe", StringComparison.OrdinalIgnoreCase)
        && args.Skip(1).Any(arg => arg.Equals("--epic", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("--epic=", StringComparison.OrdinalIgnoreCase));

    private static EpicProgressMemberGoal ToMember(string id, GoalSummary? goal)
    {
        if (goal is null)
            return new(id, EpicProgressBucket.Missing, "Missing", null, string.Empty);
        var bucket = Enum.TryParse<GoalStatus>(goal.Status, out var status) ? BucketOf(status) : EpicProgressBucket.Active;
        var updated = DateTimeOffset.TryParse(goal.UpdatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
            ? timestamp : (DateTimeOffset?)null;
        using var reader = new StringReader(goal.Objective);
        var title = reader.ReadLine() ?? string.Empty;
        return new(id, bucket, goal.Status, updated, title.Length > 100 ? title[..100] : title);
    }

    private static int GroupOf(EpicProgressBucket bucket) => bucket switch
    {
        EpicProgressBucket.Active or EpicProgressBucket.Verifying or EpicProgressBucket.Verified or EpicProgressBucket.Failed => 0,
        EpicProgressBucket.Parked => 1,
        _ => 2
    };
}
