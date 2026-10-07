namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    internal static string? ClassifyGateHeartbeatRunClassForTests(IAcceptanceRunExecutionContext? context) =>
        GateHeartbeatRunClass.Classify(context);

    internal static string? ResolveGateHeartbeatRunIdForTests(IAcceptanceRunExecutionContext? context) =>
        GateHeartbeatRunClass.ResolveRunId(context);
}
