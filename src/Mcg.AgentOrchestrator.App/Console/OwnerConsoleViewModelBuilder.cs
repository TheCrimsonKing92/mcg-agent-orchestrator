using System.Collections.Immutable;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleViewModelBuilder(IOrchestratorStateQueries state,
    IOwnerQuestionSource questions, IConductorLiveness liveness, IOwnerGoalEpicLookup epics, TimeProvider clock,
    Func<OwnerConductEvent, OwnerActivityTestEvidence?>? testEvidence = null)
{
    internal const int MaxActivityItems = 100;
    internal const int SummaryMaxLength = 120;
    private readonly Dictionary<string, int> _numbers = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GoalSummary> _metadata = [];
    private IReadOnlyDictionary<string, AgentRole> _roles = new Dictionary<string, AgentRole>();
    private readonly object _observationGate = new();
    private Dictionary<string, OwnerQuestion>? _previousQuestions;
    private readonly Dictionary<string, bool> _previousHolds = new();
    private readonly List<OwnerConductEvent> _observed = [];

    internal async Task<OwnerConsoleViewModel> BuildAsync(OwnerConsoleViewInputs inputs,
        CancellationToken cancellationToken = default)
    {
        var (decisions, hidden) = await ReadDecisionsAsync(cancellationToken);
        var model = await BuildBoardAsync(cancellationToken);
        return WithActivity(model with { Decisions = decisions, Status = model.Status with
            { LiveDecisions = decisions.Length, HiddenQuestions = hidden } }, inputs);
    }

    internal async Task<(ImmutableArray<OwnerConsoleDecision> Decisions, int Hidden)> ReadDecisionsAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await questions.ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var decisions = snapshot.Live.Select(question =>
        {
            if (!_numbers.TryGetValue(question.ItemId, out var number))
                _numbers.Add(question.ItemId, number = _numbers.Count + 1);
            return new OwnerConsoleDecision(question.ItemId, number, question.GoalId, Prefix(question.GoalId),
                question.Kind, Summary(question.Text), question.Text, question.BlastRadius,
                question.Confidence, question.ProposedDefault);
        }).ToImmutableArray();
        lock (_observationGate)
        {
            var live = snapshot.Live.ToDictionary(question => question.ItemId);
            if (_previousQuestions is not null)
                foreach (var old in _previousQuestions.Values.Where(question => !live.ContainsKey(question.ItemId)))
                    Observe(new(clock.GetUtcNow(), "owner-question-resolved", old.GoalId, "question=" + old.Text));
            _previousQuestions = live;
        }
        return (decisions, snapshot.Hidden.Count);
    }

    internal async Task<OwnerConsoleViewModel> BuildBoardAsync(CancellationToken cancellationToken = default)
    {
        var metadata = await state.ListGoalMetadataAsync(cancellationToken);
        _metadata = metadata;
        var ids = metadata.Where(item => Enum.TryParse<GoalStatus>(item.Status, true, out var status) && IsActive(status))
            .Select(item => new GoalId(item.Id)).ToArray();
        var board = ImmutableArray.CreateBuilder<OwnerConsoleBoardRow>();
        if (ids.Length > 0)
        {
            var kernel = await state.LoadGoalsAsync(ids, cancellationToken);
            // Retain roles for the tail's final read after a goal leaves the board.
            var roles = new Dictionary<string, AgentRole>(_roles);
            foreach (var task in kernel.Goals.Where(goal => ids.Contains(goal.Id)).SelectMany(goal => goal.Tasks))
                roles[task.Id.Value] = task.RequiredRole;
            _roles = roles;
            lock (_observationGate)
            {
                foreach (var goal in kernel.Goals)
                {
                    var hasHold = goal.CurrentHold is not null;
                    if (_previousHolds.GetValueOrDefault(goal.Id.Value) && !hasHold)
                        Observe(new(clock.GetUtcNow(), "owner-hold-cleared", goal.Id.Value, ""));
                    _previousHolds[goal.Id.Value] = hasHold;
                }
            }
            foreach (var goal in kernel.Goals.Where(goal => ids.Contains(goal.Id) && IsActive(goal.Status)))
                board.Add(new(Prefix(goal.Id.Value), await epics.GetTitleAsync(goal.Id.Value, cancellationToken),
                    OwnerGoalTitle.From(goal.Objective), goal.Status.ToString(),
                    OwnerConsoleGoalDetail.Stage(goal),
                    Age(goal.Timeline.OrderByDescending(item => item.OccurredAt).FirstOrDefault()?.OccurredAt), goal.Id.Value));
        }
        return new(new(liveness.IsRunning(), board.Count, 0, 0, null, 0), [], board.ToImmutable(), []);
    }

    internal OwnerConsoleViewModel WithActivity(OwnerConsoleViewModel model, OwnerConsoleViewInputs inputs)
    {
        OwnerConductEvent[] observed;
        lock (_observationGate) observed = _observed.ToArray();
        var activity = OwnerActivityNarrator.Narrate(inputs.RecentEvents.Concat(observed)
            .Select(item => OwnerGoalLifecycleEvent.WithRole(item, _roles)), Title, testEvidence).ToImmutableArray();
        return model with { Activity = activity, Status = model.Status with
            { LastEventAge = Age(inputs.LastConductEvent), LandedToday = inputs.LandedToday } };
    }

    private void Observe(OwnerConductEvent item)
    {
        _observed.Add(item);
        if (_observed.Count > MaxActivityItems) _observed.RemoveAt(0);
    }

    internal OwnerConductEvent EnrichEvent(OwnerConductEvent item) => OwnerGoalLifecycleEvent.WithRole(item, _roles);

    private string Title(string? id)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;
        var exact = _metadata.FirstOrDefault(goal => goal.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var matches = _metadata.Where(goal => goal.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToArray();
        return OwnerGoalTitle.Full((exact ?? (matches.Length == 1 ? matches[0] : null))?.Objective);
    }

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
