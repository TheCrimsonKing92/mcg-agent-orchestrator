using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Security;

// Parallel-safe: fixture-owned native groups and canonical stores, with event/phase-gated stimuli.
public sealed class WorkerBuildRequestRecoveryTests
{
    [Fact]
    public async Task ManagedHostDeathAfterCompilerStart_ReloadRetainsTerminalObligationWithoutRelaunch()
    {
        using var fixture = new WorkerBuildRequestFixture(managedHost: true);
        var suffix = Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\mcg-build-ready-" + suffix);
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\mcg-build-release-" + suffix);
        var command = "\"" + WorkerShell.Executable + "\" -NoProfile -NonInteractive -Command \"" +
            "$r=[Threading.EventWaitHandle]::OpenExisting('Local\\mcg-build-ready-" + suffix + "');$null=$r.Set();" +
            "$g=[Threading.EventWaitHandle]::OpenExisting('Local\\mcg-build-release-" + suffix + "');" +
            "if(-not $g.WaitOne(240000)){throw 'named compiler release event did not arrive'}\"";
        var path = Path.Combine(fixture.RepositoryRoot, WorkerBuildRequestFixture.Project);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
            "<Target Name=\"FixtureGate\" BeforeTargets=\"CoreCompile\"><Exec Command=\"" + SecurityElement.Escape(command) + "\" /></Target></Project>");
        Process? host = null;
        var runner = new BackgroundDispatchRunner(disableProcessStart: false, startProcess: start => host = Process.Start(start))
        { WorkerBuildInvocations = fixture.Operations };
        try
        {
            var started = runner.StartLatestDispatch(fixture.Kernel, fixture.GoalId, fixture.TaskId, Path.Combine(fixture.Root, "logs"),
                checkpointBeforeWorkerStart: (_, _, _, _) => fixture.Save(), taskIntentAdmission: fixture.Reference.Admission);
            Assert.NotNull(host);
            Assert.Equal(started.ProcessId, host.Id);
            Assert.True(await Task.Run(() => ready.WaitOne(TimeSpan.FromMinutes(3))), "Owned compiler did not signal the named readiness event.");
            var before = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
            Assert.Equal(WorkerBuildPhase.CompilerStarted, before.Phase);
            var child = Assert.Single(before.Children!);
            using var compiler = Process.GetProcessById(child.Pid);
            Assert.Equal(child, WorkerBuildRequestChannel.ReadIdentity(compiler));
            host.Kill();
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await compiler.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var restarted = new Mcg.AgentOrchestrator.App.Orchestration.WorkerBuildInvocationOperations(fixture.StatePath, fixture.Root, fixture.Storage);
            Assert.True(restarted.ReconcileOutstanding(fixture.Kernel, fixture.GoalId, fixture.TaskId));
            var terminal = Assert.Single(restarted.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
            Assert.Equal(before.Id, terminal.Id);
            Assert.Equal(WorkerBuildOutcome.Interrupted, terminal.Outcome);
            Assert.True(terminal.CustodyReleased);
            Assert.False(terminal.Consumed);
            Assert.Single(terminal.Children!);
            Assert.True(restarted.ReconcileOutstanding(fixture.Kernel, fixture.GoalId, fixture.TaskId));
            Assert.Equal(terminal.Version, Assert.Single(restarted.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations).Version);
        }
        finally
        {
            release.Set();
            if (host is not null)
            {
                WorkerProcessJobs.TryKillOrFallbackAndWait(host.Id, TimeSpan.FromSeconds(20));
                host.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(WorkerBuildPhase.Authorized, false)]
    [InlineData(WorkerBuildPhase.CompilerStarted, true)]
    public async Task OperatorIntentCancellationBeforeAndAfterStartIsAttributable(WorkerBuildPhase cancelAt, bool started)
    {
        using var fixture = new WorkerBuildRequestFixture();
        Task? intake = null;
        var invocation = await fixture.RunHelper(i =>
        {
            if (i.Phase != cancelAt || intake is not null) return;
            void Enqueue()
            {
                var store = new SqliteOperatorIntentStore(fixture.Reference.Admission!.StorePath, fixture.Root);
                store.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                    OperatorIntentVerbs.CancelDispatch, fixture.GoalId.Value, fixture.TaskId.Value, "{}", [],
                    "fixture", "test", "local-process", DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
            }
            if (started) intake = Task.Run(Enqueue); // the resume callback still owns the task admission mutex
            else { Enqueue(); intake = Task.CompletedTask; }
        });
        Assert.NotNull(intake);
        await intake.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(WorkerBuildOutcome.Cancelled, invocation.Outcome);
        Assert.Equal(started, invocation.Observations.Any(o => o.Phase == WorkerBuildPhase.CompilerStarted));
        Assert.True(invocation.CustodyReleased);
    }

    [Theory]
    [InlineData(WorkerBuildPhase.Authorized, false)]
    [InlineData(WorkerBuildPhase.CompilerStarted, true)]
    public async Task WorkerExitCancelsOutstandingInvocationAndConfirmsOwnedTermination(WorkerBuildPhase exitAt, bool compilerStarted)
    {
        using var fixture = new WorkerBuildRequestFixture();
        var exitRequested = false;
        var invocation = await fixture.RunHelper(i =>
        {
            if (i.Phase == exitAt && !exitRequested)
            {
                exitRequested = true;
                fixture.Worker!.Group.Kill();
            }
        }, assertAdapterExit: false);
        Assert.True(exitRequested);
        Assert.Equal(WorkerBuildOutcome.Cancelled, invocation.Outcome);
        Assert.Equal(compilerStarted ? 1 : 0, invocation.Children?.Count ?? 0);
        Assert.True(invocation.CustodyReleased);
        Assert.All(invocation.Children ?? [], child => Assert.False(
            Mcg.AgentOrchestrator.App.Orchestration.WorkerBuildInvocationOperations.IsExactProcessAlive(child)));
    }

    [Fact]
    public async Task ReloadAndDuplicateSubmissionDoNotLaunchOrConsumeTwice()
    {
        using var fixture = new WorkerBuildRequestFixture();
        var invocation = await fixture.RunHelper();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        var reopened = fixture.Host();
        var replay = await reopened.ExecuteAsync(new WorkerBuildRequest(1, invocation.RequestKey,
            invocation.Projects.ToArray(), invocation.Configuration), invocation.Worker, CancellationToken.None);
        Assert.Equal(invocation.Id, replay.InvocationId);
        Assert.Equal(WorkerBuildOutcome.Success, replay.Outcome);
        Assert.Single(Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations).Children!);
        Assert.False(fixture.Consume().MissingEvidence);
        var applied = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
        Assert.True(applied.Consumed);
        Assert.False(fixture.Consume().MissingEvidence);
        Assert.Equal(applied.Version, Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations).Version);
    }

