namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerDecisionEntry(string ItemId, string GoalId, string GoalPrefix,
    OwnerQuestionKind Kind, string Text, string? BlastRadius, string? Confidence,
    string? ProposedDefault, DateTimeOffset? RaisedAt);
