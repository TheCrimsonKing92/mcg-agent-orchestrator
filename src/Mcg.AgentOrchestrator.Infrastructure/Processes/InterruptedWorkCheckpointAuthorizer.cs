using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum InterruptedWorkCheckpointDispositionKind
{
    NotApplicable,
    Checkpointed,
    Reconciled,
    Skipped,
    Hold,
    CommitFailed
}

internal sealed record InterruptedWorkCheckpointDisposition(
    InterruptedWorkCheckpointDispositionKind Kind,
    string Token,
    string Message,
    InterruptedWorkCheckpoint? Checkpoint = null)
{
    internal bool IsCheckpoint =>
        Kind is InterruptedWorkCheckpointDispositionKind.Checkpointed or
            InterruptedWorkCheckpointDispositionKind.Reconciled;
}

internal sealed record InterruptedWorkCheckpointRequest(
    GoalId GoalId,
    TaskSpec Task,
    TaskProcessRecord Process,
    DispatchRecoveryDecision? RecoveryDecision,
    ProviderFailureKind ProviderFailureKind,
    GoalWorktreeInspectionResult? WorktreeInspection,
    string DispatchId,
    bool HasContentOrTestFailure);

internal sealed class InterruptedWorkCheckpointAuthorizer
{
    private static readonly TimeSpan FreshHeartbeatWindow = TimeSpan.FromMinutes(2);
    private readonly DispatchWorktreeCommitter _committer;
    private readonly Func<string, string[], GitCli.GitResult> _runGit;
    private readonly Func<int, bool> _isProcessRunning;
    private readonly Func<int, SpawnProcessIdentity?> _readCurrentIdentity;
    private readonly Func<int, (bool Available, IReadOnlyList<int> ActiveProcessIds)> _readOwnedJob;
    private readonly IClock _clock;

    internal InterruptedWorkCheckpointAuthorizer(
        DispatchWorktreeCommitter committer,
        IClock? clock = null,
        Func<string, string[], GitCli.GitResult>? runGit = null,
        Func<int, bool>? isProcessRunning = null,
        Func<int, SpawnProcessIdentity?>? readCurrentIdentity = null,
        Func<int, (bool Available, IReadOnlyList<int> ActiveProcessIds)>? readOwnedJob = null)
    {
        _committer = committer;
        _clock = clock ?? new SystemClock();
        _runGit = runGit ?? GitCli.Run;
        _isProcessRunning = isProcessRunning ?? IsProcessRunning;
        _readCurrentIdentity = readCurrentIdentity ?? DispatchProcessIdentityEvidence.ReadCurrent;
        _readOwnedJob = readOwnedJob ?? (processId =>
            WorkerProcessJobs.TryGetActiveProcessIds(processId, out var processIds)
                ? (true, processIds)
                : (false, []));
    }

