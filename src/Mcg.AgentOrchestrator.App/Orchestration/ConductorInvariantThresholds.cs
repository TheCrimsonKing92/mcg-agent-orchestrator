namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorInvariantThresholds
{
    internal static readonly TimeSpan GateIdleWhileReady = TimeSpan.FromMinutes(10);
    internal const int RepeatedRefusalCount = 5;
    internal const string GateIdleWhileReadyKind = "gate-idle-while-ready";
    internal const string RepeatedRefusalKind = "repeated-refusal";
    internal static readonly TimeSpan FinishedUnreconciled = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan RelaunchWithoutActivation = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan GenerationBehindMain = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan FinishedAttemptScanWindow = TimeSpan.FromHours(24);
    internal const string FinishedUnreconciledKind = "finished-unreconciled";
    internal const string RelaunchWithoutActivationKind = "relaunch-without-activation";
    internal const string GenerationBehindMainKind = "generation-behind-main";
}
