using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerQuestionReadModel(
    IOrchestratorStateQueries state, string orchestratorDirectory) : IOwnerQuestionSource
{
    private readonly Dictionary<string, string> _terminalUpdatedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuestionCandidate> _terminalHolds = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken)).Live;

    public async Task<OwnerQuestionSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        await ReadAsync(await state.ListGoalMetadataAsync(cancellationToken), cancellationToken);

    internal async Task<OwnerQuestionSnapshot> ReadAsync(
        IReadOnlyList<GoalSummary> metadata, CancellationToken cancellationToken)
    {
        var terminal = metadata.Where(item => IsTerminal(item.Status)).ToArray();
        var ids = metadata.Where(item => !IsTerminal(item.Status)).Select(item => new GoalId(item.Id)).ToArray();
        var requests = await state.ListOpenHumanInputRequestsAsync(cancellationToken);
        var terminalIds = terminal.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _terminalUpdatedAt.Keys.Where(id => !terminalIds.Contains(id)).ToArray())
            _terminalUpdatedAt.Remove(id);
        foreach (var id in _terminalHolds.Keys.Where(id => !terminalIds.Contains(id)).ToArray())
            _terminalHolds.Remove(id);
        var changedTerminalIds = terminal.Where(item =>
                !_terminalUpdatedAt.TryGetValue(item.Id, out var updatedAt) || updatedAt != item.UpdatedAt)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (changedTerminalIds.Count > 0)
        {
            // Unchanged terminal goals reuse the session cache; changed rows replace even removed holds.
            var holds = await state.ListTerminalOwnerQuestionHoldsAsync(
                changedTerminalIds.Select(id => new GoalId(id)).ToArray(), cancellationToken);
            foreach (var id in changedTerminalIds) _terminalHolds.Remove(id);
            foreach (var hold in holds.Where(hold => changedTerminalIds.Contains(hold.GoalId)))
                _terminalHolds[hold.GoalId] = new QuestionCandidate(new OwnerQuestion(hold.Identity, hold.GoalId,
                    OwnerQuestionKind.StewardHold, StewardQuestionText(hold.Blocker)), hold.StartedAt);
            foreach (var item in terminal.Where(item => changedTerminalIds.Contains(item.Id)))
                _terminalUpdatedAt[item.Id] = item.UpdatedAt;
        }
        var kernel = ids.Length == 0 ? new AgentOrchestratorKernel() :
            await state.LoadGoalsAsync(ids, cancellationToken);
        var loadedIds = ids.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        var loadedGoals = kernel.Goals.Where(goal => loadedIds.Contains(goal.Id.Value)).ToArray();
        var goals = loadedGoals.ToDictionary(goal => goal.Id.Value, goal => goal.Status,
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in terminal) goals[item.Id] = Enum.Parse<GoalStatus>(item.Status, ignoreCase: true);
        var candidates = new List<QuestionCandidate>();
        // The hydrated kernel owns request repair/sweeping for non-terminal goals. Detached rows
        // supply terminal and orphan requests, while retaining main's stable request order and ties.
        var detached = requests.Where(request => !loadedIds.Contains(request.GoalId)).ToArray();
        var detachedKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([], detached));
        var open = kernel.HumanInputRequests.Where(request => !request.IsCompleted && loadedIds.Contains(request.GoalId.Value))
            .Concat(detachedKernel.HumanInputRequests.Where(request => !request.IsCompleted))
            .Select(RequestCandidate).ToDictionary(candidate => candidate.Question.ItemId, StringComparer.Ordinal);
        var ordered = requests.Select(request => request.Id).Concat(open.Keys).Distinct(StringComparer.Ordinal);
        candidates.AddRange(ordered.Where(open.ContainsKey).Select(id => open[id]));
        foreach (var goal in loadedGoals)
        {
            if (goal.CurrentHold is { } hold &&
                hold.State.Equals("steward-owner-question", StringComparison.OrdinalIgnoreCase))
                candidates.Add(new QuestionCandidate(new OwnerQuestion(hold.Identity, goal.Id.Value,
                    OwnerQuestionKind.StewardHold, StewardQuestionText(hold.Blocker)), hold.StartedAt));
        }
        candidates.AddRange(_terminalHolds.Values);
        var path = Path.Combine(orchestratorDirectory, "collaboration-items.db");
        if (File.Exists(path))
        {
            var store = CollaborationItemStore.OpenExisting(orchestratorDirectory);
            var items = await store.GetAttentionQueueAsync(cancellationToken);
            foreach (var item in items.Where(item =>
                item.GoalId is not null &&
                item.CorrelationKey?.StartsWith("spec-clarification:", StringComparison.Ordinal) == true &&
                !CollaborationItemLifecycle.IsTerminal(item.Status)))
            {
                candidates.Add(new QuestionCandidate(new OwnerQuestion(item.Id, item.GoalId!,
                    OwnerQuestionKind.Clarification,
                    Field(item.Body, "Question:") ?? item.Subject,
                    Field(item.Body, "Blast radius:"),
                    Field(item.Body, "Refiner confidence:"),
                    Field(item.Body, "Proposed default:") ?? Field(item.Body, "Default:")), item.RaisedAt));
            }
        }

        var eligible = new List<QuestionCandidate>();
        var hidden = new List<HiddenOwnerQuestion>();
        foreach (var candidate in candidates)
        {
            var reason = !goals.TryGetValue(candidate.Question.GoalId, out var status) ? "goal-missing" :
                status switch
                {
                    GoalStatus.Completed => "goal-completed",
                    GoalStatus.Cancelled => "goal-cancelled",
                    GoalStatus.Superseded => "goal-superseded",
                    _ => candidate.BlocksActiveWork ? null : "non-blocking-kind"
                };
            if (reason is null) eligible.Add(candidate);
            else hidden.Add(new HiddenOwnerQuestion(candidate.Question, reason));
        }

        var seen = new HashSet<(string GoalId, string Text)>();
        var live = new HashSet<OwnerQuestion>();
        foreach (var candidate in eligible.OrderBy(candidate => candidate.CreatedAt))
        {
            var question = candidate.Question;
            if (seen.Add((question.GoalId.ToUpperInvariant(), question.Text))) live.Add(question);
            else hidden.Add(new HiddenOwnerQuestion(question, "duplicate"));
        }
        return new OwnerQuestionSnapshot(
            eligible.Select(candidate => candidate.Question).Where(live.Contains).ToArray(), hidden);
    }

    private sealed record QuestionCandidate(
        OwnerQuestion Question, DateTimeOffset CreatedAt, bool BlocksActiveWork = true);

    private static bool IsTerminal(string status) =>
        Enum.TryParse<GoalStatus>(status, ignoreCase: true, out var parsed) &&
        parsed is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded;

    private static QuestionCandidate RequestCandidate(HumanInputRequest request) =>
        new(new OwnerQuestion(request.Id.Value, request.GoalId.Value, OwnerQuestionKind.HumanInput,
            request.Question, ProposedDefault: request.SuggestedDefaultAnswer), request.RequestedAt,
            HumanWaitPolicyDefaults.BlocksActiveWork(request.Kind));

    internal static string? Field(string body, string label)
    {
        using var reader = new StringReader(body);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[label.Length..].Trim();
                return value.Length == 0 ? null : value;
            }
        }
        return null;
    }

    private static string StewardQuestionText(string blocker)
    {
        const string marker = "question=";
        var start = blocker.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return blocker;
        start += marker.Length;
        var end = blocker.IndexOf(" evidence=[", start, StringComparison.Ordinal);
        return (end < 0 ? blocker[start..] : blocker[start..end]).Trim();
    }
}