    internal InterruptedWorkCheckpointDisposition AuthorizeAndCheckpoint(InterruptedWorkCheckpointRequest request)
    {
        if (request.RecoveryDecision?.Action != DispatchRecoveryAction.PreserveInterruptedWork)
        {
            return Result(InterruptedWorkCheckpointDispositionKind.NotApplicable, "checkpoint-not-applicable");
        }

        if (request.Task.RequiredRole != AgentRole.Developer)
        {
            return Hold("checkpoint-hold-role-ineligible");
        }

        if (request.ProviderFailureKind != ProviderFailureKind.Connectivity)
        {
            return Hold("checkpoint-hold-untyped-cause");
        }

        if (request.HasContentOrTestFailure)
        {
            return Hold("checkpoint-hold-content-failure");
        }

        var dispatch = request.Task.LastDispatch;
        if (dispatch is null || !string.Equals(
                BackgroundDispatchRunner.BuildDispatchId(request.GoalId, request.Task.Id, dispatch),
                request.DispatchId,
                StringComparison.Ordinal))
        {
            return Hold("checkpoint-hold-competing-attempt");
        }

        if (!PathsEqual(dispatch.WorkingDirectory, request.Process.WorkingDirectory))
        {
            return Hold("checkpoint-hold-worktree-mismatch");
        }

        if (!GoalWorktrees.IsExclusiveGoalWorktree(request.Process.WorkingDirectory, request.GoalId))
        {
            return Hold("checkpoint-hold-worktree-unowned");
        }

        if (request.WorktreeInspection is not { IsAvailable: true } inspection)
        {
            return Hold(request.WorktreeInspection?.IsUnsafe == true
                ? "checkpoint-hold-branch-mismatch"
                : "checkpoint-hold-worktree-unsafe");
        }

        var evidence = inspection.Evidence;
        if (!string.Equals(evidence.Branch, GoalWorktrees.BranchName(request.GoalId), StringComparison.Ordinal))
        {
            return Hold("checkpoint-hold-branch-mismatch");
        }

        var heartbeat = ProcessLogReader.ReadHeartbeat(request.Process, _clock.UtcNow);
        if (!heartbeat.IsAvailable)
        {
            return Hold("checkpoint-hold-heartbeat-unavailable");
        }

        if (heartbeat.HeartbeatAge is null || heartbeat.HeartbeatAge > FreshHeartbeatWindow)
        {
            return Hold("checkpoint-hold-heartbeat-stale");
        }

        var ownedJob = _readOwnedJob(request.Process.ProcessId);
        if (!ownedJob.Available)
        {
            return Hold("checkpoint-hold-owned-job-unavailable");
        }

        if (ownedJob.ActiveProcessIds.Count > 0)
        {
            return Hold("checkpoint-hold-live-descendant");
        }

        var candidateProcessIds = request.Process.CompletionTrackedProcessIds
            .Concat(heartbeat.OwnedProcessIds)
            .Append(heartbeat.ProcessId)
            .Where(processId => processId > 0)
            .Distinct()
            .ToArray();
        if (candidateProcessIds.Length == 0 || heartbeat.OwnedProcessIdentities.Count == 0)
        {
            return Hold("checkpoint-hold-unproven-quiescence");
        }

        foreach (var processId in candidateProcessIds)
        {
            var status = DispatchProcessIdentityEvidence.ClassifyRecordedOwner(
                processId,
                heartbeat.OwnedProcessIdentities,
                _readCurrentIdentity);
            if (status == SpawnTrackedProcessStatus.LiveMatch || _isProcessRunning(processId))
            {
                return Hold("checkpoint-hold-live-descendant");
            }

            if (status != SpawnTrackedProcessStatus.DeadOrRecycled)
            {
                return Hold("checkpoint-hold-identity-unknown");
            }
        }

        if (string.IsNullOrWhiteSpace(dispatch.BaseCommit))
        {
            return Hold("checkpoint-hold-provenance-unresolved");
        }

        var checkpoint = InterruptedWorkCheckpoint.Create(
            request.DispatchId,
            request.GoalId.Value,
            request.Task.Id.Value,
            request.Task.RequiredRole,
            evidence.Branch,
            request.Process.WorkingDirectory,
            dispatch.BaseCommit,
            request.ProviderFailureKind,
            _clock.UtcNow);
        var reconciliation = TryReconcile(checkpoint);
        if (reconciliation.Kind != InterruptedWorkCheckpointDispositionKind.NotApplicable)
        {
            return reconciliation;
        }

        if (!string.Equals(evidence.Head, checkpoint.ParentCommit, StringComparison.Ordinal))
        {
            return Hold("checkpoint-hold-provenance-conflict");
        }

        if (evidence.IsClean || evidence.DirtyPaths.Count == 0)
        {
            return Result(InterruptedWorkCheckpointDispositionKind.Skipped, "checkpoint-skip-no-source-evidence");
        }

        var commit = _committer.TryCommitCheckpoint(
            request.Process.WorkingDirectory,
            checkpoint,
            evidence.DirtyPaths);
        if (!commit.Succeeded)
        {
            return Result(
                InterruptedWorkCheckpointDispositionKind.CommitFailed,
                "checkpoint-commit-failed-recoverable",
                commit.Diagnostic);
        }

        var head = _runGit(request.Process.WorkingDirectory, ["rev-parse", "HEAD"]);
        if (!head.Succeeded || string.IsNullOrWhiteSpace(head.Output))
        {
            return Result(
                InterruptedWorkCheckpointDispositionKind.CommitFailed,
                "checkpoint-commit-failed-recoverable",
                "checkpoint commit succeeded but its SHA could not be read; reconciliation is required");
        }

        var bound = checkpoint.BindCheckpoint(head.Output.Trim());
        return Result(
            InterruptedWorkCheckpointDispositionKind.Checkpointed,
            "checkpoint-created",
            $"interrupted Developer work checkpointed at {bound.CheckpointSha}",
            bound);
    }

