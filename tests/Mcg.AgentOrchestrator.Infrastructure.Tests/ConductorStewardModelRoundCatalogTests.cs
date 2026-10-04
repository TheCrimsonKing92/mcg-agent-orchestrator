using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorStewardModelRoundCatalogTests
{
    private static readonly Guid SessionId = Guid.Parse("cb9e91a9-a28f-490c-b338-afee7823171c");

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "high")]
    public async Task Catalog_selects_alias_and_preserves_all_Steward_flags(bool bound, string? effort)
    {
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        WorkerProcessRunRequest? request = null;
        var round = Round(fixture, (value, _) =>
        {
            request = value;
            return Task.FromResult(new WorkerProcessRunResult(0, "adjudication", ""));
        }, bound ? ConductorRoundModelResolverTests.Catalog(ModelFunctionPurposes.ConductorSteward, effort) : ModelFunctionCatalog.Empty);
        await round.DispatchAsync(Trigger(), fixture.Root, CancellationToken.None);
        Assert.NotNull(request);
        var modelArguments = bound ? "--model claude-sonnet-5" : "--model sonnet";
        if (effort is not null) modelArguments += " --effort high";
        Assert.Equal($"claude {modelArguments} --permission-mode plan --tools 'Read,Grep,Glob' --allowed-tools 'Read,Grep,Glob' --session-id {SessionId:D} -p", request.Command);
        foreach (var forbidden in new[] { "Bash", "Edit", "Write", "WebFetch" }) Assert.DoesNotContain(forbidden, request.Command);
        Assert.Equal(TimeSpan.FromMinutes(4), request.Timeout);
        using var receipt = fixture.Receipt();
        Assert.Equal(SessionId.ToString("D"), receipt.RootElement.GetProperty("sessionId").GetString());
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("other-profile")]
    public async Task Invalid_binding_never_launches_and_records_purpose_and_session(string shape)
    {
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        var calls = 0;
        var round = Round(fixture, (_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(0, "", ""));
        }, ConductorRoundModelResolverTests.InvalidCatalog(ModelFunctionPurposes.ConductorSteward, shape));
        var failure = await Assert.ThrowsAsync<ConductorModelRoundException>(() =>
            round.DispatchAsync(Trigger(), fixture.Root, CancellationToken.None));
        Assert.Equal(0, calls);
        Assert.StartsWith("model-binding-invalid:conductor-steward", failure.Message);
        using var receipt = fixture.Receipt();
        Assert.Equal(failure.Message, receipt.RootElement.GetProperty("failure").GetString());
        Assert.Equal(SessionId.ToString("D"), receipt.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("exitCode").ValueKind);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task Failed_exit_or_empty_output_records_the_used_alias(int exitCode)
    {
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        var round = Round(fixture, (_, _) => Task.FromResult(new WorkerProcessRunResult(exitCode, "", "bad model")),
            ConductorRoundModelResolverTests.Catalog(ModelFunctionPurposes.ConductorSteward));
        if (exitCode != 0)
            await Assert.ThrowsAsync<ConductorModelRoundException>(() => round.DispatchAsync(Trigger(), fixture.Root, CancellationToken.None));
        else Assert.Equal("", await round.DispatchAsync(Trigger(), fixture.Root, CancellationToken.None));
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
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        using var harness = new StewardHarness("B");
        harness.Seed("B");
        var calls = 0;
        var model = Round(fixture, (_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(failureKind == "exit" ? 1 : 0, "", "bad model"));
        }, failureKind == "invalid"
            ? ConductorRoundModelResolverTests.InvalidCatalog(ModelFunctionPurposes.ConductorSteward, "empty")
            : ConductorRoundModelResolverTests.Catalog(ModelFunctionPurposes.ConductorSteward));
        var host = new ConductorStewardHost(new ConductorStewardTriggerStore(Path.Combine(fixture.Root, "triggers.db")),
            new ConductorStewardTriggerDetector(), model, harness.Intents, new AdjudicationEvidenceResolver(harness.Root),
            _ => 7, _ => harness.Root, new GoalLifecycleEventWriter(Path.Combine(fixture.Root, "lifecycle")),
            new ConductEventLogWriter(harness.ConductPath));
        try
        {
            host.ServiceTick(harness.Kernel);
            Assert.NotNull(host.CurrentRound);
            if (failureKind == "empty") await host.CurrentRound!;
            else await Assert.ThrowsAsync<ConductorModelRoundException>(() => host.CurrentRound!);
            host.ServiceTick(harness.Kernel);
            var log = File.ReadAllText(harness.ConductPath);
            Assert.Contains(failureKind == "invalid" ? "reason=model-binding-invalid:conductor-steward" : "model=claude-sonnet-5", log);
            Assert.Equal(failureKind == "invalid" ? 0 : 1, calls);
            Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
            Assert.Null(harness.Goal.CurrentHold);
        }
        finally { host.Stop(); }
    }

    private static ClaudeConductorStewardModelRound Round(ConductorAuthorModelRoundCatalogTests.Fixture fixture,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> runner, ModelFunctionCatalog catalog) =>
        new(fixture.Receipts, runner, new EmptyFiles(), (_, _) => true, () => SessionId, catalog: catalog);

    private static ConductorStewardTrigger Trigger() => new("goal", "task", "candidate-sha",
        ConductorStewardTriggerKind.DeveloperNoChangeWithConfirmedRed, DateTimeOffset.UtcNow, "Assertion failure", "", [], ["criterion"]);

    private sealed class EmptyFiles : IConductorStewardTrackedFileLister
    {
        public IReadOnlyList<string> MatchingFiles(string worktree, string stem) => [];
    }
}
