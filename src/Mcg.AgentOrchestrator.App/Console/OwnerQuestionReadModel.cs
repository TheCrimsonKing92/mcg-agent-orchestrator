using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerQuestionReadModel(
    IOrchestratorStateQueries state, string orchestratorDirectory) : IOwnerQuestionSource
{
    private readonly HashSet<string> _terminalIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuestionCandidate> _terminalRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QuestionCandidate> _terminalHolds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<OwnerQuestion, HiddenOwnerQuestion> _terminalHidden = [];

    public async Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken)).Live;

    public async Task<OwnerQuestionSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var metadata = await state.ListGoalMetadataAsync(cancellationToken);
        var terminal = metadata.Where(item => IsTerminal(item.Status)).ToArray();
        var ids = metadata.Where(item => !IsTerminal(item.Status)).Select(item => new GoalId(item.Id)).ToArray();
        var requests = await state.ListOpenHumanInputRequestsAsync(cancellationToken);
        var newTerminalIds = terminal.Select(item => item.Id).Where(id => !_terminalIds.Contains(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (newTerminalIds.Count > 0)
        {
            // Terminal questions are immutable for this session; only newly terminal goals extend the cache.
            var holds = await state.ListTerminalOwnerQuestionHoldsAsync(cancellationToken);
            foreach (var hold in holds.Where(hold => newTerminalIds.Contains(hold.GoalId)))
                _terminalHolds[hold.GoalId] = new QuestionCandidate(new OwnerQuestion(hold.Identity, hold.GoalId,
                    OwnerQuestionKind.StewardHold, StewardQuestionText(hold.Blocker)), hold.StartedAt);
            var terminalKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([],
                requests.Where(request => newTerminalIds.Contains(request.GoalId)).ToArray()));
            foreach (var request in terminalKernel.HumanInputRequests.Where(request => !request.IsCompleted))
                _terminalRequests[request.Id.Value] = RequestCandidate(request);
            var statuses = terminal.ToDictionary(item => item.Id, item =>
                Enum.Parse<GoalStatus>(item.Status, ignoreCase: true), StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in _terminalRequests.Values.Concat(_terminalHolds.Values)
                .Where(candidate => newTerminalIds.Contains(candidate.Question.GoalId)))
                _terminalHidden[candidate.Question] = new HiddenOwnerQuestion(candidate.Question,
                    TerminalReason(statuses[candidate.Question.GoalId])!);
            _terminalIds.UnionWith(newTerminalIds);
        }
        var kernel = ids.Length == 0 ? new AgentOrchestratorKernel() :
            await state.LoadGoalsAsync(ids, cancellationToken);
        var goals = kernel.Goals.ToDictionary(goal => goal.Id.Value, goal => goal.Status,
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in terminal) goals[item.Id] = Enum.Parse<GoalStatus>(item.Status, ignoreCase: true);
        var candidates = new List<QuestionCandidate>();
        // The hydrated kernel owns request repair/sweeping for non-terminal goals. Detached rows
        // supply terminal and orphan requests, while retaining main's stable request order and ties.
        var loadedIds = ids.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        var detached = requests.Where(request => !loadedIds.Contains(request.GoalId) &&
            !_terminalIds.Contains(request.GoalId)).ToArray();
        var detachedKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([], detached));
        var open = kernel.HumanInputRequests.Where(request => !request.IsCompleted)
            .Concat(detachedKernel.HumanInputRequests.Where(request => !request.IsCompleted))
            .Select(RequestCandidate).ToDictionary(candidate => candidate.Question.ItemId, StringComparer.Ordinal);
        foreach (var item in _terminalRequests) open[item.Key] = item.Value;
        var ordered = requests.Select(request => request.Id).Concat(open.Keys).Distinct(StringComparer.Ordinal);
        candidates.AddRange(ordered.Where(open.ContainsKey).Select(id => open[id]));
        foreach (var goal in kernel.Goals)
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
            if (_terminalHidden.TryGetValue(candidate.Question, out var cached))
            {
                hidden.Add(cached);
                continue;
            }
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

    private static string? TerminalReason(GoalStatus status) => status switch
    {
        GoalStatus.Completed => "goal-completed", GoalStatus.Cancelled => "goal-cancelled",
        GoalStatus.Superseded => "goal-superseded", _ => null
    };

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
