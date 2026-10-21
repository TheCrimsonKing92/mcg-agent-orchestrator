using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class WorkerBuildInvocationOperations(string statePath, string executionDirectory,
    DotnetBuildStorageRoot? storageRoot = null) : IWorkerBuildInvocationOperations
{
    internal const string OperationName = "dispatch:worker-build";
    private readonly SqliteOrchestratorStateRepository _repository = new(statePath);

    public WorkerBuildHostReference Prepare(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        var task = kernel.GetTask(goalId, taskId);
        if (task.LastDispatch?.WorkerBuildScope is null)
            throw new InvalidOperationException("Host build enrollment requires dispatch-pinned project scope.");
        kernel.RecordWorkerBuildProtocol(goalId, taskId, 1);
        return new(statePath, executionDirectory, goalId.Value, taskId.Value, task.LastDispatch.DispatchedAt,
            BuildWorkerDispatchId(goalId, taskId, task.LastDispatch), StorageRoot: storageRoot?.RootPath);
    }

    // Provider sessions are learned from live heartbeats after enrollment. They are
    // metadata, not worker-build authority; keep the shared dispatch hash unchanged.
    private static string BuildWorkerDispatchId(GoalId goalId, TaskId taskId, TaskDispatchRecord dispatch) =>
        BackgroundDispatchRunner.BuildDispatchId(goalId, taskId, dispatch with { ProviderSessionId = null });

    internal AgentOrchestratorKernel Load(string goalId) => _repository.LoadGoalsAsync([new GoalId(goalId)]).GetAwaiter().GetResult();

    public bool ReconcileOutstanding(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, bool cancelled = false)
    {
        var canonical = Load(goalId.Value).GetTask(goalId, taskId);
        foreach (var invocation in canonical.WorkerBuildInvocations.Where(i => !i.IsTerminal || !i.CustodyReleased))
            Reconcile(invocation, cancelled);
        canonical = Load(goalId.Value).GetTask(goalId, taskId);
        Synchronize(kernel, goalId, taskId, canonical.WorkerBuildInvocations);
        return canonical.WorkerBuildInvocations.All(i => i.IsTerminal && i.CustodyReleased);
    }

    private static void Synchronize(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId,
        IReadOnlyList<WorkerBuildInvocation> invocations)
    {
        kernel.ReconcileWorkerBuildInvocations(goalId, taskId, invocations);
    }

    internal WorkerBuildInvocation Store(WorkerBuildInvocation next, int? expectedVersion) => _repository
        .TransactGoalStateAsync(new GoalId(next.GoalId), (state, _) =>
        {
            if (state is null) throw new InvalidOperationException("Worker build goal is unavailable.");
            var loaded = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([state.Goal], []));
            var task = loaded.GetTask(new GoalId(next.GoalId), new TaskId(next.TaskId));
            var current = task.WorkerBuildInvocations.SingleOrDefault(i => i.Id == next.Id);
            if (current?.Version != expectedVersion)
                throw new InvalidOperationException("Worker build transition lost expected-version authority.");
            if (current is null && (task.LastDispatch?.DispatchedAt != next.DispatchAt ||
                task.LastDispatch.WorkerBuildProtocolVersion != 1 || task.Status != WorkTaskStatus.Running))
                throw new UnauthorizedAccessException("Worker build request lost its current dispatch authority.");
            loaded.RecordWorkerBuildInvocation(new GoalId(next.GoalId), new TaskId(next.TaskId), next);
            var updated = state with { Goal = loaded.ExportGoalSnapshot(new GoalId(next.GoalId)) };
            return Task.FromResult((true, (GoalStateSnapshot?)updated, next));
        }).GetAwaiter().GetResult();

    internal static TaskSpec RequireOrigin(AgentOrchestratorKernel kernel, WorkerBuildHostReference reference,
        WorkerBuildProcess host, bool requireRunning)
    {
        var goal = kernel.GetGoal(new GoalId(reference.GoalId));
        var task = kernel.GetTask(goal.Id, new TaskId(reference.TaskId));
        var dispatch = task.LastDispatch;
        var process = task.LastProcess;
        // This check is the authority-negative-control mutation seam. No worker field can supply it.
        if (dispatch is null || dispatch.WorkerBuildProtocolVersion != 1 || dispatch.DispatchedAt != reference.DispatchAt ||
            BuildWorkerDispatchId(goal.Id, task.Id, dispatch) != reference.DispatchId ||
            process is null || process.ProcessId != host.Pid || process.ProcessIdentityStartedAt != host.StartedAt ||
            string.IsNullOrWhiteSpace(process.ProcessIdentityImagePath) ||
            !string.Equals(process.ProcessIdentityImagePath, host.ImagePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(dispatch.WorkingDirectory), Path.GetFullPath(process.WorkingDirectory), StringComparison.OrdinalIgnoreCase) ||
            task.Status == WorkTaskStatus.Cancelled || goal.Status is GoalStatus.Cancelled or GoalStatus.Superseded ||
            requireRunning && (task.Status != WorkTaskStatus.Running || !process.IsRunning || goal.Status != GoalStatus.Active))
            throw new UnauthorizedAccessException("Worker build canonical dispatch/host binding rejected.");
        return task;
    }

    public WorkerBuildConsumption Consume(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId,
        IReadOnlyList<string> changedPaths, IReadOnlyList<string> dirtyPaths)
    {
        var current = Load(goalId.Value);
        var task = current.GetTask(goalId, taskId);
        var dispatch = task.LastDispatch ?? throw new InvalidOperationException("Worker build consumer has no dispatch.");
        var reference = new WorkerBuildHostReference(statePath, executionDirectory, goalId.Value, taskId.Value,
            dispatch.DispatchedAt, BuildWorkerDispatchId(goalId, taskId, dispatch));
        var invocations = task.WorkerBuildInvocations.Where(i => i.DispatchId == reference.DispatchId).ToArray();
        foreach (var pending in invocations.Where(i => !i.IsTerminal || !i.CustodyReleased)) Reconcile(pending);
        current = Load(goalId.Value);
        task = current.GetTask(goalId, taskId);
        var required = WorkerBuildEvidenceRequirement.FindRequiredProjects(dispatch.WorkingDirectory, task.RequiredRole, changedPaths, dirtyPaths);
        if (required.Count == 0 && invocations.Length == 0) return new(false, false, "worker_build_host=not-required");
        WorkerBuildProjectScope.Result scope;
        try { scope = WorkerBuildProjectScope.ResolveForDispatch(dispatch.WorkingDirectory, required, goalId, taskId, dispatch); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(true, false, "worker_build_host=scope-invalid; " + ex.Message); }
        if (!WorktreeTreeDigest.TryCompute(dispatch.WorkingDirectory, out var digest, out var reason))
            return new(true, false, "worker_build_host=candidate-unavailable; " + reason);
        var applicable = task.WorkerBuildInvocations.Where(i => i.DispatchId == reference.DispatchId &&
            i.TreeDigest == digest && i.Projects.Intersect(scope.Projects, StringComparer.OrdinalIgnoreCase).Count() == scope.Projects.Count)
            .OrderByDescending(i => i.Deadline).FirstOrDefault();
        if (applicable is null) return new(true, false, "worker_build_host=missing-current-candidate-outcome");
        RequireOrigin(current, reference, applicable.Host, requireRunning: false);
        if (!applicable.IsTerminal || !applicable.CustodyReleased)
            return new(true, false, $"worker_build_invocation={applicable.Id}; phase={applicable.Phase}; reconciliation=pending");
        WorkerBuildInvocation? consumed = null;
        var admission = new TaskIntentAdmissionReference(applicable.AdmissionStorePath, applicable.GoalId,
            applicable.TaskId, applicable.AdmissionVersion, applicable.DispatchAt);
        TaskIntentAdmission.ExecuteAuthorized(admission, "worker-build-consume:" + applicable.Id, () =>
        consumed = _repository.TransactGoalStateAsync(goalId, (state, cancellationToken) =>
        {
            if (state is null) throw new InvalidOperationException("Worker build consumption lost goal.");
            var loaded = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([state.Goal], []));
            var canonicalTask = RequireOrigin(loaded, reference, applicable.Host, requireRunning: false);
            var canonical = canonicalTask.WorkerBuildInvocations.Single(i => i.Id == applicable.Id);
            if (!canonical.SameOrigin(applicable) || !canonical.IsTerminal || !canonical.CustodyReleased ||
                !WorktreeTreeDigest.TryCompute(dispatch.WorkingDirectory, out var nowDigest, out _) || nowDigest != canonical.TreeDigest ||
                WorkerBuildProjectScope.ResolveForDispatch(dispatch.WorkingDirectory, required,
                    goalId, taskId, canonicalTask.LastDispatch!).Projects
                    .Except(canonical.Projects, StringComparer.OrdinalIgnoreCase).Any())
                throw new UnauthorizedAccessException("Worker build consumption lost candidate/scope applicability.");
            if (canonical.Consumed) return Task.FromResult((false, state, canonical));
            var next = canonical with { Version = canonical.Version + 1, Consumed = true };
            loaded.RecordWorkerBuildInvocation(goalId, taskId, next);
            return Task.FromResult((true, (GoalStateSnapshot?)(state with { Goal = loaded.ExportGoalSnapshot(goalId) }), next));
        }).GetAwaiter().GetResult());
        if (consumed is null) throw new InvalidOperationException("Worker build consumption did not execute.");
        Synchronize(kernel, goalId, taskId, Load(goalId.Value).GetTask(goalId, taskId).WorkerBuildInvocations);
        return new(consumed.Outcome != WorkerBuildOutcome.Success && consumed.Outcome != WorkerBuildOutcome.CompilerFailure,
            consumed.Outcome == WorkerBuildOutcome.CompilerFailure,
            $"worker_build_invocation={consumed.Id}; phase={consumed.Phase}; outcome={consumed.Outcome}; consumed={consumed.Consumed}");
    }

    internal WorkerBuildInvocation Reconcile(WorkerBuildInvocation invocation, bool cancelled = false)
    {
        if (IsExactProcessAlive(invocation.Host)) return invocation;
        foreach (var child in invocation.Children ?? [])
            if (IsExactProcessAlive(child))
                throw new InvalidOperationException("Worker build host exited but owned child termination is unconfirmed; custody retained.");
        if (!invocation.IsTerminal)
            invocation = Store(invocation with { Version = invocation.Version + 1,
                Observations = invocation.Observations.Append(new(WorkerBuildPhase.Completed, DateTimeOffset.UtcNow)).ToArray(),
                Outcome = cancelled ? WorkerBuildOutcome.Cancelled : WorkerBuildOutcome.Interrupted,
                Diagnostic = cancelled ? "Host-owned cancellation confirmed child termination." :
                    "Host exited before durable terminal observation; no relaunch permitted." }, invocation.Version);
        if (!invocation.CustodyReleased)
        {
            var goal = Load(invocation.GoalId).GetGoal(new GoalId(invocation.GoalId));
            if (GoalEvidenceOperationOwner.TryParse(invocation.LeaseOwner, out var owner) && owner is not null)
            {
                var entries = GoalOperationJournal.ReadStrict(executionDirectory, goal.Id).Entries
                    .Where(e => e.OperationInstanceId == owner.InstanceId && e.Operation == owner.Operation).ToArray();
                var store = new ReconcileSweepRemediationStore(statePath);
                if (entries.Length == 0 && store.TryGetAcceptanceLeaseOwner(invocation.GoalId) == invocation.LeaseOwner)
                    throw new InvalidOperationException("Worker build custody lacks its matching operation journal; retained for recovery.");
                if (entries.Length > 0 && !entries.Any(e =>
                    e.Status is GoalOperationStatus.Completed or GoalOperationStatus.Failed or GoalOperationStatus.Aborted))
                    GoalOperationJournal.Aborted(executionDirectory, goal, owner.Operation,
                        "Worker build host exited; contained owned children confirmed terminal.", operationInstanceId: owner.InstanceId);
                store.ReleaseAcceptanceLease(invocation.GoalId, invocation.LeaseOwner!);
            }
            invocation = Store(invocation with { Version = invocation.Version + 1, CustodyReleased = true }, invocation.Version);
        }
        return invocation;
    }

    internal static bool IsExactProcessAlive(WorkerBuildProcess identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            if (process.HasExited) return false;
            return WorkerBuildRequestChannel.ReadIdentity(process) == identity;
        }
        catch (ArgumentException) { return false; }
        // Access/identity failures remain unknown, never proof of termination.
    }
}
