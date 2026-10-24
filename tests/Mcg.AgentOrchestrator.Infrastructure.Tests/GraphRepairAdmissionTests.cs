using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// All stores, signals, child handles and launch decisions are private to each case.
public sealed class GraphRepairAdmissionTests
{
    [Xunit.Fact]
    public async Task RejectedCheckpointRebase_ResumesBoundedRepairExpiry_AndReleasesAdmission()
    {
        using var fixture = new GraphRepairTestFixture();
        var prepared = fixture.Prepare();
        var intent = fixture.Enqueue();
        var now = DateTimeOffset.UtcNow;
        var coordinator = new Mcg.AgentOrchestrator.App.Orchestration.OperatorIntentCoordinator(fixture.Store, utcNow: () => now);
        var holder = await fixture.Repository.LoadGoalsAsync([fixture.Goal.Id]);
        Assert.True(coordinator.ExecutePending(holder, holder.GetGoal(fixture.Goal.Id)).MutatedGoalState);
        Assert.Equal(5, holder.GetGoal(fixture.Goal.Id).Tasks.Count);
        // The repository checkpoint owner rebases a rejected save to its authoritative snapshot.
        holder.ReplaceGoalWithSnapshot((await fixture.Repository.LoadGoalAsync(fixture.Goal.Id))!);
        now = intent.AdmissionExpiresAt!.Value.AddSeconds(1);
        Assert.False(coordinator.ExecutePending(holder, holder.GetGoal(fixture.Goal.Id)).MutatedGoalState);
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Store.GetAsync(intent.Id))!.Status);
        Assert.Equal(2, holder.GetGoal(fixture.Goal.Id).Tasks.Count);
        var calls = 0;
        var failure = Xunit.Record.Exception(() => TaskIntentAdmission.Launch(prepared, "expired-rebased-repair", () =>
        {
            calls++;
            throw new ProbeReached();
        }));
        Assert.True(calls == 1, "Expired repair must release native admission after a rejected checkpoint rebase.");
        Assert.IsType<ProbeReached>(failure);
    }

    [Xunit.Fact]
    public async Task CompatibleAppend_ReopensVerifiedGoalForNewPendingWork()
    {
        using var fixture = new GraphRepairTestFixture();
        var kernel = await fixture.Repository.LoadAsync();
        kernel.ActivateGoal(fixture.Goal.Id, AgentCatalog.Default().Agents);
        foreach (var task in kernel.GetGoal(fixture.Goal.Id).Tasks)
            kernel.RecordTaskVerification(fixture.Goal.Id, task.Id,
                ManualVerificationRecorder.Create(true, "fixture passed", fixture.Root, DateTimeOffset.UtcNow));
        Assert.Equal(GoalStatus.Verified, kernel.GetGoal(fixture.Goal.Id).Status);
        await fixture.Repository.SaveAsync(kernel);
        fixture.Enqueue(new([new("Reviewer", "Additional review")]));
        fixture.Apply();
        var updated = (await fixture.Repository.LoadAsync()).GetGoal(fixture.Goal.Id);
        Assert.Equal(GoalStatus.Active, updated.Status);
        Assert.Equal(WorkTaskStatus.Pending, updated.Tasks.Last().Status);
        Assert.All(updated.Tasks.Take(2), task => Assert.Equal(WorkTaskStatus.Completed, task.Status));
    }

    [Xunit.Fact]
    public async Task RepairCannotBypassGoalAdmissionWithTaskOrWorkspaceScope()
    {
        using var fixture = new GraphRepairTestFixture();
        var valid = fixture.Enqueue();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.EnqueueAsync(valid with
        {
            Id = Guid.NewGuid().ToString("N"), IdempotencyKey = "bad-task-scope", TaskId = fixture.Goal.Tasks[0].Id.Value
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.EnqueueAsync(valid with
        {
            Id = Guid.NewGuid().ToString("N"), IdempotencyKey = "bad-workspace-scope", GoalId = OperatorIntentScopes.Workspace
        }));
        Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
    }

    [Xunit.Fact]
    public void AcknowledgedUnappliedRepair_StopsAtFinalLaunchCallback()
    {
        using var fixture = new GraphRepairTestFixture();
        var prepared = fixture.Prepare();
        var intent = fixture.Enqueue();
        var calls = 0;
        var failure = Xunit.Record.Exception(() => TaskIntentAdmission.Launch(prepared,
            "pending-final-launch", () => { calls++; throw new ProbeReached(); }));
        Assert.True(calls == 0, "Acknowledged unapplied repair must prevent the final launch callback.");
        var error = Assert.IsType<TaskIntentDispatchSuppressedException>(failure);
        Assert.Equal(intent.Id, error.Receipt.IntentId);
        Assert.Equal("pending-graph-repair", error.Receipt.Reason);
        Assert.Equal(error.Receipt, fixture.Store.GetSuppression("pending-final-launch"));
        Assert.Throws<ProbeReached>(() => TaskIntentAdmission.Launch(fixture.Prepare(fixture.Other),
            "unrelated-final-launch", () => throw new ProbeReached()));
    }

    [Xunit.Fact]
    public void AppliedRepair_InvalidatesOldPreparation_FreshPreparationUsesRepairedFirstRole()
    {
        using var fixture = new GraphRepairTestFixture();
        var old = fixture.Prepare();
        fixture.Enqueue();
        fixture.Apply();
        var calls = 0;
        var failure = Xunit.Record.Exception(() => TaskIntentAdmission.Launch(old,
            "stale-final-launch", () => { calls++; throw new ProbeReached(); }));
        Assert.True(calls == 0, "Changed committed graph must prevent the stale final launch callback.");
        var error = Assert.IsType<TaskIntentDispatchSuppressedException>(failure);
        Assert.Equal("prepared-graph-changed", error.Receipt.Reason);
        var fresh = fixture.Prepare();
        var loaded = fixture.Repository.LoadAsync().GetAwaiter().GetResult().GetGoal(fixture.Goal.Id);
        Assert.Equal(AgentRole.Researcher, loaded.Tasks[0].RequiredRole);
        Assert.Equal(loaded.Tasks[0].Id.Value, fresh.TaskId);
        Assert.Throws<ProbeReached>(() => TaskIntentAdmission.Launch(fresh, "fresh-final-launch", () => throw new ProbeReached()));
    }

    [Xunit.Fact]
    public void RejectionAndExpiry_ReleaseHoldWithoutInvalidatingUnchangedGraph()
    {
        using var fixture = new GraphRepairTestFixture();
        var prepared = fixture.Prepare();
        fixture.Enqueue(new([new("Tester", "Invalid", "Researcher")]));
        fixture.Apply();
        Assert.Throws<ProbeReached>(() => TaskIntentAdmission.Launch(prepared, "released-final-launch", () => throw new ProbeReached()));
        var expired = fixture.Enqueue();
        var kernel = fixture.Repository.LoadGoalsAsync([fixture.Goal.Id]).GetAwaiter().GetResult();
        new Mcg.AgentOrchestrator.App.Orchestration.OperatorIntentCoordinator(fixture.Store,
            utcNow: () => expired.AdmissionExpiresAt!.Value.AddSeconds(1)).ExecutePending(kernel, kernel.GetGoal(fixture.Goal.Id));
        Assert.Equal(OperatorIntentStatus.Rejected, fixture.Store.GetAsync(expired.Id).GetAwaiter().GetResult()!.Status);
        Assert.Throws<ProbeReached>(() => TaskIntentAdmission.Launch(prepared, "expired-final-launch", () => throw new ProbeReached()));
    }

    [Xunit.Fact]
    public async Task EnqueueAndNativeAdmission_ShareOneGoalBoundary()
    {
        using var fixture = new GraphRepairTestFixture();
        var reference = fixture.Prepare();
        using var enqueueReached = new ManualResetEventSlim();
        Task<OperatorIntentRecord>? enqueue = null;
        Assert.Throws<ProbeReached>(() => TaskIntentAdmission.Launch(reference, "admit-first", () =>
        {
            enqueue = Task.Run(() =>
            {
                enqueueReached.Set();
                return fixture.Enqueue();
            });
            Assert.True(enqueueReached.Wait(TimeSpan.FromSeconds(30)), "Enqueue thread did not reach admission boundary.");
            // The callback owns goal admission: acknowledgement must wait until it leaves.
            Assert.False(enqueue.IsCompleted);
            throw new ProbeReached();
        }));
        var intent = await enqueue!.WaitAsync(TimeSpan.FromSeconds(30));
        var error = Assert.Throws<TaskIntentDispatchSuppressedException>(() => TaskIntentAdmission.Launch(reference,
            "enqueue-first", () => throw new ProbeReached()));
        Assert.Equal(intent.Id, error.Receipt.IntentId);
    }

    [Xunit.Fact]
    public async Task AdmittedWorker_ProtectsOwnership_ThenRetryAllowsPredecessor()
    {
        using var fixture = new GraphRepairTestFixture();
        var original = fixture.Goal.Tasks[0].Id;
        var admitted = fixture.Prepare();
        var preparedKernel = await fixture.Repository.LoadAsync();
        preparedKernel.ActivateGoal(fixture.Goal.Id, AgentCatalog.Default().Agents);
        preparedKernel.RecordTaskDispatch(fixture.Goal.Id, original,
            new TaskDispatchRecord("fixture", "held worker", fixture.Root, admitted.DispatchedAt!.Value));
        await fixture.Repository.SaveAsync(preparedKernel);
        using var process = TaskIntentAdmission.Launch(admitted, "owned-worker", () =>
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/D /Q")
                : new ProcessStartInfo("/bin/sh");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.Environment.Clear();
            if (OperatingSystem.IsWindows())
                start.Environment["SystemRoot"] = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.System));
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            return Process.Start(start)!;
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            Assert.False(process.HasExited);
            Assert.Contains(original.Value, fixture.Store.ListAdmittedTaskIds(preparedKernel.GetGoal(fixture.Goal.Id)));
            var rejected = fixture.Enqueue(new([new("Researcher", "Forbidden predecessor", "Developer")]));
            fixture.Apply();
            Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Store.GetAsync(rejected.Id))!.Status);
            Assert.Contains("owned-task-predecessor", (await fixture.Store.GetAsync(rejected.Id))!.Outcome);
            Assert.False(process.HasExited);
            var prerequisite = fixture.Enqueue(new([new("Researcher", "Forbidden prerequisite even when appended")]));
            fixture.Apply();
            Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Store.GetAsync(prerequisite.Id))!.Status);
            Assert.Contains("owned-task-prerequisite", (await fixture.Store.GetAsync(prerequisite.Id))!.Outcome);
            Assert.False(process.HasExited);
            var compatible = fixture.Enqueue(new([new("Tester", "Compatible", "Reviewer")]));
            var ownedActions = 0;
            TaskIntentAdmission.ExecuteAuthorized(admitted, "owned-build-during-repair", () => ownedActions++);
            fixture.Apply();
            Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync(compatible.Id))!.Status);
            var kernel = await fixture.Repository.LoadAsync();
            Assert.Equal(original, kernel.GetGoal(fixture.Goal.Id).Tasks[0].Id);
            Assert.False(process.HasExited);
            TaskIntentAdmission.ExecuteAuthorized(admitted, "owned-build-after-compatible-repair", () => ownedActions++);
            Assert.Equal(2, ownedActions);
            await process.StandardInput.WriteLineAsync("exit 0");
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, process.ExitCode);
            kernel.ReportTaskProgress(fixture.Goal.Id, original, WorkTaskStatus.Completed, "Owned fixture completed");
            await fixture.Repository.SaveAsync(kernel);
            Assert.Equal(WorkTaskStatus.Completed, (await fixture.Repository.LoadAsync()).GetGoal(fixture.Goal.Id).Tasks.Single(task => task.Id == original).Status);
            Assert.True(fixture.Store.HasLaunch("owned-worker"));

            kernel.RetryTask(fixture.Goal.Id, original, "Revise after fixture exit", RetryCause.NewSourceFinding);
            await fixture.Repository.SaveAsync(kernel);
            var retried = (await fixture.Repository.LoadAsync()).GetGoal(fixture.Goal.Id);
            var task = retried.Tasks.Single(task => task.Id == original);
            Assert.Null(task.LastDispatch);
            Assert.Null(task.LastProcess);
            Assert.Null(task.LastExecution);
            var repair = fixture.Enqueue(new([new("Researcher", "Research retry", "Developer")]));
            fixture.Apply();
            var repaired = (await fixture.Repository.LoadAsync()).GetGoal(fixture.Goal.Id);
            Assert.True(repaired.Tasks[0].RequiredRole == AgentRole.Researcher,
                "Exited worker launch history must not prevent predecessor repair after retry reset.");
            Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync(repair.Id))!.Status);
            Assert.Equal(original, repaired.Tasks[1].Id);
            Assert.True(fixture.Store.HasLaunch("owned-worker"));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task LaunchReceipt_CurrentAttemptOnly_RetryClearsOwnership(bool legacy)
    {
        using var fixture = new GraphRepairTestFixture();
        var admission = fixture.Prepare();
        // Record the durable boundary directly: no child or process metadata is needed
        // to prove the launch receipt alone protects this attempt.
        fixture.Store.RecordLaunch(legacy ? admission with { DispatchedAt = null } : admission,
            "attempt-receipt", new SpawnProcessIdentity(123, admission.DispatchedAt!.Value, "fixture"));
        var kernel = await fixture.Repository.LoadAsync();
        kernel.ActivateGoal(fixture.Goal.Id, AgentCatalog.Default().Agents);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var task = goal.Tasks[0];
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("fixture", "first attempt", fixture.Root, admission.DispatchedAt.Value));
        Assert.Contains(task.Id.Value, fixture.Store.ListAdmittedTaskIds(goal));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Fixture exited");
        kernel.RetryTask(goal.Id, task.Id, "Retry fixture", RetryCause.NewSourceFinding);
        await fixture.Repository.SaveAsync(kernel);
        kernel = await fixture.Repository.LoadAsync();
        goal = kernel.GetGoal(goal.Id);
        Assert.Empty(fixture.Store.ListAdmittedTaskIds(goal));
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("fixture", "next attempt", fixture.Root, admission.DispatchedAt.Value.AddMinutes(1)));
        if (legacy)
            Assert.Contains(task.Id.Value, fixture.Store.ListAdmittedTaskIds(goal));
        else
            Assert.Empty(fixture.Store.ListAdmittedTaskIds(goal));
        Assert.True(fixture.Store.HasLaunch("attempt-receipt"));
    }

    private sealed class ProbeReached : Exception { }
}
