using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class WorkerBuildRequestHost : IWorkerBuildRequestService
{
    private readonly WorkerBuildHostReference _reference;
    private readonly WorkerBuildInvocationOperations _operations;
    private readonly WorkerBuildProcess _host;
    private readonly TimeProvider _time;
    private readonly TimeSpan _budget;
    private readonly Action<WorkerBuildInvocation>? _observe;
    private readonly DotnetBuildStorageRoot _storageRoot;
    private readonly List<object> _unresolved = [];

    internal WorkerBuildRequestHost(WorkerBuildHostReference reference, TimeProvider? time = null,
        TimeSpan? budget = null, Action<WorkerBuildInvocation>? observe = null, DotnetBuildStorageRoot? storageRoot = null)
    {
        _reference = reference;
        _operations = new(reference.StatePath, reference.ExecutionDirectory);
        using var host = Process.GetCurrentProcess();
        _host = WorkerBuildRequestChannel.ReadIdentity(host);
        _time = time ?? TimeProvider.System;
        _budget = budget ?? OrchestratorBuildEvidenceCheck.DefaultTimeout;
        _observe = observe;
        _storageRoot = storageRoot ?? (reference.StorageRoot is { } root
            ? new DotnetBuildStorageRoot(root) : DotnetBuildStorageLayout.CaptureStorageRoot());
        if (reference.Admission is not { } admission || admission.GoalId != reference.GoalId ||
            admission.TaskId != reference.TaskId || admission.DispatchedAt != reference.DispatchAt)
            throw new UnauthorizedAccessException("Worker build host lacks exact dispatch admission authority.");
        WorkerBuildInvocationOperations.RequireOrigin(_operations.Load(reference.GoalId), reference, _host, true);
    }

    public Task<WorkerBuildResponse> ExecuteAsync(WorkerBuildRequest request, WorkerBuildProcess worker,
        CancellationToken workerLifetime) => Task.Run(() => Execute(request, worker, workerLifetime), CancellationToken.None);

    private WorkerBuildResponse Execute(WorkerBuildRequest request, WorkerBuildProcess worker, CancellationToken lifetime)
    {
        var kernel = _operations.Load(_reference.GoalId);
        var task = WorkerBuildInvocationOperations.RequireOrigin(kernel, _reference, _host, true);
        var root = Path.GetFullPath(task.LastDispatch!.WorkingDirectory);
        var scope = WorkerBuildProjectScope.Resolve(root, request.Projects,
            expectedMetadataDigest: task.LastDispatch.WorkerBuildScope!.MetadataDigest);
        if (!WorktreeTreeDigest.TryCompute(root, out var digest, out var reason))
            throw new InvalidOperationException("Worker build candidate unavailable: " + reason);
        var previous = task.WorkerBuildInvocations.SingleOrDefault(i =>
            i.DispatchId == _reference.DispatchId && i.RequestKey == request.RequestKey);
        if (previous is not null)
        {
            if (previous.Worker != worker || previous.TreeDigest != digest || previous.ScopeIdentity != scope.Identity ||
                previous.Configuration != request.Configuration || !previous.Projects.SequenceEqual(scope.Projects))
                throw new UnauthorizedAccessException("Worker build idempotency key was reused for another payload/candidate.");
            return new(previous.Id, previous.Outcome, previous.Diagnostic);
        }
        var invocation = new WorkerBuildInvocation(Guid.NewGuid().ToString("N"), request.RequestKey,
            _reference.GoalId, _reference.TaskId, _reference.DispatchAt, _reference.DispatchId, root, digest,
            scope.Identity, scope.Projects, request.Configuration, _time.GetUtcNow() + _budget, _host, worker,
            Path.GetFullPath(_reference.Admission!.StorePath), _reference.Admission.Version, 1,
            [new(WorkerBuildPhase.Requested, _time.GetUtcNow())]);
        invocation = _operations.Store(invocation, null);
        _observe?.Invoke(invocation);
        GoalEvidenceOperationScope? custody = null;
        DotnetBuildEnvironmentLease? slot = null;
        OwnedProcessGroup.SuspendedProcessStart? child = null;
        using var deadline = new CancellationTokenSource(_budget, _time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime, deadline.Token);
        var terminalSafe = true;
        try
        {
            Advance(WorkerBuildPhase.Authorized);
            var owner = new GoalEvidenceOperationOwner(WorkerBuildInvocationOperations.OperationName, _host.Pid, invocation.Id);
            Save(invocation with { LeaseOwner = owner.Serialize() });
            Advance(WorkerBuildPhase.WaitingForCustody);
            while (custody is null)
            {
                CheckCurrent();
                var current = _operations.Load(_reference.GoalId);
                var begin = new GoalEvidenceOperationCoordinator(new ReconcileSweepRemediationStore(_reference.StatePath),
                    _reference.ExecutionDirectory, TimeSpan.FromMinutes(10), () => _time.GetUtcNow(),
                    () => _host.Pid, () => invocation.Id, () => _host.StartedAt, new ConductLockPidProbe())
                    .TryBegin(current.GetGoal(new GoalId(_reference.GoalId)), WorkerBuildInvocationOperations.OperationName);
                custody = begin.Scope;
                if (custody is null) stop.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
            }
            Advance(WorkerBuildPhase.CustodyAcquired);
            Advance(WorkerBuildPhase.WaitingForBuildSlot);
            var environment = DotnetBuildEnvironmentManager.CreateAttempt(new GoalId(invocation.GoalId),
                "worker-host-" + invocation.Id, storageRoot: _storageRoot, repositoryRoot: root);
            var acquisition = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment,
                invocation.Deadline - _time.GetUtcNow(), stop.Token,
                onWait: () => Advance(WorkerBuildPhase.WaitingForBuildSlot), timeProvider: _time);
            if (acquisition is DotnetBuildLeaseAcquisition.SlotsBusy)
            {
                Complete(WorkerBuildOutcome.CapacityBeforeStart, null, "Host build-slot acquisition ended before process launch.");
            }
            else if (acquisition is not DotnetBuildLeaseAcquisition.Acquired acquired)
            {
                Complete(WorkerBuildOutcome.ApparatusFailure, null, "Host build-slot acquisition was blocked by apparatus.");
            }
            else
            {
                slot = acquired.Lease;
                var logRoot = Path.Combine(slot.Environment.ArtifactsPath, "worker-host-builds", invocation.Id);
                Directory.CreateDirectory(logRoot);
                // Exec adds MSBuildTemp<user>/tmp<guid>.exec.cmd beneath TEMP. cmd.exe
                // cannot execute that batch file beyond MAX_PATH, even when .NET can
                // create it. Keep invocation isolation without nesting below the logs.
                var tempRoot = Path.Combine(slot.Environment.ArtifactsPath, "tmp", invocation.Id);
                Directory.CreateDirectory(tempRoot);
                if (task.LastDispatch.SandboxLowIntegrity &&
                    !new IcaclsIntegrityLabeler().SetIntegrity(slot.Environment.ArtifactsPath, "(OI)(CI)L", recursive: true))
                    throw new UnauthorizedAccessException("Host compiler output LowIL preparation failed.");
                foreach (var project in scope.Projects)
                {
                    CheckCurrent();
                    Advance(WorkerBuildPhase.PreparingLaunch);
                    var info = BuildStartInfo(root, project, request.Configuration, slot.Environment.Arguments, tempRoot);
                    terminalSafe = false;
                    child = OwnedProcessGroup.StartSuspendedContainedWithFileCapture(info,
                        Path.Combine(logRoot, $"{invocation.Children?.Count ?? 0}.out.log"),
                        Path.Combine(logRoot, $"{invocation.Children?.Count ?? 0}.err.log"));
                    var read = child.ReadLifecycleIdentity(child.Process.Id);
                    if (read.Status != ProcessInspectionStatus.Available || read.StartedAt is not { } at ||
                        string.IsNullOrWhiteSpace(read.ExecutablePath))
                        throw new UnauthorizedAccessException("Owned compiler process identity unavailable.");
                    var identity = new WorkerBuildProcess(child.Process.Id, at, read.ExecutablePath);
                    Save(invocation with { Children = (invocation.Children ?? []).Append(identity).ToArray() });
                    Advance(WorkerBuildPhase.ChildRegistered);
                    if (task.LastDispatch.SandboxLowIntegrity) child.LowerIntegrityBeforeResume();
                    TaskIntentAdmission.ExecuteAuthorized(_reference.Admission!, "worker-build:" + invocation.Id + ":" + identity.Pid, () =>
                    {
                        CheckCurrent();
                        // No user code executes before durable registration and this canonical/source check.
                        child.Resume();
                        Advance(WorkerBuildPhase.CompilerStarted);
                    });
                    while (!child.WaitForOwnedExit(TimeSpan.FromMilliseconds(100))) CheckCurrent(validateCandidate: false);
                    var exit = child.TryReadOwnedExitCode() ?? throw new InvalidOperationException("Owned compiler exit was not observed.");
                    if (!child.Group.TerminateAndConfirmEmpty(TimeSpan.FromSeconds(10)))
                        throw new InvalidOperationException("Owned compiler descendants did not become terminal.");
                    terminalSafe = true;
                    child.Dispose();
                    child = null;
                    CheckCurrent();
                    if (exit != 0) { Complete(WorkerBuildOutcome.CompilerFailure, exit, "Host observed compiler/build exit failure."); break; }
                }
                if (!invocation.IsTerminal) Complete(WorkerBuildOutcome.Success, 0, "Host observed all scoped compiler/build exits successfully.");
            }
        }
        catch (OperationCanceledException)
        {
            StopChild();
            var started = invocation.Observations.Any(o => o.Phase == WorkerBuildPhase.CompilerStarted);
            var outcome = lifetime.IsCancellationRequested || !deadline.IsCancellationRequested || !WorkerBuildInvocationOperations.IsExactProcessAlive(worker)
                ? WorkerBuildOutcome.Cancelled
                : started ? WorkerBuildOutcome.TimeoutAfterStart
                : invocation.Observations.Any(o => o.Phase == WorkerBuildPhase.PreparingLaunch)
                    ? WorkerBuildOutcome.Interrupted : invocation.Phase == WorkerBuildPhase.WaitingForBuildSlot
                        ? WorkerBuildOutcome.CapacityBeforeStart : WorkerBuildOutcome.ApparatusFailure;
            Complete(outcome, null, "Host observed lifetime cancellation or deadline.");
        }
        catch (TaskIntentDispatchSuppressedException)
        {
            StopChild();
            Complete(WorkerBuildOutcome.Cancelled, null, "Operator intent revoked dispatch action authority.");
        }
        catch (WorkerBuildCandidateChangedException)
        {
            StopChild();
            Complete(WorkerBuildOutcome.StaleCandidate, null, "Candidate content or project scope changed before launch/completion.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StopChild();
            Complete(WorkerBuildOutcome.ApparatusFailure, null, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (terminalSafe)
            {
                child?.Dispose();
                slot?.Dispose();
                if (invocation.IsTerminal)
                {
                    if (invocation.Outcome == WorkerBuildOutcome.Success) custody?.Complete(invocation.Id);
                    else custody?.Abort(invocation.Id);
                    custody?.Dispose();
                    Save(invocation with { CustodyReleased = true });
                }
            }
            else
            {
                if (child is not null) _unresolved.Add(child);
                if (slot is not null) _unresolved.Add(slot);
                if (custody is not null) _unresolved.Add(custody);
            }
            // Unknown termination must keep the obligation/custody; no retry or force unlock.
        }
        return new(invocation.Id, invocation.Outcome, invocation.Diagnostic);

        void Save(WorkerBuildInvocation next)
        {
            invocation = _operations.Store(next with { Version = invocation.Version + 1 }, invocation.Version);
            _observe?.Invoke(invocation);
        }
        void Advance(WorkerBuildPhase phase) => Save(invocation with
        { Observations = invocation.Observations.Append(new(phase, _time.GetUtcNow())).ToArray() });
        void Complete(WorkerBuildOutcome outcome, int? exit, string diagnostic)
        {
            if (!terminalSafe) throw new InvalidOperationException("Compiler termination is unconfirmed; terminal obligation retained.");
            if (invocation.IsTerminal) return;
            Save(invocation with { Outcome = outcome, CompilerExitCode = exit, Diagnostic = diagnostic,
                Observations = invocation.Observations.Append(new(WorkerBuildPhase.Completed, _time.GetUtcNow())).ToArray() });
        }
        void CheckCurrent(bool validateCandidate = true)
        {
            stop.Token.ThrowIfCancellationRequested();
            if (!WorkerBuildInvocationOperations.IsExactProcessAlive(worker)) throw new OperationCanceledException("Worker exited.");
            TaskIntentAdmission.ExecuteAuthorized(_reference.Admission!, "worker-build-lifetime:" + invocation.Id, () => { });
            var latestKernel = _operations.Load(_reference.GoalId);
            if (latestKernel.GetTask(new GoalId(_reference.GoalId), new TaskId(_reference.TaskId)).Status == WorkTaskStatus.Cancelled)
                throw new OperationCanceledException("Dispatch cancelled.");
            var latest = WorkerBuildInvocationOperations.RequireOrigin(latestKernel, _reference, _host, true);
            if (validateCandidate && (!WorktreeTreeDigest.TryCompute(root, out var currentDigest, out _) || currentDigest != invocation.TreeDigest ||
                WorkerBuildProjectScope.Resolve(root, request.Projects,
                    expectedMetadataDigest: latest.LastDispatch!.WorkerBuildScope!.MetadataDigest).Identity != invocation.ScopeIdentity))
                throw new WorkerBuildCandidateChangedException();
        }
        void StopChild()
        {
            if (child is null) return;
            var groupTerminal = child.Group.TerminateAndConfirmEmpty(TimeSpan.FromSeconds(10));
            if (!child.WaitForOwnedExit(TimeSpan.FromSeconds(10)) || !groupTerminal)
                throw new InvalidOperationException("Owned compiler termination is unconfirmed; custody retained.");
            terminalSafe = true;
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string root, string project, string configuration,
        IReadOnlyList<string> isolatedArguments, string temporaryDirectory)
    {
        var executable = Environment.ProcessPath;
        if (executable is null || !Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        info.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "PATHEXT", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT", "NUGET_PACKAGES", "SSL_CERT_FILE" })
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        info.Environment["TEMP"] = temporaryDirectory;
        info.Environment["TMP"] = temporaryDirectory;
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = root;
        foreach (var argument in new[] { "build", Path.Combine(root, project), "--nologo", "--configuration", configuration,
            "--verbosity", "minimal", "-maxcpucount:1", "-p:BuildInParallel=false", "-p:UseSharedCompilation=false", "-tl:off" }.Concat(isolatedArguments))
            info.ArgumentList.Add(argument);
        return info;
    }

    private sealed class WorkerBuildCandidateChangedException : Exception;
}
