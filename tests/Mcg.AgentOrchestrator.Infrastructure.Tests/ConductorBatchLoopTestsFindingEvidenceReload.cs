using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsFindingEvidenceReload : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsFindingEvidenceReload(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [Xunit.InlineData("unavailable")]
    public void DifferentOrUnavailableCandidateDoesNotHandoffStoredDelivery(string candidate)
    {
        var fixture = new ConductorFindingEvidenceLoopFixture();
        var driver = fixture.CreateDriver();
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        fixture.Kernel.RefreshTrackedGoals(fixture.StoredProjection());
        fixture.CurrentCandidate = candidate;

        driver.AdvanceOnce(fixture.Kernel.Goals.Single(), ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, fixture.FocusedRuns);
        Assert.Equal(fixture.ReviewerId, Assert.Single(fixture.Retries).TaskId);
        Assert.Equal(WorkTaskStatus.Completed,
            fixture.Kernel.Goals.Single().Tasks.Single(task => task.Id == fixture.DeveloperId).Status);
    }

    [Xunit.Fact]
    public void DeliveryMarkerWithoutStoredReceiptDoesNotRetryDeveloper()
    {
        var fixture = new ConductorFindingEvidenceLoopFixture();
        var driver = fixture.CreateDriver(recordReceipt: false);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        fixture.Kernel.RefreshTrackedGoals(fixture.StoredProjection());

        driver.AdvanceOnce(fixture.Kernel.Goals.Single(), ConductorAutonomyPolicy.Permissive);

        Assert.Equal(fixture.ReviewerId, Assert.Single(fixture.Retries).TaskId);
        Assert.Empty(fixture.Kernel.Goals.Single().Tasks.Single(task => task.Id == fixture.ReviewerId)
            .VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? []));
        Assert.Equal(WorkTaskStatus.Completed,
            fixture.Kernel.Goals.Single().Tasks.Single(task => task.Id == fixture.DeveloperId).Status);
    }

    [Xunit.Fact(Timeout = 30000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void StoredReceiptHandsOpenSourceFindingToDeveloperAfterFirstDelivery()
    {
        var fixture = new ConductorFindingEvidenceLoopFixture();
        var stored = fixture.StoredProjection();
        var reloads = 0;
        var writtenTicks = new List<int>();
        var tickOpen = false;
        var loop = new ConductorBatchLoop(
            utcNow: () => ConductorFindingEvidenceLoopFixture.Now,
            sweep: kernel =>
            {
                kernel.RefreshTrackedGoals(stored);
                reloads++;
                tickOpen = true;
            });

        var result = loop.Run(fixture.Kernel, fixture.CreateDriver(), ConductorAutonomyPolicy.Permissive,
            NoStopPath(), maxIterations: 4, watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false,
            onTick: _ => tickOpen = false,
            persistGoalTick: (_, ids) =>
            {
                if (!tickOpen || !ids.Contains(fixture.Goal.Id)) return;
                stored = fixture.StoredProjection();
                writtenTicks.Add(reloads);
            });

        Assert.Equal(4, result.Ticks);
        Assert.Equal(4, reloads);
        Assert.Equal(1, fixture.FocusedRuns);
        Assert.Collection(fixture.Retries,
            firstDelivery =>
            {
                Assert.Equal(fixture.ReviewerId, firstDelivery.TaskId);
                Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, firstDelivery.Cause);
                Assert.StartsWith("finding evidence-on-demand:", firstDelivery.Message);
            },
            afterRun =>
            {
                Assert.Equal(fixture.DeveloperId, afterRun.TaskId);
                Assert.Equal(RetryCause.NewSourceFinding, afterRun.Cause);
                Assert.Contains(ConductorFindingEvidenceLoopFixture.FindingId, afterRun.Message);
            });
        Assert.Contains(1, writtenTicks);
        Assert.Contains(2, writtenTicks);
        var durable = AgentOrchestratorKernel.FromSnapshot(stored).Goals.Single();
        Assert.Equal(RetryCause.NewSourceFinding,
            durable.Tasks.Single(task => task.Id == fixture.DeveloperId).PendingRetryCause);
        Assert.Single(durable.Tasks.Single(task => task.Id == fixture.ReviewerId).VerificationHistory
            .SelectMany(record => record.FindingEvidenceReceipts ?? [])
            .Where(receipt => receipt.CandidateSha == ConductorFindingEvidenceLoopFixture.Candidate));
    }
}
