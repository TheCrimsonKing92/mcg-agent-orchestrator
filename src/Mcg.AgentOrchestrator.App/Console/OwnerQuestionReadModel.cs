using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerQuestionReadModel(
    IOrchestratorStateQueries state, string orchestratorDirectory) : IOwnerQuestionSource
{
    public async Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken)
    {
        var metadata = await state.ListGoalMetadataAsync(cancellationToken);
        var ids = metadata.Select(item => new GoalId(item.Id)).ToArray();
        var kernel = ids.Length == 0 ? new AgentOrchestratorKernel() :
            await state.LoadGoalsAsync(ids, cancellationToken);
        var open = new List<OwnerQuestion>();
        foreach (var request in kernel.HumanInputRequests.Where(item => !item.IsCompleted))
            open.Add(new OwnerQuestion(request.Id.Value, request.GoalId.Value,
                OwnerQuestionKind.HumanInput, request.Question,
                ProposedDefault: request.SuggestedDefaultAnswer));
        foreach (var goal in kernel.Goals)
        {
            if (goal.CurrentHold is { } hold &&
                hold.State.Equals("steward-owner-question", StringComparison.OrdinalIgnoreCase))
                open.Add(new OwnerQuestion(hold.Identity, goal.Id.Value,
                    OwnerQuestionKind.StewardHold, StewardQuestionText(hold.Blocker)));
        }
        var path = Path.Combine(orchestratorDirectory, "collaboration-items.db");
        if (!File.Exists(path)) return open;
        var store = CollaborationItemStore.OpenExisting(orchestratorDirectory);
        var items = await store.GetAttentionQueueAsync(cancellationToken);
        foreach (var item in items.Where(item =>
            item.GoalId is not null &&
            item.CorrelationKey?.StartsWith("spec-clarification:", StringComparison.Ordinal) == true &&
            !CollaborationItemLifecycle.IsTerminal(item.Status)))
        {
            open.Add(new OwnerQuestion(item.Id, item.GoalId!, OwnerQuestionKind.Clarification,
                Field(item.Body, "Question:") ?? item.Subject,
                Field(item.Body, "Blast radius:"),
                Field(item.Body, "Refiner confidence:"),
                Field(item.Body, "Proposed default:") ?? Field(item.Body, "Default:")));
        }
        return open;
    }

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
