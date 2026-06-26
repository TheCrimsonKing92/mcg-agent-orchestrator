using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
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

    public IReadOnlyList<string> BuildLines(Goal goal, bool quiet, TimeSpan? stallThreshold = null)
    {
        if (quiet)
        {
            return [];
        }

        var now = _now();
        var active = GetActiveTask(goal);
        if (active is null || active.LastDispatch is null)
        {
            return [];
        }

        var lines = new List<string>();
        var goalKey = goal.Id.Value;
        var previous = _lastEmitted.GetValueOrDefault(goalKey);
        if (previous is not null && previous.TaskId != active.Id.Value)
        {
            var priorTask = goal.Tasks.FirstOrDefault(task => task.Id.Value == previous.TaskId);
            lines.Add(FormatTransition(goal, previous, priorTask, active));
            _lastEmitted.Remove(goalKey);
            previous = null;
        }

        var snapshot = BuildSnapshot(goal, active, now);
        var stdoutDelta = previous is null
            ? snapshot.OutputBytes
            : Math.Max(0, snapshot.OutputBytes - previous.OutputBytes);
        var shouldEmit =
            previous is null ||
            stdoutDelta > 0 ||
            snapshot.ChangedFileCount != previous.ChangedFileCount ||
            now - previous.EmittedAt >= TimeSpan.FromSeconds(DefaultThrottleSeconds);

        if (shouldEmit)
        {
            lines.Add(FormatProgress(snapshot, stdoutDelta));
            _lastEmitted[goalKey] = snapshot with { EmittedAt = now };
        }

        var warning = BuildWarning(snapshot, stallThreshold ?? TimeSpan.FromMinutes(DefaultStallWarningMinutes));
        if (warning is not null)
        {
            lines.Add(warning);
        }

        return lines;
    }

    private ConductorWatchProgressSnapshot BuildSnapshot(Goal goal, TaskSpec task, DateTimeOffset now)
    {
        var taskNumber = ConsoleViews.GetTaskDisplayNumber(goal, task.Id);
        var totalTasks = goal.Tasks.Count;
        var dispatch = task.LastDispatch!;
        var process = task.LastProcess;
        var heartbeat = process is null
            ? null
            : _readHeartbeat(process, now);
        var changes = _readChanges(dispatch.WorkingDirectory, dispatch.BaseCommit);
        var liveness = ResolveLiveness(process, heartbeat);
        var outputBytes = (heartbeat?.StandardOutputBytes ?? 0) + (heartbeat?.StandardErrorBytes ?? 0);
        var lastProgressAge = heartbeat?.IdleDuration ?? (now - dispatch.DispatchedAt);
        return new ConductorWatchProgressSnapshot(
            goal.Id.Value.Length >= 8 ? goal.Id.Value[..8] : goal.Id.Value,
            task.Id.Value,
            task.RequiredRole.ToString(),
            taskNumber,
            totalTasks,
            dispatch.DispatchedAt,
            now - dispatch.DispatchedAt,
            liveness,
            outputBytes,
            lastProgressAge,
            changes.Files.Count,
            changes.DisplayFiles,
            changes.RemainingFileCount,
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

    private static string FormatProgress(ConductorWatchProgressSnapshot snapshot, long outputDelta)
    {
        var files = FormatFiles(snapshot.DisplayFiles, snapshot.RemainingFileCount);
        return
            $"WATCH_PROGRESS goal={snapshot.GoalPrefix} role={snapshot.Role} task={snapshot.TaskNumber}/{snapshot.TotalTasks} " +
            $"elapsed={FormatDuration(snapshot.Elapsed)} liveness=\"{snapshot.Liveness}\" output_delta={outputDelta} " +
            $"last_progress_age={FormatDuration(snapshot.LastProgressAge)} files={snapshot.ChangedFileCount} [{files}]";
    }

    private static string FormatTransition(Goal goal, EmittedSnapshot previous, TaskSpec? priorTask, TaskSpec next)
    {
        var commitValue = priorTask?.LastDispatch?.ResultCommit ?? previous.ResultCommit;
        var commit = string.IsNullOrWhiteSpace(commitValue) ? "unknown" : commitValue;
        if (commit.Length > 12)
        {
            commit = commit[..12];
        }

        return
            $"WATCH_TRANSITION goal={previous.GoalPrefix} {previous.Role}=done commit={commit} " +
            $"files={previous.ChangedFileCount} elapsed={FormatDuration(previous.Elapsed)} next={next.RequiredRole} task={ConsoleViews.GetTaskDisplayNumber(goal, next.Id)}/{goal.Tasks.Count}";
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
        string Liveness,
        long OutputBytes,
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
            ChangedFileCount,
            ResultCommit,
            EmittedAt);

    private record EmittedSnapshot(
        string GoalPrefix,
        string TaskId,
        string Role,
        TimeSpan Elapsed,
        long OutputBytes,
        int ChangedFileCount,
        string? ResultCommit,
        DateTimeOffset EmittedAt);
}