    [Fact]
    public async Task DuplicateKeyWithChangedPayloadCannotLaunchAnAdditionalChild()
    {
        using var fixture = new WorkerBuildRequestFixture();
        var invocation = await fixture.RunHelper();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Host().ExecuteAsync(
            new WorkerBuildRequest(1, invocation.RequestKey, invocation.Projects.ToArray(), "Release"),
            invocation.Worker, CancellationToken.None));
        Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
    }

    [Fact]
    public async Task StaleSnapshotCannotDropTerminalOutcomeOrConsumedState()
    {
        using var fixture = new WorkerBuildRequestFixture();
        var repository = new SqliteOrchestratorStateRepository(fixture.StatePath);
        var stale = await repository.LoadGoalsAsync([fixture.GoalId]);
        var before = stale.ExportGoalSnapshot(fixture.GoalId);
        var invocation = await fixture.RunHelper();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.False(fixture.Consume().MissingEvidence);
        var result = await repository.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(before, stale.ExportGoalSnapshot(fixture.GoalId))]);
        Assert.NotEmpty(result);
        var restored = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
        Assert.Equal(WorkerBuildOutcome.Success, restored.Outcome);
        Assert.True(restored.CustodyReleased);
        Assert.True(restored.Consumed);
        Assert.Equal(0, fixture.Kernel.GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildCheckRecoveryCount);
    }
}
