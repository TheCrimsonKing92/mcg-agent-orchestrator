using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record CompletionBuildChildLaunch(
    int Pid, DateTimeOffset StartedAt, string ImagePath, Action Authorize, Action Stop);

// SQLite task state owns scheduling; files below the existing build artifacts home are only receipts.
internal sealed class CompletionBuildEvidenceCoordinator : ICompletionBuildEvidenceOperation
{
    internal const string Subcommand = "__completion-build-evidence";
    internal const string OperationName = "conductor:completion-build-evidence";
    private readonly string _statePath;
    private readonly string _executionDirectory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string, TimeSpan> _remoteBuildBudget;
    private readonly Func<CompletionBuildEvidenceOperation, CompletionBuildEvidenceOperation?, CompletionBuildEvidenceOperation> _persist;
    private readonly Func<CompletionBuildEvidenceOperation, CompletionBuildChildLaunch> _launch;
    private readonly Func<CompletionBuildEvidenceOperation, CompletionBuildChildLiveness> _probe;
    private readonly Func<CompletionBuildEvidenceOperation, string?> _stop;
    private readonly Func<Goal, CompletionBuildChildLaunch, string, GoalEvidenceOperationStart> _begin;
    private readonly Action<Goal, CompletionBuildEvidenceOperation> _finish;
    private readonly Func<ProcessStartInfo, Process> _startProcess;
    private readonly Func<Process, SpawnProcessIdentityReadResult> _readIdentity;
    private readonly Func<Process, (TextReader Output, TextReader Error)> _hostReaders;
    private readonly Dictionary<string, CompletionBuildHostCapture> _captures = new();

    internal CompletionBuildEvidenceCoordinator(string statePath, string executionDirectory,
        Func<DateTimeOffset>? utcNow = null,
        Func<CompletionBuildEvidenceOperation, CompletionBuildEvidenceOperation?, CompletionBuildEvidenceOperation>? persist = null,
        Func<CompletionBuildEvidenceOperation, CompletionBuildChildLaunch>? launch = null,
        Func<CompletionBuildEvidenceOperation, CompletionBuildChildLiveness>? probe = null,
        Action<CompletionBuildEvidenceOperation>? stop = null,
        Func<Goal, CompletionBuildChildLaunch, string, GoalEvidenceOperationStart>? begin = null,
        Action<Goal, CompletionBuildEvidenceOperation>? finish = null,
        Func<ProcessStartInfo, Process>? startProcess = null,
        Func<Process, SpawnProcessIdentityReadResult>? readIdentity = null,
        Func<Process, (TextReader Output, TextReader Error)>? hostReaders = null,
        Func<string, TimeSpan>? remoteBuildBudget = null)
    {
        _statePath = statePath;
        _executionDirectory = executionDirectory;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _remoteBuildBudget = remoteBuildBudget ?? (worktree =>
        {
            try
            {
                var settings = RemoteLaneExecutorConfiguration.LoadForWorkerBuildCheck(
                    RemoteLaneExecutorConfiguration.ResolveStorePath(worktree)).WorkerBuildCheck;
                return settings.Mode == "remote-first" ? TimeSpan.FromSeconds(settings.RemoteSeconds) : TimeSpan.Zero;
            }
            catch (Exception) { return TimeSpan.Zero; }
        });
        _persist = persist ?? Persist;
        _launch = launch ?? Launch;
        _probe = probe ?? CompletionBuildEvidenceChildIdentity.Probe;
        _stop = stop is null ? StopChild : operation => { stop(operation); return null; };
        _begin = begin ?? Begin;
        _finish = finish ?? FinishLease;
        _startProcess = startProcess ?? (info => ProcessTreeGuiSuppression.Start(info)
            ?? throw new InvalidOperationException("Completion build child launch failed."));
        _readIdentity = readIdentity ?? SpawnProcessIdentityReader.ReadForRegistration;
        _hostReaders = hostReaders ?? (process => (process.StandardOutput, process.StandardError));
    }

