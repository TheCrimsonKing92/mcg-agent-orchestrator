using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ModelOutcomeScorecardTests
{
    private static readonly IReadOnlyList<AgentDefinition> DefaultAgents =
        AgentCatalog.Default().Agents;

    private const string WorkDir = "C:\\work";
    private const string DispatchCommand = "worker-cli run";

    [Xunit.Fact(DisplayName = "ModelOutcomeScorecard_recommends_Prefer_when_all_dispatches_completed")]
    public void ModelOutcomeScorecardRecommendsPreferWhenAllDispatchesCompleted()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal1 = kernel.CreateGoal("Feature A", [MakeTask(AgentRole.Developer)]);
        var goal2 = kernel.CreateGoal("Feature B", [MakeTask(AgentRole.Developer)]);
        kernel.ActivateGoal(goal1.Id, DefaultAgents);
        kernel.ActivateGoal(goal2.Id, DefaultAgents);

        RecordCompletedDispatch(kernel, goal1, goal1.Tasks[0], "Anthropic", "claude-sonnet-4-6",
            modelFitNote: "adequate");
        RecordCompletedDispatch(kernel, goal2, goal2.Tasks[0], "Anthropic", "claude-sonnet-4-6",
            modelFitNote: "adequate");

        var scorecard = kernel.BuildModelOutcomeScorecard();

        var record = scorecard.Single(r => r.ProviderName == "Anthropic" && r.ModelName == "claude-sonnet-4-6");
        Assert.Equal(ModelOutcomeRecommendation.Prefer, record.Recommendation);
        Assert.Equal(2, record.Completed);
        Assert.Equal(0, record.Failed);
        Assert.Equal(0, record.SelfRatedUnderpowered);
        Assert.True(record.Reason.Contains("All 2", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "ModelOutcomeScorecard_recommends_Avoid_when_majority_of_dispatches_failed")]
    public void ModelOutcomeScorecardRecommendsAvoidWhenMajorityOfDispatchesFailed()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal1 = kernel.CreateGoal("Task X", [MakeTask(AgentRole.Developer)]);
        var goal2 = kernel.CreateGoal("Task Y", [MakeTask(AgentRole.Developer)]);
        var goal3 = kernel.CreateGoal("Task Z", [MakeTask(AgentRole.Developer)]);
        kernel.ActivateGoal(goal1.Id, DefaultAgents);
        kernel.ActivateGoal(goal2.Id, DefaultAgents);
        kernel.ActivateGoal(goal3.Id, DefaultAgents);

        RecordFailedDispatch(kernel, goal1, goal1.Tasks[0], "OpenAI", "gpt-5.5");
        RecordFailedDispatch(kernel, goal2, goal2.Tasks[0], "OpenAI", "gpt-5.5");
        RecordCompletedDispatch(kernel, goal3, goal3.Tasks[0], "OpenAI", "gpt-5.5");

        var scorecard = kernel.BuildModelOutcomeScorecard();

        var record = scorecard.Single(r => r.ProviderName == "OpenAI" && r.ModelName == "gpt-5.5");
        Assert.Equal(ModelOutcomeRecommendation.Avoid, record.Recommendation);
        Assert.Equal(1, record.Completed);
        Assert.Equal(2, record.Failed);
        Assert.True(record.Reason.Contains("2/3", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "ModelOutcomeScorecard_counts_divergence_for_adequate_self_rating_that_failed")]
    public void ModelOutcomeScorecardCountsDivergenceForAdequateSelfRatingThatFailed()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal1 = kernel.CreateGoal("Task P", [MakeTask(AgentRole.Developer)]);
        var goal2 = kernel.CreateGoal("Task Q", [MakeTask(AgentRole.Developer)]);
        var goal3 = kernel.CreateGoal("Task R", [MakeTask(AgentRole.Developer)]);
        kernel.ActivateGoal(goal1.Id, DefaultAgents);
        kernel.ActivateGoal(goal2.Id, DefaultAgents);
        kernel.ActivateGoal(goal3.Id, DefaultAgents);

        // Two failures, one with adequate self-rating (divergence), one without
        RecordFailedDispatch(kernel, goal1, goal1.Tasks[0], "Anthropic", "claude-haiku-4-5",
            modelFitNote: "adequate");
        RecordFailedDispatch(kernel, goal2, goal2.Tasks[0], "Anthropic", "claude-haiku-4-5",
            modelFitNote: null);
        RecordCompletedDispatch(kernel, goal3, goal3.Tasks[0], "Anthropic", "claude-haiku-4-5");

        var scorecard = kernel.BuildModelOutcomeScorecard();

        var record = scorecard.Single(r => r.ProviderName == "Anthropic" && r.ModelName == "claude-haiku-4-5");
        Assert.Equal(1, record.Divergence);
        Assert.Equal(1, record.SelfRatedAdequate);
        Assert.Equal(ModelOutcomeRecommendation.Avoid, record.Recommendation);
        Assert.True(record.Reason.Contains("1 self-rated adequate", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "ModelOutcomeScorecard_recommends_Neutral_when_samples_are_insufficient")]
    public void ModelOutcomeScorecardRecommendsNeutralWhenSamplesAreInsufficient()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Single task", [MakeTask(AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents);

        RecordCompletedDispatch(kernel, goal, goal.Tasks[0], "Ollama", "qwen3:8b",
            modelFitNote: "adequate");

        var scorecard = kernel.BuildModelOutcomeScorecard();

        var record = scorecard.Single(r => r.ProviderName == "Ollama" && r.ModelName == "qwen3:8b");
        Assert.Equal(ModelOutcomeRecommendation.Neutral, record.Recommendation);
        Assert.True(record.Reason.Contains("Insufficient", StringComparison.OrdinalIgnoreCase));
        Assert.True(record.Reason.Contains("1/2", StringComparison.OrdinalIgnoreCase));
    }

    private static TaskSpec MakeTask(AgentRole role) =>
        new(TaskId.New(), $"{role} implementation task", role);

    private static void RecordCompletedDispatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string providerName,
        string modelName,
        string? modelFitNote = null)
    {
        var dispatch = new TaskDispatchRecord(
            "worker-cli",
            DispatchCommand,
            WorkDir,
            DateTimeOffset.UtcNow,
            ProviderName: providerName,
            ModelName: modelName);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);

        var stdout = "Task completed successfully." +
            (modelFitNote is not null
                ? $"\nModel fit: {providerName}/{modelName} - {modelFitNote} - unit test - deterministic"
                : string.Empty);

        var verification = new TaskVerificationRecord(
            DispatchCommand,
            WorkDir,
            0,
            stdout,
            string.Empty,
            DateTimeOffset.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
    }

    private static void RecordFailedDispatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string providerName,
        string modelName,
        string? modelFitNote = null)
    {
        var dispatch = new TaskDispatchRecord(
            "worker-cli",
            DispatchCommand,
            WorkDir,
            DateTimeOffset.UtcNow,
            ProviderName: providerName,
            ModelName: modelName);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);

        var stdout = "Task failed with errors." +
            (modelFitNote is not null
                ? $"\nModel fit: {providerName}/{modelName} - {modelFitNote} - unit test - deterministic"
                : string.Empty);

        var verification = new TaskVerificationRecord(
            DispatchCommand,
            WorkDir,
            1,
            stdout,
            "Error: task failed",
            DateTimeOffset.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
        kernel.RecordTaskNote(
            goal.Id,
            task.Id,
            "CLASSIFIER rule=real-failure; outcome_class=real-failure; verdict=UnknownFailure");
    }
}
