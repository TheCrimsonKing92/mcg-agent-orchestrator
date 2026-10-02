namespace Mcg.AgentOrchestrator.Core;

public sealed record EscapeRecordOperatorIntentPayload(
    string GoalPrefix,
    string Reason,
    IReadOnlyList<string> EvidenceReferences,
    string? FoundByGoalPrefix,
    string WorkingDirectory);
