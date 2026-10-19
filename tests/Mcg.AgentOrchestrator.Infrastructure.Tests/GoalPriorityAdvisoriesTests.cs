using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalPriorityAdvisoriesTests
{
    [Theory]
    [InlineData(2, false, 1, "high=3 active=6")]
    [InlineData(1, false, 0, "")]
    [InlineData(2, true, 1, "high=3 active=5")]
    public async Task DilutionUsesOwnPriorityAndNonTerminalMembers(int alreadyHigh, bool terminal, int warnings, string count)
    {
        using var fixture = new Fixture();
        var goals = Enumerable.Range(0, 6).Select(index => fixture.Kernel.CreateGoal("Member " + index)).ToArray();
        var portfolio = new PortfolioStore(fixture.Context.Workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Epic");
        foreach (var goal in goals) await portfolio.AssignGoalToEpicAsync(goal.Id.Value, epic.Id);
        foreach (var goal in goals.Skip(1).Take(alreadyHigh)) fixture.Kernel.SetGoalPriority(goal.Id, GoalPriority.High, "test", "setup");
        if (terminal) fixture.Kernel.CancelGoal(goals[^1].Id, "done");
        var stderr = fixture.Submit(goals[0], "high");
        var events = fixture.Events("PRIORITY_DILUTION");
        Assert.Equal(warnings, events.Length);
        if (warnings > 0)
        {
            Assert.Contains(count, Assert.Single(events));
            Assert.Contains($"goal={goals[0].Id.Value[..8]} epic={epic.Id[..8]}", events[0]);
            Assert.Contains("threshold=1/3", events[0]);
            Assert.Contains(events[0], stderr);
        }
        Assert.Single(await fixture.Store.ListForGoalAsync(goals[0].Id.Value));
        Assert.Equal(GoalPriority.Normal, goals[0].Priority);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("low")]
    public async Task LowerLevelsNeverRunAdvisories(string level)
    {
        using var fixture = new Fixture();
        var goal = fixture.Kernel.CreateGoal("Change src/Mcg.AgentOrchestrator.App/Orchestration/OverlapProbe.cs");
        fixture.Kernel.CreateGoal(goal.Objective);
        fixture.Submit(goal, level, () => throw new InvalidOperationException("must not open"));
        Assert.Empty(fixture.Events("PRIORITY_"));
        Assert.Single(await fixture.Store.ListForGoalAsync(goal.Id.Value));
    }

    [Fact]
    public async Task AbsentEpicIsSilentAndPortfolioFailureKeepsJsonAndIntent()
    {
        using var fixture = new Fixture();
        var goal = fixture.Kernel.CreateGoal("No epic");
        fixture.Submit(goal, "high");
        Assert.Empty(fixture.Events("PRIORITY_"));
        string stdout = "";
        var stderr = InfrastructureTestSupport.CaptureConsoleError(() => stdout = InfrastructureTestSupport.CaptureConsole(() =>
            CliGoalPriorityCommand.Execute(["goal-priority", goal.Id.Value[..8], "critical", "--json"], fixture.Context, fixture.Store,
                () => throw new IOException("portfolio probe failed"))));
        Assert.Contains("Priority dilution advisory unavailable: portfolio probe failed", stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal("mcg.operator-intent", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(2, (await fixture.Store.ListForGoalAsync(goal.Id.Value)).Count);
    }

    [Theory]
    [InlineData(0, "src/Mcg.AgentOrchestrator.App/Orchestration/OverlapProbe.cs", true)]
    [InlineData(1, "src/Mcg.AgentOrchestrator.App/Orchestration/OverlapProbe.cs", false)]
    [InlineData(2, "src/Mcg.AgentOrchestrator.App/Orchestration/OverlapProbe.cs", false)]
    [InlineData(0, "tests/Mcg.AgentOrchestrator.Core.Tests/", false)]
    public async Task OverlapWarnsOnceOnlyWithoutDeclaredEdges(int edge, string path, bool warn)
    {
        using var fixture = new Fixture();
        var x = fixture.Kernel.CreateGoal("Change " + path + " only.");
        var y = fixture.Kernel.CreateGoal("Change " + (path.EndsWith('/') ? path + "OverlapProbe.cs" : path) + " only.");
        var collision = Assert.Single(GoalScopeCollisionAdvisor.Build([x.Objective], [y]).Collisions);
        Assert.Equal(path.EndsWith('/') ? ScopeCollisionKind.DirectoryPrefix : ScopeCollisionKind.ExactFile, collision.Kind);
        if (edge == 1) fixture.Kernel.SetGoalDependency(x.Id, y.Id);
        if (edge == 2) fixture.Kernel.SetGoalDependency(y.Id, x.Id);
        var xEdges = x.DependsOn.ToArray();
        var yEdges = y.DependsOn.ToArray();
        fixture.Submit(x, "high", key: "same-intent");
        fixture.Submit(x, "high", key: "same-intent");
        var lines = fixture.Events("PRIORITY_OVERLAP");
        if (warn)
        {
            var line = Assert.Single(lines);
            Assert.StartsWith($"PRIORITY_OVERLAP goal={x.Id.Value[..8]} other={y.Id.Value[..8]} kind=ExactFile path={path}", line);
        }
        else Assert.Empty(lines);
        Assert.Equal(xEdges, x.DependsOn);
        Assert.Equal(yEdges, y.DependsOn);
        Assert.Single(await fixture.Store.ListForGoalAsync(x.Id.Value));
    }

    [Fact]
    public async Task LogFailureKeepsEveryOverlapWarningAndTheSubmittedIntent()
    {
        using var fixture = new Fixture();
        const string path = "src/Mcg.AgentOrchestrator.App/Orchestration/OverlapProbe.cs";
        var goal = fixture.Kernel.CreateGoal("Change " + path + " only.");
        var others = new[] { fixture.Kernel.CreateGoal(goal.Objective), fixture.Kernel.CreateGoal(goal.Objective) };
        Directory.CreateDirectory(fixture.Context.Workspace.ConductEventsLogPath);
        var stderr = fixture.Submit(goal, "high");
        foreach (var other in others)
            Assert.Contains($"PRIORITY_OVERLAP goal={goal.Id.Value[..8]} other={other.Id.Value[..8]} kind=ExactFile path={path}", stderr);
        Assert.Contains("Priority log advisory unavailable:", stderr);
        Assert.Single(await fixture.Store.ListForGoalAsync(goal.Id.Value));
        Assert.Empty(goal.DependsOn);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly AgentOrchestratorKernel Kernel = new();
        internal readonly CliExecutionContext Context;
        internal readonly SqliteOperatorIntentStore Store;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "priority-advisories-" + Guid.NewGuid().ToString("N"));
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            var workspace = OrchestratorWorkspace.ForDirectory(_root);
            Context = new(Kernel, workspace, new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), null);
            Store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        }
        internal string Submit(Goal goal, string level, Func<PortfolioStore>? open = null, string? key = null) =>
            InfrastructureTestSupport.CaptureConsoleError(() => InfrastructureTestSupport.CaptureConsole(() =>
                CliGoalPriorityCommand.Execute(["goal-priority", goal.Id.Value[..8], level, "--idempotency-key", key ?? Guid.NewGuid().ToString("N")], Context, Store, open)));
        internal string[] Events(string head) => !File.Exists(Context.Workspace.ConductEventsLogPath) ? [] :
            File.ReadAllLines(Context.Workspace.ConductEventsLogPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(item => item.GetProperty("eventKind").GetString() == "priority")
                .Select(item => item.GetProperty("detail").GetString()!).Where(line => line.StartsWith(head, StringComparison.Ordinal)).ToArray();
        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
    }
}
