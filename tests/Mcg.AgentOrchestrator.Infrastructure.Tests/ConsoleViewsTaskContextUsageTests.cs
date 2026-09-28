using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConsoleViewsTaskContextUsageTests
{
    [Fact]
    public void TaskViewUsesTheLastReceiptBearingDispatchAndShowsUnknownReasons()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Print worker context", [
            new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = Assert.Single(goal.Tasks);
        var first = ContextUsageTestFixture.Start;
        ContextUsageTestFixture.Add(kernel, goal, task, first, "gpt-5",
            ContextUsageTestFixture.Receipt(90000, 60000, 2000, 30000));

        var reported = AsyncLocalConsoleRouter.Capture(() => ConsoleViews.PrintTask(goal, task));
        Assert.Contains("Context usage: input=90000, cached=60000, uncached=30000, output=2000, prompt-bytes=512, estimate=30000, harness-overhead=60000", reported);

        ContextUsageTestFixture.Add(kernel, goal, task, first.AddMinutes(1), "gpt-5",
            new WorkerContextPackageReceipt("unknown", [],
                ProviderUsageValue.Unknown("provider omitted usage"),
                ProviderUsageValue.Reported(100),
                ProviderUsageValue.Unknown("output unavailable"),
                RenderedPromptBytes: 0, ModelInputTokenEstimate: 30000));
        var unknown = AsyncLocalConsoleRouter.Capture(() => ConsoleViews.PrintTask(goal, task));
        Assert.Contains("input=unknown(provider omitted usage)", unknown);
        Assert.Contains("uncached=unknown(provider omitted usage)", unknown);
        Assert.Contains("output=unknown(output unavailable)", unknown);
        Assert.Contains("prompt-bytes=unknown(not recorded)", unknown);
        Assert.Contains("harness-overhead=unknown(provider omitted usage)", unknown);
        Assert.DoesNotContain("uncached=30000", unknown);

        ContextUsageTestFixture.Add(kernel, goal, task, first.AddMinutes(2), "gpt-5", null);
        var afterReceiptlessDispatch = AsyncLocalConsoleRouter.Capture(() => ConsoleViews.PrintTask(goal, task));
        Assert.Contains("input=unknown(provider omitted usage)", afterReceiptlessDispatch);
    }

    [Fact]
    public void TaskViewOmitsLineWhenNoDispatchHasAReceipt()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("No receipt", [
            new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = Assert.Single(goal.Tasks);
        ContextUsageTestFixture.Add(kernel, goal, task, ContextUsageTestFixture.Start, "gpt-5", null);

        Assert.DoesNotContain("Context usage:", AsyncLocalConsoleRouter.Capture(() => ConsoleViews.PrintTask(goal, task)));
    }
}
