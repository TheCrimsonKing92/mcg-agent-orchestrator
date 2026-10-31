namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerDecisionRead(OwnerQuestionSnapshot Snapshot,
    IReadOnlyList<OwnerDecisionEntry> Entries, IReadOnlyDictionary<string, int> HiddenByReason);
