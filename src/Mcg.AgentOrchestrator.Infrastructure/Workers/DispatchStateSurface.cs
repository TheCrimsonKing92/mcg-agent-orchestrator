using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum DispatchStateKind
{
    None,
    ActiveTestChild,
    ExitedAwaitingReconcile,
    StaleCleanup,
    WedgedProcess,
    Running,
    Completed
}

public sealed record DispatchProcessTreeNode(int ProcessId, bool IsAlive, string? CommandLine);

public sealed record DispatchProcessTreeSummary(
    int WrapperProcessId,
    int? ChildProcessId,
    IReadOnlyList<int> OwnedProcessIds,
    IReadOnlyList<DispatchProcessTreeNode> Processes,
    string? ChildCommandLine)
{
    public bool HasLiveProcess => Processes.Any(process => process.IsAlive);
    public bool HasLiveChild => ChildProcessId is { } childPid &&
        Processes.Any(process => process.ProcessId == childPid && process.IsAlive);
}

public sealed record DispatchArtifactStatus(
    string StandardOutputPath,
    bool StandardOutputExists,
    long StandardOutputBytes,
    string StandardErrorPath,
    bool StandardErrorExists,
    long StandardErrorBytes,
    string ExitCodePath,
    bool ExitCodeExists,
    string HeartbeatPath,
    bool HeartbeatExists);

public sealed record DispatchWorktreeState(
    string WorkingDirectory,
    bool Exists,
    bool IsGitWorktree,
    bool? IsDirty,
    string? HeadCommit,
    int? CommitsAfterDispatch,
    IReadOnlyList<string> StatusEntries,
    string? Error);

public sealed record DispatchStaleThresholds(
    TimeSpan RecentHeartbeatGrace,
    TimeSpan LiveIdleTimeout,
    int StaleRetryBudgetRemaining);

public sealed record DispatchAuthoritativeState(
    DispatchStateKind Kind,
    string RecommendedAction,
    DispatchRecoveryDecision RecoveryDecision,
    DispatchProcessTreeSummary ProcessTree,
    DispatchArtifactStatus Artifacts,
    DispatchHeartbeatStatus Heartbeat,
    DispatchWorktreeState Worktree,
    DispatchStaleThresholds StaleThresholds,
    string Summary);

public sealed class DispatchStateSurface
{
    private readonly IClock _clock;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<IEnumerable<int>, IReadOnlyDictionary<int, string>> _readCommandLines;
    private readonly TimeSpan _recentHeartbeatGrace;
    private readonly TimeSpan _liveIdleTimeout;
    private readonly DispatchRecoveryPolicy _recoveryPolicy;
    private readonly bool _inspectWorktree;

    public DispatchStateSurface(
        IClock? clock = null,
        Func<int, bool>? isProcessAlive = null,
        Func<IEnumerable<int>, IReadOnlyDictionary<int, string>>? readCommandLines = null,
        TimeSpan? recentHeartbeatGrace = null,
        TimeSpan? liveIdleTimeout = null,
        bool inspectWorktree = true)
    {
        _clock = clock ?? new SystemClock();
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
        _readCommandLines = readCommandLines ?? ProcessCommandLines.Read;
        _recentHeartbeatGrace = recentHeartbeatGrace ?? DispatchRecoveryPolicy.DefaultRecentHeartbeatGrace;
        _liveIdleTimeout = liveIdleTimeout ?? DispatchRecoveryPolicy.DefaultLiveIdleTimeout;
        _recoveryPolicy = new DispatchRecoveryPolicy(_clock, _recentHeartbeatGrace, _liveIdleTimeout);
        _inspectWorktree = inspectWorktree;
    }

    public DispatchAuthoritativeState Evaluate(GoalId goalId, TaskSpec task)
    {
        return Evaluate(goalId, task, commandLineSnapshot: null);
    }

