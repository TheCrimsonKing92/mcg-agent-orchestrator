namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private sealed partial record GateHeartbeatContext
    {
        internal string? RunClass { get; init; }
    }

    internal static string? ClassifyGateHeartbeatRunClassForTests(IAcceptanceRunExecutionContext? context) =>
        GateHeartbeatRunClass.Classify(context);
}
