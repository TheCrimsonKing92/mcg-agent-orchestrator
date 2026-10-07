using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerQuestionReadModel(
    IOrchestratorStateQueries state, string orchestratorDirectory) : IOwnerQuestionSource
{
    public async Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken)).Live;

    public async Task<OwnerQuestionSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var metadata = await state.ListGoalMetadataAsync(cancellationToken);
        var ids = metadata.Select(item => new GoalId(item.Id)).ToArray();
        var kernel = ids.Length == 0 ? new AgentOrchestratorKernel() :
            await state.LoadGoalsAsync(ids, cancellationToken);
        var goals = kernel.Goals.ToDictionary(goal => goal.Id.Value, goal => goal.Status,
            StringComparer.OrdinalIgnoreCase);
        var candidates = new List<QuestionCandidate>();
        foreach (var request in kernel.HumanInputRequests.Where(item => !item.IsCompleted))
            candidates.Add(new QuestionCandidate(new OwnerQuestion(request.Id.Value, request.GoalId.Value,
                OwnerQuestionKind.HumanInput, request.Question,
                ProposedDefault: request.SuggestedDefaultAnswer), request.RequestedAt,
                HumanWaitPolicyDefaults.BlocksActiveWork(request.Kind)));
        foreach (var goal in kernel.Goals)
        {
            if (goal.CurrentHold is { } hold &&
                hold.State.Equals("steward-owner-question", StringComparison.OrdinalIgnoreCase))
                candidates.Add(new QuestionCandidate(new OwnerQuestion(hold.Identity, goal.Id.Value,
                    OwnerQuestionKind.StewardHold, StewardQuestionText(hold.Blocker)), hold.StartedAt));
        }
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
