using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorJudgePanelReScopeProvenanceTests
{
    [Fact]
    public async Task Resolve_MintedReScope_UsesHistoricalCriteriaWithRecordedProvenance()
    {
        using var scenario = new GoalRefinementReScopeVersioningTests.Scenario(
            GoalRefinementReScopeVersioningTests.Original);
        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);
        var originalTime = Assert.Single(scenario.Goal.RefinedSpecVersions).RecordedAt;
        var question = Assert.Single(scenario.Goal.RefinedSpec!.OpenQuestions);
        scenario.Clock.UtcNow = originalTime.AddHours(1);
        Assert.True(await scenario.Service.TryResolveOpenClarificationAsync(
            scenario.Kernel, question.Id, $"re-scope: {GoalRefinementReScopeVersioningTests.Replacement}"));
        var snapshot = scenario.Kernel.ExportGoalSnapshot(scenario.Goal.Id);
        Assert.Equal(2, snapshot.RefinedSpecVersions!.Count);
        Assert.Equal(2, snapshot.RefinedSpecVersions[0].SupersededByVersion);
        Assert.Contains(snapshot.RefinedSpecVersions[1].Spec.Decisions,
            decision => decision.Rationale.StartsWith("Feasibility re-scope recorded as", StringComparison.Ordinal));

        var before = ConductorJudgePanelCriteriaAtTrigger.Resolve(snapshot, originalTime);
        var after = ConductorJudgePanelCriteriaAtTrigger.Resolve(snapshot, scenario.Clock.UtcNow);

        Assert.Equal([GoalRefinementReScopeVersioningTests.Original], before.Criteria);
        Assert.Equal("as-recorded", before.Provenance);
        Assert.Equal([GoalRefinementReScopeVersioningTests.Replacement], after.Criteria);
        Assert.Equal("as-recorded", after.Provenance);
        Assert.NotEqual(before.Version, after.Version);
    }
}
