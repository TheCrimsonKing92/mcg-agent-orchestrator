using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorParallelAcceptanceAttemptOutcome
{
    Running,
    Passed,
    Failed,
    StaleCandidate,
    ProcessDied,
    CorruptArtifacts,
    Cancelled,
    BlockedBuildSlot,
    BlockedBuildLock,
    LaunchFailed,
    Reconciled
}

internal enum ConductorParallelAcceptanceAttemptDecisionKind
{
    Started,
    Running,
    Completed,
    TerminalWithoutRun
}

internal sealed record ConductorParallelAcceptanceAttempt(
    string AttemptId,
    string GoalId,
    string GoalPrefix,
    int SlotIndex,
    string? BranchHeadSha,
    string? MainHeadSha,
    DateTimeOffset StartedAt,
    DateTimeOffset LastHeartbeatAt,
    int OwnerProcessId,
    ConductorParallelAcceptanceAttemptOutcome Outcome,
    string StdoutPath,
    string StderrPath,
    string ExitCodePath,
    string HeartbeatPath,
    string ResultPath,
    string MetadataPath,
    DateTimeOffset? CompletedAt = null,
    DateTimeOffset? ReconciledAt = null,
    string? Detail = null)
{
    public string CandidateKey => $"{GoalId}:{BranchHeadSha ?? "unknown-branch"}:{MainHeadSha ?? "unknown-main"}";
}

internal sealed record ConductorParallelAcceptanceAttemptDecision(
    ConductorParallelAcceptanceAttemptDecisionKind Kind,
    ConductorParallelAcceptanceAttempt Attempt,
    ConductorParallelAcceptanceRunResult? Run = null)
{
    public static ConductorParallelAcceptanceAttemptDecision Started(ConductorParallelAcceptanceAttempt attempt) =>
        new(ConductorParallelAcceptanceAttemptDecisionKind.Started, attempt);

    public static ConductorParallelAcceptanceAttemptDecision Running(ConductorParallelAcceptanceAttempt attempt) =>
        new(ConductorParallelAcceptanceAttemptDecisionKind.Running, attempt);

    public static ConductorParallelAcceptanceAttemptDecision Completed(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceRunResult run) =>
        new(ConductorParallelAcceptanceAttemptDecisionKind.Completed, attempt, run);

    public static ConductorParallelAcceptanceAttemptDecision TerminalWithoutRun(
        ConductorParallelAcceptanceAttempt attempt) =>
        new(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, attempt);
}

