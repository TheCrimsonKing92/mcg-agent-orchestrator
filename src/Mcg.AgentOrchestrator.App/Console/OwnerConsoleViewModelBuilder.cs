using System.Collections.Immutable;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleViewModelBuilder(IOrchestratorStateQueries state,
    IOwnerQuestionSource questions, IConductorLiveness liveness, IOwnerGoalEpicLookup epics, TimeProvider clock)
{
    internal const int MaxActivityItems = 100;
    internal const int SummaryMaxLength = 120;
    private readonly Dictionary<string, int> _numbers = new(StringComparer.OrdinalIgnoreCase);

    internal async Task<OwnerConsoleViewModel> BuildAsync(OwnerConsoleViewInputs inputs,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await questions.ReadAsync(cancellationToken);
        var decisions = snapshot.Live.Select(question =>
        {
            if (!_numbers.TryGetValue(question.ItemId, out var number))
                _numbers.Add(question.ItemId, number = _numbers.Count + 1);
            return new OwnerConsoleDecision(question.ItemId, number, question.GoalId, Prefix(question.GoalId),
                question.Kind, Summary(question.Text), question.Text, question.BlastRadius,
                question.Confidence, question.ProposedDefault);
        }).ToImmutableArray();
        var metadata = await state.ListGoalMetadataAsync(cancellationToken);
        var ids = metadata.Where(item => Enum.TryParse<GoalStatus>(item.Status, true, out var status) && IsActive(status))
            .Select(item => new GoalId(item.Id)).ToArray();
        var board = ImmutableArray.CreateBuilder<OwnerConsoleBoardRow>();
        if (ids.Length > 0)
        {
            var kernel = await state.LoadGoalsAsync(ids, cancellationToken);
            foreach (var goal in kernel.Goals.Where(goal => ids.Contains(goal.Id) && IsActive(goal.Status)))
                board.Add(new(Prefix(goal.Id.Value), await epics.GetTitleAsync(goal.Id.Value, cancellationToken),
                    OwnerGoalTitle.From(goal.Objective), goal.Status.ToString(),
                    OwnerConsoleGoalDetail.Stage(goal),
                    Age(goal.Timeline.OrderByDescending(item => item.OccurredAt).FirstOrDefault()?.OccurredAt), goal.Id.Value));
        }
        var activity = inputs.RecentEvents.OrderByDescending(item => item.Timestamp)
            .Select(item => (Item: item, Tag: OwnerConsoleActivityPresentation.Classify(item)))
            .Where(pair => pair.Tag is not null).Take(MaxActivityItems)
            .Select(pair => new OwnerConsoleActivityItem(pair.Item.Timestamp, pair.Item.EventKind, pair.Tag!,
                Prefix(pair.Item.GoalId ?? string.Empty), pair.Item.Detail,
                OwnerGoalTitle.From(metadata.FirstOrDefault(goal => goal.Id == pair.Item.GoalId)?.Objective),
                OwnerConsoleActivityPresentation.Phrase(pair.Item, pair.Tag!))).ToImmutableArray();
        return new(new(liveness.IsRunning(), board.Count, decisions.Length, snapshot.Hidden.Count,
            Age(inputs.LastConductEvent), inputs.LandingsSinceOpen), decisions, board.ToImmutable(), activity);
    }

    internal static bool IsLanding(OwnerConductEvent item) => item.EventKind == "acceptance" &&
        item.Detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("result=passed", StringComparer.Ordinal);

    internal static string Prefix(string id) => id[..Math.Min(8, id.Length)];

    private TimeSpan? Age(DateTimeOffset? timestamp) => timestamp is null ? null :
        TimeSpan.FromTicks(Math.Max(0, (clock.GetUtcNow() - timestamp.Value).Ticks));

    private static bool IsActive(GoalStatus status) => status is not
        (GoalStatus.Parked or GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded);

    private static string Summary(string text)
    {
        var line = text.Split(['\r', '\n'], 2)[0];
        line = string.Join(" ", line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= SummaryMaxLength ? line : line[..(SummaryMaxLength - 1)] + "…";
    }
}
