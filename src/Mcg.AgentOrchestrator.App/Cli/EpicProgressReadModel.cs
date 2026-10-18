using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
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
        IReadOnlyList<BacklogItem> backlog,
        DateTimeOffset? since = null)
    {
        var projectsById = projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var goalsById = goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
        var backlogById = backlog.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var membersByEpic = members.ToLookup(member => member.EpicId, StringComparer.Ordinal);
        return epics.Select(epic =>
        {
            var epicMembers = membersByEpic[epic.Id].ToArray();
            var memberGoals = epicMembers.Where(member => member.Kind == PortfolioMemberKind.Goal)
                .DistinctBy(member => member.MemberId, StringComparer.Ordinal)
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
                backlogMembers.Length - done, done,
                since is null ? null : memberGoals.Count(member => member.CreatedAt >= since),
                since is null ? null : memberGoals.Count(member => member.UpdatedAt >= since),
                since is null ? null : memberGoals.Count(member => member.Bucket == EpicProgressBucket.Failed && member.UpdatedAt >= since),
                since is null ? null : memberGoals.Count(member => member.LandedInWindow(since)));
        }).OrderBy(row => row.Project?.Title ?? "~")
            .ThenBy(row => row.Epic.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool IsEpicGoalsListing(IReadOnlyList<string> args) =>
        args.Count >= 2 && args[0].Equals("goals", StringComparison.OrdinalIgnoreCase)
        && !args[1].Equals("subscribe", StringComparison.OrdinalIgnoreCase)
        && args.Skip(1).Any(arg => arg.Equals("--epic", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("--epic=", StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<EpicProgressRollup> Load(OrchestratorWorkspace workspace, DateTimeOffset? since = null,
        bool includeReasons = false)
    {
        if (!File.Exists(workspace.PortfolioStorePath))
            return [];
        var store = PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath);
        return Load(workspace, store, store.ListEpicsAsync().GetAwaiter().GetResult(), since, includeReasons);
    }

    internal static EpicProgressRollup LoadEpic(OrchestratorWorkspace workspace, string reference, bool includeReasons = false)
    {
        return EpicDetailReadSnapshot.ReadAsync(workspace, reference, null, CancellationToken.None,
            includeReasons: includeReasons, includeDetailGoals: false).GetAwaiter().GetResult().Row;
    }

    internal static IReadOnlyList<GoalSummary> ListMemberGoals(OrchestratorWorkspace workspace, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Usage: goals [--epic <id-or-title>]");
        var (store, epic) = ResolveEpic(workspace, reference);
        var ids = store.ListEpicMembersAsync(epic.Id).GetAwaiter().GetResult()
            .Where(member => member.Kind == PortfolioMemberKind.Goal)
            .Select(member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        return LoadGoalMetadata(workspace).Where(goal => ids.Contains(goal.Id)).ToArray();
    }

    private static (PortfolioStore Store, PortfolioEpic Epic) ResolveEpic(OrchestratorWorkspace workspace, string reference)
    {
        if (!File.Exists(workspace.PortfolioStorePath))
            throw new InvalidOperationException($"Epic '{reference}' was not found.");
        var store = PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath);
        var epic = store.ResolveEpicAsync(reference).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException($"Epic '{reference}' was not found.");
        return (store, epic);
    }

    private static IReadOnlyList<EpicProgressRollup> Load(
        OrchestratorWorkspace workspace, PortfolioStore store, IReadOnlyList<PortfolioEpic> epics,
        DateTimeOffset? since = null, bool includeReasons = false)
    {
        var projects = store.ListProjectsAsync().GetAwaiter().GetResult();
        var members = epics.SelectMany(epic => store.ListEpicMembersAsync(epic.Id).GetAwaiter().GetResult()).ToArray();
        var backlog = BoardFillBacklogSnapshot.Read(workspace.BacklogStorePath);
        var goals = LoadGoalMetadata(workspace, includeTerminalCreatedAt: since is not null);
        var plans = EpicPlanStatusReader.Load(workspace, epics, includeReasons, backlog, goals, members);
        return Build(epics, projects, members, goals, backlog, since)
            .Select(row => row with { Plan = plans[row.Epic.Id] }).ToArray();
    }

    internal static IReadOnlyList<GoalSummary> LoadGoalMetadata(OrchestratorWorkspace workspace, bool includeTerminalCreatedAt = false)
    {
        if (!File.Exists(workspace.SqliteStatePath))
            return [];
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
        return (includeTerminalCreatedAt
            ? repository.ListGoalMetadataAsync(includeTerminalCreatedAt: true)
            : repository.ListGoalMetadataAsync()).GetAwaiter().GetResult();
    }

    private static EpicProgressMemberGoal ToMember(string id, GoalSummary? goal)
    {
        if (goal is null)
            return new(id, EpicProgressBucket.Missing, "Missing", null, string.Empty);
        var bucket = Enum.TryParse<GoalStatus>(goal.Status, out var status) ? BucketOf(status) : EpicProgressBucket.Active;
        var updated = DateTimeOffset.TryParse(goal.UpdatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
            ? timestamp : (DateTimeOffset?)null;
        var title = OwnerGoalTitle.Display(goal.Objective);
        return new(id, bucket, goal.Status, updated, title.Length > 100 ? title[..100] : title,
            goal.CreatedAt, goal.PromotionFact?.GoalId == id ? goal.PromotionFact : null);
    }

    private static int GroupOf(EpicProgressBucket bucket) => bucket switch
    {
        EpicProgressBucket.Active or EpicProgressBucket.Verifying or EpicProgressBucket.Verified or EpicProgressBucket.Failed => 0,
        EpicProgressBucket.Parked => 1,
        _ => 2
    };
}
