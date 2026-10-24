using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every database belongs to a unique test directory.
public sealed class GoalLandingBlockerPersistenceTests
{
    private static readonly GoalLandingBlocker Blocker = new("landing-completion/criterion-evidence-hold",
        "operator evidence outstanding", "evidence:operator", new DateTimeOffset(2026, 10, 24, 9, 0, 0,
            TimeSpan.FromHours(-5)), "evidence-owner", "criterion-evidence-record");

    [Fact]
    public void Snapshot_RecordAndAbsentRecord_RoundTripAndOmitNullProperty()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("snapshot blocker");
        Assert.DoesNotContain("LandingBlocker", JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id)),
            StringComparison.OrdinalIgnoreCase);
        goal.ObserveLandingBlocker(Blocker);
        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        Assert.Equal(Blocker, restored.GetGoal(goal.Id).LandingBlocker);
    }

    [Fact]
    public async Task Save_Record_ReadOnlyReloadPreservesEveryField()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("persist blocker");
        goal.ObserveLandingBlocker(Blocker);
        await InfrastructureTestSupport.CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath)
            .SaveAsync(kernel);

        var restored = await SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath)
            .LoadAsync();
        Assert.Equal(Blocker, restored.GetGoal(goal.Id).LandingBlocker);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveTick_StoredRowAdvanced_MergesTickRecordOrClear(bool clear)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var repo = InfrastructureTestSupport.CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("merge blocker");
        if (clear) goal.ObserveLandingBlocker(Blocker);
        await repo.SaveAsync(kernel);
        var baseline = kernel.ExportGoalSnapshot(goal.Id);
        var tickKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([baseline], []));
        if (clear) tickKernel.GetGoal(goal.Id).ClearLandingBlocker();
        else tickKernel.GetGoal(goal.Id).ObserveLandingBlocker(Blocker);
        var tick = tickKernel.ExportGoalSnapshot(goal.Id);

        await repo.TransactGoalAsync<bool>(goal.Id, (stored, _) =>
            Task.FromResult((true, stored! with { Objective = "operator changed objective" }, true)));
        var result = Assert.Single(await repo.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, tick)]));

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var restored = (await repo.LoadAsync()).GetGoal(goal.Id);
        Assert.Equal("operator changed objective", restored.Objective);
        Assert.Equal(clear ? null : Blocker, restored.LandingBlocker);
    }
}
