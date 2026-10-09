namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AuthorDraftTrackedEdits(IReadOnlyList<string> Paths)
{
    internal const string Reason = "tracked-edits";

    internal string Render() => string.Join(", ", Paths.Take(5)) +
        (Paths.Count > 5 ? $" (+{Paths.Count - 5} more)" : "");
}
