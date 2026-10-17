namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorInvariantThresholds
{
    internal static readonly TimeSpan GateIdleWhileReady = TimeSpan.FromMinutes(10);
    internal const int RepeatedRefusalCount = 5;
    internal const string GateIdleWhileReadyKind = "gate-idle-while-ready";
    internal const string RepeatedRefusalKind = "repeated-refusal";
}
