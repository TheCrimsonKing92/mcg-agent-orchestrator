using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class LocalDispatchRunner
{
    private readonly LocalProcessVerifier _processVerifier;

    public LocalDispatchRunner(LocalProcessVerifier? processVerifier = null)
    {
        _processVerifier = processVerifier ?? new LocalProcessVerifier();
    }

    public async Task<TaskVerificationRecord> ExecuteLatestDispatchAsync(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        CancellationToken cancellationToken = default)
    {
        var task = kernel.GetTask(goalId, taskId);
        var dispatch = task.LastDispatch
            ?? throw new InvalidOperationException($"Task '{taskId}' has no dispatch to execute.");

        if (DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, DateTimeOffset.UtcNow, out var retryAfter))
        {
            throw new InvalidOperationException($"Task '{taskId}' hit a recoverable subscription usage limit; retry after {retryAfter:u}.");
        }

        if (task.Status != WorkTaskStatus.Running)
        {
            throw new InvalidOperationException($"Task '{taskId}' status is {task.Status}; prepare or retry the dispatch before executing it.");
        }

        var verification = await _processVerifier
            .RunAsync(dispatch.Command, dispatch.WorkingDirectory, goalId, taskId, cancellationToken)
            .ConfigureAwait(false);

        kernel.RecordDispatchExecutionResult(goalId, taskId, verification);
        return verification;
    }
}
