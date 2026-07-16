using Mcg.AgentOrchestrator.Core;

public sealed class ModelOutcomeScorecardTests
{
    private const string WorkDir = "C:\\work";
    private const string DispatchCommand = "worker run";

    [Xunit.Fact(DisplayName = "ModelOutcomeScorecard_recent_failure_downweights_old_success")]
    public void ModelOutcomeScorecardRecentFailureDownweightsOldSuccess()
    {
        // 4 old successes (T+0..T+3) + 2 recent failures (T+4, T+5), window=6.
        // Flat: 2/6 failed (33%) → Neutral (2*2=4 < 6).
        // Decay (linear rank, newest weight=6): weightedFailed=6+5=11, totalWeight=21 → 11*2=22>21 → Avoid.
        const string provider = "TestProvider";
        const string model = "test-model";
        var kernel = new AgentOrchestratorKernel();
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var goals = Enumerable.Range(0, 6)
            .Select(i =>
            {
                var goal = kernel.CreateGoal($"Goal {i}", [new TaskSpec(TaskId.New(), "task", AgentRole.Developer)]);
                kernel.ActivateGoal(goal.Id, DefaultAgents());
                return goal;
            })
            .ToList();

        for (var i = 0; i < 4; i++)
        {
            Dispatch(kernel, goals[i], provider, model, baseTime.AddSeconds(i), exitCode: 0);
        }

        for (var i = 4; i < 6; i++)
        {
            Dispatch(kernel, goals[i], provider, model, baseTime.AddSeconds(i), exitCode: 1);
        }

        var allTasks = goals.SelectMany(g => g.Tasks).ToList();
        var scorecard = ModelOutcomeScorecard.Build(allTasks, windowSize: 6);
        var record = scorecard.Single(r => r.ProviderName == provider && r.ModelName == model);

        Assert.Equal(4, record.Completed);
        Assert.Equal(2, record.Failed);
        Assert.Equal(ModelOutcomeRecommendation.Avoid, record.Recommendation);
    }

    [Xunit.Fact(DisplayName = "ModelOutcomeScorecard_decay_weighting_is_deterministic")]
    public void ModelOutcomeScorecardDecayWeightingIsDeterministic()
    {
        // 2 old completions (T+0, T+1) + 1 recent failure (T+2), window=3.
        // Decay: newest(failed)=3, middle(ok)=2, oldest(ok)=1 → weightedFailed=3, totalWeight=6 → 3*2=6>=6 → Avoid.
        // Two calls with identical inputs must produce identical recommendation and reason.
        const string provider = "TestProvider";
        const string model = "test-model";
        var kernel = new AgentOrchestratorKernel();
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var goals = Enumerable.Range(0, 3)
            .Select(i =>
            {
                var goal = kernel.CreateGoal($"Goal {i}", [new TaskSpec(TaskId.New(), "task", AgentRole.Developer)]);
                kernel.ActivateGoal(goal.Id, DefaultAgents());
                return goal;
            })
            .ToList();

        Dispatch(kernel, goals[0], provider, model, baseTime, exitCode: 0);
        Dispatch(kernel, goals[1], provider, model, baseTime.AddSeconds(1), exitCode: 0);
        Dispatch(kernel, goals[2], provider, model, baseTime.AddSeconds(2), exitCode: 1);

        var allTasks = goals.SelectMany(g => g.Tasks).ToList();
        var first = ModelOutcomeScorecard.Build(allTasks, windowSize: 3);
        var second = ModelOutcomeScorecard.Build(allTasks, windowSize: 3);

        var r1 = first.Single(r => r.ProviderName == provider && r.ModelName == model);
        var r2 = second.Single(r => r.ProviderName == provider && r.ModelName == model);
        Assert.Equal(ModelOutcomeRecommendation.Avoid, r1.Recommendation);
        Assert.Equal(r1.Recommendation, r2.Recommendation);
        Assert.Equal(r1.Reason, r2.Reason);
    }

    [Xunit.Fact(DisplayName = "ModelOutcomeScorecard_segments_real_and_environmental_failures")]
    public void ModelOutcomeScorecardSegmentsRealAndEnvironmentalFailures()
    {
        var rows = new[]
        {
            Row(0, WorkTaskStatus.Failed, "succeeded-worker-result-failing-tests"),
            Row(1, WorkTaskStatus.Failed, "provider-connectivity"),
            Row(2, WorkTaskStatus.Failed, "retry-round-produced-no-commit-and-no-deferral"),
            Row(3, WorkTaskStatus.Completed, "committed-worker-result-evidence")
        };

        var record = ModelOutcomeScorecard.Build(rows, windowSize: 4).Single();

        Assert.Equal(1, record.Completed);
        Assert.Equal(3, record.Failed);
        Assert.Equal(1, record.RealFailures);
        Assert.Equal(1, record.EnvironmentalFailures);
        Assert.Equal(1, record.ManufacturedFixedFailures);
        Assert.Equal(0, record.UnknownEraFailures);
        Assert.Equal(ModelOutcomeRecommendation.Neutral, record.Recommendation);
        Assert.Contains("1/4 real/code failure", record.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 environmental", record.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static void Dispatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string provider,
        string model,
        DateTimeOffset at,
        int exitCode)
    {
        var task = goal.Tasks[0];
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("worker-cli", DispatchCommand, WorkDir, at, provider, model));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                DispatchCommand,
                WorkDir,
                exitCode,
                exitCode == 0 ? "ok" : "failed",
                exitCode == 0 ? string.Empty : "error",
            at));
    }

    private static ModelFitHistoryRow Row(int seconds, WorkTaskStatus outcome, string rule)
    {
        var classification = TaskOutcomeClassifier.Classify(outcome, rule);
        return new ModelFitHistoryRow(
            "goal",
            $"task-{seconds}",
            AgentRole.Developer,
            "OpenAI",
            "gpt-5.5",
            TaskComplexity.Complex,
            "implementation",
            outcome,
            ModelFitHistory.Adequate,
            new DateTimeOffset(2026, 1, 1, 0, 0, seconds, TimeSpan.Zero),
            classification.Rule,
            classification.Class);
    }
}
