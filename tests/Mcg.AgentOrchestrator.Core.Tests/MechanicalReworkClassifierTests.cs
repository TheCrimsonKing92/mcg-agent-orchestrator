using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory snapshots and fixed timestamps.
public sealed class MechanicalReworkClassifierTests
{
    private const string Prefix = "developer-completion structural pre-check failed: ";
    private const string Ratchet = "src/Alpha.cs has 11 lines, exceeding the recorded ceiling of 10. Extract behavior.";
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-05T12:00:00Z");

    [Theory]
    [InlineData(Ratchet, true)]
    [InlineData(Ratchet + "\r\n\r\n" + Ratchet, true)]
    [InlineData(Ratchet + "\nother finding", false)]
    [InlineData("", false)]
    [InlineData("src/Al,pha.cs has 11 lines, exceeding the recorded ceiling of 10.", false)]
    [InlineData("src/Al;pha.cs has 11 lines, exceeding the recorded ceiling of 10.", false)]
    [InlineData("src/Al pha.cs has 11 lines, exceeding the recorded ceiling of 10.", false)]
    public void StructuralRatchet_RequiresEveryLineAndMarkerSafeIds(string body, bool expected)
    {
        var result = Classify(Prefix + body);
        Assert.Equal(expected, result.IsMechanical);
        Assert.Equal(expected ? "structural-ratchet" : null, result.RuleId);
        Assert.Equal(expected ? new[] { "src/Alpha.cs" } : [], result.Ids);
    }

    [Fact]
    public void StructuralManifest_UsesExactPrefixAndCanonicalId()
    {
        var result = Classify(Prefix + "config/acceptance-manifest.json: collection ownership inconsistent.");
        Assert.True(result.IsMechanical);
        Assert.Equal("structural-manifest", result.RuleId);
        Assert.Equal(["config/acceptance-manifest.json"], result.Ids);
        Assert.False(Classify(Prefix + "config/other.json: collection ownership inconsistent.").IsMechanical);
    }

    [Theory]
    [InlineData(FindingCategory.SpecCompliance, "SourceSizeRatchet overrun", "finding", true)]
    [InlineData(FindingCategory.Correctness, "sourcesizeratchet overrun", "finding", true)]
    [InlineData(FindingCategory.CodeQuality, "EXCEEDING THE RECORDED CEILING", "finding", true)]
    [InlineData(FindingCategory.TestEvidence, "SourceSizeRatchet overrun", "finding", false)]
    [InlineData(FindingCategory.Correctness, "Behavior is wrong", "finding", false)]
    [InlineData(FindingCategory.CodeQuality, "SourceSizeRatchet overrun", "bad,id", false)]
    [InlineData(FindingCategory.CodeQuality, "SourceSizeRatchet overrun", "bad id", false)]
    [InlineData(FindingCategory.CodeQuality, "SourceSizeRatchet overrun", "bad;id", false)]
    public void ReviewRatchet_RequiresAllowedCategoryDescriptionAndId(
        FindingCategory category, string description, string id, bool expected)
    {
        var finding = new ReviewFinding(id, ReviewFindingState.Open, new("src/Alpha.cs", "file"),
            description, FindingSeverity.Blocking, category);
        var result = Classify("auto-review-retry round 1 convergence brief", [finding]);
        Assert.Equal(expected, result.IsMechanical);
        Assert.Equal(expected ? "review-ratchet" : null, result.RuleId);
        Assert.Equal(expected ? new[] { id } : [], result.Ids);
        Assert.False(Classify("ordinary retry mentioning SourceSizeRatchet", [finding]).IsMechanical);
    }

