using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CohortStableSlotAcquisitionRoundsTests : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void StartsOnFirstAcquiredRound(int successfulRound)
    {
        using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
            TimeSpan.Zero, storageRoot: StorageRoot);
        var events = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var rounds = new CohortStableSlotAcquisitionRounds(3, (duration, token) =>
        {
            Assert.Equal(CohortStableSlotAcquisitionRounds.InterRoundDelay, duration);
            Assert.Equal(cancellation.Token, token);
            events.Add("delay");
            return Task.CompletedTask;
        });
        var acquisitions = 0;

        var acquired = rounds.Acquire("cohort", token =>
        {
            Assert.Equal(cancellation.Token, token);
            events.Add("acquire");
            if (++acquisitions < successfulRound) throw Busy();
            return lease;
        }, cancellation.Token);

        Assert.Same(lease, acquired);
        Assert.Equal(successfulRound, acquisitions);
        Assert.Equal(Enumerable.Range(1, successfulRound).SelectMany(round =>
            round == successfulRound ? new[] { "acquire" } : new[] { "acquire", "delay" }), events);
    }

    [Fact]
    public void AllBusyRoundsDeferAndNameFinalHolders()
    {
        var acquisitions = 0;
        var delays = 0;
        var rounds = new CohortStableSlotAcquisitionRounds(3, (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });
        DotnetBuildEnvironmentLease? lease = null;
        var output = AsyncLocalConsoleRouter.Capture(() => lease = rounds.Acquire("same-cohort", _ =>
        {
            acquisitions++;
            throw new DotnetBuildSlotsBusyException(new("first-available-stable-slot",
                [new(0, 123) { HolderLabel = $"acceptance-attempt:round-{acquisitions}" }]));
        }));

        Assert.Null(lease);
        Assert.Equal(3, acquisitions);
        Assert.Equal(2, delays);
        Assert.Contains("COHORT_GATE_SLOTS_DEFERRED cohort=same-cohort rounds=3", output);
        Assert.Contains("slot-0:pid-123:holder-acceptance-attempt:round-3", output);
    }

    [Fact]
    public void CancellationDuringDelayStopsFurtherAcquisition()
    {
        var acquisitions = 0;
        using var cancellation = new CancellationTokenSource();
        var rounds = new CohortStableSlotAcquisitionRounds(3, (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        });

        Assert.ThrowsAny<OperationCanceledException>(() => rounds.Acquire("cohort", _ =>
        {
            acquisitions++;
            throw Busy();
        }, cancellation.Token));
        Assert.Equal(1, acquisitions);
    }

    [Fact]
    public void NonBusyFailurePropagatesWithoutDelay()
    {
        var failure = new IOException("gate storage unavailable");
        var rounds = new CohortStableSlotAcquisitionRounds(3, (_, _) =>
            throw new InvalidOperationException("A non-busy failure must not retry."));
        Assert.Same(failure, Assert.Throws<IOException>(() => rounds.Acquire("cohort", _ => throw failure)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CohortStableSlotAcquisitionRounds(0));
    }

    [Fact]
    public void DeferredRunRecordsBuildSlotsBusyDecisionWithoutGateReceipt()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Cohort member", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);

        var run = CohortStableSlotAcquisitionRounds.DeferredRun([goal], ConductorAutonomyPolicy.Permissive, "same-cohort");

        Assert.Null(run.Receipt);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(Assert.Single(run.MemberResults).Value.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Equal("same-cohort", held.StableIdentity);
        Assert.NotNull(held.Decision);
        Assert.Equal(VerifiedAdmissionPolicy.StageName, held.Decision.Stage);
        Assert.Equal(nameof(VerifiedAdmissionAction.Hold), held.Decision.Action);
        Assert.Equal("gate-start-build-slots-busy", held.Decision.DiscriminatingEvidence);
        var facts = VerifiedAdmissionFacts.FromRecordedFacts(held.Decision.Facts);
        Assert.Equal(goal.Status, facts.GoalStatus);
        Assert.Equal("build-slots-busy", facts.GateStartDeferral);
        Assert.Contains("cohort=same-cohort", facts.BuildSlotsBusyDetail);
        Assert.Equal(held.Reason, VerifiedAdmissionPolicy.Evaluate(facts).Reason);
    }

    private static DotnetBuildSlotsBusyException Busy() => new(new("first-available-stable-slot", []));
}
