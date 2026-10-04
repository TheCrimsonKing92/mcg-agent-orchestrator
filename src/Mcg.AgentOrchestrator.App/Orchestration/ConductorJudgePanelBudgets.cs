namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorJudgePanelBudgets
{
    internal const int MaxJudgeCallsPerCase = 2;
    internal const int MaxCasesPerEnrollmentWindow = 20;
    internal const int EnrollmentWindowGoals = 50;
    internal const int SuspendAfterConsecutiveProtocolFailures = 2;
    internal const int MaxQueuedCases = 20;
    internal static readonly TimeSpan JudgeTimeout = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan ClaimExpiry = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan ShutdownDrain = TimeSpan.FromSeconds(30);
}
