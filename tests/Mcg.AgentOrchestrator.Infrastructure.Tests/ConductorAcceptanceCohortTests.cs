using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorAcceptanceCohortTests
{
    private const string MainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Selector_EnumeratesPairsLexicographically_AndDoesNotLetOldestConflictHideLaterPair()
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
        Assert.StartsWith("cohort-v1-", identity.Value, StringComparison.Ordinal);
        Assert.Equal("cohort-v1-".Length + 64, identity.Value.Length);
        Assert.NotEqual(identity.Value, reversed.Value);
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
                AcceptanceCohortGateOutcome.Passed,
                ConductorDriver.ClassifyCohortVerification(new AcceptanceVerificationResult(
                    Passed: true, Skipped: false, ExitCode: 0, OutputTail: null, TestResultPaths: [trx])));
        }
        finally
        {
            if (File.Exists(trx)) File.Delete(trx);
        }
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
        string resource) => new(
            new GoalId(goalValue),
            new string(revisionCharacter, 40),
            new string(revisionCharacter, 40),
            [path],
            [resource]);
}
