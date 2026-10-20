using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Cli;

// Parallel safe: every case owns an isolated SQLite store.
public sealed class SqliteExplicitDispositionMergeTests
{
    [Fact]
    public async Task TickMerge_LateRed_PreservesCommittedParkAndRetryEvidence()
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        var baseline = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        Assert.Equal(GoalStatus.Verifying, baseline.Status);
        await f.Park();
        await f.Repository.TransactGoalAsync(f.Goal.Id, (stored, _) =>
        {
            var writer = AgentOrchestratorKernel.FromSnapshot(new([stored!], []));
            writer.RecordTaskNote(f.Goal.Id, f.Task.Id, "Unrelated committed progress");
            return Task.FromResult((true, writer.ExportGoalSnapshot(f.Goal.Id), true));
        });
        // The stale tick never saw the park: its terminal RED changes Verifying to Active.
        f.ReconcileRed(f.Driver());
        var tick = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        Assert.Equal(GoalStatus.Active, tick.Status);
        Assert.NotEqual(baseline.Status, tick.Status);
        var result = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, tick)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var raw = (await f.Repository.LoadGoalAsync(f.Goal.Id))!;
        Assert.Equal(GoalStatus.Parked, raw.Status);
        Assert.Equal(GoalStatus.Active, raw.ParkedLifecycleStatus);
        Assert.Equal(new GoalExplicitDisposition(1, GoalExplicitDispositionAction.Park), raw.ExplicitDisposition);
        // Rebase the running tick exactly as the durable-save continuation does, then restart.
        f.Kernel.ReplaceGoalStateWithSnapshot(result.PersistedSnapshot!, result.PersistedHumanInputRequests ?? []);
        Assert.Equal(GoalStatus.Parked, f.Kernel.GetGoal(f.Goal.Id).Status);
        await f.Reload();
        f.AssertFailure();
        Assert.Contains(f.Goal.Timeline, e => e.Message == "Unrelated committed progress");
        Assert.Contains(f.Goal.Timeline, e => e.Message.Contains("terminal-attempt", StringComparison.Ordinal));
        Assert.IsType<Mcg.AgentOrchestrator.Core.Conductor.ConductorAdvanceOutcome.Held>(
            f.Driver().AdvanceOnce(f.Goal, ExplicitParkTestFixture.Policy).Outcome);
        Assert.Equal(0, f.Dispatches + f.Acceptances + f.Landings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TickMerge_RefreshedAuthorityWithOldBaseline_RetainsRed(bool unpark)
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        var baseline = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        Assert.Null(baseline.ExplicitDisposition);
        Assert.Equal(GoalStatus.Verifying, baseline.Status);
        await f.Park();
        if (unpark) await f.Unpark();
        // The conductor refreshes its kernel but retains the older checkpoint baseline.
        await f.Reload();
        var committed = (await f.Repository.LoadGoalAsync(f.Goal.Id))!;
        Assert.NotEqual(baseline.ExplicitDisposition, f.Goal.ExplicitDisposition);
        Assert.Equal(committed.ExplicitDisposition, f.Goal.ExplicitDisposition);
        f.ReconcileRed(f.Driver());
        var tick = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        Assert.Equal(unpark ? GoalStatus.Active : GoalStatus.Parked, tick.Status);
        Assert.Equal(GoalStatus.Active, f.Goal.LifecycleStatus);
        var result = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, tick)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var raw = (await f.Repository.LoadGoalAsync(f.Goal.Id))!;
        Assert.Equal(tick.Status, raw.Status);
        Assert.Equal(committed.ExplicitDisposition, raw.ExplicitDisposition);
        Assert.Equal(unpark ? (GoalStatus?)null : GoalStatus.Active, raw.ParkedLifecycleStatus);
        Assert.Equal(1, raw.AutomaticAcceptanceRetryCount);
        Assert.True(raw.AcceptanceFailureDeferredForRetry);
        await f.Reload();
        f.AssertFailure();
        Assert.Contains(f.Goal.Timeline, e => e.Message.Contains("terminal-attempt", StringComparison.Ordinal));
        f.ReconcileRed(f.Driver());
        f.AssertFailure();
        f.Driver().AdvanceOnce(f.Goal, ExplicitParkTestFixture.Policy);
        Assert.Equal(unpark ? 1 : 0, f.Dispatches);
        Assert.Equal(0, f.Acceptances + f.Landings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TickMerge_RefreshedThenSupersededAuthority_RetainsRed(bool repark)
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        var baseline = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        Assert.Null(baseline.ExplicitDisposition);
        Assert.Equal(GoalStatus.Verifying, baseline.Status);
        await f.Park();
        if (repark) Unpark(f);
        await f.Reload(); // Refresh does not advance the conductor's checkpoint baseline.
        Assert.Equal(new GoalExplicitDisposition(repark ? 2 : 1, repark
            ? GoalExplicitDispositionAction.Unpark : GoalExplicitDispositionAction.Park),
            f.Goal.ExplicitDisposition);
        f.ReconcileRed(f.Driver());
        var tick = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        Assert.Equal(repark ? GoalStatus.Active : GoalStatus.Parked, tick.Status);
        Assert.Equal(GoalStatus.Active, f.Goal.LifecycleStatus);

        // The next explicit command commits after refresh and RED reconciliation.
        if (repark) await f.Park();
        else Unpark(f);
        await f.Repository.TransactGoalAsync(f.Goal.Id, (stored, _) =>
        {
            var writer = AgentOrchestratorKernel.FromSnapshot(new([stored!], []));
            writer.RecordTaskNote(f.Goal.Id, f.Task.Id, "Progress after newer disposition");
            return Task.FromResult((true, writer.ExportGoalSnapshot(f.Goal.Id), true));
        });
        var committed = (await f.Repository.LoadGoalAsync(f.Goal.Id))!;
        Assert.Equal(new GoalExplicitDisposition(repark ? 3 : 2, repark
            ? GoalExplicitDispositionAction.Park : GoalExplicitDispositionAction.Unpark),
            committed.ExplicitDisposition);
        Assert.NotEqual(tick.ExplicitDisposition, committed.ExplicitDisposition);
        var result = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, tick)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var raw = (await f.Repository.LoadGoalAsync(f.Goal.Id))!;
        Assert.Equal(repark ? GoalStatus.Parked : GoalStatus.Active, raw.Status);
        Assert.Equal(committed.ExplicitDisposition, raw.ExplicitDisposition);
        Assert.Equal(repark ? GoalStatus.Active : (GoalStatus?)null, raw.ParkedLifecycleStatus);
        Assert.Equal(1, raw.AutomaticAcceptanceRetryCount);
        Assert.True(raw.AcceptanceFailureDeferredForRetry);
        f.Kernel.ReplaceGoalStateWithSnapshot(result.PersistedSnapshot!, result.PersistedHumanInputRequests ?? []);
        Assert.Equal(raw.Status, f.Kernel.GetGoal(f.Goal.Id).Status);
        await f.Reload();
        f.AssertFailure();
        Assert.Equal(WorkTaskStatus.Assigned, f.Task.Status);
        Assert.Contains(f.Goal.Timeline, e => e.Message == "Progress after newer disposition");
        Assert.Contains(f.Goal.Timeline, e => e.Message.Contains("terminal-attempt", StringComparison.Ordinal));
        f.ReconcileRed(f.Driver());
        f.AssertFailure(); // Re-consuming the attempt must not count the retry again.
        f.Driver().AdvanceOnce(f.Goal, ExplicitParkTestFixture.Policy);
        Assert.Equal(repark ? 0 : 1, f.Dispatches);
        Assert.Equal(0, f.Acceptances + f.Landings);
    }

    [Fact]
    public async Task TickSave_OlderAuthorityWithCurrentBaseline_PreservesCommittedUnpark()
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        await f.Park();
        await f.Reload();
        f.ReconcileRed(f.Driver());
        var tick = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        Unpark(f);
        var baseline = (await f.Repository.LoadGoalAsync(f.Goal.Id))!;
        Assert.Equal(1, tick.ExplicitDisposition!.Revision);
        Assert.Equal(2, baseline.ExplicitDisposition!.Revision);
        var result = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, tick)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var raw = (await f.Repository.LoadGoalAsync(f.Goal.Id))!;
        Assert.Equal(baseline.ExplicitDisposition, raw.ExplicitDisposition);
        Assert.Equal(GoalStatus.Active, raw.Status);
        Assert.Null(raw.ParkedLifecycleStatus);
        await f.Reload();
        f.AssertFailure();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TickSave_UncommittedAuthority_RejectsNewOrConflictingRevision(bool sameRevision)
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        await f.Park();
        var baseline = (await f.Repository.LoadGoalAsync(f.Goal.Id))!;
        var tick = baseline with
        {
            ExplicitDisposition = new GoalExplicitDisposition(sameRevision ? 1 : 2,
                sameRevision ? GoalExplicitDispositionAction.Unpark : GoalExplicitDispositionAction.Park)
        };
        var result = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, tick)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Skipped, result.Disposition);
        var raw = (await f.Repository.LoadGoalAsync(f.Goal.Id))!;
        Assert.Equal(baseline.ExplicitDisposition, raw.ExplicitDisposition);
        Assert.Equal(GoalStatus.Parked, raw.Status);
    }

    private static void Unpark(ExplicitParkTestFixture f)
    {
        var outcome = CliPersistentStateRunner.ExecuteGoalUnparkApplication(
            new(f.Goal.Id.Value, "Supported later unpark", true), f.Goal.Id, f.Repository);
        Assert.True(outcome.ShouldSave);
    }

    [Theory]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Superseded)]
    public async Task TickMerge_StopAfterPark_RemainsTerminalWithLateEvidence(GoalStatus stopped)
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        await f.Park();
        await f.Reload();
        var baseline = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        await f.Repository.TransactGoalAsync(f.Goal.Id, (stored, _) =>
        {
            var writer = AgentOrchestratorKernel.FromSnapshot(new([stored!], []));
            if (stopped == GoalStatus.Cancelled)
                writer.CancelGoal(f.Goal.Id, "Later committed cancel");
            else
                writer.SupersedeGoal(f.Goal.Id, "Later committed supersede");
            Assert.Equal(stopped, writer.GetGoal(f.Goal.Id).Status);
            return Task.FromResult((true, writer.ExportGoalSnapshot(f.Goal.Id), true));
        });
        f.ReconcileRed(f.Driver());
        Assert.Equal(GoalStatus.Parked, f.Goal.Status);
        Assert.Equal(GoalStatus.Active, f.Goal.LifecycleStatus);
        var result = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, f.Kernel.ExportGoalSnapshot(f.Goal.Id))]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var raw = (await f.Repository.LoadGoalAsync(f.Goal.Id))!;
        Assert.Equal(stopped, raw.Status);
        Assert.Equal(stopped, raw.ParkedLifecycleStatus);
        Assert.Equal(baseline.ExplicitDisposition, raw.ExplicitDisposition);
        await f.Reload();
        Assert.Equal(stopped, f.Goal.Status);
        Assert.True(f.Goal.IsTerminal);
        Assert.False(f.Goal.IsExplicitlyParked);
        f.AssertFailure();
        var stopKind = stopped == GoalStatus.Cancelled ? ProgressKind.GoalCancelled : ProgressKind.GoalSuperseded;
        Assert.Single(f.Goal.Timeline.Where(e => e.Kind == stopKind));
        f.Driver().AdvanceOnce(f.Goal, ExplicitParkTestFixture.Policy);
        Assert.Equal(0, f.Dispatches + f.Acceptances + f.Landings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TickMerge_LaterDisposition_WinsWithoutLosingProgress(bool parkAgain)
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        await f.Park();
        await f.Reload();
        var baseline = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        Assert.Equal(GoalStatus.Parked, baseline.Status);
        await f.Unpark();
        if (parkAgain) await f.Park();
        f.ReconcileRed(f.Driver());
        var result = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, f.Kernel.ExportGoalSnapshot(f.Goal.Id))]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var raw = (await f.Repository.LoadGoalAsync(f.Goal.Id))!;
        Assert.Equal(parkAgain ? GoalStatus.Parked : GoalStatus.Active, raw.Status);
        Assert.Equal(parkAgain ? 3 : 2, raw.ExplicitDisposition!.Revision);
        await f.Reload();
        f.AssertFailure();
        var driver = f.Driver();
        driver.AdvanceOnce(f.Goal, ExplicitParkTestFixture.Policy);
        Assert.Equal(parkAgain ? 0 : 1, f.Dispatches);
    }

    [Fact]
    public async Task TickMerge_ParkThenUnparkDuringOldActiveTick_DoesNotStick()
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        var baseline = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        await f.Park();
        await f.Unpark();
        f.ReconcileRed(f.Driver());
        Assert.Equal(GoalStatus.Active, f.Goal.Status);
        var result = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, f.Kernel.ExportGoalSnapshot(f.Goal.Id))]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        await f.Reload();
        Assert.Equal(GoalStatus.Active, f.Goal.Status);
        Assert.Equal(GoalExplicitDispositionAction.Unpark, f.Goal.ExplicitDisposition!.Action);
        f.AssertFailure();
    }

    [Fact]
    public async Task TickSave_InventedDisposition_IsRejectedWithoutConflict()
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        var baseline = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        f.Kernel.ParkGoal(f.Goal.Id, "Uncommitted tick proposal");
        var result = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, f.Kernel.ExportGoalSnapshot(f.Goal.Id))]));
        Assert.Equal(GoalSnapshotSaveDisposition.Skipped, result.Disposition);
        Assert.Null((await f.Repository.LoadGoalAsync(f.Goal.Id))!.ExplicitDisposition);
    }

    [Fact]
    public async Task TickMerge_CompletionAfterPark_RetainsCompletionUntilUnpark()
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        var baseline = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        await f.Park();
        f.Kernel.CompleteGoal(f.Goal.Id, "Already owned landing completed");
        var result = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, f.Kernel.ExportGoalSnapshot(f.Goal.Id))]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var raw = (await f.Repository.LoadGoalAsync(f.Goal.Id))!;
        Assert.Equal(GoalStatus.Parked, raw.Status);
        Assert.Equal(GoalStatus.Completed, raw.ParkedLifecycleStatus);
        await f.Reload();
        Assert.Equal(GoalStatus.Parked, f.Goal.Status);
        await f.Unpark();
        await f.Reload();
        Assert.Equal(GoalStatus.Completed, f.Goal.Status);
        var landings = 0;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true),
            land: _ => { landings++; throw new InvalidOperationException("Cannot land twice"); });
        Assert.IsType<Mcg.AgentOrchestrator.Core.Conductor.ConductorAdvanceOutcome.Done>(
            driver.AdvanceOnce(f.Goal, ExplicitParkTestFixture.Policy).Outcome);
        Assert.Equal(0, landings);
    }
}
