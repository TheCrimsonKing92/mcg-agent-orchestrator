using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private databases, AsyncLocal output capture and injected time.
public sealed class ProviderUsageHoldJsonTests
{
    private const string Key = "xai::<provider-default>";
    private const string Exhausted = "API error (status 402 Payment Required): Grok Build usage balance exhausted";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-17T11:07:00Z");
    private static readonly string[] Fields = ["bindingKey", "provider", "sourceGoalId", "sourceTaskId", "placedAt", "evidence",
        "scheduleAvailable", "attempt", "nextProbeAt", "resetSource", "resetAt", "probeInFlight", "admittedGoalId", "admittedTaskId", "lastProbe", "resetCommand"];

    [Fact]
    public async Task HeldBinding_StatusAndAttentionEmitIdenticalOrderedRows()
    {
        using var fixture = new Fixture();
        var source = fixture.Exhaust();
        await fixture.Save();
        using var status = JsonDocument.Parse(fixture.Status(json: true));
        var row = Assert.Single(status.RootElement.GetProperty("payload").GetProperty("providerHolds").EnumerateArray());
        Assert.Equal(Fields, row.EnumerateObject().Select(property => property.Name));
        Assert.Equal(Key, row.GetProperty("bindingKey").GetString());
        Assert.Equal("xai", row.GetProperty("provider").GetString()!.ToLowerInvariant());
        Assert.Equal(source.Id.Value, row.GetProperty("sourceGoalId").GetString());
        Assert.Equal(source.Tasks.Single().Id.Value, row.GetProperty("sourceTaskId").GetString());
        var projected = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.Goals, Start.AddMinutes(2)));
        Assert.Equal(ProviderBudgetHoldSchedules.FormatInstant(projected.Hold.PlacedAt), row.GetProperty("placedAt").GetString());
        Assert.Equal(projected.Hold.EvidenceReceipt, row.GetProperty("evidence").GetString());
        Assert.True(row.GetProperty("scheduleAvailable").GetBoolean());
        Assert.Equal(ProviderBudgetHoldSchedules.FormatInstant(projected.Schedule!.NextProbeAt), row.GetProperty("nextProbeAt").GetString());
        Assert.Equal(projected.Schedule.Attempt, row.GetProperty("attempt").GetInt32());
        Assert.Equal(projected.Schedule.ResetSource, row.GetProperty("resetSource").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lastProbe").ValueKind);
        Assert.Equal($"provider-reset {Key} <ISO-8601 instant> --text-file <path>", row.GetProperty("resetCommand").GetString());
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath);
        var output = InfrastructureTestSupport.CaptureConsole(() => CliAttentionJsonQueryCommand.Execute(
            ["attention", "--json"], repository, fixture.Workspace, new FixedTime()));
        using var attention = JsonDocument.Parse(output);
        var attentionRow = Assert.Single(attention.RootElement.GetProperty("payload").GetProperty("providerHolds").EnumerateArray());
        Assert.Equal(row.GetRawText(), attentionRow.GetRawText());
    }

    [Fact]
    public async Task NoHold_BothPayloadsAlwaysContainEmptyArrays()
    {
        using var fixture = new Fixture();
        fixture.NewGoal();
        await fixture.Save();
        using var status = JsonDocument.Parse(fixture.Status(json: true));
        Assert.Empty(status.RootElement.GetProperty("payload").GetProperty("providerHolds").EnumerateArray());
        using var attention = JsonDocument.Parse(AttentionAgentReadView.Render([], [], new FixedTime()));
        Assert.Empty(attention.RootElement.GetProperty("payload").GetProperty("providerHolds").EnumerateArray());
    }

    [Fact]
    public async Task CompletedSource_WithExhaustionHistory_RemainsVisibleInStatusAndFleet()
    {
        using var fixture = new Fixture();
        var source = fixture.Exhaust();
        fixture.Kernel.ReplaceGoalWithSnapshot(fixture.Kernel.ExportGoalSnapshot(source.Id) with { Status = GoalStatus.Completed });
        await fixture.Save();
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath);
        Assert.Contains(await repository.ListModelFitHistoryAsync(), row => row.GoalId == source.Id.Value && row.OutcomeRule == "provider-budget-exhausted");
        Assert.Equal(nameof(GoalStatus.Completed), Assert.Single(await repository.ListGoalMetadataAsync()).Status);
        Assert.Contains("Provider usage holds: 1", fixture.Status());
        using var status = JsonDocument.Parse(fixture.Status(json: true));
        Assert.Equal(source.Id.Value, Assert.Single(status.RootElement.GetProperty("payload").GetProperty("providerHolds").EnumerateArray()).GetProperty("sourceGoalId").GetString());
        Assert.Equal(source.Id, Assert.Single(ProviderUsageHoldReader.ReadWorkspace(fixture.Workspace, Start.AddMinutes(2))).Hold.SourceGoalId);
    }

    [Fact]
    public void NoHistoryCandidates_NeverLoadsGoalsAndMatchesPlainView()
    {
        using var fixture = new Fixture();
        fixture.Exhaust();
        var repository = DispatchProxy.Create<IOrchestratorStateRepository, QueryProbe>();
        var spy = (QueryProbe)repository;
        var expected = ProviderUsageHoldView.Read(fixture.Kernel.Goals, Start.AddMinutes(2));
        Assert.Single(expected);
        Assert.Equal(expected.ToArray(), ProviderUsageHoldReader.Read(repository, fixture.Kernel.Goals, Start.AddMinutes(2)).ToArray());
        Assert.Equal(1, spy.HistoryCalls);
        Assert.Equal(0, spy.LoadCalls);
    }

    [Fact]
    public void HistoryReadFailure_FallsBackToLoadedGoals()
    {
        using var fixture = new Fixture();
        fixture.Exhaust();
        var repository = DispatchProxy.Create<IOrchestratorStateRepository, QueryProbe>();
        var spy = (QueryProbe)repository;
        spy.FailHistory = true;
        Assert.Single(ProviderUsageHoldReader.Read(repository, fixture.Kernel.Goals, Start.AddMinutes(2)));
        Assert.Equal(1, spy.HistoryCalls);
        Assert.Equal(0, spy.LoadCalls);
    }

    [Fact]
    public void NullSchedule_EmitsNullForEveryScheduleField()
    {
        using var fixture = new Fixture();
        fixture.Exhaust();
        var held = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.Goals, Start));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ProviderUsageHoldJson.Row(held with { Schedule = null })));
        var row = json.RootElement;
        Assert.Equal(Fields, row.EnumerateObject().Select(property => property.Name));
        Assert.False(row.GetProperty("scheduleAvailable").GetBoolean());
        foreach (var field in new[] { "attempt", "nextProbeAt", "resetSource", "resetAt", "probeInFlight", "admittedGoalId", "admittedTaskId", "lastProbe" })
            Assert.Equal(JsonValueKind.Null, row.GetProperty(field).ValueKind);
    }

    public class QueryProbe : DispatchProxy
    {
        internal int HistoryCalls;
        internal int LoadCalls;
        internal bool FailHistory;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IOrchestratorStateRepository.ListModelFitHistoryAsync):
                    HistoryCalls++;
                    return FailHistory ? Task.FromException<IReadOnlyList<ModelFitHistoryRow>>(new IOException("fixture history failure"))
                        : Task.FromResult<IReadOnlyList<ModelFitHistoryRow>>([]);
                case nameof(IOrchestratorStateQueries.LoadGoalsAsync):
                    LoadCalls++;
                    return Task.FromResult(new AgentOrchestratorKernel());
                default: throw new InvalidOperationException($"Unexpected repository query: {method.Name}");
            }
        }
    }

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
                ProviderName: "xAI", ModelName: _agent.Model.ModelName, AssignedAgentId: _agent.Id.Value));
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
