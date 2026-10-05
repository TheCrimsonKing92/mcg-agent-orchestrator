using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record InterruptedWorkCheckpointContextProjection(
    string LogicalIdentity,
    byte[] Bytes,
    InterruptedWorkCheckpointProjectionMetrics Metrics);

internal static class InterruptedWorkCheckpointContextProjector
{
    internal static InterruptedWorkCheckpointContextProjection? Project(TaskSpec task)
    {
        if (task.RequiredRole != AgentRole.Developer ||
            task.PendingInterruptedWorkCheckpoint is not { CheckpointSha.Length: > 0 } checkpoint)
        {
            return null;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            contract_version = ContextContractVersion.V1.Value,
            source = "interrupted-work-checkpoint",
            continuation = "Resume the preserved implementation. Normal build, result, review, and acceptance contracts still apply.",
            checkpoint.DispatchId,
            checkpoint.GoalId,
            checkpoint.TaskId,
            checkpoint.Role,
            checkpoint.Branch,
            checkpoint.WorktreePath,
            checkpoint.ParentCommit,
            checkpoint.CheckpointSha,
            checkpoint.Cause,
            checkpoint.RecordedAt,
            checkpoint.IdempotencyKey
        });
        var hash = WorkerContextArtifact.Hash(bytes);
        return new InterruptedWorkCheckpointContextProjection(
            $"goal/interrupted-work-checkpoint/{hash}.json",
            bytes,
            new InterruptedWorkCheckpointProjectionMetrics(checkpoint.CheckpointSha, hash));
    }
}
