namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GateHeartbeatRunClass
{
    internal const string Acceptance = "acceptance";
    internal const string FocusedEvidence = "focused-evidence";

    internal static string? Classify(IAcceptanceRunExecutionContext? context) => context switch
    {
        AcceptanceRunExecutionContextView view => Classify(view.Owner),
        AcceptanceAttemptExecutionOwner => Acceptance,
        AcceptanceFocusedVerificationOwner => FocusedEvidence,
        _ => null
    };

    internal static bool CountsAsAcceptanceOccupant(string? runClass) =>
        !string.Equals(runClass, FocusedEvidence, StringComparison.OrdinalIgnoreCase);
}