    public DispatchAuthoritativeState Evaluate(
        GoalId goalId,
        TaskSpec task,
        ProcessCommandLineSnapshot? commandLineSnapshot)
    {
        if (task.LastProcess is not { } process)
        {
            var heartbeat = new DispatchHeartbeatStatus(string.Empty, false, "no-process", 0, null, [], "none", null, null, null, null, 0, 0);
            var artifacts = new DispatchArtifactStatus(string.Empty, false, 0, string.Empty, false, 0, string.Empty, false, string.Empty, false);
            var tree = new DispatchProcessTreeSummary(0, null, [], [], null);
            var worktree = InspectWorktree(string.Empty, task.LastDispatch?.DispatchedAt, _inspectWorktree);
            var decision = new DispatchRecoveryDecision(DispatchRecoveryAction.Hold, DispatchRecoveryPolicy.ToActionName(DispatchRecoveryAction.Hold), "process-absent", "task has no dispatch process");
            return new DispatchAuthoritativeState(
                DispatchStateKind.None,
                "none",
                decision,
                tree,
                artifacts,
                heartbeat,
                worktree,
                new DispatchStaleThresholds(_recentHeartbeatGrace, _liveIdleTimeout, DispatchRecoveryPolicy.GetStaleRetryBudgetRemaining(task)),
                "no dispatch process recorded");
        }

        var heartbeatStatus = ProcessLogReader.ReadHeartbeat(process, _clock.UtcNow);
        var processTree = BuildProcessTree(process, heartbeatStatus, commandLineSnapshot);
        var artifactsStatus = ReadArtifacts(process, heartbeatStatus);
        var worktreeState = InspectWorktree(process.WorkingDirectory, task.LastDispatch?.DispatchedAt, _inspectWorktree);
        var staleBudget = DispatchRecoveryPolicy.GetStaleRetryBudgetRemaining(task);
        var recovery = _recoveryPolicy.Evaluate(
            process,
            processTree.HasLiveProcess,
            staleBudget,
            ToRecoveryWorktreeInspection(worktreeState, _inspectWorktree));
        var kind = Classify(process, recovery, processTree, heartbeatStatus, artifactsStatus);
        return new DispatchAuthoritativeState(
            kind,
            Recommend(kind, recovery),
            recovery,
            processTree,
            artifactsStatus,
            heartbeatStatus,
            worktreeState,
            new DispatchStaleThresholds(_recentHeartbeatGrace, _liveIdleTimeout, staleBudget),
            BuildSummary(goalId, task, kind, recovery, processTree, artifactsStatus, heartbeatStatus, worktreeState));
    }

    private DispatchProcessTreeSummary BuildProcessTree(
        TaskProcessRecord process,
        DispatchHeartbeatStatus heartbeat,
        ProcessCommandLineSnapshot? commandLineSnapshot)
    {
        var processIds = new List<int>();
        Add(processIds, process.ProcessId);
        foreach (var pid in process.TrackedProcessIds)
        {
            Add(processIds, pid);
        }

        if (heartbeat.IsAvailable)
        {
            Add(processIds, heartbeat.ProcessId);
            if (heartbeat.ChildProcessId is { } observedChildPid)
            {
                Add(processIds, observedChildPid);
            }

            foreach (var pid in heartbeat.OwnedProcessIds)
            {
                Add(processIds, pid);
            }
        }

        var commandLines = commandLineSnapshot?.Read(processIds) ?? _readCommandLines(processIds);
        var nodes = processIds
            .Select(pid => new DispatchProcessTreeNode(
                pid,
                _isProcessAlive(pid),
                commandLines.TryGetValue(pid, out var commandLine) ? commandLine : null))
            .ToArray();
        var childCommandLine = heartbeat.ChildProcessId is { } childPid &&
            commandLines.TryGetValue(childPid, out var childLine)
                ? childLine
                : null;
        var ownedPids = heartbeat.OwnedProcessIds.Count > 0
            ? heartbeat.OwnedProcessIds
            : process.TrackedProcessIds;
        return new DispatchProcessTreeSummary(
            process.ProcessId,
            heartbeat.ChildProcessId,
            ownedPids,
            nodes,
            childCommandLine);

        static void Add(List<int> values, int pid)
        {
            if (pid > 0 && !values.Contains(pid))
            {
                values.Add(pid);
            }
        }
    }

    private static DispatchArtifactStatus ReadArtifacts(TaskProcessRecord process, DispatchHeartbeatStatus heartbeat)
    {
        var heartbeatPath = heartbeat.Path;
        return new DispatchArtifactStatus(
            process.StandardOutputPath,
            File.Exists(process.StandardOutputPath),
            SafeFileLength(process.StandardOutputPath),
            process.StandardErrorPath,
            File.Exists(process.StandardErrorPath),
            SafeFileLength(process.StandardErrorPath),
            process.ExitCodePath,
            File.Exists(process.ExitCodePath),
            heartbeatPath,
            File.Exists(heartbeatPath));
    }