internal sealed class ConductorParallelAcceptanceAttemptCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Dictionary<string, Task> RunningTasks = new(StringComparer.Ordinal);
    private static readonly object RunningTasksGate = new();
    private static readonly object MetadataWriteGate = new();

    private readonly string _rootDirectory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly bool _runInline;

    internal ConductorParallelAcceptanceAttemptCoordinator(
        string rootDirectory,
        Func<DateTimeOffset>? utcNow = null,
        Func<int, bool>? isProcessAlive = null,
        bool runInline = false)
    {
        _rootDirectory = rootDirectory;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
        _runInline = runInline;
    }

    internal ConductorParallelAcceptanceAttemptDecision Evaluate(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        Func<ConductorParallelAcceptanceCandidate, ConductorAutonomyPolicy, ConductorParallelAcceptanceRunResult> runAcceptance)
    {
        var current = TryReadLatest(candidate.Goal.Id.Value);
        if (current is { Outcome: ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts })
        {
            return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(current);
        }

        if (current is not null &&
            current.Outcome != ConductorParallelAcceptanceAttemptOutcome.Reconciled &&
            !string.Equals(current.CandidateKey, candidate.CandidateKey, StringComparison.Ordinal))
        {
            Persist(current with
            {
                Outcome = ConductorParallelAcceptanceAttemptOutcome.StaleCandidate,
                CompletedAt = _utcNow(),
                ReconciledAt = _utcNow(),
                Detail = "candidate branch/main SHA moved before reconciliation"
            });
            current = null;
        }

        if (current is not null && current.Outcome != ConductorParallelAcceptanceAttemptOutcome.Reconciled)
        {
            var terminal = TryCompleteRunningAttempt(current, candidate);
            if (terminal is { Run: not null })
            {
                return terminal;
            }

            if (terminal is not null)
            {
                return terminal;
            }

            return ConductorParallelAcceptanceAttemptDecision.Running(current);
        }

        return Launch(candidate, policy, runAcceptance);
    }

    internal void MarkReconciled(ConductorParallelAcceptanceAttempt attempt)
    {
        Persist(attempt with
        {
            Outcome = ConductorParallelAcceptanceAttemptOutcome.Reconciled,
            ReconciledAt = _utcNow()
        });
    }

    private ConductorParallelAcceptanceAttemptDecision Launch(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        Func<ConductorParallelAcceptanceCandidate, ConductorAutonomyPolicy, ConductorParallelAcceptanceRunResult> runAcceptance)
    {
        var attempt = CreateAttempt(candidate);
        try
        {
            Persist(attempt);
            WriteHeartbeat(attempt, "starting");
            File.AppendAllText(attempt.StdoutPath, $"acceptance attempt {attempt.AttemptId} started for {attempt.GoalPrefix} slot-{attempt.SlotIndex}{Environment.NewLine}");

            if (_runInline)
            {
                RunAttempt(attempt, candidate, policy, runAcceptance);
                var completed = TryReadLatest(candidate.Goal.Id.Value) ?? attempt;
                return TryCompleteRunningAttempt(completed, candidate)
                    ?? ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(completed);
            }

            var task = Task.Run(() => RunAttempt(attempt, candidate, policy, runAcceptance));
            lock (RunningTasksGate)
            {
                RunningTasks[attempt.AttemptId] = task;
            }

            return ConductorParallelAcceptanceAttemptDecision.Started(attempt);
        }
        catch (Exception ex)
        {
            var failed = attempt with
            {
                Outcome = ConductorParallelAcceptanceAttemptOutcome.LaunchFailed,
                CompletedAt = _utcNow(),
                Detail = ex.Message
            };
            Persist(failed);
            TryAppend(attempt.StderrPath, $"launch failed: {ex}{Environment.NewLine}");
            TryWriteExit(attempt.ExitCodePath, 1);
            return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(failed);
        }
    }

    private void RunAttempt(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        Func<ConductorParallelAcceptanceCandidate, ConductorAutonomyPolicy, ConductorParallelAcceptanceRunResult> runAcceptance)
    {
        try
        {
            WriteHeartbeat(attempt, "running");
            var run = runAcceptance(candidate, policy);
            WriteResult(attempt.ResultPath, ToArtifact(run));
            var outcome = OutcomeFor(run);
            TryWriteExit(attempt.ExitCodePath, outcome == ConductorParallelAcceptanceAttemptOutcome.Passed ? 0 : 1);
            Persist(attempt with
            {
                Outcome = outcome,
                CompletedAt = _utcNow(),
                LastHeartbeatAt = _utcNow(),
                Detail = AcceptanceRunDetail(run)
            });
            File.AppendAllText(attempt.StdoutPath, $"acceptance attempt {attempt.AttemptId} completed outcome={outcome}{Environment.NewLine}");
            WriteHeartbeat(attempt, "exiting");
        }
        catch (OperationCanceledException ex)
        {
            CompleteWithoutResult(attempt, ConductorParallelAcceptanceAttemptOutcome.Cancelled, ex.Message);
        }
        catch (Exception ex)
        {
            var run = ConductorParallelAcceptanceRunResult.Fault(candidate, ex);
            WriteResult(attempt.ResultPath, ToArtifact(run));
            var outcome = OutcomeFor(run);
            TryWriteExit(attempt.ExitCodePath, 1);
            Persist(attempt with
            {
                Outcome = outcome,
                CompletedAt = _utcNow(),
                LastHeartbeatAt = _utcNow(),
                Detail = ex.Message
            });
            TryAppend(attempt.StderrPath, $"{ex}{Environment.NewLine}");
            WriteHeartbeat(attempt, "exiting");
        }
        finally
        {
            lock (RunningTasksGate)
            {
                RunningTasks.Remove(attempt.AttemptId);
            }
        }
    }

    private void CompleteWithoutResult(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceAttemptOutcome outcome,
        string detail)
    {
        TryWriteExit(attempt.ExitCodePath, 1);
        Persist(attempt with
        {
            Outcome = outcome,
            CompletedAt = _utcNow(),
            LastHeartbeatAt = _utcNow(),
            Detail = detail
        });
        TryAppend(attempt.StderrPath, $"{outcome}: {detail}{Environment.NewLine}");
        WriteHeartbeat(attempt, "exiting");
    }

    private ConductorParallelAcceptanceAttemptDecision? TryCompleteRunningAttempt(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate)
    {
        if (File.Exists(attempt.ResultPath))
        {
            try
            {
                var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
                    File.ReadAllText(attempt.ResultPath),
                    JsonOptions);
                if (artifact is null)
                {
                    return MarkCorrupt(attempt, "result artifact was empty");
                }

                var run = FromArtifact(candidate, artifact);
                return ConductorParallelAcceptanceAttemptDecision.Completed(attempt, run);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                if (ex is IOException or UnauthorizedAccessException)
                {
                    return _isProcessAlive(attempt.OwnerProcessId) ? null : MarkCorrupt(attempt, ex.Message);
                }

                return MarkCorrupt(attempt, ex.Message);
            }
        }

        if (File.Exists(attempt.ExitCodePath))
        {
            return MarkCorrupt(attempt, "exit artifact exists without a result artifact");
        }

        if (!_isProcessAlive(attempt.OwnerProcessId))
        {
            var dead = attempt with
            {
                Outcome = ConductorParallelAcceptanceAttemptOutcome.ProcessDied,
                CompletedAt = _utcNow(),
                Detail = "owner process was not alive and no terminal result artifact existed"
            };
            Persist(dead);
            return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(dead);
        }

        return null;
    }

    private ConductorParallelAcceptanceAttemptDecision MarkCorrupt(
        ConductorParallelAcceptanceAttempt attempt,
        string detail)
    {
        var corrupt = attempt with
        {
            Outcome = ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts,
            CompletedAt = _utcNow(),
            Detail = detail
        };
        Persist(corrupt);
        return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(corrupt);
    }

    private ConductorParallelAcceptanceAttempt CreateAttempt(ConductorParallelAcceptanceCandidate candidate)
    {
        var startedAt = _utcNow();
        var rawId = $"{candidate.GoalPrefix}-{candidate.SlotIndex}-{startedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var id = rawId[..Math.Min(64, rawId.Length)];
        var directory = Path.Combine(_rootDirectory, candidate.Goal.Id.Value);
        Directory.CreateDirectory(directory);
        var prefix = Path.Combine(directory, id);
        return new ConductorParallelAcceptanceAttempt(
            id,
            candidate.Goal.Id.Value,
            candidate.GoalPrefix,
            candidate.SlotIndex,
            candidate.BranchHeadSha,
            candidate.MainHeadSha,
            startedAt,
            startedAt,
            Environment.ProcessId,
            ConductorParallelAcceptanceAttemptOutcome.Running,
            prefix + ".out.log",
            prefix + ".err.log",
            prefix + ".exit.txt",
            prefix + ".heartbeat.json",
            prefix + ".result.json",
            prefix + ".attempt.json");
    }

    private ConductorParallelAcceptanceAttempt? TryReadLatest(string goalId)
    {
        var directory = Path.Combine(_rootDirectory, goalId);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        ConductorParallelAcceptanceAttempt? latest = null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.attempt.json"))
        {
            ConductorParallelAcceptanceAttempt? attempt;
            try
            {
                attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new ConductorParallelAcceptanceAttempt(
                    Path.GetFileNameWithoutExtension(path),
                    goalId,
                    goalId[..Math.Min(8, goalId.Length)],
                    0,
                    null,
                    null,
                    _utcNow(),
                    _utcNow(),
                    0,
                    ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts,
                    path + ".out.log",
                    path + ".err.log",
                    path + ".exit.txt",
                    path + ".heartbeat.json",
                    path + ".result.json",
                    path,
                    CompletedAt: _utcNow(),
                    Detail: ex.Message);
            }

            if (attempt is null)
            {
                continue;
            }

            latest = latest is null || attempt.StartedAt > latest.StartedAt ? attempt : latest;
        }

        return latest;
    }

    private void Persist(ConductorParallelAcceptanceAttempt attempt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(attempt.MetadataPath) ?? _rootDirectory);
        var current = attempt with { LastHeartbeatAt = _utcNow() };
        var tmp = TemporarySiblingPath(attempt.MetadataPath);
        lock (MetadataWriteGate)
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(current, JsonOptions));
            File.Move(tmp, attempt.MetadataPath, overwrite: true);
        }
    }

    private void WriteHeartbeat(ConductorParallelAcceptanceAttempt attempt, string state)
    {
        var now = _utcNow();
        var payload = new
        {
            pid = Environment.ProcessId,
            childPid = (int?)null,
            ownedPids = Array.Empty<int>(),
            startedAt = attempt.StartedAt.ToString("O"),
            lastObservedAt = now.ToString("O"),
            lastProgressAt = now.ToString("O"),
            state,
            stdoutBytes = FileLength(attempt.StdoutPath),
            stderrBytes = FileLength(attempt.StderrPath),
            ownedCpuMs = 0L,
            exitFileExists = File.Exists(attempt.ExitCodePath)
        };

        try
        {
            var tmp = TemporarySiblingPath(attempt.HeartbeatPath);
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOptions));
            File.Move(tmp, attempt.HeartbeatPath, overwrite: true);
        }
        catch
        {
            // Heartbeat is evidence, not the gate result.
        }
    }

    private static ConductorParallelAcceptanceRunArtifact ToArtifact(ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is not null)
        {
            return new ConductorParallelAcceptanceRunArtifact(
                "fault",
                run.Exception switch
                {
                    DotnetBuildSlotsBusyException => "blocked-build-slot",
                    BuildLockBlockedException => "blocked-build-lock",
                    OperationCanceledException => "cancelled",
                    _ => "exception"
                },
                run.Exception.Message,
                null,
                null,
                null,
                null,
                null);
        }

        if (run.EarlyResult is { Outcome: var outcome })
        {
            return outcome switch
            {
                ConductorAdvanceOutcome.Held held => new("early-held", null, null, held.State.ToString(), held.Reason, null, null, null),
                ConductorAdvanceOutcome.Escalated escalated => new("early-escalated", null, null, escalated.State.ToString(), escalated.Reason, null, null, null),
                ConductorAdvanceOutcome.Done done => new("early-done", null, null, done.State.ToString(), null, null, null, null),
                ConductorAdvanceOutcome.Executed executed => new("early-executed", null, null, executed.FromState.ToString(), executed.Description, null, null, null),
                _ => new("fault", "exception", "unknown early acceptance result", null, null, null, null, null)
            };
        }

        return new ConductorParallelAcceptanceRunArtifact("accepted", null, null, null, null, run.Acceptance, null, null);
    }

    private static ConductorParallelAcceptanceRunResult FromArtifact(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorParallelAcceptanceRunArtifact artifact)
    {
        return artifact.Kind switch
        {
            "accepted" when artifact.Acceptance is not null =>
                ConductorParallelAcceptanceRunResult.Accepted(candidate, artifact.Acceptance),
            "early-held" => ConductorParallelAcceptanceRunResult.Early(
                candidate,
                new ConductorAdvanceResult(
                    candidate.Goal.Id.Value,
                    candidate.GoalPrefix,
                    string.Empty,
                    new ConductorAdvanceOutcome.Held(ParseState(artifact.State), artifact.Message ?? "held"))),
            "early-escalated" => ConductorParallelAcceptanceRunResult.Early(
                candidate,
                new ConductorAdvanceResult(
                    candidate.Goal.Id.Value,
                    candidate.GoalPrefix,
                    string.Empty,
                    new ConductorAdvanceOutcome.Escalated(ParseState(artifact.State), artifact.Message ?? "escalated"))),
            "early-done" => ConductorParallelAcceptanceRunResult.Early(
                candidate,
                new ConductorAdvanceResult(
                    candidate.Goal.Id.Value,
                    candidate.GoalPrefix,
                    string.Empty,
                    new ConductorAdvanceOutcome.Done(ParseState(artifact.State)))),
            "early-executed" => ConductorParallelAcceptanceRunResult.Early(
                candidate,
                new ConductorAdvanceResult(
                    candidate.Goal.Id.Value,
                    candidate.GoalPrefix,
                    string.Empty,
                    new ConductorAdvanceOutcome.Executed(ParseState(artifact.State), artifact.Message ?? "executed"))),
            "fault" => ConductorParallelAcceptanceRunResult.Fault(candidate, RehydrateFault(artifact)),
            _ => throw new InvalidOperationException("unrecognized acceptance attempt result artifact")
        };
    }

    private static Exception RehydrateFault(ConductorParallelAcceptanceRunArtifact artifact) =>
        artifact.FaultKind switch
        {
            "blocked-build-slot" => new DotnetBuildSlotsBusyException(
                new DotnetBuildLeaseAcquisition.SlotsBusy("background-acceptance", [])),
            "blocked-build-lock" => new BuildLockBlockedException(
                new BuildLockAttribution("unknown", [], "background-acceptance", "acceptance", "background-acceptance")),
            "cancelled" => new OperationCanceledException(artifact.FaultMessage),
            _ => new InvalidOperationException(artifact.FaultMessage ?? "background acceptance failed")
        };

    private static GoalLifecycleState ParseState(string? value) =>
        Enum.TryParse<GoalLifecycleState>(value, out var state) ? state : GoalLifecycleState.Verified;

    private static ConductorParallelAcceptanceAttemptOutcome OutcomeFor(ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is DotnetBuildSlotsBusyException)
        {
            return ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot;
        }

        if (run.Exception is BuildLockBlockedException)
        {
            return ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock;
        }

        if (run.Exception is OperationCanceledException)
        {
            return ConductorParallelAcceptanceAttemptOutcome.Cancelled;
        }

        if (run.Exception is not null)
        {
            return ConductorParallelAcceptanceAttemptOutcome.Failed;
        }

        if (run.EarlyResult is not null)
        {
            return run.EarlyResult.WasEscalated
                ? ConductorParallelAcceptanceAttemptOutcome.Failed
                : ConductorParallelAcceptanceAttemptOutcome.Passed;
        }

        return run.Acceptance?.Passed == true
            ? ConductorParallelAcceptanceAttemptOutcome.Passed
            : ConductorParallelAcceptanceAttemptOutcome.Failed;
    }

    private static string AcceptanceRunDetail(ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is not null)
        {
            return run.Exception.Message;
        }

        if (run.EarlyResult is not null)
        {
            return run.EarlyResult.Outcome.ToString() ?? "early result";
        }

        return run.Acceptance?.Passed == true ? "acceptance passed" : "acceptance failed";
    }

    private static void WriteResult(string path, ConductorParallelAcceptanceRunArtifact artifact)
    {
        var tmp = TemporarySiblingPath(path);
        File.WriteAllText(tmp, JsonSerializer.Serialize(artifact, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private static string TemporarySiblingPath(string path) =>
        $"{path}.{Guid.NewGuid():N}.tmp";

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch
        {
            return 0L;
        }
    }

    private static void TryWriteExit(string path, int exitCode)
    {
        try { File.WriteAllText(path, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch { }
    }

    private static void TryAppend(string path, string text)
    {
        try { File.AppendAllText(path, text); }
        catch { }
    }
}

internal sealed record ConductorParallelAcceptanceRunArtifact(
    string Kind,
    string? FaultKind,
    string? FaultMessage,
    string? State,
    string? Message,
    AcceptanceVerificationSummary? Acceptance,
    string? Reserved1,
    string? Reserved2);
