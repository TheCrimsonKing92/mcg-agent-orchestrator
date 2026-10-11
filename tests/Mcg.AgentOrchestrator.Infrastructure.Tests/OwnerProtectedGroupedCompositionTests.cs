using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: pure selectors and projections, with no shared state or I/O.
public sealed class OwnerProtectedGroupedCompositionTests
{
    [Theory]
    [InlineData("config/acceptance-manifest.json")]
    [InlineData("config\\acceptance-manifest.json")]
    [InlineData("./CONFIG/ACCEPTANCE-MANIFEST.JSON")]
    [InlineData("/config/acceptance-manifest.json")]
    [InlineData("config/conductor-policy.json")]
    [InlineData("nested\\CONDUCTOR-POLICY.JSON")]
    public void Contains_ProtectedPath_ReturnsTrue(string path)
    {
        Assert.True(OwnerProtectedLandingPaths.Contains(["src/Sample.cs", path]));
    }

    [Theory]
    [InlineData("src/config/acceptance-manifest.json")]
    [InlineData("config/acceptance-manifest.json.bak")]
    [InlineData("docs/acceptance-manifest.md")]
    [InlineData("conductor-policy.json.bak")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Contains_UnprotectedPath_ReturnsFalse(string? path)
    {
        Assert.False(OwnerProtectedLandingPaths.Contains([path!]));
    }

    [Fact]
    public void Contains_NoPaths_ReturnsFalse()
    {
        Assert.False(OwnerProtectedLandingPaths.Contains(null));
        Assert.False(OwnerProtectedLandingPaths.Contains([]));
    }

    [Theory]
    [InlineData("config/acceptance-manifest.json")]
    [InlineData("config\\acceptance-manifest.json")]
    [InlineData("config/conductor-policy.json")]
    [InlineData("nested\\conductor-policy.json")]
    public void Cohort_ProtectedMember_SelectsOtherTwoAndRecordsReason(string path)
    {
        var protectedMember = Ready('1', 'a', path);
        var b = Ready('2', 'b');
        var c = Ready('3', 'c');

        var result = ConductorAcceptanceCohortSelector.Select([protectedMember, b, c]);

        Assert.NotNull(result.Selection);
        Assert.Equal([b.GoalId, c.GoalId], result.Selection.Members.Select(member => member.GoalId));
        var exclusion = Assert.Single(result.Exclusions);
        Assert.Equal(ConductorAcceptanceCohortPairExclusionReason.OwnerProtectedMember, exclusion.Reason);
        Assert.Equal(protectedMember.GoalId, exclusion.FirstGoalId);
        Assert.Equal(protectedMember.GoalId, exclusion.SecondGoalId);
        Assert.Equal($"goal={protectedMember.GoalId.Value}", exclusion.Evidence);
        Assert.Null(ConductorAcceptanceCohortSelector.Select([protectedMember, b]).Selection);
        Assert.Null(ConductorAcceptanceCohortSelector.Select([protectedMember, b, c],
            forcedCandidate: protectedMember.GoalId).Selection);
    }

    [Fact]
    public void Cohort_UnprotectedMembers_PreservesFirstPair()
    {
        var a = Ready('1', 'a');
        var b = Ready('2', 'b');
        var c = Ready('3', 'c');

        var result = ConductorAcceptanceCohortSelector.Select([a, b, c]);

        Assert.NotNull(result.Selection);
        Assert.Equal([a.GoalId, b.GoalId], result.Selection.Members.Select(member => member.GoalId));
        Assert.DoesNotContain(result.Exclusions,
            exclusion => exclusion.Reason == ConductorAcceptanceCohortPairExclusionReason.OwnerProtectedMember);
    }

    [Theory]
    [InlineData("config/acceptance-manifest.json")]
    [InlineData("config\\acceptance-manifest.json")]
    [InlineData("config/conductor-policy.json")]
    [InlineData("nested\\conductor-policy.json")]
    public void Train_ProtectedMember_SelectsOtherThreeOrReturnsNull(string path)
    {
        var protectedMember = Ready('1', 'a', path);
        var b = Ready('2', 'b');
        var c = Ready('3', 'c');
        var d = Ready('4', 'd');

        var result = ConductorMergeTrainSelector.Select([protectedMember, b, c, d]);

        Assert.NotNull(result);
        Assert.Equal([b.GoalId, c.GoalId, d.GoalId], result.Members.Select(member => member.GoalId));
        Assert.Null(ConductorMergeTrainSelector.Select([protectedMember, b, c]));
    }

    [Fact]
    public void Train_UnprotectedMembers_PreservesAllThree()
    {
        var a = Ready('1', 'a');
        var b = Ready('2', 'b');
        var c = Ready('3', 'c');

        var result = ConductorMergeTrainSelector.Select([a, b, c]);

        Assert.NotNull(result);
        Assert.Equal([a.GoalId, b.GoalId, c.GoalId], result.Members.Select(member => member.GoalId));
    }

    private static ConductorSpeculativeAcceptanceCandidate Ready(char goal, char revision, string? path = null)
    {
        var goalId = new GoalId(new string(goal, 32));
        var projection = new GateReadyCandidateProjection(goalId, GoalLifecycleState.Verified,
            GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto, path is null ? [$"src/{goal}.cs"] : [$"src/{goal}.cs", path],
            [$"resource:{goal}"],
            new GateReadyMergeEvidence(new string(revision, 40), new string('f', 40),
                GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected));
        return new ConductorSpeculativeAcceptanceCandidate(goalId,
            new GateReadyCandidateProjectionResult.Ready(projection));
    }
}
