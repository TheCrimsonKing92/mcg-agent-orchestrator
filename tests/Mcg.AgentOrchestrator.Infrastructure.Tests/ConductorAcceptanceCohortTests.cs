using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorAcceptanceCohortTests
{
    private const string MainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Selector_EnumeratesPairsLexically_WithoutConflictMaskingLaterPair()
    {
        var oldest = Ready("11111111111111111111111111111111", "src", "resource:oldest");
        var second = Ready("22222222222222222222222222222222", "src/Second.cs", "resource:second");
        var third = Ready("33333333333333333333333333333333", "src/Third.cs", "resource:third");

        var selection = Assert.IsType<ConductorAcceptanceCohortSelection>(
            ConductorAcceptanceCohortSelector.Select([oldest, second, third]));

        Assert.Equal([second.GoalId, third.GoalId], selection.Members.Select(member => member.GoalId));
        Assert.Equal(2, selection.Members.Count);
        Assert.All(selection.Exclusions, exclusion =>
            Assert.Equal(ConductorAcceptanceCohortPairExclusionReason.LandingPathOverlap, exclusion.Reason));
    }

    [Fact]
    public void Selector_ForcedCandidateIsFirst_WithoutReorderingRemainingCandidates()
    {
        var first = Ready("11111111111111111111111111111111", "src/First.cs", "resource:first");
        var forced = Ready("33333333333333333333333333333333", "src/Forced.cs", "resource:forced");
        var second = Ready("22222222222222222222222222222222", "src/Second.cs", "resource:second");

        var selection = Assert.IsType<ConductorAcceptanceCohortSelection>(
            ConductorAcceptanceCohortSelector.Select([first, second, forced], forced.GoalId));

        Assert.Equal([forced.GoalId, first.GoalId], selection.Members.Select(member => member.GoalId));
    }

    [Fact]
    public void Selector_ForcedCandidateWithoutCompatiblePeer_DoesNotAdmitLaterPair()
    {
        var forced = Ready("11111111111111111111111111111111", "src", "resource:forced");
        var second = Ready("22222222222222222222222222222222", "src/Second.cs", "resource:second");
        var third = Ready("33333333333333333333333333333333", "src/Third.cs", "resource:third");

        Assert.Null(ConductorAcceptanceCohortSelector.Select([forced, second, third], forced.GoalId));
    }

    [Fact]
    public void Selector_ForcedCandidateWithoutReadyProjection_DoesNotAdmitLaterPair()
    {
        var forcedGoal = new GoalId("11111111111111111111111111111111");
        var excludedForced = new ConductorSpeculativeAcceptanceCandidate(
            forcedGoal,
            new GateReadyCandidateProjectionResult.Excluded(
                GateReadyCandidateExclusionReason.LifecycleNotReady));
        var second = Ready("22222222222222222222222222222222", "src/Second.cs", "resource:second");
        var third = Ready("33333333333333333333333333333333", "tests/Third.cs", "resource:third");

        Assert.Null(ConductorAcceptanceCohortSelector.Select([excludedForced, second, third], forcedGoal));
    }

    [Fact]
    public void Selector_FailsClosedForEmptyResources_AndChecksResourcesCaseInsensitively()
    {
        var empty = Ready("11111111111111111111111111111111", "src/First.cs", resource: null);
        var sharedA = Ready("22222222222222222222222222222222", "src/Second.cs", "ownership:Shared");
        var sharedB = Ready("33333333333333333333333333333333", "tests/Third.cs", "OWNERSHIP:SHARED");

        Assert.Null(ConductorAcceptanceCohortSelector.Select([empty, sharedA]));
        Assert.Null(ConductorAcceptanceCohortSelector.Select([sharedA, sharedB]));
    }

    [Fact]
    public void Identity_IsStableLengthPrefixedAndMemberOrderSensitive()
    {
        var first = Binding("11111111111111111111111111111111", 'b', "a:b", "resource:a");
        var second = Binding("22222222222222222222222222222222", 'c', "a", "b:resource:a");

        var identity = AcceptanceCohortIdentity.Create(
            [first, second],
            MainRevision,
            "dddddddddddddddddddddddddddddddddddddddd",
            "manifest-v1");
        var repeated = AcceptanceCohortIdentity.Create(
            [first, second],
            MainRevision,
            "dddddddddddddddddddddddddddddddddddddddd",
            "manifest-v1");
        var reversed = AcceptanceCohortIdentity.Create(
            [second, first],
            MainRevision,
            "dddddddddddddddddddddddddddddddddddddddd",
            "manifest-v1");

        Assert.Equal(identity.Value, repeated.Value);
        Assert.StartsWith($"{AcceptanceCohortIdentity.Version}-", identity.Value, StringComparison.Ordinal);
        Assert.Equal(AcceptanceCohortIdentity.Version.Length + 1 + 64, identity.Value.Length);
        Assert.NotEqual(identity.Value, reversed.Value);
    }

    [Fact]
    public void Identity_BindsLandingScopeAndResources()
    {
        var first = Binding("11111111111111111111111111111111", 'b', "src/A.cs", "resource:a");
        var second = Binding("22222222222222222222222222222222", 'c', "tests/B.cs", "resource:b");
        var changedPath = Binding("11111111111111111111111111111111", 'b', "src/Changed.cs", "resource:a");
        var changedResource = Binding("11111111111111111111111111111111", 'b', "src/A.cs", "resource:changed");
        var changedRisk = Binding(
            "11111111111111111111111111111111", 'b', "src/A.cs", "resource:a", ChangeRiskTier.Security);
        var changedPromotion = Binding(
            "11111111111111111111111111111111", 'b', "src/A.cs", "resource:a",
            promotion: ConductorTransitionDecision.Escalate);
        var changedMerge = Binding(
            "11111111111111111111111111111111", 'b', "src/A.cs", "resource:a",
            mergeStatus: "Conflict", mergeReason: "ConflictsDetected");

        var original = AcceptanceCohortIdentity.Create(
            [first, second], MainRevision, new string('d', 40), "manifest-v1");
        var withChangedPath = AcceptanceCohortIdentity.Create(
            [changedPath, second], MainRevision, new string('d', 40), "manifest-v1");
        var withChangedResource = AcceptanceCohortIdentity.Create(
            [changedResource, second], MainRevision, new string('d', 40), "manifest-v1");
        var withChangedRisk = AcceptanceCohortIdentity.Create(
            [changedRisk, second], MainRevision, new string('d', 40), "manifest-v1");
        var withChangedPromotion = AcceptanceCohortIdentity.Create(
            [changedPromotion, second], MainRevision, new string('d', 40), "manifest-v1");
        var withChangedMerge = AcceptanceCohortIdentity.Create(
            [changedMerge, second], MainRevision, new string('d', 40), "manifest-v1");

        Assert.NotEqual(original.Value, withChangedPath.Value);
        Assert.NotEqual(original.Value, withChangedResource.Value);
        Assert.NotEqual(original.Value, withChangedRisk.Value);
        Assert.NotEqual(original.Value, withChangedPromotion.Value);
        Assert.NotEqual(original.Value, withChangedMerge.Value);
    }

    [Fact]
    public void Identity_RequiresExactlyTwoDistinctMembers()
    {
        var member = Binding("11111111111111111111111111111111", 'b', "src/A.cs", "resource:a");

        Assert.Throws<ArgumentException>(() => AcceptanceCohortIdentity.Create(
            [member], MainRevision, "dddddddddddddddddddddddddddddddddddddddd", "manifest-v1"));
        Assert.Throws<ArgumentException>(() => AcceptanceCohortIdentity.Create(
            [member, member], MainRevision, "dddddddddddddddddddddddddddddddddddddddd", "manifest-v1"));
    }

    [Theory]
    [InlineData(AcceptanceCohortGateOutcome.Failed, AcceptanceCohortGateOutcome.Passed, AcceptanceCohortAttributionOutcome.FirstMemberFailed)]
    [InlineData(AcceptanceCohortGateOutcome.Passed, AcceptanceCohortGateOutcome.Failed, AcceptanceCohortAttributionOutcome.SecondMemberFailed)]
    [InlineData(AcceptanceCohortGateOutcome.Failed, AcceptanceCohortGateOutcome.Failed, AcceptanceCohortAttributionOutcome.BothMembersFailed)]
    [InlineData(AcceptanceCohortGateOutcome.Passed, AcceptanceCohortGateOutcome.Passed, AcceptanceCohortAttributionOutcome.InteractionOnly)]
    [InlineData(AcceptanceCohortGateOutcome.InfrastructureFailure, AcceptanceCohortGateOutcome.Passed, AcceptanceCohortAttributionOutcome.Indeterminate)]
    public void Attribution_UsesOnlyCompletePositiveVerdicts(
        AcceptanceCohortGateOutcome first,
        AcceptanceCohortGateOutcome second,
        AcceptanceCohortAttributionOutcome expected)
    {
        Assert.Equal(expected, ConductorAcceptanceCohortAttribution.Classify(first, second));
    }

    [Fact]
    public void PassingGateRequiresParseableTrxEvidence()
    {
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-{Guid.NewGuid():N}.trx");
        try
        {
            Assert.Equal(
                AcceptanceCohortGateOutcome.InfrastructureFailure,
                ConductorDriver.ClassifyCohortVerification(new AcceptanceVerificationResult(
                    Passed: true, Skipped: false, ExitCode: 0, OutputTail: null)));
            File.WriteAllText(trx, "not xml");
            Assert.Equal(
                AcceptanceCohortGateOutcome.InfrastructureFailure,
                ConductorDriver.ClassifyCohortVerification(new AcceptanceVerificationResult(
                    Passed: true, Skipped: false, ExitCode: 0, OutputTail: null, TestResultPaths: [trx])));
            File.WriteAllText(trx, "<TestRun />");
            Assert.Equal(
                AcceptanceCohortGateOutcome.InfrastructureFailure,
                ConductorDriver.ClassifyCohortVerification(new AcceptanceVerificationResult(
                    Passed: true, Skipped: false, ExitCode: 0, OutputTail: null, TestResultPaths: [trx])));
            File.WriteAllText(trx, ValidPassingTrx());
            Assert.Equal(
                AcceptanceCohortGateOutcome.Passed,
                ConductorDriver.ClassifyCohortVerification(new AcceptanceVerificationResult(
                    Passed: true, Skipped: false, ExitCode: 0, OutputTail: null, TestResultPaths: [trx])));
        }
        finally
        {
            if (File.Exists(trx)) File.Delete(trx);
        }
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Aborted")]
    [InlineData("Timeout")]
    [InlineData("NotExecuted")]
    public void NonVerdictTrxOutcome_IsInfrastructureFailure(string outcome)
    {
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-nonverdict-{Guid.NewGuid():N}.trx");
        try
        {
            File.WriteAllText(trx, TrxWithOutcome(outcome));

            Assert.Equal(
                AcceptanceCohortGateOutcome.InfrastructureFailure,
                ConductorDriver.ClassifyCohortVerification(new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    TestResultPaths: [trx])));
        }
        finally
        {
            if (File.Exists(trx)) File.Delete(trx);
        }
    }

    private static string ValidPassingTrx() => """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testId="1" testName="Passes" outcome="Passed" />
          </Results>
          <ResultSummary outcome="Completed">
            <Counters total="1" executed="1" passed="1" failed="0" />
          </ResultSummary>
        </TestRun>
        """;

    private static string TrxWithOutcome(string outcome) => $"""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testId="1" testName="NonVerdict" outcome="{outcome}" />
          </Results>
          <ResultSummary outcome="Completed">
            <Counters total="1" executed="1" passed="0" failed="0" />
          </ResultSummary>
        </TestRun>
        """;

    [Fact]
    public void FailedGateWithoutTrxIsInfrastructureFailure()
    {
        Assert.Equal(
            AcceptanceCohortGateOutcome.InfrastructureFailure,
            ConductorDriver.ClassifyCohortVerification(new AcceptanceVerificationResult(
                Passed: false,
                Skipped: false,
                ExitCode: 1,
                OutputTail: "runner returned no result artifact")));
    }

    private static ConductorSpeculativeAcceptanceCandidate Ready(
        string goalValue,
        string path,
        string? resource,
        string mainRevision = MainRevision)
    {
        var goalId = new GoalId(goalValue);
        var projection = new GateReadyCandidateProjection(
            goalId,
            GoalLifecycleState.Verified,
            GateReadyVerificationState.Satisfied,
            ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto,
            [path],
            resource is null ? [] : [resource],
            new GateReadyMergeEvidence(
                goalValue.PadRight(40, 'f')[..40],
                mainRevision,
                GateReadyMergeStatus.Clean,
                GateReadyMergeReason.NoConflictsDetected));
        return new ConductorSpeculativeAcceptanceCandidate(
            goalId,
            new GateReadyCandidateProjectionResult.Ready(projection));
    }

    private static AcceptanceCohortMemberBinding Binding(
        string goalValue,
        char revisionCharacter,
        string path,
        string resource,
        ChangeRiskTier risk = ChangeRiskTier.Behavior,
        ConductorTransitionDecision promotion = ConductorTransitionDecision.Auto,
        string? mergeStatus = null,
        string? mergeReason = null) => new(
            new GoalId(goalValue),
            new string(revisionCharacter, 40),
            new string(revisionCharacter, 40),
            [path],
            [resource],
            risk,
            promotion,
            mergeStatus ?? GateReadyMergeStatus.Clean.ToString(),
            mergeReason ?? GateReadyMergeReason.NoConflictsDetected.ToString());
}
