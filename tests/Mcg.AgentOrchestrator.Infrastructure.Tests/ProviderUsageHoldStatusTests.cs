using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private workspace/database, local writers, and an injected clock.
public sealed class ProviderUsageHoldStatusTests
{
    private const string Key = "xai::<provider-default>";
    private const string Exhausted = "API error (status 402 Payment Required): Grok Build usage balance exhausted";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-17T11:07:00Z");

    [Fact]
    public async Task FailedSource_StatusShowsHoldAfterIntentsAndTracksOperatorReset()
    {
        using var fixture = new Fixture();
        var source = fixture.Exhaust();
        Assert.Equal(GoalStatus.Failed, source.Status);
        await fixture.Save();
        var intents = SqliteOperatorIntentStore.ForDirectories(fixture.Workspace.OrchestratorDirectory, fixture.Workspace.LogDirectory);
        await intents.EnqueueAsync(new OperatorIntentRecord("pending", "pending", "retry", source.Id.Value,
            source.Tasks.Single().Id.Value, "{}", [], "operator", "cli", "local", Start.AddMinutes(2)));
        var lines = fixture.Status().Split(Environment.NewLine);
        var index = Array.IndexOf(lines, "Provider usage holds: 1");
        Assert.True(index > 0);
        Assert.Contains(" | intents=1 |", lines[index - 1]);
        Assert.Equal($"  {Key} | attempt=1 | next_probe=2026-10-17T11:38:00.0000000+00:00 | reset_source=backoff | last_probe=none | source={source.Id.Value[..8]}/{source.Tasks.Single().Id.Value[..8]}", lines[index + 1]);
        Assert.Equal("Owner directives: 0 active", lines[index + 2]);
        Assert.Contains("Non-terminal goals: 0", lines);

        fixture.Reset(source);
        await fixture.Save();
        Assert.Contains("next_probe=2026-10-21T02:03:00.0000000+00:00 | reset_source=operator", fixture.Status());
    }

    [Fact]
    public async Task NoHold_OmitsHumanBlockAndKeepsJsonPropertyNamesIdentical()
    {
        using var held = new Fixture();
        using var clear = new Fixture();
        held.Exhaust();
        clear.NewGoal();
        await held.Save();
        await clear.Save();
        Assert.DoesNotContain(clear.Status().Split(Environment.NewLine), line => line.StartsWith("Provider usage holds", StringComparison.Ordinal));
        Assert.Contains("Provider usage holds: 1", held.Status());
        using var heldJson = JsonDocument.Parse(held.Status(json: true));
        using var clearJson = JsonDocument.Parse(clear.Status(json: true));
        Assert.Equal(PropertyPaths(clearJson.RootElement), PropertyPaths(heldJson.RootElement));
    }

    [Fact]
    public void MissingDatabase_OmitsHoldBlock()
    {
        using var fixture = new Fixture();
        Assert.False(File.Exists(fixture.Workspace.SqliteStatePath));
        Assert.DoesNotContain("Provider usage holds", fixture.Status());
    }

