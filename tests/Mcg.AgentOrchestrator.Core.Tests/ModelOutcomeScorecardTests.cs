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

        var scorecard = ModelOutcomeScorecard.Build(goals, windowSize: 6);
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

        var first = ModelOutcomeScorecard.Build(goals, windowSize: 3);
        var second = ModelOutcomeScorecard.Build(goals, windowSize: 3);

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

    [Xunit.Fact(DisplayName = "ModelOutcomeScorecard_groups_same_model_by_dispatch_lane")]
    public void ModelOutcomeScorecardGroupsSameModelByDispatchLane()
    {
        var rows = new[]
        {
            Row(0, WorkTaskStatus.Completed, "committed-worker-result-evidence") with { DispatchLane = "codex-cli" },
            Row(1, WorkTaskStatus.Failed, "succeeded-worker-result-failing-tests") with { DispatchLane = "codex-spark" }
        };

        var scorecard = ModelOutcomeScorecard.Build(rows, windowSize: 2);

        Assert.Equal(2, scorecard.Count);
        Assert.Contains(scorecard, record => record.ProviderName == "OpenAI" &&
            record.ModelName == "gpt-5.5" &&
            record.DispatchLane == "codex-cli" &&
            record.Completed == 1);
        Assert.Contains(scorecard, record => record.ProviderName == "OpenAI" &&
            record.ModelName == "gpt-5.5" &&
            record.DispatchLane == "codex-spark" &&
            record.Failed == 1);
    }

    [Xunit.Fact(DisplayName = "TaskOutcomeClassifier_provider_neutral_progress_stall_is_environmental")]
    public void TaskOutcomeClassifierProviderNeutralProgressStallIsEnvironmental()
    {
        var classification = TaskOutcomeClassifier.Classify(
            WorkTaskStatus.Failed,
            "provider-neutral-progress-stall");

        Assert.Equal("provider-neutral-progress-stall", classification.Rule);
        Assert.Equal(TaskOutcomeClass.Environmental, classification.Class);
    }

    [Xunit.Fact(DisplayName = "TaskOutcomeClassifier_producer_rule_catalog_has_exact_coverage")]
    public void TaskOutcomeClassifierProducerRuleCatalogHasExactCoverage()
    {
        var declaredProducerRules = typeof(TaskOutcomeRules)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(TaskOutcomeRule))
            .Select(field => Assert.IsType<TaskOutcomeRule>(field.GetValue(null)))
            .ToList();
        var duplicateTokens = TaskOutcomeRules.Produced
            .GroupBy(rule => rule.Token, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicateTokens);
        Assert.Equal(
            declaredProducerRules.Select(rule => rule.Token).OrderBy(token => token, StringComparer.OrdinalIgnoreCase),
            TaskOutcomeRules.Produced.Select(rule => rule.Token).OrderBy(token => token, StringComparer.OrdinalIgnoreCase));
        Assert.All(TaskOutcomeRules.Produced, rule =>
        {
            Assert.True(TaskOutcomeRules.Known.TryGetValue(rule.Token, out var known));
            Assert.Equal(rule.Class, known!.Class);
        });
    }

    [Xunit.Theory(DisplayName = "TaskOutcomeClassifier_non_merit_failures_never_become_real_failures")]
    [Xunit.InlineData("silent-launch-failure", TaskOutcomeClass.Environmental)]
    [Xunit.InlineData("provider-unknown", TaskOutcomeClass.UnknownEra)]
    [Xunit.InlineData("unknown-failure", TaskOutcomeClass.UnknownEra)]
    [Xunit.InlineData("tester-verification-inconclusive", TaskOutcomeClass.UnknownEra)]
    [Xunit.InlineData("unrecognized-future-rule", TaskOutcomeClass.UnknownEra)]
    public void TaskOutcomeClassifierNonMeritFailuresNeverBecomeRealFailures(
        string rule,
        TaskOutcomeClass expected)
    {
        var classification = TaskOutcomeClassifier.Classify(WorkTaskStatus.Failed, rule);

        Assert.Equal(expected, classification.Class);
        Assert.NotEqual(TaskOutcomeClass.RealFailure, classification.Class);
    }

    [Xunit.Fact(DisplayName = "TaskOutcomeClassifier_timeline_uses_producer_outcome_class_before_legacy_rule_fallback")]
    public void TaskOutcomeClassifierTimelineUsesProducerOutcomeClassBeforeLegacyRuleFallback()
    {
        var goalId = GoalId.New();
        var taskId = TaskId.New();
        var timeline = new[]
        {
            new ProgressEvent(
                goalId,
                taskId,
                ProgressKind.TaskNote,
                "CLASSIFIER rule=provider-sandbox1312; outcome_class=manufactured-fixed; verdict=SandboxCommitBlocked",
                DateTimeOffset.UtcNow)
        };

        var classification = TaskOutcomeClassifier.FromTimeline(timeline, taskId, WorkTaskStatus.Failed);

        Assert.Equal("provider-sandbox1312", classification.Rule);
        Assert.Equal(TaskOutcomeClass.ManufacturedFixed, classification.Class);
    }

    [Xunit.Fact(DisplayName = "TaskOutcomeClassifier_timeline_recognizes_legacy_sandbox1312_rule_without_outcome_class")]
    public void TaskOutcomeClassifierTimelineRecognizesLegacySandbox1312RuleWithoutOutcomeClass()
    {
        var goalId = GoalId.New();
        var taskId = TaskId.New();
        var timeline = new[]
        {
            new ProgressEvent(
                goalId,
                taskId,
                ProgressKind.TaskNote,
                "CLASSIFIER rule=provider-sandbox1312; verdict=SandboxCommitBlocked",
                DateTimeOffset.UtcNow)
        };

        var classification = TaskOutcomeClassifier.FromTimeline(timeline, taskId, WorkTaskStatus.Failed);

        Assert.Equal("provider-sandbox1312", classification.Rule);
        Assert.Equal(TaskOutcomeClass.ManufacturedFixed, classification.Class);
    }

    [Xunit.Fact(DisplayName = "LoopHealthReport_uses_timeline_outcome_class_for_failed_model_mix")]
    public void LoopHealthReportUsesTimelineOutcomeClassForFailedModelMix()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Launch apparatus failure", [new TaskSpec(TaskId.New(), "task", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        Dispatch(kernel, goal, "OpenAI", "gpt-5.5", DateTimeOffset.UtcNow, exitCode: 1);
        kernel.RecordTaskNote(
            goal.Id,
            goal.Tasks[0].Id,
            "CLASSIFIER rule=silent-launch-failure; outcome_class=environmental; verdict=LaunchFailure");

        var snapshot = LoopHealthReport.Build([goal], []);
        var record = Assert.Single(snapshot.ModelOutcomeMix);

        Assert.Equal(0, record.RealFailures);
        Assert.Equal(1, record.EnvironmentalFailures);
        Assert.Equal("worker-cli", record.DispatchLane);
    }

    [Xunit.Fact(DisplayName = "ModelFitHistory_best_fit_uses_real_failures_not_total_failures")]
    public void ModelFitHistoryBestFitUsesRealFailuresNotTotalFailures()
    {
        var rows = new[]
        {
            Row(0, WorkTaskStatus.Failed, "provider-connectivity"),
            Row(1, WorkTaskStatus.Failed, "provider-neutral-progress-stall"),
            Row(2, WorkTaskStatus.Completed, "committed-worker-result-evidence")
        };

        var best = ModelFitHistory.QueryBestFitForRole(rows, AgentRole.Developer, windowSize: 3);

        Assert.NotNull(best);
        Assert.Equal("OpenAI", best!.ProviderName);
        Assert.Equal("gpt-5.5", best.ModelName);
        Assert.Equal(ModelOutcomeRecommendation.Neutral, best.Recommendation);
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
                exitCode == 0 ? "ok" : "worker produced substantive failure output",
                exitCode == 0 ? string.Empty : "error",
            at));
        if (exitCode != 0)
        {
            kernel.RecordTaskNote(
                goal.Id,
                task.Id,
                "CLASSIFIER rule=real-failure; outcome_class=real-failure; verdict=UnknownFailure");
        }
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
