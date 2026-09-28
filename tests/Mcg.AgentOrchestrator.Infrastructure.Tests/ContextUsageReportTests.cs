using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ContextUsageReportTests
{
    [Fact]
    public void CountsEveryDispatchButStatisticsUseOnlyReportedValues()
    {
        var goal = ContextUsageTestFixture.CreateGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);

        var report = ContextUsageReport.Build([goal]);
        var dev = Assert.Single(report.Rows, row => row.Role == "Developer");
        Assert.Equal("claude-opus-5-5", dev.Model);
        Assert.Equal(6, dev.DispatchCount);
        Assert.Equal(4, dev.ReportedInputCount);
        Assert.Equal(25000, dev.Input.Median);
        Assert.Equal(40000, dev.Input.P90);
        Assert.Equal(15000, dev.Uncached.Median);
        Assert.Equal(24000, dev.Uncached.P90);
        Assert.Equal(20000, dev.HarnessOverhead.Median);
        Assert.Equal(35000, dev.HarnessOverhead.P90);

        var other = Assert.Single(report.Rows, row => row.Role == "Tester");
        Assert.Equal(3, other.DispatchCount);
        Assert.Equal(3, other.ReportedInputCount);
        Assert.Equal(70000, other.Input.Median);
        Assert.Equal(90000, other.Input.P90);
        Assert.Equal(40000, other.Uncached.Median);
        Assert.Equal(50000, other.Uncached.P90);
        Assert.Equal(30000, other.HarnessOverhead.Median);
        Assert.Equal(40000, other.HarnessOverhead.P90);
    }
}

internal static class ContextUsageTestFixture
{
    internal static readonly DateTimeOffset Start = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    internal static Goal CreateGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Measure context usage", [
            new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Test", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var inputs = new long[] { 10000, 20000, 30000, 40000 };
        for (var i = 0; i < inputs.Length; i++)
            Add(kernel, goal, developer, Start.AddMinutes(i), "claude-opus-5-5",
                Receipt(inputs[i], inputs[i] * 2 / 5, 100, 5000));
        Add(kernel, goal, developer, Start.AddMinutes(4), "claude-opus-5-5",
            new WorkerContextPackageReceipt("unknown", [],
                ProviderUsageValue.Unknown("provider omitted usage"),
                ProviderUsageValue.Unknown("provider omitted usage"),
                ProviderUsageValue.Unknown("provider omitted usage")));
        Add(kernel, goal, developer, Start.AddMinutes(5), "claude-opus-5-5", null);

        Add(kernel, goal, tester, Start.AddMinutes(6), "gpt-5", Receipt(50000, 10000, 100, 30000));
        Add(kernel, goal, tester, Start.AddMinutes(7), "gpt-5", Receipt(70000, 20000, 100, 30000));
        Add(kernel, goal, tester, Start.AddMinutes(8), "gpt-5", Receipt(90000, 60000, 100, 0));
        return goal;
    }

    internal static WorkerContextPackageReceipt Receipt(long input, long cached, long output, int estimate) =>
        new("test", [], ProviderUsageValue.Reported(input),
            ProviderUsageValue.Reported(cached), ProviderUsageValue.Reported(output),
            RenderedPromptBytes: 512, ModelInputTokenEstimate: estimate);

    internal static void Add(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        DateTimeOffset at, string model, WorkerContextPackageReceipt? receipt) =>
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "test command", "C:\\work", at,
                ModelName: model, ContextPackageReceipt: receipt),
            allowPendingRecordedDispatchRefresh: true);
}
