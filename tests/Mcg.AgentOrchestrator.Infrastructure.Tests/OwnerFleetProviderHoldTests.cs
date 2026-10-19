using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Text;

// Parallel-safe: pure reports and fixture-owned database paths.
public sealed class OwnerFleetProviderHoldTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-17T11:08:00Z");

    [Fact]
    public void HeldBinding_RendersSixRowsInsideDoubleEdgesAfterHost()
    {
        var row = HoldRow();
        var model = OwnerFleetViewModelBuilder.Build(Report(), true, TimeZoneInfo.Utc, providerHolds: [row]);
        var text = OwnerFleetDiagram.Render(model, 60);
        var lines = text.Split('\n');
        var top = Array.FindIndex(lines, line => line.StartsWith('╔'));
        Assert.True(top > Array.FindIndex(lines, line => line.StartsWith('└')));
        Assert.StartsWith("│", lines[top - 1]);
        var expected = new[]
        {
            "Provider xai: HELD !", "binding: xai::<provider-default>",
            "next probe: 2026-10-17 11:38 +00:00 attempt 1 (backoff)",
            "last probe: none", "held since: 2026-10-17 11:08 +00:00",
            "source: 11111111/22222222"
        };
        Assert.Equal(expected, lines.Skip(top + 1).Take(6).Select(line => line[2..^2].TrimEnd()));
        Assert.All(lines.Skip(top + 1).Take(6), line => { Assert.StartsWith("║", line); Assert.EndsWith("║", line); });
        Assert.StartsWith("╚", lines[top + 7]);
        Assert.True(text.IndexOf("Provider xai", StringComparison.Ordinal) < text.IndexOf("Executor alpha", StringComparison.Ordinal));
    }

    [Fact]
    public void MinimumWidth_LongBindingAndUnavailableSchedule_FitEveryLine()
    {
        var row = HoldRow();
        row = row with { Hold = row.Hold with { BindingKey = @"claude::c:\users\a very long fixture directory\.claude-2" }, Schedule = null };
        var zone = TimeZoneInfo.CreateCustomTimeZone("fixture", TimeSpan.FromHours(-5), "fixture", "fixture");
        var model = OwnerFleetViewModelBuilder.Build(Report(), false, zone, providerHolds: [row]);
        var node = Assert.Single(model.ProviderHolds!);
        Assert.Equal("claude (.claude-2)", node.Name);
        Assert.Equal(TimeSpan.FromHours(-5), node.PlacedAt.Offset);
        var text = OwnerFleetDiagram.Render(model, OwnerFleetDiagram.MinimumWidth);
        Assert.All(text.Split('\n'), line => Assert.True(line.GetColumns() <= OwnerFleetDiagram.MinimumWidth, line));
        Assert.Contains("next probe: unavailable", text);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(100)]
    public void EmptyHoldList_IsByteIdenticalToOmittedArgument(int width)
    {
        foreach (var report in new[] { Report(), RemoteExecutorReport.Build([], [], []),
                     RemoteExecutorReport.Build(["unprobed"], [], [], configuredSlots: new Dictionary<string, int> { ["unprobed"] = 1 }),
                     new RemoteExecutorReport([Report().Executors[0] with { ConfiguredSlots = null }], 2, 3) })
        {
            var baseline = OwnerFleetDiagram.Render(OwnerFleetViewModelBuilder.Build(report, true, TimeZoneInfo.Utc), width);
            var empty = OwnerFleetDiagram.Render(OwnerFleetViewModelBuilder.Build(report, true, TimeZoneInfo.Utc, providerHolds: []), width);
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(baseline), System.Text.Encoding.UTF8.GetBytes(empty));
        }
    }

    [Fact]
    public async Task WorkspaceReader_MissingDatabaseIsEmptyAndSavedExhaustionIsVisible()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        Assert.False(File.Exists(workspace.SqliteStatePath));
        Assert.Empty(ProviderUsageHoldReader.ReadWorkspace(workspace, At));
        Assert.False(File.Exists(workspace.SqliteStatePath));
        var kernel = new AgentOrchestratorKernel(new Clock());
        var agent = ProviderHoldAgentSubstitutionTests.Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.7");
        var goal = kernel.CreateGoal("Fleet hold fixture", [new TaskSpec(TaskId.New(), "Verify fixture", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var start = At.AddMinutes(-1);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new("grok-cli", "fixture", workspace.ExecutionDirectory, start,
            ProviderName: "xAI", AssignedAgentId: agent.Id.Value));
        const string exhausted = "API error (status 402 Payment Required): Grok Build usage balance exhausted";
        var kind = WorkerProviderCatalog.Default().ResolveProfile("grok-cli").ParseOutcome(new(1, "", exhausted));
        kernel.RecordTaskVerification(goal.Id, task.Id, new("fixture", workspace.ExecutionDirectory, 1, "", exhausted,
            At, ProviderFailureKind: kind, DispatchStartedAt: start));
        Assert.Equal(ProviderFailureKind.BudgetExhausted, task.LastVerification!.ProviderFailureKind);
        await InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        var before = File.ReadAllBytes(workspace.SqliteStatePath);
        var row = Assert.Single(ProviderUsageHoldReader.ReadWorkspace(workspace, At));
        Assert.Equal(goal.Id, row.Hold.SourceGoalId);
        Assert.Equal("xai::<provider-default>", row.Hold.BindingKey);
        Assert.Equal(before, File.ReadAllBytes(workspace.SqliteStatePath));
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => At; }

    private static ProviderUsageHoldRow HoldRow()
    {
        var hold = new ProviderBudgetExhaustionHold("xai", "xai::<provider-default>", "provider-default",
            new GoalId("11111111000000000000000000000000"), new TaskId("22222222000000000000000000000000"), At, "fixture exhaustion");
        return new(hold, new(hold, 1, null, "backoff", At.AddMinutes(30), false, null, null, "none"));
    }

    private static RemoteExecutorReport Report()
    {
        var at = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        return RemoteExecutorReport.Build(["alpha", "beta"],
        [
            new(at.AddSeconds(20), "alpha", "a", "lane", "accepted", null, 10, at),
            new(at.AddSeconds(30), "alpha", "b", "lane", "accepted", null, 20, at.AddSeconds(10)),
            new(at.AddSeconds(90), "alpha", "c", "lane", "accepted", null, 30, at.AddSeconds(60))
        ],
        [
            new(at, "alpha", true, 0, false, "Ready", true, [], ""),
            new(at, "beta", false, 1, true, null, null, ["ssh-timeout"], "")
        ], configuredSlots: new Dictionary<string, int> { ["alpha"] = 2, ["beta"] = 1 });
    }
}
