using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

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
