using System.Collections.Immutable;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleViewModelBuilder(IOrchestratorStateQueries state,
    IOwnerQuestionSource questions, IConductorLiveness liveness, IOwnerGoalEpicLookup epics, TimeProvider clock,
    Func<OwnerConductEvent, OwnerActivityTestEvidence?>? testEvidence = null, Func<string, string?>? gateMotion = null)
{
    internal const int MaxActivityItems = 100;
    private readonly Dictionary<string, int> _numbers = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GoalSummary> _metadata = [];
    private IReadOnlyDictionary<string, AgentRole> _roles = new Dictionary<string, AgentRole>();
    private IReadOnlyDictionary<string, Goal> _goals = new Dictionary<string, Goal>();
    private readonly object _observationGate = new();
    private readonly OwnerNeedsYouLedger _attention = new();
    private readonly Dictionary<string, bool> _previousHolds = new();
    private readonly List<OwnerConductEvent> _observed = [];

    internal async Task<OwnerConsoleViewModel> BuildAsync(OwnerConsoleViewInputs inputs,
        CancellationToken cancellationToken = default)
    {
        var (decisions, hidden) = await ReadDecisionsAsync(cancellationToken);
        var model = await BuildBoardAsync(cancellationToken);
        return WithActivity(WithDecisions(model, decisions, hidden), inputs);
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
            _attention.Observe(snapshot.Live, clock.GetUtcNow());
        }
        return (decisions, snapshot.Hidden.Count);
    }

    internal async Task<OwnerConsoleViewModel> BuildBoardAsync(CancellationToken cancellationToken = default)
    {
        var metadata = await state.ListGoalMetadataAsync(cancellationToken);
        _metadata = metadata;
        var now = clock.GetUtcNow();
        var ids = metadata.Where(item => Enum.TryParse<GoalStatus>(item.Status, true, out var status) &&
                (IsActive(status) || status is GoalStatus.Failed or GoalStatus.Parked &&
                    (!DateTimeOffset.TryParse(item.UpdatedAt, out var updated) || now - updated <= TimeSpan.FromHours(24))))
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
            var goals = new Dictionary<string, Goal>(_goals);
            foreach (var goal in kernel.Goals) goals[goal.Id.Value] = goal;
            _goals = goals;
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
            var ranked = kernel.Goals.Where(goal => ids.Contains(goal.Id))
                .Select(goal => (Goal: goal, Key: OwnerConsoleBoardSortKey.For(goal)))
                .Where(item => IsActive(item.Goal.Status) || item.Key.Rank == OwnerConsoleAttention.RecentTerminal &&
                    item.Key.Since is { } at && now - at <= TimeSpan.FromHours(24)).OrderBy(item => item.Key).ToArray();
            var queuePosition = 0;
            foreach (var (goal, key) in ranked)
            {
                var stage = OwnerConsoleStageDescriber.Describe(goal, key,
                    key.Rank == OwnerConsoleAttention.WaitingForGate ? ++queuePosition : null, now);
                if (key.Rank == OwnerConsoleAttention.GateRunning && gateMotion?.Invoke(goal.Id.Value) is { } motion &&
                    !string.IsNullOrWhiteSpace(motion)) stage += ", " + motion;
                board.Add(new(Prefix(goal.Id.Value), await epics.GetTitleAsync(goal.Id.Value, cancellationToken),
                    OwnerGoalTitle.Full(goal.Objective), goal.Status.ToString(), stage,
                    Age(goal.Timeline.MaxBy(item => item.OccurredAt)?.OccurredAt), goal.Id.Value,
                    key.Rank == OwnerConsoleAttention.RecentTerminal) { SortKey = key, StageWithoutQuestion = stage });
            }
        }
        return new(new(liveness.IsRunning(), board.Count(row => !row.Dimmed), 0, 0, null, 0,
            board.Count(row => row.Dimmed && row.State == nameof(GoalStatus.Failed))), [], board.ToImmutable(), []);
    }

    internal OwnerConsoleViewModel WithDecisions(OwnerConsoleViewModel model,
        ImmutableArray<OwnerConsoleDecision> decisions, int hidden)
    {
        var questionGoals = decisions.Select(item => item.GoalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = model.Board.Select(row => row with
        {
            StageWithoutQuestion = row.StageWithoutQuestion ?? row.Stage,
            Stage = !row.Dimmed && questionGoals.Contains(row.GoalId) ? OwnerConsoleStageDescriber.OwnerQuestion :
                row.StageWithoutQuestion ?? row.Stage
        }).OrderBy(row => row.SortKey.WithOwnerQuestion(questionGoals.Contains(row.GoalId))).ToImmutableArray();
        return model with { Board = rows, Decisions = decisions,
            Status = model.Status with { LiveDecisions = decisions.Length, HiddenQuestions = hidden } };
    }

    internal OwnerConsoleViewModel WithActivity(OwnerConsoleViewModel model, OwnerConsoleViewInputs inputs)
    {
        OwnerConductEvent[] observed;
        OwnerAttentionObservation[] attention;
        lock (_observationGate) { observed = _observed.ToArray(); attention = _attention.Snapshot(); }
        var activity = OwnerActivityNarrator.Narrate(inputs.RecentEvents.Concat(observed)
            .Select(item => OwnerGoalLifecycleEvent.WithRole(item, _roles)), Title, testEvidence, attention, item =>
            {
                var matches = _goals.Values.Where(goal => item.GoalId is not null &&
                    goal.Id.Value.StartsWith(item.GoalId, StringComparison.OrdinalIgnoreCase)).ToArray();
                return matches.Length == 1 ? OwnerConsoleGoalDetailFormatter.Finding(matches[0], item) : null;
            }).ToImmutableArray();
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
        return line;
    }
}
