using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Core;

public sealed record InterruptedWorkCheckpoint(
    string DispatchId,
    string GoalId,
    string TaskId,
    AgentRole Role,
    string Branch,
    string WorktreePath,
    string ParentCommit,
    string CheckpointSha,
    ProviderFailureKind Cause,
    DateTimeOffset RecordedAt,
    string IdempotencyKey)
{
    private const char KeySeparator = '\x1f';
    private const string Marker = "interrupted-work";

    public static InterruptedWorkCheckpoint Create(
        string dispatchId,
        string goalId,
        string taskId,
        AgentRole role,
        string branch,
        string worktreePath,
        string parentCommit,
        ProviderFailureKind cause,
        DateTimeOffset recordedAt,
        string checkpointSha = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentCommit);
        return new InterruptedWorkCheckpoint(
            dispatchId.Trim(),
            goalId.Trim(),
            taskId.Trim(),
            role,
            branch.Trim(),
            Path.GetFullPath(worktreePath),
            parentCommit.Trim(),
            checkpointSha.Trim(),
            cause,
            recordedAt,
            ComputeIdempotencyKey(dispatchId, parentCommit));
    }

    public InterruptedWorkCheckpoint BindCheckpoint(string checkpointSha) =>
        string.IsNullOrWhiteSpace(checkpointSha)
            ? throw new ArgumentException("Checkpoint SHA is required.", nameof(checkpointSha))
            : this with { CheckpointSha = checkpointSha.Trim() };

    public string RenderCommitMessage() =>
        $"orchestrator: checkpoint interrupted {Role} work for task {TaskId[..Math.Min(8, TaskId.Length)]}\n\n" +
        $"Orchestrator-Checkpoint: {Marker}\n" +
        $"Checkpoint-Key: {IdempotencyKey}\n" +
        $"Checkpoint-Dispatch-Id: {DispatchId}\n" +
        $"Checkpoint-Goal: {GoalId}\n" +
        $"Checkpoint-Task: {TaskId}\n" +
        $"Checkpoint-Role: {Role}\n" +
        $"Checkpoint-Branch: {Branch}\n" +
        $"Checkpoint-Parent: {ParentCommit}\n" +
        $"Checkpoint-Cause: {Cause}";

    public static string ComputeIdempotencyKey(string dispatchId, string parentCommit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentCommit);
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{dispatchId.Trim()}{KeySeparator}{parentCommit.Trim()}"))).ToLowerInvariant();
    }

    public static bool TryParseCommitMessage(string message, out InterruptedWorkCheckpointCommitMetadata? metadata)
    {
        metadata = null;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var trailers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 || !line.StartsWith("Checkpoint-", StringComparison.Ordinal) &&
                !line.StartsWith("Orchestrator-Checkpoint:", StringComparison.Ordinal))
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length == 0 || !trailers.TryAdd(key, value))
            {
                return false;
            }
        }

        if (!trailers.TryGetValue("Orchestrator-Checkpoint", out var marker) || marker != Marker ||
            !TryRequired(trailers, "Checkpoint-Key", out var idempotencyKey) ||
            !TryRequired(trailers, "Checkpoint-Dispatch-Id", out var dispatchId) ||
            !TryRequired(trailers, "Checkpoint-Goal", out var goalId) ||
            !TryRequired(trailers, "Checkpoint-Task", out var taskId) ||
            !TryRequired(trailers, "Checkpoint-Role", out var roleText) ||
            !Enum.TryParse<AgentRole>(roleText, ignoreCase: false, out var role) ||
            !TryRequired(trailers, "Checkpoint-Branch", out var branch) ||
            !TryRequired(trailers, "Checkpoint-Parent", out var parentCommit) ||
            !TryRequired(trailers, "Checkpoint-Cause", out var causeText) ||
            !Enum.TryParse<ProviderFailureKind>(causeText, ignoreCase: false, out var cause))
        {
            return false;
        }

        metadata = new InterruptedWorkCheckpointCommitMetadata(
            idempotencyKey,
            dispatchId,
            goalId,
            taskId,
            role,
            branch,
            parentCommit,
            cause);
        return true;
    }

    private static bool TryRequired(
        IReadOnlyDictionary<string, string> trailers,
        string key,
        out string value) =>
        trailers.TryGetValue(key, out value!) && !string.IsNullOrWhiteSpace(value);
}

public sealed record InterruptedWorkCheckpointCommitMetadata(
    string IdempotencyKey,
    string DispatchId,
    string GoalId,
    string TaskId,
    AgentRole Role,
    string Branch,
    string ParentCommit,
    ProviderFailureKind Cause);