    private InterruptedWorkCheckpointDisposition TryReconcile(InterruptedWorkCheckpoint checkpoint)
    {
        var log = _runGit(
            checkpoint.WorktreePath,
            ["log", "--format=%H%x1e%P%x1e%B%x1d", "--no-merges", $"{checkpoint.ParentCommit}..HEAD"]);
        if (!log.Succeeded)
        {
            return Hold("checkpoint-hold-provenance-unresolved", log.Error);
        }

        var matches = new List<string>();
        foreach (var rawRecord in log.Output.Split('\x1d', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = rawRecord.Trim().Split('\x1e', 3);
            if (fields.Length != 3)
            {
                continue;
            }

            var message = fields[2];
            var isCheckpointCommit = message.Contains(
                "Orchestrator-Checkpoint: interrupted-work",
                StringComparison.Ordinal);
            if (!InterruptedWorkCheckpoint.TryParseCommitMessage(message, out var metadata))
            {
                if (isCheckpointCommit)
                {
                    return Hold("checkpoint-hold-provenance-conflict");
                }

                continue;
            }

            if (!string.Equals(metadata!.IdempotencyKey, checkpoint.IdempotencyKey, StringComparison.Ordinal) ||
                !string.Equals(metadata.DispatchId, checkpoint.DispatchId, StringComparison.Ordinal))
            {
                return Hold("checkpoint-hold-provenance-conflict");
            }

            var parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length != 1 ||
                !string.Equals(parents[0], checkpoint.ParentCommit, StringComparison.Ordinal) ||
                !Matches(metadata, checkpoint) ||
                !HasOnlyAttributablePaths(checkpoint.WorktreePath, fields[0]))
            {
                return Hold("checkpoint-hold-provenance-conflict");
            }

            matches.Add(fields[0]);
        }

        return matches.Count switch
        {
            0 => Result(InterruptedWorkCheckpointDispositionKind.NotApplicable, "checkpoint-not-found"),
            1 => Result(
                InterruptedWorkCheckpointDispositionKind.Reconciled,
                "checkpoint-reconciled",
                $"reconciled interrupted Developer checkpoint {matches[0]}",
                checkpoint.BindCheckpoint(matches[0])),
            _ => Hold("checkpoint-hold-provenance-conflict")
        };
    }

    private bool HasOnlyAttributablePaths(string workingDirectory, string commitSha)
    {
        var paths = _runGit(workingDirectory, ["diff-tree", "--no-commit-id", "--name-only", "-r", commitSha]);
        return paths.Succeeded && paths.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            is { Length: > 0 } changedPaths &&
            changedPaths.All(path => !GitCli.IsOrchestratorInternalArtifactPath(path));
    }

    private static bool Matches(
        InterruptedWorkCheckpointCommitMetadata metadata,
        InterruptedWorkCheckpoint checkpoint) =>
        string.Equals(metadata.DispatchId, checkpoint.DispatchId, StringComparison.Ordinal) &&
        string.Equals(metadata.GoalId, checkpoint.GoalId, StringComparison.Ordinal) &&
        string.Equals(metadata.TaskId, checkpoint.TaskId, StringComparison.Ordinal) &&
        metadata.Role == checkpoint.Role &&
        string.Equals(metadata.Branch, checkpoint.Branch, StringComparison.Ordinal) &&
        string.Equals(metadata.ParentCommit, checkpoint.ParentCommit, StringComparison.Ordinal) &&
        metadata.Cause == checkpoint.Cause;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static InterruptedWorkCheckpointDisposition Hold(string token, string? detail = null) =>
        Result(InterruptedWorkCheckpointDispositionKind.Hold, token, detail);

    private static InterruptedWorkCheckpointDisposition Result(
        InterruptedWorkCheckpointDispositionKind kind,
        string token,
        string? detail = null,
        InterruptedWorkCheckpoint? checkpoint = null) =>
        new(kind, token, string.IsNullOrWhiteSpace(detail) ? token : $"{token}: {detail}", checkpoint);
}
