namespace Mcg.AgentOrchestrator.Core;

public static partial class DispatchFailureClassifier
{
    internal const int DetachedWithoutWorkerResultRetryLimit = 3;

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
