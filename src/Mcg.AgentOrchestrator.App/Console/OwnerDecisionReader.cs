namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerDecisionReader
{
    internal static async Task<OwnerDecisionRead> ReadAsync(IOwnerQuestionSource questions,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await questions.ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var entries = snapshot.Live.Select(question => new OwnerDecisionEntry(question.ItemId, question.GoalId,
            OwnerConsoleViewModelBuilder.Prefix(question.GoalId), question.Kind, question.Text,
            question.BlastRadius, question.Confidence, question.ProposedDefault,
            snapshot.RaisedAt?.TryGetValue(question.ItemId, out var raisedAt) == true ? raisedAt : null)).ToArray();
        var hiddenByReason = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in snapshot.Hidden)
            hiddenByReason[item.Reason] = hiddenByReason.GetValueOrDefault(item.Reason) + 1;
        return new(snapshot, entries, hiddenByReason);
    }
}
