using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each fact owns its in-memory kernel and uses fixed dispatch timestamps.
public sealed class WorkerRoundLedgerDispatchUsageTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-24T00:00:00Z");
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void OpenAIReceiptReportedUsageWinsOverDispatchUsage()
    {
        var receipt = new WorkerContextPackageReceipt("package", [], ProviderUsageValue.Reported(100),
            ProviderUsageValue.Reported(10), ProviderUsageValue.Reported(20));
        var kernel = KernelWith(new TaskDispatchSnapshot("worker", "command", "root", Start,
            WorkerProviderKind: ProviderKind.OpenAICodexCli, ContextPackageReceipt: receipt,
            ProviderUsage: DispatchProviderUsage.From(new ProviderReportedUsage(999, 888, 777), null)));

        var round = Assert.Single(WorkerRoundLedger.FromGoals(kernel.Goals));
        Assert.True(round.UsageReported);
        Assert.Equal(100, round.InputTokens);
        Assert.Equal(10, round.CachedInputTokens);
        Assert.Equal(20, round.OutputTokens);
    }

    [Fact]
    public void ReceiptUnknownUsageWinsOverReportedDispatchUsage()
    {
        var unknown = ProviderUsageValue.Unknown("receipt-unavailable");
        var receipt = new WorkerContextPackageReceipt("package", [], unknown, unknown, unknown);
        var kernel = KernelWith(new TaskDispatchSnapshot("worker", "command", "root", Start,
            ContextPackageReceipt: receipt,
            ProviderUsage: DispatchProviderUsage.From(new ProviderReportedUsage(100, 20, 10), null)));

        var round = Assert.Single(WorkerRoundLedger.FromGoals(kernel.Goals));
        Assert.False(round.UsageReported);
        Assert.Null(round.InputTokens);
        Assert.Null(round.CachedInputTokens);
        Assert.Null(round.OutputTokens);
    }

    [Fact]
    public void LegacyDispatchJsonLoadsWithoutProviderUsage()
    {
        const string json = """
            {"WorkerName":"worker","Command":"command","WorkingDirectory":"root","DispatchedAt":"2026-09-24T00:00:00Z","WorkerProviderKind":"AnthropicClaudeCli"}
            """;
        var snapshot = JsonSerializer.Deserialize<TaskDispatchSnapshot>(json, JsonOptions)!;
        Assert.Null(snapshot.ProviderUsage);
        Assert.DoesNotContain("ProviderUsage", JsonSerializer.Serialize(snapshot, JsonOptions));
        var kernel = KernelWith(snapshot);
        Assert.Null(kernel.Goals.Single().Tasks.Single().LastDispatch!.ProviderUsage);
        Assert.False(Assert.Single(WorkerRoundLedger.FromGoals(kernel.Goals)).UsageReported);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DispatchUsageSurvivesKernelAndJsonSnapshotRoundTrip(bool reported)
    {
        var usage = DispatchProviderUsage.From(reported ? new ProviderReportedUsage(3330, 12, 3000) : null,
            "claude-transcript-missing: fixture");
        var kernel = KernelWith(new TaskDispatchSnapshot("worker", "command", "root", Start));
        var goal = kernel.Goals.Single();
        var task = goal.Tasks.Single();
        kernel.RecordDispatchProviderUsage(goal.Id, task.Id, Start, usage);
        Assert.Equal(usage, task.LastDispatch!.ProviderUsage);
        Assert.Equal(usage, Assert.Single(task.DispatchHistory).ProviderUsage);

        var json = JsonSerializer.Serialize(kernel.ExportSnapshot(), JsonOptions);
        var restored = AgentOrchestratorKernel.FromSnapshot(
            JsonSerializer.Deserialize<OrchestratorSnapshot>(json, JsonOptions)!);
        Assert.Equal(usage, restored.Goals.Single().Tasks.Single().LastDispatch!.ProviderUsage);
        Assert.Equal(usage, restored.Goals.Single().Tasks.Single().DispatchHistory.Single().ProviderUsage);
        var round = Assert.Single(WorkerRoundLedger.FromGoals(restored.Goals));
        Assert.Equal(reported, round.UsageReported);
        Assert.Equal(reported ? 3330L : (long?)null, round.InputTokens);
        Assert.Equal(reported ? 3000L : (long?)null, round.CachedInputTokens);
        Assert.Equal(reported ? 12L : (long?)null, round.OutputTokens);
    }

    [Fact]
    public void RecordingDispatchUsageOverwritesRatherThanAccumulates()
    {
        var kernel = KernelWith(new TaskDispatchSnapshot("worker", "command", "root", Start));
        var goal = kernel.Goals.Single();
        var task = goal.Tasks.Single();
        kernel.RecordDispatchProviderUsage(goal.Id, task.Id, Start,
            DispatchProviderUsage.From(new ProviderReportedUsage(10, 5, 2), null));
        var replacement = DispatchProviderUsage.From(new ProviderReportedUsage(30, 12, 8), null);
        kernel.RecordDispatchProviderUsage(goal.Id, task.Id, Start, replacement);
        Assert.Equal(replacement, task.LastDispatch!.ProviderUsage);
        Assert.Equal(replacement, Assert.Single(task.DispatchHistory).ProviderUsage);
    }

    private static AgentOrchestratorKernel KernelWith(TaskDispatchSnapshot dispatch)
    {
        var task = new TaskSnapshot("task-1", "Work", AgentRole.Developer, WorkTaskStatus.Failed,
            null, null, null, [], null, null, DispatchHistory: [dispatch]);
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot("goal-1", "Work", GoalStatus.Active, [task], [])], []));
    }
}