    public CompletionBuildEvidenceOperation Reconcile(AgentOrchestratorKernel kernel, GoalId goalId,
        TaskId taskId, OrchestratorBuildCheckRequest request, TaskProcessRecord workerProcess)
    {
        var task = kernel.GetTask(goalId, taskId);
        var goal = kernel.GetGoal(goalId);
        var operation = task.CompletionBuildEvidence;
        if (operation is not null && (task.LastDispatch?.DispatchedAt != operation.DispatchAt ||
            workerProcess.ProcessId != operation.WorkerProcess.ProcessId || workerProcess.StartedAt != operation.WorkerProcess.StartedAt))
        {
            ReconcileAttached(kernel, goalId, taskId, CompletionBuildEvidenceTarget.From(operation));
            throw new InvalidOperationException("Historical completion build evidence cannot certify the current worker; use operation-only recovery.");
        }
        if (operation is null)
        {
            if (!WorktreeTreeDigest.TryCompute(request.WorktreeRoot, out var digest, out var reason))
                throw new InvalidOperationException($"Completion build candidate identity unavailable: {reason}");
            using var owner = Process.GetCurrentProcess();
            var id = Guid.NewGuid().ToString("N");
            operation = new CompletionBuildEvidenceOperation(id, goalId.Value, taskId.Value,
                task.LastDispatch!.DispatchedAt, BackgroundDispatchRunner.BuildDispatchId(goalId, taskId, task.LastDispatch),
                Path.GetFullPath(request.WorktreeRoot), digest, NormalizeProjects(request.Projects), workerProcess,
                _utcNow() + request.Timeout + _remoteBuildBudget(request.WorktreeRoot), CompletionBuildEvidencePhase.Launching,
                _statePath, _executionDirectory,
                Path.Combine(DotnetBuildStorageLayout.GoalArtifactsPath(goalId), "completion-build-evidence", id + ".result.json"),
                Environment.ProcessId, new DateTimeOffset(owner.StartTime.ToUniversalTime()));
            try { operation = Store(kernel, operation, null); }
            catch (Exception ex)
            {
                // No child has been launched. Report apparatus evidence, never a compile verdict.
                operation = operation with { Phase = CompletionBuildEvidencePhase.ApparatusFailure,
                    Diagnostic = $"Completion build launch intent could not be persisted: {ex.Message}" };
                kernel.RecordCompletionBuildEvidence(goalId, taskId, operation);
                return operation;
            }
            // A concurrent/restarted reconciliation can only adopt the first durable operation.
            if (operation.Id != id || operation.ChildPid is not null || !operation.IsPending)
                return Poll(kernel, goal, operation, request);
            return Start(kernel, goal, operation);
        }
        return Poll(kernel, goal, operation, request);
    }

    private CompletionBuildEvidenceOperation Start(AgentOrchestratorKernel kernel, Goal goal,
        CompletionBuildEvidenceOperation operation)
    {
        CompletionBuildChildLaunch? child = null;
        GoalEvidenceOperationScope? scope = null;
        var previous = operation;
        try
        {
            child = _launch(operation); // Waits on stdin; no build work is authorized yet.
            var start = _begin(goal, child, operation.Id);
            scope = start.Scope;
            if (!start.Acquired)
            {
                child.Stop();
                return Store(kernel, operation with
                { Phase = CompletionBuildEvidencePhase.WaitingForLease, Diagnostic = "Completion build lease is held by another operation." }, previous);
            }
            operation = Store(kernel, operation with
            {
                ChildPid = child.Pid, ChildStartedAt = child.StartedAt, ChildImagePath = child.ImagePath,
                LeaseOwner = start.LeaseFact!.Owner, Phase = CompletionBuildEvidencePhase.Pending
            }, previous);
            if (operation.ChildPid != child.Pid || !operation.IsPending)
                throw new InvalidOperationException("Completion build launch authorization lost its operation authority.");
            child.Authorize();
            // The child now owns this scope's exact lease and terminal journal entry. Do not dispose it here.
            return operation;
        }
        catch (Exception ex)
        {
            var diagnostic = ex.Message;
            try { child?.Stop(); }
            catch (Exception cleanup) { diagnostic += $" Cleanup failed: {cleanup.Message}"; }
            if (_captures.TryGetValue(operation.Id, out var held) && held.HasUnreapedChild)
                throw new InvalidOperationException($"Completion build launch child termination unconfirmed; custody is retained. {diagnostic}", ex);
            scope?.Abort("Completion build launch was not authorized.");
            scope?.Dispose();
            var terminal = operation with
            { Phase = CompletionBuildEvidencePhase.ApparatusFailure, Diagnostic = $"Completion build launch failed: {diagnostic}" };
            return Store(kernel, terminal, operation);
        }
    }