    [Fact]
    public void ReviewRatchet_EmptyOrMixedOpenFindingsStayPrimary()
    {
        const string retry = "auto-review-retry round 1 convergence brief";
        Assert.False(Classify(retry).IsMechanical);
        var ratchet = new ReviewFinding("ratchet", ReviewFindingState.Open, new("src/Alpha.cs", "file"),
            "SourceSizeRatchet overrun", FindingSeverity.Blocking, FindingCategory.CodeQuality);
        Assert.False(Classify(retry, [ratchet, ratchet with { StableId = "defect", Description = "Incorrect retry behavior" }]).IsMechanical);
        Assert.False(Classify(retry, [ratchet with { State = ReviewFindingState.Resolved }]).IsMechanical);
    }

    [Fact]
    public void LatestRetry_AfterDispatchWinsRegardlessOfClock()
    {
        var goal = CreateGoal([
            Event(ProgressKind.TaskRetried, Prefix + Ratchet),
            Event(ProgressKind.TaskDispatchRecorded, "dispatch"),
            Event(ProgressKind.TaskRetried, Prefix + Ratchet),
            Event(ProgressKind.TaskRetried, "pre-review build repair: CS1002")]);
        Assert.False(MechanicalReworkClassifier.Classify(goal, goal.Tasks.Single()).IsMechanical);
        var stale = CreateGoal([Event(ProgressKind.TaskRetried, Prefix + Ratchet), Event(ProgressKind.TaskDispatchRecorded, "dispatch")]);
        Assert.False(MechanicalReworkClassifier.Classify(stale, stale.Tasks.Single()).IsMechanical);
        // Dispatch timestamp is deliberately later than all events; the retained event anchor wins.
        Assert.True(Classify(Prefix + Ratchet).IsMechanical);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void TrimmedTimeline_RequiresRetryAfterDispatchTimestamp(int minutes, bool expected)
    {
        var goal = CreateGoal([Event(ProgressKind.TaskRetried, Prefix + Ratchet) with { OccurredAt = At.AddHours(1).AddMinutes(minutes) }]);
        Assert.Equal(expected, MechanicalReworkClassifier.Classify(goal, goal.Tasks.Single()).IsMechanical);
    }

    [Fact]
    public void MissingDispatchOrDifferentRole_CannotClassifyMechanical()
    {
        var noDispatch = CreateGoal([Event(ProgressKind.TaskRetried, Prefix + Ratchet)], dispatch: false);
        Assert.False(MechanicalReworkClassifier.Classify(noDispatch, noDispatch.Tasks.Single()).IsMechanical);
        var tester = CreateGoal([Event(ProgressKind.TaskDispatchRecorded, "dispatch"), Event(ProgressKind.TaskRetried, Prefix + Ratchet)], role: AgentRole.Tester);
        Assert.False(MechanicalReworkClassifier.Classify(tester, tester.Tasks.Single()).IsMechanical);
    }

    private static MechanicalReworkClassification Classify(string message, IReadOnlyList<ReviewFinding>? findings = null)
    {
        var goal = CreateGoal([Event(ProgressKind.TaskDispatchRecorded, "dispatch"), Event(ProgressKind.TaskRetried, message)], findings);
        return MechanicalReworkClassifier.Classify(goal, goal.Tasks.Single());
    }
    private static ProgressEventSnapshot Event(ProgressKind kind, string text) => new("goal", "developer", kind, text, At);
    private static Goal CreateGoal(IReadOnlyList<ProgressEventSnapshot> events, IReadOnlyList<ReviewFinding>? findings = null,
        bool dispatch = true, AgentRole role = AgentRole.Developer)
    {
        TaskVerificationSnapshot[] verifications = findings is null ? [] :
            [new("review", "root", 1, "findings", "", At, MergedReviewFindings: findings)];
        var task = new TaskSnapshot("developer", "Implement", role, WorkTaskStatus.Assigned,
            null, null, null, verifications, null, null,
            DispatchHistory: dispatch ? [new("worker", "command", "root", At.AddHours(1))] : []);
        return AgentOrchestratorKernel.FromSnapshot(new([new("goal", "goal", GoalStatus.Active, [task], events)], [])).Goals.Single();
    }
}
