using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each fixture uses its own in-memory kernel and starts no processes.
public sealed class ApiPromptCostGuardTests
{
    [Xunit.Fact(DisplayName = "ApiPromptCostGuard_retains_earlier_model_fit_attempts")]
    public void ApiPromptCostGuardRetainsEarlierModelFitAttempts()
    {
        var kernel = new AgentOrchestratorKernel();
        var priorTask = new TaskSpec(TaskId.New(), "Update the old label.", AgentRole.Developer);
        var nextTask = new TaskSpec(TaskId.New(), "Update the next label.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Avoid repeated overkill API model from full history", [priorTask, nextTask]);
        var agent = new AgentDefinition(
            AgentId.New(),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 768),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var agents = new[] { agent };
        kernel.ActivateGoal(goal.Id, agents);
        kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
            "manual-verification passed",
            "C:\\repo",
            0,
            "Evidence checked.\nModel fit: OpenAI/gpt-5-codex - overkill - label-only change.",
            string.Empty,
            DateTimeOffset.UtcNow));
        kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
            "manual-verification passed",
            "C:\\repo",
            0,
            "Evidence checked.\nModel fit: OpenAI/gpt-5.4-mini - adequate - focused parser fix.",
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(1)));

        var preview = AgentTaskRunner.PreviewRun(goal, nextTask, agents);
        var risk = ApiPromptCostGuard.Evaluate(preview, goal);
        var requiredRisk = risk ?? throw new InvalidOperationException("Expected paid API prompt risk.");

        Assert.Equal("prior overkill API model", ApiPromptCostGuard.BuildInlineLabel(requiredRisk));
        Assert.Equal(1, requiredRisk.PriorOverkillCount);
        Assert.True(requiredRisk.PriorTaskShapes?.Contains("label-only change") == true);
        Assert.True(ApiPromptCostGuard.BuildRecommendation(requiredRisk)?.Contains("try local Ollama/qwen3:8b", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "ApiPromptCostGuard_uses_complex_threshold_for_evidence_escalated_model")]
    public void ApiPromptCostGuardUsesComplexThresholdForEvidenceEscalatedModel()
    {
        var preview = new AgentTaskRunPreview(
            AgentId.New(),
            "Developer",
            "OpenAI",
            AgentCatalog.OpenAiSubscriptionModelAlias,
            TaskComplexity.Simple,
            MaxOutputTokens: 1200,
            ReasoningEffort: "high",
            PromptCharacterCount: 5000,
            UsesComplexModel: true);

        var risk = ApiPromptCostGuard.Evaluate(preview);

        Assert.True(risk is not null);
        Assert.Equal(6000, risk!.PromptThreshold);
        Assert.False(risk.PromptExceedsThreshold);
        Assert.True(risk.UsesComplexPaidModel);
        Assert.Equal("complex paid API model", ApiPromptCostGuard.BuildInlineLabel(risk));
    }
}
