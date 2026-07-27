using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorWatchProgressReporter
{
    public const int DefaultThrottleSeconds = 30;
    public const int DefaultStallWarningMinutes = 10;

    private readonly Func<TaskProcessRecord, DateTimeOffset, DispatchHeartbeatStatus> _readHeartbeat;
    private readonly Func<string?, string?, DispatchLiveChangeSnapshot> _readChanges;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, EmittedSnapshot> _lastEmitted = new(StringComparer.Ordinal);

    public ConductorWatchProgressReporter(
        Func<TaskProcessRecord, DateTimeOffset, DispatchHeartbeatStatus>? readHeartbeat = null,
        Func<string?, string?, DispatchLiveChangeSnapshot>? readChanges = null,
        Func<int, bool>? isProcessAlive = null,
        Func<DateTimeOffset>? now = null)
    {
        _readHeartbeat = readHeartbeat ?? ((process, observedAt) => ProcessLogReader.ReadHeartbeat(process, observedAt));
        _readChanges = readChanges ?? ((worktreePath, baseCommit) => GoalChangesReader.BuildLiveDispatchSnapshot(worktreePath, baseCommit));
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<string> BuildLines(
        Goal goal,
        bool quiet,
        ConductorAutonomyPolicy policy,
        TimeSpan? watchInterval = null,
        TimeSpan? stallThreshold = null,
        DispatchLiveChangeSnapshot? liveChanges = null)
    {
        if (quiet)
        {
            return [];
        }

        var now = _now();
        var lines = new List<string>();
        var goalKey = goal.Id.Value;
        var previous = _lastEmitted.GetValueOrDefault(goalKey);
        var active = GetActiveTask(goal);
        if (active is null || active.LastDispatch is null)
        {
            var priorTask = previous is null
                ? null
                : goal.Tasks.FirstOrDefault(task => task.Id.Value == previous.TaskId);
            if (previous is not null && ShouldEmitTerminalTransition(goal, priorTask))
            {
                lines.AddRange(FormatTerminalTransition(goal, previous, priorTask!, ResolveTerminalNext(goal, policy)));
                _lastEmitted.Remove(goalKey);
            }

            return lines;
        }

        if (previous is not null && previous.TaskId != active.Id.Value)
        {
            var priorTask = goal.Tasks.FirstOrDefault(task => task.Id.Value == previous.TaskId);
            lines.AddRange(FormatTransition(goal, previous, priorTask, active));
            _lastEmitted.Remove(goalKey);
            previous = null;
        }

        var snapshot = BuildSnapshot(goal, active, now, previous);
        var stdoutDelta = previous is null
            ? snapshot.StandardOutputBytes
            : Math.Max(0, snapshot.StandardOutputBytes - previous.StandardOutputBytes);
        var outputDelta = previous is null
            ? snapshot.OutputBytes
            : Math.Max(0, snapshot.OutputBytes - previous.OutputBytes);
        var probeChanges =
            previous is null ||
            outputDelta > 0 ||
            now - previous.EmittedAt >= TimeSpan.FromSeconds(DefaultThrottleSeconds);
        if (probeChanges)
        {
            var changes = liveChanges ??
                _readChanges(active.LastDispatch.WorkingDirectory, active.LastDispatch.BaseCommit);
            snapshot = snapshot with
            {
                ChangedFileCount = changes.Files.Count,
                DisplayFiles = changes.DisplayFiles,
                RemainingFileCount = changes.RemainingFileCount
            };
        }

        var shouldEmit =
            previous is null ||
            outputDelta > 0 ||
            snapshot.ChangedFileCount != previous.ChangedFileCount ||
            now - previous.EmittedAt >= TimeSpan.FromSeconds(DefaultThrottleSeconds);

        if (shouldEmit)
        {
            lines.Add(FormatProgress(snapshot, outputDelta));
            lines.Add(FormatHumanProgress(snapshot, stdoutDelta));
            _lastEmitted[goalKey] = snapshot with { EmittedAt = now };
        }

        var effectiveStallThreshold = ResolveStallThreshold(watchInterval, stallThreshold);
        var warning = BuildWarning(snapshot, effectiveStallThreshold);
        if (warning is not null)
        {
            lines.Add(warning);
            lines.Add(FormatHumanWarning(snapshot));
        }

        return lines;
    }

    private ConductorWatchProgressSnapshot BuildSnapshot(
        Goal goal,
        TaskSpec task,
        DateTimeOffset now,
        EmittedSnapshot? previous)
    {
        var taskNumber = ConsoleViews.GetTaskDisplayNumber(goal, task.Id);
        var totalTasks = goal.Tasks.Count;
        var dispatch = task.LastDispatch!;
        var process = task.LastProcess;
        var heartbeat = process is null
            ? null
            : _readHeartbeat(process, now);
        var liveness = ResolveLiveness(process, heartbeat);
        var workerPid = ResolveWorkerPid(process, heartbeat);
        var outputBytes = (heartbeat?.StandardOutputBytes ?? 0) + (heartbeat?.StandardErrorBytes ?? 0);
        var stdoutBytes = heartbeat?.StandardOutputBytes ?? 0;
        var lastProgressAge = heartbeat?.IdleDuration ?? (now - dispatch.DispatchedAt);
        return new ConductorWatchProgressSnapshot(
            goal.Id.Value.Length >= 8 ? goal.Id.Value[..8] : goal.Id.Value,
            task.Id.Value,
            task.RequiredRole.ToString(),
            taskNumber,
            totalTasks,
            dispatch.DispatchedAt,
            now - dispatch.DispatchedAt,
            workerPid,
            liveness,
            outputBytes,
            stdoutBytes,
            lastProgressAge,
            previous?.ChangedFileCount ?? 0,
            [],
            0,
            dispatch.ResultCommit,
            now);
    }

    private string ResolveLiveness(TaskProcessRecord? process, DispatchHeartbeatStatus? heartbeat)
    {
        if (process is null)
        {
            return "NO LIVE WORKER";
        }

        var pids = heartbeat?.OwnedProcessIds is { Count: > 0 }
            ? heartbeat.OwnedProcessIds
            : heartbeat?.ChildProcessId is { } childPid
                ? [childPid]
                : process.TrackedProcessIds;

        if (pids.Count == 0)
        {
            pids = [process.ProcessId];
        }

        if (pids.Any(_isProcessAlive))
        {
            return "alive";
        }

        return process.IsRunning ? "NO LIVE WORKER" : "exiting";
    }

    private int? ResolveWorkerPid(TaskProcessRecord? process, DispatchHeartbeatStatus? heartbeat)
    {
        var pids = heartbeat?.OwnedProcessIds is { Count: > 0 }
            ? heartbeat.OwnedProcessIds
            : heartbeat?.ChildProcessId is { } childPidForSet
                ? [childPidForSet]
                : process?.TrackedProcessIds ?? [];
        var livePid = pids.FirstOrDefault(_isProcessAlive);
        if (livePid > 0)
        {
            return livePid;
        }

        if (heartbeat?.ChildProcessId is { } childPid)
        {
            return childPid;
        }

        if (heartbeat?.OwnedProcessIds is { Count: > 0 })
        {
            return heartbeat.OwnedProcessIds[0];
        }

        return process?.ProcessId;
    }

    private static string FormatProgress(ConductorWatchProgressSnapshot snapshot, long outputDelta)
    {
        var files = FormatFiles(snapshot.DisplayFiles, snapshot.RemainingFileCount);
        return
            $"WATCH_PROGRESS goal={snapshot.GoalPrefix} role={snapshot.Role} task={snapshot.TaskNumber}/{snapshot.TotalTasks} " +
            $"elapsed={FormatDuration(snapshot.Elapsed)} liveness=\"{snapshot.Liveness}\" output_delta={outputDelta} " +
            $"last_progress_age={FormatDuration(snapshot.LastProgressAge)} files={snapshot.ChangedFileCount} [{files}]";
    }

    private static string FormatHumanProgress(ConductorWatchProgressSnapshot snapshot, long stdoutDelta)
    {
        var files = FormatHumanFiles(snapshot.ChangedFileCount, snapshot.DisplayFiles, snapshot.RemainingFileCount);
        return
            $"[{snapshot.GoalPrefix}] {snapshot.Role} (task {snapshot.TaskNumber}/{snapshot.TotalTasks}) - " +
            $"running {FormatDuration(snapshot.Elapsed)} - {FormatWorker(snapshot)} - " +
            $"+{FormatBytes(stdoutDelta)} stdout - last progress {FormatDuration(snapshot.LastProgressAge)} ago - {files}";
    }

    private static IReadOnlyList<string> FormatTransition(Goal goal, EmittedSnapshot previous, TaskSpec? priorTask, TaskSpec next)
    {
        var commitValue = priorTask?.LastDispatch?.ResultCommit ?? previous.ResultCommit;
        var commit = string.IsNullOrWhiteSpace(commitValue) ? "unknown" : commitValue;
        if (commit.Length > 12)
        {
            commit = commit[..12];
        }

        var machine =
            $"WATCH_TRANSITION goal={previous.GoalPrefix} {previous.Role}=✓ commit={commit} " +
            $"files={previous.ChangedFileCount} elapsed={FormatDuration(previous.Elapsed)} next={next.RequiredRole} task={ConsoleViews.GetTaskDisplayNumber(goal, next.Id)}/{goal.Tasks.Count}";
        var humanCommit = commit == "unknown" ? commit : commit[..Math.Min(7, commit.Length)];
        var human =
            $"[{previous.GoalPrefix}] {previous.Role} - committed {humanCommit} ({FormatFileCount(previous.ChangedFileCount)}, {FormatDuration(previous.Elapsed)}) -> {next.RequiredRole} dispatched";
        return [machine, human];
    }

    private static bool ShouldEmitTerminalTransition(Goal goal, TaskSpec? priorTask) =>
        priorTask is { Status: WorkTaskStatus.Completed } &&
        goal.Tasks.All(task => task.Status == WorkTaskStatus.Completed);

    private static string ResolveTerminalNext(Goal goal, ConductorAutonomyPolicy policy)
    {
        return goal.Status == GoalStatus.Verified &&
            policy.GetTransitionDecision(GoalLifecycleState.Verified) == ConductorTransitionDecision.Auto
                ? "acceptance-gate"
                : "none";
    }

    private static IReadOnlyList<string> FormatTerminalTransition(
        Goal goal,
        EmittedSnapshot previous,
        TaskSpec priorTask,
        string next)
    {
        var commitValue = priorTask.LastDispatch?.ResultCommit ?? previous.ResultCommit;
        var commit = string.IsNullOrWhiteSpace(commitValue) ? "unknown" : commitValue;
        if (commit.Length > 12)
        {
            commit = commit[..12];
        }

        var taskNumber = ConsoleViews.GetTaskDisplayNumber(goal, priorTask.Id);
        var machine =
            $"WATCH_TRANSITION goal={previous.GoalPrefix} {previous.Role}=✓ commit={commit} " +
            $"files={previous.ChangedFileCount} elapsed={FormatDuration(previous.Elapsed)} next={next} task={taskNumber}/{goal.Tasks.Count}";
        var humanCommit = commit == "unknown" ? commit : commit[..Math.Min(7, commit.Length)];
        var humanNext = next == "none" ? "complete" : next;
        var human =
            $"[{previous.GoalPrefix}] {previous.Role} - committed {humanCommit} ({FormatFileCount(previous.ChangedFileCount)}, {FormatDuration(previous.Elapsed)}) -> {humanNext}";
        return [machine, human];
    }

    private static string? BuildWarning(ConductorWatchProgressSnapshot snapshot, TimeSpan stallThreshold)
    {
        if (snapshot.Liveness == "NO LIVE WORKER")
        {
            return $"WATCH_WARNING goal={snapshot.GoalPrefix} reason=no-live-worker stall={FormatDuration(snapshot.LastProgressAge)}";
        }

        if (snapshot.LastProgressAge >= stallThreshold)
        {
            return $"WATCH_WARNING goal={snapshot.GoalPrefix} reason=last-progress-stale stall={FormatDuration(snapshot.LastProgressAge)}";
        }

        return null;
    }

    private static string FormatHumanWarning(ConductorWatchProgressSnapshot snapshot) =>
        $"[{snapshot.GoalPrefix}] WARNING: no worker progress for {FormatDuration(snapshot.LastProgressAge)} " +
        $"(stdout flat, last edit {FormatDuration(snapshot.LastProgressAge)} ago) - possible stall";

    private static TimeSpan ResolveStallThreshold(TimeSpan? watchInterval, TimeSpan? explicitThreshold)
    {
        if (explicitThreshold is { } threshold)
        {
            return threshold;
        }

        var defaultThreshold = TimeSpan.FromMinutes(DefaultStallWarningMinutes);
        if (watchInterval is null)
        {
            return defaultThreshold;
        }

        var pollBasedThreshold = TimeSpan.FromTicks(watchInterval.Value.Ticks * 4);
        return pollBasedThreshold > defaultThreshold ? pollBasedThreshold : defaultThreshold;
    }

    private static TaskSpec? GetActiveTask(Goal goal) =>
        goal.Tasks.FirstOrDefault(task => task.LastProcess is { IsRunning: true }) ??
        goal.Tasks.FirstOrDefault(task => task.Status == WorkTaskStatus.Running && task.LastDispatch is not null) ??
        goal.Tasks.FirstOrDefault(task => task.LastDispatch is not null && task.Status is WorkTaskStatus.Assigned);

    private static string FormatFiles(IReadOnlyList<string> files, int remaining)
    {
        if (files.Count == 0)
        {
            return "none";
        }

        var text = string.Join(", ", files);
        return remaining > 0 ? $"{text}, +{remaining} more" : text;
    }

    private static string FormatHumanFiles(
        int changedFileCount,
        IReadOnlyList<string> files,
        int remaining)
    {
        var count = FormatFileCount(changedFileCount);
        if (changedFileCount == 0 || files.Count == 0)
        {
            return count;
        }

        return $"{count} ({FormatFiles(files, remaining)})";
    }

    private static string FormatFileCount(int count) =>
        count == 1 ? "1 file changed" : $"{count} files changed";

    private static string FormatWorker(ConductorWatchProgressSnapshot snapshot) =>
        snapshot.WorkerPid is { } pid
            ? $"worker pid {pid} {snapshot.Liveness}"
            : snapshot.Liveness;

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{Math.Max(0, bytes)}B";
        }

        var kib = bytes / 1024d;
        if (kib < 1024)
        {
            return $"{kib:0.#}KB";
        }

        return $"{kib / 1024d:0.#}MB";
    }

    private static string FormatDuration(TimeSpan value)
    {
        var duration = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h{duration.Minutes}m{duration.Seconds}s";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes}m{duration.Seconds}s";
        }

        return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed record ConductorWatchProgressSnapshot(
        string GoalPrefix,
        string TaskId,
        string Role,
        int TaskNumber,
        int TotalTasks,
        DateTimeOffset DispatchedAt,
        TimeSpan Elapsed,
        int? WorkerPid,
        string Liveness,
        long OutputBytes,
        long StandardOutputBytes,
        TimeSpan LastProgressAge,
        int ChangedFileCount,
        IReadOnlyList<string> DisplayFiles,
        int RemainingFileCount,
        string? ResultCommit,
        DateTimeOffset EmittedAt) : EmittedSnapshot(
            GoalPrefix,
            TaskId,
            Role,
            Elapsed,
            OutputBytes,
            StandardOutputBytes,
            ChangedFileCount,
            ResultCommit,
            EmittedAt);

    private record EmittedSnapshot(
        string GoalPrefix,
        string TaskId,
        string Role,
        TimeSpan Elapsed,
        long OutputBytes,
        long StandardOutputBytes,
        int ChangedFileCount,
        string? ResultCommit,
        DateTimeOffset EmittedAt);
}
