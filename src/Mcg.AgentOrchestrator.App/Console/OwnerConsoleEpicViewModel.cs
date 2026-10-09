using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Rollups own membership and counts; hydration only supplies stage and reason text.
internal sealed record OwnerConsoleEpicViewModel(OwnerConsoleEpicWindow Window, DateTimeOffset? Since,
    IReadOnlyList<EpicProgressRollup> Epics, OwnerConsoleEpicViewModel.Detail? SelectedDetail = null,
    DateTimeOffset? LoadedAt = null)
{
    internal sealed record GoalLine(string Id, string Title, string Stage, string? Reason = null);
    internal sealed record Detail(PortfolioEpic Epic, IReadOnlyList<GoalLine> InFlight,
        IReadOnlyList<GoalLine> Landed, IReadOnlyList<GoalLine> Failed, IReadOnlyList<GoalLine> Parked);

    internal static DateTimeOffset? Cutoff(OwnerConsoleEpicWindow window, DateTimeOffset now)
    {
        if (window == OwnerConsoleEpicWindow.AllTime) return null;
        CliSinceArgument.TryParse(window == OwnerConsoleEpicWindow.Day ? "24h" : "7d", now, out var since);
        return since;
    }

    internal static async Task<Detail> DetailAsync(EpicProgressRollup row, DateTimeOffset? since,
        IOrchestratorStateQueries state, CancellationToken cancellationToken)
    {
        var inFlight = row.MemberGoals.Where(member => member.Bucket is EpicProgressBucket.Active or EpicProgressBucket.Verifying).ToArray();
        // Mirror Build's inclusive window predicate without changing or recomputing its counts.
        var landed = row.MemberGoals.Where(member => member.Bucket == EpicProgressBucket.Landed && InWindow(member)).ToArray();
        var failed = row.MemberGoals.Where(member => member.Bucket == EpicProgressBucket.Failed && InWindow(member)).ToArray();
        var parked = row.MemberGoals.Where(member => member.Bucket == EpicProgressBucket.Parked).ToArray();
        var ids = inFlight.Concat(failed).Concat(parked).Select(member => new GoalId(member.Id)).Distinct().ToArray();
        var goals = ids.Length == 0 ? new Dictionary<string, Goal>() :
            (await state.LoadGoalsAsync(ids, cancellationToken).ConfigureAwait(false)).Goals.ToDictionary(goal => goal.Id.Value);
        return new(row.Epic, inFlight.Select(Line).ToArray(), landed.Select(Line).ToArray(),
            failed.Select(member => Trouble(member, true)).ToArray(), parked.Select(member => Trouble(member, false)).ToArray());

        bool InWindow(EpicProgressMemberGoal member) => since is null || member.UpdatedAt >= since;
        GoalLine Line(EpicProgressMemberGoal member) => new(member.Id, member.Title,
            goals.TryGetValue(member.Id, out var goal) ? OwnerConsoleGoalDetail.Stage(goal) : "-");
        GoalLine Trouble(EpicProgressMemberGoal member, bool failure)
        {
            string? reason = null;
            if (goals.TryGetValue(member.Id, out var goal))
            {
                var events = goal.Timeline.OrderByDescending(item => item.OccurredAt).ToArray();
                if (failure) reason = events.FirstOrDefault(item => item.Kind == ProgressKind.TaskFailed)?.Message;
                if (string.IsNullOrWhiteSpace(reason))
                    reason = events.FirstOrDefault(item => item.Kind == ProgressKind.GoalPolicyDecision)?.Message;
            }
            return Line(member) with { Reason = string.IsNullOrWhiteSpace(reason) ? "no reason recorded" : reason };
        }
    }
}
