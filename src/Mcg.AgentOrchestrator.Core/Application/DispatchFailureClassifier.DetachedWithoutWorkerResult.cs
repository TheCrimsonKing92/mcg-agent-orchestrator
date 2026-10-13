namespace Mcg.AgentOrchestrator.Core;

public static partial class DispatchFailureClassifier
{
    internal const int DetachedWithoutWorkerResultRetryLimit = 3;

    private static DispatchOutcome BuildDetachedWithoutWorkerResultOutcome(
        TaskSpec task, TaskVerificationRecord verification, bool workerResultPresent, bool hasCommittedChanges) =>
        BuildOutcome(TaskOutcomeRules.DetachedWithoutWorkerResult, task, verification,
            workerResultPresent, hasCommittedChanges,
            new DispatchOutcome(DispatchOutcomeKind.ProviderInterruption, verification.ExitCode,
                HasZeroByteStandardOutput(verification), null, null, RecoveryRecommendation.AutoRetry,
                $"detached-without-worker-result: process {task.LastProcess!.ProcessId} was gracefully detached and exited 0 without a WORKER_RESULT block"));

    private static bool IsDetachedWithoutWorkerResult(
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent) =>
        task.LastProcess is { CompletedAt: not null, WasGracefullyDetachedByConductor: true } &&
        !workerResultPresent &&
        verification.Succeeded;

    public static int CountConsecutiveDetachedWithoutWorkerResultRounds(TaskSpec task)
    {
        var count = 0;
        for (var index = task.VerificationHistory.Count - 1; index >= 0; index--)
        {
            if (!string.Equals(
                    task.VerificationHistory[index].CompletionVerdictRule,
                    TaskOutcomeRules.DetachedWithoutWorkerResult.Token,
                    StringComparison.Ordinal))
            {
                break;
            }

            count++;
        }

        return count;
    }
}
