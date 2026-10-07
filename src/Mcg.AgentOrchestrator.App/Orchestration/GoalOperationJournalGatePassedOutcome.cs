namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalOperationJournalGatePassedOutcome
{
    internal const string Value = "gate-passed";

    internal static bool Matches(GoalOperationJournalEntry entry) =>
        string.Equals(entry.AcceptanceOutcome, Value, StringComparison.OrdinalIgnoreCase);
}