    private CompletionBuildEvidenceOperation Poll(AgentOrchestratorKernel kernel, Goal goal,
        CompletionBuildEvidenceOperation operation, OrchestratorBuildCheckRequest request)
    {
        operation = RefreshAttached(kernel, goal.Id, new TaskId(operation.TaskId), CompletionBuildEvidenceTarget.From(operation));
        if (operation.IsPending && ObserveTerminal(kernel, goal, operation) is { } observed)
            operation = observed;
        if (!operation.IsPending) operation = ReleaseCustody(kernel, goal, operation);
        if (!operation.IsPending && operation.Phase != CompletionBuildEvidencePhase.Success) return operation;
        var task = kernel.GetTask(goal.Id, new TaskId(operation.TaskId));
        if (operation.IsPending && (task.Status == WorkTaskStatus.Cancelled || task.LastDispatch?.DispatchedAt != operation.DispatchAt))
            return Terminal(kernel, goal, operation, CompletionBuildEvidencePhase.Cancelled, "Originating dispatch was cancelled or superseded.");
        if (!WorktreeTreeDigest.TryCompute(request.WorktreeRoot, out var digest, out _) ||
            digest != operation.TreeDigest || !NormalizeProjects(request.Projects).SequenceEqual(operation.Projects) ||
            !string.Equals(Path.GetFullPath(request.WorktreeRoot), operation.WorktreeRoot, StringComparison.OrdinalIgnoreCase))
            return Terminal(kernel, goal, operation, CompletionBuildEvidencePhase.StaleCandidate, "Completion build candidate or required projects changed.");
        if (!operation.IsPending) return operation;
        if (operation.ChildPid is null && _utcNow() >= operation.Deadline)
            return Terminal(kernel, goal, operation, CompletionBuildEvidencePhase.TimedOut, "Completion build evidence exceeded its deadline.");
        if (operation.Phase == CompletionBuildEvidencePhase.WaitingForLease)
            return Start(kernel, goal, Store(kernel, operation with { Phase = CompletionBuildEvidencePhase.Launching }, operation));
        if (operation.ChildPid is null)
        {
            // Never replay a launch intent after a crash in the launch/identity checkpoint window.
            // A child lacking authorization exits on stdin EOF; an authorized child always has durable identity.
            using var owner = TryGetProcess(operation.LaunchOwnerPid);
            if (owner is not null && !owner.HasExited &&
                new DateTimeOffset(owner.StartTime.ToUniversalTime()) == operation.LaunchOwnerStartedAt)
                return operation;
            return Terminal(kernel, goal, operation, CompletionBuildEvidencePhase.ApparatusFailure,
                "Completion build launch owner exited before durable child authorization.");
        }
        var liveness = _probe(operation);
        if (liveness == CompletionBuildChildLiveness.Unknown)
            return operation with { Diagnostic = "Completion build child liveness is unknown; reconciliation is held." };
        if (liveness == CompletionBuildChildLiveness.Live)
        {
            if (_utcNow() >= operation.Deadline)
                return Terminal(kernel, goal, operation, CompletionBuildEvidencePhase.TimedOut, "Completion build evidence exceeded its deadline.");
            return operation;
        }
        return ObserveTerminal(kernel, goal, operation) ?? operation;
    }

