using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorAcceptanceCohortAttributionVerdictTests
{
    private static readonly string FirstCandidate = new('b', 40);
    private static readonly string SecondCandidate = new('c', 40);
    private static readonly string Main = new('a', 40);

    [Fact]
    public void ReproducingMember_RecordsItsOwnPartitionFailure_AndSkipsInnocentPeer()
    {
        var first = Goal("11111111111111111111111111111111");
        var second = Goal("22222222222222222222222222222222");
        var cohort = Cohort(first, second, AcceptanceCohortAttributionOutcome.FirstMemberFailed,
            [new AcceptanceCohortAttributedMember(first.Id, 0, FirstCandidate, ["Tests.T"])]);
        var recorded = new List<(GoalId Goal, IReadOnlyList<string> Checks, string Branch, string Main,
            IReadOnlyList<AcceptanceCheckAttribution> Attributions)>();
        var lines = new List<string>();
        void Record(Goal goal, IReadOnlyList<string> checks, string branch, string main,
            IReadOnlyList<AcceptanceCheckAttribution> attributions) =>
            recorded.Add((goal.Id, checks, branch, main, attributions));

        var failed = ConductorAcceptanceCohortAttributionVerdicts.Apply(cohort, first,
            Partition(first, 0, FirstCandidate, AcceptanceCohortGateOutcome.Failed, ["partition-check"]),
            FirstCandidate, Main, false, Record, lines.Add);
        var innocent = ConductorAcceptanceCohortAttributionVerdicts.Apply(cohort, second,
            Partition(second, 1, SecondCandidate, AcceptanceCohortGateOutcome.Passed),
            SecondCandidate, Main, false, Record, lines.Add);

        Assert.True(failed.ShouldRecord);
        Assert.False(innocent.ShouldRecord);
        var call = Assert.Single(recorded);
        Assert.Equal(first.Id, call.Goal);
        Assert.Equal(["partition-check"], call.Checks);
        Assert.Equal(FirstCandidate, call.Branch);
        Assert.Equal(Main, call.Main);
        Assert.Contains("cohort-partition=partition-0", Assert.Single(call.Attributions).Evidence);
        Assert.Contains("reproduced=Tests.T", call.Attributions[0].Evidence);
        Assert.Equal(2, lines.Count);
        Assert.Contains("action=recorded reason=identity-match partition=partition-0", lines[0]);
        Assert.Contains("action=skipped reason=not-attributed partition=partition-1", lines[1]);
        var innocentHeld = new ConductorAdvanceResult(second.Id.Value, second.Id.Value[..8],
            "permissive", new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "Cohort held."));
        var innocentResult = ConductorAcceptanceCohortAttributionVerdicts.CompleteMember(cohort, second,
            Partition(second, 1, SecondCandidate, AcceptanceCohortGateOutcome.Passed),
            SecondCandidate, Main, false, Record,
            innocentHeld, _ => { });
        Assert.Same(innocentHeld, innocentResult);
        Assert.Equal(GoalStatus.Draft, first.Status);
        Assert.Equal(GoalStatus.Draft, second.Status);
    }

    [Theory]
    [InlineData(true, "candidate-changed")]
    [InlineData(false, "main-changed")]
    public void StaleIdentity_SkipsFailure(bool candidateMoved, string reason)
    {
        var first = Goal("11111111111111111111111111111111");
        var second = Goal("22222222222222222222222222222222");
        var cohort = Cohort(first, second, AcceptanceCohortAttributionOutcome.FirstMemberFailed,
            [new AcceptanceCohortAttributedMember(first.Id, 0, FirstCandidate, ["Tests.T"])]);
        var calls = 0;
        var lines = new List<string>();
        var verdict = ConductorAcceptanceCohortAttributionVerdicts.Apply(cohort, first,
            Partition(first, 0, FirstCandidate, AcceptanceCohortGateOutcome.Failed),
            candidateMoved ? new string('d', 40) : FirstCandidate,
            candidateMoved ? Main : new string('e', 40), false,
            (_, _, _, _, _) => calls++, lines.Add);

        Assert.False(verdict.ShouldRecord);
        Assert.Equal(0, calls);
        Assert.Contains($"action=skipped reason={reason}", Assert.Single(lines));
    }

    [Theory]
    [InlineData(AcceptanceCohortAttributionOutcome.InteractionOnly, AcceptanceCohortGateOutcome.Failed)]
    [InlineData(AcceptanceCohortAttributionOutcome.Indeterminate, AcceptanceCohortGateOutcome.InfrastructureFailure)]
    [InlineData(AcceptanceCohortAttributionOutcome.FirstMemberFailed, AcceptanceCohortGateOutcome.Passed)]
    public void UnattributedOrPassingPartition_DoesNotRecordOrChangeGoal(
        AcceptanceCohortAttributionOutcome outcome, AcceptanceCohortGateOutcome partitionOutcome)
    {
        var first = Goal("11111111111111111111111111111111");
        var second = Goal("22222222222222222222222222222222");
        var cohort = Cohort(first, second, outcome, []);
        var calls = 0;
        var verdict = ConductorAcceptanceCohortAttributionVerdicts.Apply(cohort, first,
            Partition(first, 0, FirstCandidate, partitionOutcome), FirstCandidate, Main, false,
            (_, _, _, _, _) => calls++, _ => { });

        Assert.False(verdict.ShouldRecord);
        Assert.Equal("not-attributed", verdict.Reason);
        Assert.Equal(0, calls);
        Assert.Equal(GoalStatus.Draft, first.Status);
    }

    [Fact]
    public void FailedPartitionWithoutReproducedTest_DoesNotRecord()
    {
        var first = Goal("11111111111111111111111111111111");
        var second = Goal("22222222222222222222222222222222");
        var cohort = Cohort(first, second, AcceptanceCohortAttributionOutcome.FirstMemberFailed,
            [new AcceptanceCohortAttributedMember(first.Id, 0, FirstCandidate, ["Tests.T"])]);
        var calls = 0;
        var unrelated = Partition(first, 0, FirstCandidate, AcceptanceCohortGateOutcome.Failed) with
        { FailingTestIdentities = ["Tests.U"] };
        var verdict = ConductorAcceptanceCohortAttributionVerdicts.Apply(cohort, first, unrelated,
            FirstCandidate, Main, false, (_, _, _, _, _) => calls++, _ => { });
        Assert.Equal("not-attributed", verdict.Reason);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public void BothFailedClassification_RecordsOnlyMembersWithOwnReproduction(
        bool secondReproduced, int expectedCalls)
    {
        var first = Goal("11111111111111111111111111111111");
        var second = Goal("22222222222222222222222222222222");
        var attributed = new List<AcceptanceCohortAttributedMember>
        {
            new(first.Id, 0, FirstCandidate, ["Tests.T"])
        };
        if (secondReproduced)
        {
            attributed.Add(new AcceptanceCohortAttributedMember(second.Id, 1, SecondCandidate, ["Tests.T"]));
        }
        var cohort = Cohort(first, second, AcceptanceCohortAttributionOutcome.BothMembersFailed, attributed);
        var calls = new List<GoalId>();
        void Record(Goal goal, IReadOnlyList<string> _, string __, string ___,
            IReadOnlyList<AcceptanceCheckAttribution> ____) => calls.Add(goal.Id);
        _ = ConductorAcceptanceCohortAttributionVerdicts.Apply(cohort, first,
            Partition(first, 0, FirstCandidate, AcceptanceCohortGateOutcome.Failed),
            FirstCandidate, Main, false, Record, _ => { });
        _ = ConductorAcceptanceCohortAttributionVerdicts.Apply(cohort, second,
            Partition(second, 1, SecondCandidate, AcceptanceCohortGateOutcome.Failed),
            SecondCandidate, Main, false, Record, _ => { });
        Assert.Equal(expectedCalls, calls.Count);
        Assert.Contains(first.Id, calls);
        Assert.Equal(secondReproduced, calls.Contains(second.Id));
    }

    [Fact]
    public void ReplayedReceipt_SkipsSecondFailureWithDurableEvidenceKey()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = kernel.CreateGoal("Cohort replay test",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var second = Goal("22222222222222222222222222222222");
        var cohort = Cohort(first, second, AcceptanceCohortAttributionOutcome.FirstMemberFailed,
            [new AcceptanceCohortAttributedMember(first.Id, 0, FirstCandidate, ["Tests.T"])]);
        var partition = Partition(first, 0, FirstCandidate, AcceptanceCohortGateOutcome.Failed);
        var calls = 0;
        void Record(Goal goal, IReadOnlyList<string> checks, string branch, string main,
            IReadOnlyList<AcceptanceCheckAttribution> attributions)
        {
            calls++;
            kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attributions);
        }
        _ = ConductorAcceptanceCohortAttributionVerdicts.Apply(cohort, first, partition,
            FirstCandidate, Main, false, Record, _ => { });
        var resumedKernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var resumedGoal = Assert.Single(resumedKernel.Goals);
        var replayLines = new List<string>();
        _ = ConductorAcceptanceCohortAttributionVerdicts.Apply(cohort, resumedGoal, partition,
            FirstCandidate, Main,
            ConductorAcceptanceCohortAttributionVerdicts.IsRecordedFailure(
                resumedGoal, cohort.Identity.Value, partition.ReceiptId), Record, replayLines.Add);

        Assert.Equal(1, calls);
        Assert.Contains("disposition=already-recorded", Assert.Single(replayLines));
    }

    [Fact]
    public void Completion_HoldsAttributedMember_ThenNextTickRoutesOnceAfterRestart()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = kernel.CreateGoal("Cohort member awaiting acceptance",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(first.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskVerification(first.Id, first.Tasks.Single().Id,
            ManualVerificationRecorder.Create(true, "Passed.", Path.GetTempPath(), DateTimeOffset.UtcNow));
        Assert.Equal(GoalStatus.Verified, first.Status);
        var second = Goal("22222222222222222222222222222222");
        var cohort = Cohort(first, second, AcceptanceCohortAttributionOutcome.FirstMemberFailed,
            [new AcceptanceCohortAttributedMember(first.Id, 0, FirstCandidate, ["Tests.T"])]);
        var partition = Partition(first, 0, FirstCandidate, AcceptanceCohortGateOutcome.Failed);
        var held = new ConductorAdvanceResult(first.Id.Value, first.Id.Value[..8], "permissive",
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "Cohort held."));
        var failureRecords = 0;
        var result = ConductorAcceptanceCohortAttributionVerdicts.CompleteMember(cohort, first,
            partition,
            FirstCandidate, Main, false,
            (goal, checks, branch, main, attributions) =>
            {
                failureRecords++;
                kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attributions);
            }, held, _ => { });

        Assert.Equal(GoalStatus.Verified, first.Status);
        Assert.Equal(1, failureRecords);
        Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);

        var resumedKernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var resumedGoal = Assert.Single(resumedKernel.Goals);
        var replay = ConductorAcceptanceCohortAttributionVerdicts.CompleteMember(cohort, resumedGoal,
            partition, FirstCandidate, Main,
            ConductorAcceptanceCohortAttributionVerdicts.IsRecordedFailure(
                resumedGoal, cohort.Identity.Value, partition.ReceiptId),
            (_, _, _, _, _) => failureRecords++, held, _ => { });
        Assert.Equal(result.Outcome, replay.Outcome);
        Assert.Equal(1, failureRecords);
        Assert.Equal(GoalStatus.Verified, resumedGoal.Status);

        // The next tick observes the durable failure and routes without running another gate.
        var failure = Assert.IsType<AcceptanceFailureSummary>(resumedGoal.LatestAcceptanceFailure);
        Assert.Contains("cohort-partition=partition-0", failure.CheckAttributions![0].Evidence);
        Assert.True(resumedKernel.RouteRecordedAcceptanceFailure(resumedGoal.Id,
            "Acceptance verification failed; review and fix before landing."));
        Assert.Equal(GoalStatus.AcceptanceFailed, resumedGoal.Status);
    }

    private static Goal Goal(string id) => new(new GoalId(id), "Cohort verdict test",
        [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);

    private static AcceptanceCohortPartitionReceipt Partition(Goal goal, int ordinal, string candidate,
        AcceptanceCohortGateOutcome outcome, IReadOnlyList<string>? failedChecks = null) =>
        new($"partition-{ordinal}", goal.Id, ordinal, candidate, Main, new string('d', 40),
            "manifest-v1", outcome, 1, [])
        {
            FailedChecks = failedChecks ?? [],
            FailingTestIdentities = outcome == AcceptanceCohortGateOutcome.Failed ? ["Tests.T"] : []
        };

    private static AcceptanceCohortReceipt Cohort(Goal first, Goal second,
        AcceptanceCohortAttributionOutcome outcome, IReadOnlyList<AcceptanceCohortAttributedMember> attributed)
    {
        var bindings = new[]
        {
            Binding(first, FirstCandidate, "src/First.cs", "resource:first"),
            Binding(second, SecondCandidate, "src/Second.cs", "resource:second")
        };
        var identity = AcceptanceCohortIdentity.Create(bindings, Main, new string('e', 40), "manifest-v1");
        return new AcceptanceCohortReceipt("combined-red", identity, AcceptanceCohortGateOutcome.Failed,
            DateTimeOffset.UtcNow, 1, ["combined-check"], 1, [], outcome)
        { AttributedMembers = attributed };
    }

    private static AcceptanceCohortMemberBinding Binding(Goal goal, string candidate, string path, string resource) =>
        new(goal.Id, candidate, candidate, [path], [resource], ChangeRiskTier.DocsOnly,
            ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");
}
