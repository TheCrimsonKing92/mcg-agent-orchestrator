using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: in-memory goals, dispatch records and plan rows with fixed inputs; no external resources.
public sealed class SubscriptionPromptCostGuardLunaThresholdTests
{
    [Fact]
    public void PreparedLunaTypedContext_ProceedsWithoutConfirmation()
    {
        var risk = PreparedRisk(AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias, 15710);

        Assert.NotNull(risk);
        Assert.Equal(15710, risk.PromptCharacterCount);
        Assert.False(risk.IsAnomalous);
        SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(risk, confirmed: false);
    }

    [Theory]
    [InlineData(AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias, 15710)]
    [InlineData(AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias, 19000)]
    [InlineData("gpt-5-codex", 15710)]
    public void PreparedAnomalousPrompt_StillRequiresConfirmation(string model, int characters)
    {
        var risk = PreparedRisk(model, characters);

        Assert.NotNull(risk);
        Assert.True(risk.IsAnomalous);
        Assert.Throws<InvalidOperationException>(() =>
            SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(risk, confirmed: false));
    }

    [Fact]
    public void FourReadyLunaTasks_KeepBatchCountAndConfirmationThresholds()
    {
        var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(ReadyItems(4, 9000));

        Assert.NotNull(risk);
        Assert.Equal(4, risk.TaskCount);
        Assert.True(risk.TaskCountExceedsThreshold);
        Assert.Equal(18000, risk.BatchPromptThreshold);
        Assert.Equal(3, risk.BatchTaskThreshold);
        var error = Assert.Throws<InvalidOperationException>(() =>
            SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(risk, confirmed: false));
        Assert.Contains("thresholds 18000 chars or 3 task(s)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoReadyLunaPrompts_KeepBatchOversizeAdvisory()
    {
        var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(ReadyItems(2, 10000));

        Assert.NotNull(risk);
        Assert.Equal(20000, risk.PromptCharacterCount);
        Assert.True(risk.PromptExceedsBatchThreshold);
        Assert.False(risk.TaskCountExceedsThreshold);
        Assert.False(risk.IsAnomalous);
        SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(risk, confirmed: false);
    }

    [Theory]
    [InlineData("OpenAI", AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias, 9500)]
    [InlineData("openai", "GPT-6-LUNA", 9500)]
    [InlineData("Anthropic", AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias, 6000)]
    [InlineData("OpenAI", AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias, 6000)]
    [InlineData("OpenAI", AgentCatalog.OpenAiTerraSubscriptionModelAlias, 6000)]
    [InlineData("OpenAI", "gpt-5-codex", 6000)]
    [InlineData("OpenAI", null, 6000)]
    public void SimpleThreshold_GrantsLargerBudgetOnlyToOpenAiLuna(string provider, string? model, int expected)
    {
        Assert.Equal(expected, PaidPromptThresholds.PromptThreshold(provider, model, TaskComplexity.Simple, false));
        Assert.Equal(expected * 2, PaidPromptThresholds.AnomalyPromptThreshold(provider, model, TaskComplexity.Simple, false));
        Assert.Equal(9500, PaidPromptThresholds.PromptThreshold(provider, model, TaskComplexity.Complex, false));
        Assert.Equal(9500, PaidPromptThresholds.PromptThreshold(provider, model, TaskComplexity.Simple, true));
    }

    private static PaidSubscriptionPromptRisk? PreparedRisk(string model, int characters)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Update one label.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Fix a typo", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", DateTimeOffset.Parse("2026-07-17T12:00:00Z"),
            ProviderName: "OpenAI", ModelName: model, TaskComplexity: TaskComplexity.Simple,
            PromptCharacterCount: characters, WorkerProviderKind: ProviderKind.OpenAICodexCli));
        return SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(goal, task);
    }

    private static SubscriptionPlanItem[] ReadyItems(int count, int characters) =>
        Enumerable.Range(1, count).Select(number => new SubscriptionPlanItem(
            TaskNumber: number, TaskId: $"task-{number}", Role: AgentRole.Tester,
            TaskStatus: WorkTaskStatus.Assigned, Description: "Inspect focused verification.",
            AgentId: "tester", AgentName: "Tester", ProviderName: "OpenAI",
            ModelName: AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias,
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription, ProfileName: "codex-spark",
            SubscriptionModelAlias: AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias,
            ProfileExists: true, ProfileIsResolvable: true, ProfileIsEchoOnly: false,
            ProfileIsPatchCapable: true, CanPrepare: true, Detail: "Ready",
            TaskComplexity: TaskComplexity.Simple,
            SubscriptionModelName: AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias,
            EstimatedPromptCharacterCount: characters)).ToArray();
}