    private CompletionBuildEvidenceOperation? ObserveTerminal(AgentOrchestratorKernel kernel, Goal goal,
        CompletionBuildEvidenceOperation operation)
    {
        if (!operation.IsPending || operation.ChildPid is null || _probe(operation) != CompletionBuildChildLiveness.Dead)
            return null;
        if (_captures.TryGetValue(operation.Id, out var capture) && capture.Matches(operation))
        {
            var captureFailure = capture.Collect();
            if (capture.HasUnreapedChild)
                throw new InvalidOperationException("Completion build child termination unconfirmed; custody is retained.");
            if (!capture.HasUnreapedChild) _captures.Remove(operation.Id);
            if (capture.DeadlineExceeded)
                return SaveTerminal(kernel, goal, operation, CompletionBuildEvidencePhase.TimedOut,
                    captureFailure ?? "Completion build child exceeded its deadline.");
            if (captureFailure is not null)
                return SaveTerminal(kernel, goal, operation, CompletionBuildEvidencePhase.ApparatusFailure, captureFailure);
        }
        if (CompletionBuildHostCapture.ReadRecordedFailure(operation) is { } recordedFailure)
            return SaveTerminal(kernel, goal, operation, recordedFailure.Phase, recordedFailure.Diagnostic);
        CompletionBuildEvidenceOperation? result = null;
        try
        {
            if (File.Exists(operation.ResultPath))
                result = JsonSerializer.Deserialize<CompletionBuildEvidenceOperation>(File.ReadAllText(operation.ResultPath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return SaveTerminal(kernel, goal, operation, CompletionBuildEvidencePhase.ApparatusFailure, $"Completion build result unreadable: {ex.Message}");
        }
        if (result is null || result.IsPending || !MatchesResult(operation, result))
            return SaveTerminal(kernel, goal, operation, CompletionBuildEvidencePhase.ApparatusFailure, "Completion build child exited without a matching terminal receipt.");
        return SaveTerminal(kernel, goal, operation, result.Phase, result.Diagnostic, result.BuildExitCode);
    }

    internal static bool MatchesResult(CompletionBuildEvidenceOperation expected, CompletionBuildEvidenceOperation result) =>
        result.WorkerProcess is not null && result.Projects is not null &&
        expected.HasSameOrigin(result) &&
        result.Id == expected.Id && result.GoalId == expected.GoalId && result.TaskId == expected.TaskId &&
        result.DispatchId == expected.DispatchId && result.DispatchAt == expected.DispatchAt &&
        result.TreeDigest == expected.TreeDigest && result.WorktreeRoot == expected.WorktreeRoot &&
        result.ChildPid == expected.ChildPid && result.ChildStartedAt == expected.ChildStartedAt &&
        result.ChildImagePath == expected.ChildImagePath && result.LeaseOwner == expected.LeaseOwner &&
        result.WorkerProcess.ProcessId == expected.WorkerProcess.ProcessId &&
        result.WorkerProcess.StartedAt == expected.WorkerProcess.StartedAt &&
        result.WorkerProcess.ExitCode == expected.WorkerProcess.ExitCode &&
        result.WorkerProcess.StandardOutputPath == expected.WorkerProcess.StandardOutputPath &&
        result.WorkerProcess.StandardErrorPath == expected.WorkerProcess.StandardErrorPath &&
        result.Deadline == expected.Deadline && Enum.IsDefined(result.Phase) &&
        (result.Phase != CompletionBuildEvidencePhase.Success || result.BuildExitCode == 0) &&
        result.Projects.SequenceEqual(expected.Projects);

    public void Cancel(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        if (kernel.GetTask(goalId, taskId).CompletionBuildEvidence is { IsPending: true } operation)
        {
            var disposition = CancelAttached(kernel, goalId, taskId, CompletionBuildEvidenceTarget.From(operation));
            if (disposition.IsPending || !disposition.CustodyReleased)
                throw new InvalidOperationException("Completion build cancellation is held; custody is retained.");
        }
    }

    public CompletionBuildEvidenceOperation ReconcileAttached(AgentOrchestratorKernel kernel, GoalId goalId,
        TaskId taskId, CompletionBuildEvidenceTarget target)
    {
        var operation = RefreshAttached(kernel, goalId, taskId, target);
        var goal = kernel.GetGoal(goalId);
        if (!operation.IsPending) return ReleaseCustody(kernel, goal, operation);
        if (ObserveTerminal(kernel, goal, operation) is { } terminal) return terminal;
        // Recovery is lifecycle-only. It never launches or restores the original worker.
        var task = kernel.GetTask(goalId, taskId);
        if (task.Status == WorkTaskStatus.Cancelled || task.LastDispatch?.DispatchedAt != operation.DispatchAt || task.LastProcess is null)
            return Terminal(kernel, goal, operation, CompletionBuildEvidencePhase.Cancelled, "Originating dispatch was cancelled or superseded.");
        if (_utcNow() >= operation.Deadline)
            return Terminal(kernel, goal, operation, CompletionBuildEvidencePhase.TimedOut, "Completion build evidence exceeded its deadline.");
        return operation;
    }

    public CompletionBuildEvidenceOperation CancelAttached(AgentOrchestratorKernel kernel, GoalId goalId,
        TaskId taskId, CompletionBuildEvidenceTarget target)
    {
        var operation = RefreshAttached(kernel, goalId, taskId, target);
        return !operation.IsPending ? ReleaseCustody(kernel, kernel.GetGoal(goalId), operation) :
            Terminal(kernel, kernel.GetGoal(goalId), operation, CompletionBuildEvidencePhase.Cancelled,
                "Operator cancelled completion build evidence.");
    }

    private CompletionBuildEvidenceOperation RefreshAttached(AgentOrchestratorKernel kernel, GoalId goalId,
        TaskId taskId, CompletionBuildEvidenceTarget target)
    {
        var operation = kernel.GetTask(goalId, taskId).CompletionBuildEvidence
            ?? throw new InvalidOperationException("No attached completion build operation.");
        if (!target.Matches(operation)) throw new InvalidOperationException("Completion build operation selector mismatch.");
        ValidateOrigin(kernel.GetTask(goalId, taskId), goalId, operation);
        var saved = _persist(operation, operation); // Re-read durable authority at the action boundary.
        if (!target.Matches(saved) || !operation.HasSameOrigin(saved))
            throw new InvalidOperationException("Completion build operation authority changed.");
        ValidateOrigin(kernel.GetTask(goalId, taskId), goalId, saved);
        kernel.RecordCompletionBuildEvidence(goalId, taskId, saved);
        return saved;
    }

    private void ValidateOrigin(TaskSpec task, GoalId goalId, CompletionBuildEvidenceOperation operation)
    {
        if (CompletionBuildOriginPredicates.FirstFailure(task, goalId, operation, _statePath, _executionDirectory) is { } failure)
            throw new CompletionBuildOriginRejectedException(failure.PredicateName, failure.Observed, failure.Expected);
        if (operation.ChildPid is not null && (operation.ChildStartedAt is null || string.IsNullOrWhiteSpace(operation.ChildImagePath) ||
            !GoalEvidenceOperationOwner.TryParse(operation.LeaseOwner, out var owner) || owner is null ||
            owner.Operation != OperationName || owner.InstanceId != operation.Id || owner.ProcessId != operation.ChildPid))
            throw new InvalidOperationException("Completion build operation child/lease identity is unproven.");
    }

    private CompletionBuildEvidenceOperation Terminal(AgentOrchestratorKernel kernel, Goal goal,
        CompletionBuildEvidenceOperation operation, CompletionBuildEvidencePhase phase, string diagnostic)
    {
        if (_captures.TryGetValue(operation.Id, out var held) && held.HasUnreapedChild)
            throw new InvalidOperationException("Completion build child termination unconfirmed; custody is retained.");
        if (HasTerminalFact(operation) && ObserveTerminal(kernel, goal, operation) is { } observed) return observed;
        if (operation.ChildPid is not null && _probe(operation) == CompletionBuildChildLiveness.Unknown)
            return operation with { Diagnostic = "Completion build child liveness is unknown; reconciliation is held." };
        if (operation.ChildPid is not null && _stop(operation) is { } captureFailure)
            diagnostic += " " + captureFailure;
        // A terminal receipt may have arrived during termination. Preserve it before fallback disposition.
        if (operation.ChildPid is not null && HasTerminalFact(operation) &&
            ObserveTerminal(kernel, goal, operation) is { } completed) return completed;
        return SaveTerminal(kernel, goal, operation, phase, diagnostic);
    }

    private bool HasTerminalFact(CompletionBuildEvidenceOperation operation) => File.Exists(operation.ResultPath) ||
        _captures.TryGetValue(operation.Id, out var capture) && capture.Matches(operation) && capture.DeadlineExceeded ||
        CompletionBuildHostCapture.ReadRecordedFailure(operation)?.Phase == CompletionBuildEvidencePhase.TimedOut;

    private CompletionBuildEvidenceOperation SaveTerminal(AgentOrchestratorKernel kernel, Goal goal,
        CompletionBuildEvidenceOperation operation, CompletionBuildEvidencePhase phase, string diagnostic, int? exitCode = null) =>
        ReleaseCustody(kernel, goal, Store(kernel, operation with
        { Phase = phase, Diagnostic = diagnostic, BuildExitCode = exitCode }, operation));

    private CompletionBuildEvidenceOperation ReleaseCustody(AgentOrchestratorKernel kernel, Goal goal,
        CompletionBuildEvidenceOperation operation)
    {
        if (operation.IsPending) return operation;
        if (_captures.TryGetValue(operation.Id, out var capture))
        {
            if (!capture.Matches(operation) || capture.HasUnreapedChild)
                throw new InvalidOperationException("Completion build held child identity or termination is unproven; custody is retained.");
            _stop(operation);
        }
        if (operation.CustodyReleased) return operation;
        if (operation.ChildPid is not null && _probe(operation) != CompletionBuildChildLiveness.Dead)
            throw new InvalidOperationException("Completion build terminal child is not proven dead; custody is retained.");
        _finish(goal, operation);
        return Store(kernel, operation with { CustodyReleased = true }, operation);
    }

    private CompletionBuildEvidenceOperation Store(AgentOrchestratorKernel kernel,
        CompletionBuildEvidenceOperation operation, CompletionBuildEvidenceOperation? previous)
    {
        var saved = _persist(operation, previous);
        kernel.RecordCompletionBuildEvidence(new GoalId(saved.GoalId), new TaskId(saved.TaskId), saved);
        return saved;
    }

    private CompletionBuildEvidenceOperation Persist(CompletionBuildEvidenceOperation operation,
        CompletionBuildEvidenceOperation? previous) => new SqliteOrchestratorStateRepository(_statePath)
        .TransactGoalStateAsync(new GoalId(operation.GoalId), (state, _) =>
        {
            var task = state?.Goal.Tasks.SingleOrDefault(t => t.Id == operation.TaskId);
            if (task is null) throw new InvalidOperationException("Completion build evidence lost task authority.");
            var loaded = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([state!.Goal], []));
            ValidateOrigin(loaded.GetTask(new GoalId(operation.GoalId), new TaskId(operation.TaskId)), new GoalId(operation.GoalId), operation);
            var current = task.CompletionBuildEvidence;
            if (current is null && (previous is not null || task.LastDispatch?.DispatchedAt != operation.DispatchAt || task.Status == WorkTaskStatus.Cancelled))
                throw new InvalidOperationException("Completion build evidence lost attached operation authority.");
            if (current is not null && (previous is null || !current.HasSameOrigin(operation) ||
                !CompletionBuildEvidenceTarget.From(current).Matches(previous) || current.Phase != previous.Phase ||
                current.Applied != previous.Applied || current.CustodyReleased != previous.CustodyReleased))
                return Task.FromResult((false, state, current));
            if (current is not null && operation.Phase == current.Phase && operation.CustodyReleased == current.CustodyReleased &&
                operation.Applied == current.Applied && CompletionBuildEvidenceTarget.From(current).Matches(operation) && operation.Diagnostic == current.Diagnostic)
                return Task.FromResult((false, state, current));
            loaded.RecordCompletionBuildEvidence(new GoalId(operation.GoalId), new TaskId(operation.TaskId), operation);
            var updated = state! with { Goal = state!.Goal with
            { Tasks = state.Goal.Tasks.Select(t => t.Id == task.Id ? t with { CompletionBuildEvidence = operation } : t).ToArray() } };
            return Task.FromResult((true, (GoalStateSnapshot?)updated, operation));
        }).GetAwaiter().GetResult();

    private GoalEvidenceOperationStart Begin(Goal goal, CompletionBuildChildLaunch child, string id) =>
        new GoalEvidenceOperationCoordinator(new ReconcileSweepRemediationStore(_statePath), _executionDirectory,
            TimeSpan.FromMinutes(10), _utcNow, () => child.Pid, () => id, () => child.StartedAt,
            new ConductLockPidProbe()).TryBegin(goal, OperationName);

    private void FinishLease(Goal goal, CompletionBuildEvidenceOperation operation)
    {
        if (!GoalEvidenceOperationOwner.TryParse(operation.LeaseOwner, out var owner) || owner is null) return;
        var entries = GoalOperationJournal.ReadStrict(_executionDirectory, goal.Id).Entries
            .Where(e => e.OperationInstanceId == owner.InstanceId && e.Operation == owner.Operation).ToArray();
        if (!entries.Any(e => e.Status is GoalOperationStatus.Completed or GoalOperationStatus.Failed or GoalOperationStatus.Aborted))
            GoalOperationJournal.Aborted(_executionDirectory, goal, owner.Operation,
                "Completion build child is terminal; reconciled by conductor.", operationInstanceId: owner.InstanceId);
        new ReconcileSweepRemediationStore(_statePath).ReleaseAcceptanceLease(goal.Id.Value, operation.LeaseOwner!);
    }

    private CompletionBuildChildLaunch Launch(CompletionBuildEvidenceOperation operation)
    {
        var capture = new CompletionBuildHostCapture(operation, _utcNow);
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = operation.WorktreeRoot,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(typeof(CompletionBuildEvidenceCoordinator).Assembly.Location);
        foreach (var argument in new[] { Subcommand, _statePath, operation.GoalId, operation.TaskId, operation.Id })
            info.ArgumentList.Add(argument);
        try
        {
            var process = _startProcess(info);
            capture.Attach(process, _hostReaders);
            var read = _readIdentity(process);
            capture.Registration = read;
            if (!read.Succeeded)
                throw new InvalidOperationException($"Completion build child identity unavailable. {read.Evidence}");
            var identity = read.Identity!;
            if (identity.ProcessId != process.Id || process.HasExited)
                throw new InvalidOperationException("Completion build child exited or registration described a different child.");
            _captures[operation.Id] = capture;
            return new(identity.ProcessId, identity.StartedAt, identity.ImagePath,
                () => capture.Authorize(operation.Id), () =>
                {
                    var failure = capture.Stop();
                    if (!capture.HasUnreapedChild && _captures.GetValueOrDefault(operation.Id) == capture) _captures.Remove(operation.Id);
                    if (failure is not null) throw new InvalidOperationException(failure);
                });
        }
        catch (Exception ex)
        {
            capture.LaunchFailure = ex.ToString();
            var cleanup = capture.Stop();
            if (capture.HasUnreapedChild) _captures[operation.Id] = capture;
            else if (_captures.GetValueOrDefault(operation.Id) == capture) _captures.Remove(operation.Id);
            throw new InvalidOperationException($"{ex.Message} Host diagnostics: {capture.MetadataPath}." +
                (cleanup is null ? string.Empty : $" {cleanup}"), ex);
        }
    }

    private string? StopChild(CompletionBuildEvidenceOperation operation)
    {
        if (_captures.TryGetValue(operation.Id, out var capture) && capture.Matches(operation))
        {
            var failure = capture.Stop();
            if (capture.HasUnreapedChild)
                throw new InvalidOperationException(failure ?? "Completion build child termination unconfirmed.");
            _captures.Remove(operation.Id);
            // Diagnostic custody faults do not revoke confirmed termination or prevent
            // the caller from releasing the lease and persisting cancellation.
            return failure;
        }
        CompletionBuildEvidenceChildIdentity.Stop(operation);
        return null;
    }

    internal static int RunChild(string[] args)
    {
        if (args.Length != 5) return 2;
        var permit = Console.ReadLine();
        if (permit != args[4]) return 2;
        var kernel = new SqliteOrchestratorStateRepository(args[1])
            .LoadGoalsAsync([new GoalId(args[2])]).GetAwaiter().GetResult();
        var goal = kernel.GetGoal(new GoalId(args[2]));
        var operation = kernel.GetTask(goal.Id, new TaskId(args[3])).CompletionBuildEvidence;
        var identity = DispatchProcessIdentityEvidence.ReadCurrent(Environment.ProcessId);
        if (operation is not { Phase: CompletionBuildEvidencePhase.Pending } || operation.Id != permit ||
            identity is null || operation.ChildPid != identity.ProcessId || operation.ChildStartedAt != identity.StartedAt ||
            operation.ChildImagePath != identity.ImagePath ||
            !GoalEvidenceOperationOwner.TryParse(operation.LeaseOwner, out var owner) || owner is null)
            return 2;
        var store = new ReconcileSweepRemediationStore(args[1]);
        if (store.TryGetAcceptanceLease(goal.Id.Value, TimeSpan.FromMinutes(10))?.Owner != operation.LeaseOwner) return 2;
        using var scope = new GoalEvidenceOperationScope(store, operation.ExecutionDirectory, goal, owner,
            operation.LeaseOwner!, null, () => DateTimeOffset.UtcNow);
        var result = operation;
        try
        {
            var remaining = operation.Deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new OperationCanceledException();
            if (!WorktreeTreeDigest.TryCompute(operation.WorktreeRoot, out var digest, out _) || digest != operation.TreeDigest)
                result = operation with { Phase = CompletionBuildEvidencePhase.StaleCandidate, Diagnostic = "Candidate changed before build authorization." };
            else
            {
                RemoteLaneExecutorConfiguration configuration;
                try
                {
                    configuration = RemoteLaneExecutorConfiguration.LoadForWorkerBuildCheck(
                        RemoteLaneExecutorConfiguration.ResolveStorePath(operation.WorktreeRoot));
                }
                catch (Exception) { configuration = new([], [], "invalid"); }
                var receiptPath = Path.Combine(DotnetBuildStorageLayout.GoalArtifactsPath(goal.Id), WorkerBuildReceipt.FileName);
                var executor = new SshRemoteLaneExecutor(configuration, operation.WorktreeRoot, operation.ResultPath,
                    TimeProvider.System, GoalAcceptanceVerifier.RunRemoteLaneTransportAsync, GitCli.Run);
                var placement = CompletionBuildPlacement.Run(operation, receiptPath,
                    Path.Combine(Path.GetDirectoryName(RemoteLaneExecutorConfiguration.ResolveStorePath(operation.WorktreeRoot))!, "remote-build-check.jsonl"),
                    configuration.WorkerBuildCheck,
                    request => RemoteWorkerBuildCheck.RunAsync(request, new(configuration, executor, TimeProvider.System,
                        TimeSpan.FromSeconds(1), RemoteBuildCandidateCommit.RunGit)),
                    budget => CompletionBuildPlacement.RunLocal(operation, receiptPath, budget,
                        () => CompletionBuildPlacement.DefaultLocalBuild(operation, budget)), TimeProvider.System);
                result = operation with
                {
                    Phase = placement.Phase, BuildExitCode = placement.BuildExitCode, Diagnostic = placement.Diagnostic
                };
            }
        }
        catch (OperationCanceledException) { result = operation with { Phase = CompletionBuildEvidencePhase.TimedOut, Diagnostic = "Completion build evidence exceeded its deadline." }; }
        catch (Exception ex) { result = operation with { Phase = CompletionBuildEvidencePhase.ApparatusFailure, Diagnostic = ex.ToString() }; }
        Directory.CreateDirectory(Path.GetDirectoryName(operation.ResultPath)!);
        var temporary = operation.ResultPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result));
        File.Move(temporary, operation.ResultPath);
        if (result.Phase == CompletionBuildEvidencePhase.Success) scope.Complete(result.Diagnostic);
        else scope.Fail(result.Diagnostic);
        return result.Phase == CompletionBuildEvidencePhase.Success ? 0 : 1;
    }

    internal static string[] NormalizeProjects(IEnumerable<string> projects) => projects
        .Select(p => p.Replace('\\', '/').Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    private static Process? TryGetProcess(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }
}