    private static DispatchWorktreeState InspectWorktree(string workingDirectory, DateTimeOffset? dispatchedAt, bool inspectGit)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return new DispatchWorktreeState(workingDirectory, false, false, null, null, null, [], "working directory missing");
        }

        if (!inspectGit)
        {
            return new DispatchWorktreeState(workingDirectory, true, false, null, null, null, [], "worktree git inspection skipped for bounded diagnostics");
        }

        if (!File.Exists(Path.Combine(workingDirectory, ".git")) && !Directory.Exists(Path.Combine(workingDirectory, ".git")))
        {
            return new DispatchWorktreeState(workingDirectory, true, false, null, null, null, [], "not a git worktree");
        }

        var status = GitCli.Run(workingDirectory, "status", "--short");
        var head = GitCli.Run(workingDirectory, "rev-parse", "--short", "HEAD");
        int? commitsAfterDispatch = null;
        if (dispatchedAt is not null)
        {
            var log = GitCli.Run(workingDirectory, "log", "--format=%H", $"--since={dispatchedAt:O}");
            commitsAfterDispatch = log.ExitCode == 0
                ? log.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length
                : null;
        }

        var filteredStatusOutput = GitCli.FilterCommitWorthyStatus(status.Output);
        var statusEntries = status.ExitCode == 0
            ? filteredStatusOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        var error = status.ExitCode == 0 && head.ExitCode == 0
            ? null
            : string.Join("; ", new[]
            {
                status.ExitCode == 0 ? null : $"git status failed: {status.Error.Trim()}",
                head.ExitCode == 0 ? null : $"git rev-parse failed: {head.Error.Trim()}"
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new DispatchWorktreeState(
            workingDirectory,
            true,
            true,
            status.ExitCode == 0 ? statusEntries.Length > 0 : null,
            head.ExitCode == 0 ? head.Output.Trim() : null,
            commitsAfterDispatch,
            statusEntries,
            error);
    }

    private static DispatchWorktreeInspectionStatus ToRecoveryWorktreeInspection(
        DispatchWorktreeState worktree,
        bool inspectionRequested)
    {
        if (!inspectionRequested)
        {
            return DispatchWorktreeInspectionStatus.NotRequired;
        }

        if (worktree.IsDirty is { } isDirty && string.IsNullOrWhiteSpace(worktree.Error))
        {
            return DispatchWorktreeInspectionStatus.Available(isDirty, worktree.WorkingDirectory);
        }

        var reason = !worktree.Exists
            ? "directory-missing"
            : !worktree.IsGitWorktree
                ? "git-metadata-missing"
                : "git-inspection-failed";
        return DispatchWorktreeInspectionStatus.Unavailable(
            worktree.WorkingDirectory,
            reason,
            worktree.Error ?? "worktree-state-unavailable");
    }

    private DispatchStateKind Classify(
        TaskProcessRecord process,
        DispatchRecoveryDecision recovery,
        DispatchProcessTreeSummary tree,
        DispatchHeartbeatStatus heartbeat,
        DispatchArtifactStatus artifacts)
    {
        if (!process.IsRunning)
        {
            return DispatchStateKind.Completed;
        }

        if (recovery.Action == DispatchRecoveryAction.ReconcileFromExit || (!tree.HasLiveProcess && artifacts.ExitCodeExists))
        {
            return DispatchStateKind.ExitedAwaitingReconcile;
        }

        if (recovery.Action is DispatchRecoveryAction.ClassifyBlocker or DispatchRecoveryAction.Reap ||
            recovery.Action == DispatchRecoveryAction.Hold && !string.IsNullOrWhiteSpace(recovery.Blocker))
        {
            return DispatchStateKind.WedgedProcess;
        }

        if ((recovery.Action is DispatchRecoveryAction.MarkStale or DispatchRecoveryAction.BudgetExhausted) && !tree.HasLiveProcess)
        {
            return DispatchStateKind.StaleCleanup;
        }

        if (tree.HasLiveChild && heartbeat.HeartbeatAge <= _recentHeartbeatGrace)
        {
            return DispatchStateKind.ActiveTestChild;
        }

        return DispatchStateKind.Running;
    }

    private static string Recommend(DispatchStateKind kind, DispatchRecoveryDecision recovery) =>
        kind switch
        {
            DispatchStateKind.ActiveTestChild => "hold",
            DispatchStateKind.ExitedAwaitingReconcile => "refresh-dispatch",
            DispatchStateKind.StaleCleanup => recovery.ActionName,
            DispatchStateKind.WedgedProcess => recovery.ActionName,
            DispatchStateKind.Completed => "none",
            DispatchStateKind.None => "none",
            _ => recovery.ActionName
        };

    private static string BuildSummary(
        GoalId goalId,
        TaskSpec task,
        DispatchStateKind kind,
        DispatchRecoveryDecision recovery,
        DispatchProcessTreeSummary tree,
        DispatchArtifactStatus artifacts,
        DispatchHeartbeatStatus heartbeat,
        DispatchWorktreeState worktree)
    {
        var child = tree.ChildProcessId?.ToString() ?? "none";
        var dirty = worktree.IsDirty?.ToString() ?? "unknown";
        return
            $"goal={goalId.Value[..Math.Min(8, goalId.Value.Length)]} task={task.Id.Value[..Math.Min(8, task.Id.Value.Length)]} " +
            $"state={kind} action={Recommend(kind, recovery)} live={tree.HasLiveProcess} child_pid={child} " +
            $"exit_artifact={artifacts.ExitCodeExists} heartbeat={(heartbeat.IsAvailable ? heartbeat.State : heartbeat.UnavailableReason ?? "unavailable")} " +
            $"dirty_worktree={dirty} reason={recovery.Reason}";
    }

    private static bool IsProcessAlive(int processId)
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

    private static long SafeFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch (IOException)
        {
            return 0L;
        }
        catch (UnauthorizedAccessException)
        {
            return 0L;
        }
    }
}
