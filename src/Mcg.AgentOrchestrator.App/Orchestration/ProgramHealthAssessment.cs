namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ProgramHealthAssessment(IReadOnlyList<string> Conditions, string TopBlocker,
    int MinutesSinceLanding, int CurrentReceipts, int OverThreshold, int VerifiedGoals,
    int TopBlockerMinutes, string Diagnosis)
{
    internal bool Tripped => Conditions.Count > 0;
    internal string Key => Tripped ? string.Join(",", Conditions) + "|" + TopBlocker : string.Empty;
}
