namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private sealed partial record GateHeartbeatContext
    {
        internal string? RunClass { get; init; }
        internal string? RunId { get; init; }
    }

    internal static string? ClassifyGateHeartbeatRunClassForTests(IAcceptanceRunExecutionContext? context) =>
        GateHeartbeatRunClass.Classify(context);

    internal static string? ResolveGateHeartbeatRunIdForTests(IAcceptanceRunExecutionContext? context) =>
        GateHeartbeatRunClass.ResolveRunId(context);
}
