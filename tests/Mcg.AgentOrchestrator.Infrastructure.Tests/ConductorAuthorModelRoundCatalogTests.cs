using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorAuthorModelRoundCatalogTests
{
    [Theory]
    [InlineData(false, null, "claude --model sonnet --permission-mode plan -p")]
    [InlineData(true, null, "claude --model claude-sonnet-5 --permission-mode plan -p")]
    [InlineData(true, "high", "claude --model claude-sonnet-5 --effort high --permission-mode plan -p")]
    public async Task Catalog_selects_alias_and_preserves_command_flags(bool bound, string? effort, string expected)
    {
        using var fixture = new Fixture();
        WorkerProcessRunRequest? request = null;
        var round = new ClaudeConductorAuthorModelRound(fixture.Receipts, (value, _) =>
        {
            request = value;
            return Task.FromResult(new WorkerProcessRunResult(0, "answer", ""));
        }, bound ? ConductorRoundModelResolverTests.Catalog(ModelFunctionPurposes.ConductorAuthor, effort) : ModelFunctionCatalog.Empty);
        Assert.Equal("answer", await round.DispatchAsync(Input(), fixture.Root, CancellationToken.None));
        Assert.NotNull(request);
        Assert.Equal(expected, request.Command);
        Assert.Equal(TimeSpan.FromMinutes(10), request.Timeout);
        Assert.Equal(fixture.Root, request.WorkingDirectory);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("other-profile")]
    public async Task Invalid_binding_records_failure_and_never_launches(string shape)
    {
        using var fixture = new Fixture();
        var calls = 0;
        var round = new ClaudeConductorAuthorModelRound(fixture.Receipts, (_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(0, "", ""));
        }, ConductorRoundModelResolverTests.InvalidCatalog(ModelFunctionPurposes.ConductorAuthor, shape));
        var failure = await Assert.ThrowsAsync<ConductorModelRoundException>(() =>
            round.DispatchAsync(Input(), fixture.Root, CancellationToken.None));
        Assert.Equal(0, calls);
        Assert.StartsWith("model-binding-invalid:conductor-author", failure.Message);
        using var receipt = fixture.Receipt();
        Assert.Equal(failure.Message, receipt.RootElement.GetProperty("failure").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("exitCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("model").ValueKind);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task Failed_exit_or_empty_output_records_the_used_alias(int exitCode)
    {
        using var fixture = new Fixture();
        var round = new ClaudeConductorAuthorModelRound(fixture.Receipts, (_, _) =>
            Task.FromResult(new WorkerProcessRunResult(exitCode, "", "bad model")),
            ConductorRoundModelResolverTests.Catalog(ModelFunctionPurposes.ConductorAuthor));
        if (exitCode != 0)
        {
            var failure = await Assert.ThrowsAsync<ConductorModelRoundException>(() =>
                round.DispatchAsync(Input(), fixture.Root, CancellationToken.None));
            Assert.Equal("claude-sonnet-5", failure.ModelAlias);
        }
        else Assert.Equal("", await round.DispatchAsync(Input(), fixture.Root, CancellationToken.None));
        using var receipt = fixture.Receipt();
        Assert.Equal("claude-sonnet-5", receipt.RootElement.GetProperty("model").GetString());
        Assert.Equal(exitCode, receipt.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("exit")]
    [InlineData("empty")]
    public async Task Host_conduct_event_exposes_invalid_purpose_or_used_alias(string failureKind)
    {
        using var fixture = new Fixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var calls = 0;
        var model = new ClaudeConductorAuthorModelRound(fixture.Receipts, (_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(failureKind == "exit" ? 1 : 0, "", "bad model"));
        }, failureKind == "invalid"
            ? ConductorRoundModelResolverTests.InvalidCatalog(ModelFunctionPurposes.ConductorAuthor, "empty")
            : ConductorRoundModelResolverTests.Catalog(ModelFunctionPurposes.ConductorAuthor));
        var host = new ConductorAuthorHost(new ConductorAuthorClaimStore(Path.Combine(fixture.Root, "claims.db")),
            collaboration, model, intents, new SpecRefinerPrecedentStore(workspace.SpecRefinerPrecedentsPath),
            _ => fixture.Root, new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory),
            new ConductEventLogWriter(workspace.ConductEventsLogPath));
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Choose runtime");
            await collaboration.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value,
                "Runtime", "Question: Which runtime?\nFork kind: runtime", $"spec-clarification:{goal.Id.Value}:runtime");
            host.ServiceTick(kernel);
            foreach (var task in host.CurrentRounds)
            {
                if (failureKind == "empty") await task;
                else await Assert.ThrowsAsync<ConductorModelRoundException>(() => task);
            }
            host.ServiceTick(kernel);
            var log = File.ReadAllText(workspace.ConductEventsLogPath);
            Assert.Contains(failureKind == "invalid" ? "reason=model-binding-invalid:conductor-author" : "model=claude-sonnet-5", log);
            if (failureKind == "invalid") Assert.Equal(0, calls);
            else Assert.True(calls > 0);
            Assert.Empty(await intents.ListForGoalAsync(goal.Id.Value));
        }
        finally { host.Stop(); }
    }

    internal static ConductorAuthorRoundInput Input() => new(
        new ConductorAuthorItem(OperatorAnswerTargetKind.Clarification, "item", "goal", "question", "brief"), "brief", "{}", null);

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "author-model-catalog-" + Guid.NewGuid().ToString("N"));
        internal string Receipts => Path.Combine(Root, "receipts");
        internal Fixture() => Directory.CreateDirectory(Root);
        internal JsonDocument Receipt() => JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(Receipts, "*.json"))));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
