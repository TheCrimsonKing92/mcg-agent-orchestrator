namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private static void ClearStaleVerificationForAnsweredRestore(TaskSpec task)
    {
        // Keep the prior round in verification history while allowing its answered continuation to dispatch.
        if (task.LastVerification is { Succeeded: true }) task.ClearLatestVerification();
    }
}
