using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalIsolationIntentTests
{
    [Fact]
    public async Task SubmissionQueuesAndTickAppliesSoloThenNoneWithoutChangingPriority()
    {
        using var fixture = new Fixture();
        fixture.Kernel.SetGoalPriority(fixture.Goal.Id, GoalPriority.High, "test", "setup");
        fixture.Submit("solo", "--reason", "Risky");
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        Assert.Equal(OperatorIntentVerbs.GoalIsolation, intent.Verb);
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        var payload = JsonSerializer.Deserialize<GoalIsolationOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Equal("solo", payload.Mode);
        Assert.Equal("Risky", payload.Rationale);
        Assert.Equal(GoalIsolation.None, fixture.Goal.Isolation);
        var coordinator = new OperatorIntentCoordinator(fixture.Store);
        coordinator.ExecutePending(fixture.Kernel, fixture.Goal);
        Assert.Equal(GoalIsolation.Solo, fixture.Goal.Isolation);
        Assert.Equal(GoalPriority.High, fixture.Goal.Priority);
        var audit = Assert.Single(fixture.Goal.Timeline.Where(item => item.Message.StartsWith("Goal isolation set:")));
        Assert.StartsWith("Goal isolation set: previous=None new=Solo", audit.Message);
        Assert.Contains("reason=Risky", audit.Message);
        Assert.Contains("actor=" + intent.Actor, audit.Message);
        coordinator.CompletePersisted([fixture.Goal.Id]);
        fixture.Submit("none");
        coordinator.ExecutePending(fixture.Kernel, fixture.Goal);
        Assert.Equal(GoalIsolation.None, fixture.Goal.Isolation);
        Assert.Equal(GoalPriority.High, fixture.Goal.Priority);
    }

    [Fact]
    public async Task InvalidSubmissionWritesNoIntent()
    {
        using var fixture = new Fixture();
        var error = Assert.Throws<ArgumentException>(() => fixture.Submit("sometimes"));
        Assert.Contains("solo, none", error.Message);
        Assert.Empty(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
    }

    [Fact]
    public async Task InvalidPersistedPayloadIsRejectedWithoutMutation()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid().ToString("N");
        await fixture.Store.EnqueueAsync(new(id, id, OperatorIntentVerbs.GoalIsolation,
            fixture.Goal.Id.Value, null, "{\"mode\":\"sometimes\",\"rationale\":\"bad\"}", [], "test", "cli", "local", DateTimeOffset.UtcNow));
        new OperatorIntentCoordinator(fixture.Store).ExecutePending(fixture.Kernel, fixture.Goal);
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Store.GetAsync(id))!.Status);
        Assert.Equal(GoalIsolation.None, fixture.Goal.Isolation);
        Assert.Equal(GoalPriority.Normal, fixture.Goal.Priority);
        Assert.DoesNotContain(fixture.Goal.Timeline, item => item.Message.StartsWith("Goal isolation set:"));
    }

    [Fact]
    public async Task SoloRoundTripsThroughSnapshotAndTickMergeWhileNoneOmitsField()
    {
        using var fixture = new Fixture();
        var repository = new SqliteOrchestratorStateRepository(fixture.Context.Workspace.SqliteStatePath);
        var normal = fixture.Kernel.ExportSnapshot().Goals.Single();
        Assert.Null(normal.Isolation);
        Assert.DoesNotContain("\"Isolation\"", JsonSerializer.Serialize(normal));
        await repository.SaveAsync(fixture.Kernel);
        var baseline = fixture.Kernel.ExportSnapshot();
        fixture.Submit(" SOLO ");
        new OperatorIntentCoordinator(fixture.Store).ExecutePending(fixture.Kernel, fixture.Goal);
        var snapshot = fixture.Kernel.ExportSnapshot();
        Assert.Equal("solo", snapshot.Goals.Single().Isolation);
        Assert.Equal(GoalIsolation.Solo, AgentOrchestratorKernel.FromSnapshot(snapshot).GetGoal(fixture.Goal.Id).Isolation);
        var concurrent = await repository.LoadAsync();
        concurrent.SetGoalSourceBacklogItemId(fixture.Goal.Id, "concurrent");
        await repository.SaveAsync(concurrent);
        await repository.SaveGoalSnapshotsWithMergeAsync([new(baseline.Goals.Single(), snapshot.Goals.Single(), snapshot.HumanInputRequests)]);
        var restored = (await repository.LoadAsync()).GetGoal(fixture.Goal.Id);
        Assert.Equal(GoalIsolation.Solo, restored.Isolation);
        Assert.Equal("concurrent", restored.SourceBacklogItemId);
        Assert.Equal(GoalIsolation.None, AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = [snapshot.Goals.Single() with { Isolation = "unknown" }] }).GetGoal(fixture.Goal.Id).Isolation);
    }

    [Fact]
    public async Task PriorityRejectsIsolationFlagAndIsolationHasCatalogAndUsage()
    {
        using var fixture = new Fixture();
        var error = Assert.Throws<ArgumentException>(() => CliGoalPriorityCommand.Execute(
            ["goal-priority", fixture.Goal.Id.Value[..8], "high", "--isolation", "solo"], fixture.Context, fixture.Store));
        Assert.Contains("--isolation", error.Message);
        Assert.Empty(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        Assert.Equal(GoalIsolation.None, fixture.Goal.Isolation);
        Assert.Contains("goal-isolation", CliArgumentParser.RecognizedCommands);
        var help = InfrastructureTestSupport.CaptureConsole(() => Assert.True(CliCommandHelp.TryPrintStartupHelp(["help", "goal-isolation"])));
        Assert.Contains(CliGoalIsolationCommand.Usage, help.Split(Environment.NewLine));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly AgentOrchestratorKernel Kernel = new();
        internal readonly Goal Goal;
        internal readonly CliExecutionContext Context;
        internal readonly SqliteOperatorIntentStore Store;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "goal-isolation-" + Guid.NewGuid().ToString("N"));
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Goal = Kernel.CreateGoal("Isolation intent");
            var workspace = OrchestratorWorkspace.ForDirectory(_root);
            Context = new(Kernel, workspace, new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), Goal);
            Store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        }
        internal void Submit(string mode, params string[] flags) => InfrastructureTestSupport.CaptureConsole(() =>
            CliGoalIsolationCommand.Execute(["goal-isolation", Goal.Id.Value[..8], mode, .. flags], Context, Store));
        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
    }
}