    [Fact]
    public void AssignedHeldTask_AttentionShowsScheduleResetCommandAndExistingBlockers()
    {
        using var fixture = new Fixture();
        var source = fixture.Exhaust();
        var target = fixture.NewGoal();
        var inbox = fixture.Inbox(target);
        var item = Assert.Single(inbox.Items.Where(item => item.Kind == OperatorInboxKind.BudgetWarning && item.Title == $"Provider usage hold: {Key}"));
        Assert.Equal(OperatorInboxSeverity.Warning, item.Severity);
        Assert.Null(item.TaskId);
        Assert.Null(item.TaskNumber);
        Assert.Equal($"Binding {Key} is held for usage-balance exhaustion; next probe 2026-10-17T11:38:00.0000000+00:00 (reset_source=backoff; attempt=1; last_probe=none).", item.Message);
        Assert.Equal($"provider-reset {Key} <ISO-8601 instant> --text-file <path>", item.SuggestedCommand);
        Assert.Equal("The conductor probes the binding automatically at next probe; if the provider states a reset time, record it with provider-reset.", item.SuggestedAction);
        var hold = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.Goals, Start)).Hold;
        Assert.Equal($"source goal {source.Id.Value[..8]} task {source.Tasks.Single().Id.Value[..8]} receipt {hold.EvidenceReceipt}", item.Evidence);
        Assert.Equal(hold.PlacedAt, item.RaisedAt);
        Assert.Equal($"budget:usage-hold:{Key}:{Start.AddMinutes(31).ToUnixTimeSeconds()}", item.Source);
        Assert.Single(inbox.Items.Where(item => item.Kind == OperatorInboxKind.SubscriptionRouteWarning));
        Assert.Single(inbox.Items.Where(item => item.Kind == OperatorInboxKind.ReadinessPreflight &&
            item.Severity == OperatorInboxSeverity.Blocker && item.Message.Contains(Key, StringComparison.OrdinalIgnoreCase)));

        fixture.Reset(source);
        var updated = Assert.Single(fixture.Inbox(target).Items.Where(candidate => candidate.Title == item.Title));
        Assert.Contains("next probe 2026-10-21T02:03:00.0000000+00:00 (reset_source=operator", updated.Message);
        Assert.NotEqual(item.Id, updated.Id);
        Assert.Equal($"budget:usage-hold:{Key}:{DateTimeOffset.Parse("2026-10-21T02:03:00Z").ToUnixTimeSeconds()}", updated.Source);
        // The Failed source has no Assigned task, so it does not raise this item itself.
        Assert.DoesNotContain(fixture.Inbox(source).Items, candidate => candidate.Title.StartsWith("Provider usage hold:", StringComparison.Ordinal));
        using var clear = new Fixture();
        Assert.DoesNotContain(clear.Inbox(clear.NewGoal()).Items, candidate => candidate.Title.StartsWith("Provider usage hold:", StringComparison.Ordinal));
    }

    [Fact]
    public void MultipleAssignedTasksOnOneBinding_RaiseOneHoldItem()
    {
        using var fixture = new Fixture();
        fixture.Exhaust();
        var target = fixture.NewGoal(2);
        Assert.Single(fixture.Inbox(target).Items.Where(item => item.Title == $"Provider usage hold: {Key}"));
    }

    private static string[] PropertyPaths(JsonElement element, string prefix = "") => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property =>
            new[] { prefix + property.Name }.Concat(PropertyPaths(property.Value, prefix + property.Name + ".")))
            .Distinct().Order(StringComparer.Ordinal).ToArray(),
        // Array membership differs between fixtures; its element values do not change the JSON contract.
        _ => []
    };

    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = Start; }
    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Start.AddMinutes(2); }
    private sealed class Fixture : IDisposable
    {
        private readonly ConductorVerbStartTests.Fixture _workspace = new();
        internal OrchestratorWorkspace Workspace => _workspace.Workspace;
        internal readonly Clock Clock = new();
        internal readonly AgentOrchestratorKernel Kernel;
        private readonly AgentDefinition _agent = ProviderHoldAgentSubstitutionTests.Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.7");
        internal Fixture() => Kernel = new(Clock);
        internal Goal NewGoal(int count = 1)
        {
            var goal = Kernel.CreateGoal("Hold visibility fixture", Enumerable.Range(0, count)
                .Select(index => new TaskSpec(TaskId.New(), $"Verify fixture {index}", AgentRole.Tester)).ToArray());
            Kernel.ActivateGoal(goal.Id, [_agent]);
            return goal;
        }
        internal Goal Exhaust()
        {
            var goal = NewGoal();
            var task = goal.Tasks.Single();
            Kernel.RecordTaskDispatch(goal.Id, task.Id, new("grok-cli", "fixture", Workspace.ExecutionDirectory, Start,
                ProviderName: "xAI", AssignedAgentId: _agent.Id.Value));
            Clock.UtcNow = Start.AddMinutes(1);
            var kind = WorkerProviderCatalog.Default().ResolveProfile("grok-cli").ParseOutcome(new(1, "", Exhausted));
            Kernel.RecordTaskVerification(goal.Id, task.Id, new("fixture", Workspace.ExecutionDirectory, 1, "", Exhausted,
                Clock.UtcNow, ProviderFailureKind: kind, DispatchStartedAt: Start));
            Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Fixture provider failure.");
            Kernel.EscalateTaskFailure(goal.Id, task.Id, "Fixture provider failure.");
            return goal;
        }
        internal void Reset(Goal goal)
        {
            Clock.UtcNow = Start.AddMinutes(3);
            Kernel.RecordTaskNote(goal.Id, goal.Tasks.Single().Id, ProviderBudgetHoldSchedules.FormatReset(
                new(Key, DateTimeOffset.Parse("2026-10-21T02:03:00Z"), "operator-reset")));
        }
        internal Task Save() => InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath)
            .SaveAsync(Kernel, Xunit.TestContext.Current.CancellationToken);
        internal OperatorInboxReport Inbox(Goal goal) => OperatorInbox.Build(Kernel, [_agent], WorkerProfileCatalog.Default(), Workspace, goal.Id.Value[..8]);
        internal string Status(bool json = false)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, CliConductorCommand.Run(json ? ["conductor", "status", "--json"] : ["conductor", "status"], Workspace,
                lockProbe: new ConductorVerbStartTests.FixedProbe(null), bootTime: () => Start.AddDays(-1),
                mainCommit: () => "fixture", output: output, error: error, time: new FixedTime()));
            Assert.Equal("", error.ToString());
            return output.ToString();
        }
        public void Dispose() => _workspace.Dispose();
    }
}
