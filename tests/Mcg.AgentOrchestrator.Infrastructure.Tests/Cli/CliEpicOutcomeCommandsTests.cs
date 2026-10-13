using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: in-process command/capture seams and real stores use one private root per instance.
public sealed class CliEpicOutcomeCommandsTests : CliTaskQueryTestSupport, IDisposable
{
    private readonly string root = CreateTempDirectory();
    private static EpicOutcomeDefinition Definition(long version = 0) => new(1, version, "Service outcome",
        [new("service", "Owner observes service behavior", "file-sha256", "owner-receipt")]);

    [Fact]
    public void Commands_apply_unknown_unmet_proven_and_stale_states_to_all_shared_views()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = portfolio.AddEpicAsync("Epic", description: "Narrative claims success").GetAwaiter().GetResult();
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var item = backlog.AddAsync("Full scope").GetAwaiter().GetResult();
        new EpicPlanStore(workspace.PortfolioStorePath).AddSliceAsync(epic.Id, item.Id).GetAwaiter().GetResult();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Delivered full scope");
        kernel.SetGoalSourceBacklogItemLink(goal.Id, item.Id, SourceBacklogCoverage.Full);
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(member => member with { Status = GoalStatus.Completed }).ToArray()
        });
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        portfolio.AssignGoalToEpicAsync(goal.Id.Value, epic.Id).GetAwaiter().GetResult();
        backlog.CloseAsync(item.Id).GetAwaiter().GetResult();
        CheckViews(workspace, epic.Id, "unknown", 0, "outcome evidence required");
        var definitionFile = WriteJson(Definition());
        var queued = Submit(workspace, "epic-outcome-define", epic.Id, definitionFile, "definition");
        Assert.Equal(OperatorIntentStatus.Pending, queued.Status);
        Assert.Equal(0, EpicProgressReadModel.LoadEpic(workspace, epic.Id).Outcome.BarVersion);
        Assert.Equal(queued.Id, Submit(workspace, "epic-outcome-define", epic.Id, definitionFile, "definition").Id);
        Tick(workspace, queued);
        CheckViews(workspace, epic.Id, "unknown", 1, "outcome evidence required");
        var unmet = Evidence(false);
        var first = Submit(workspace, "epic-outcome-record", epic.Id, WriteJson(unmet), "unmet");
        Tick(workspace, first);
        CheckViews(workspace, epic.Id, "unmet", 1, "outcome unmet");
        File.Delete(Path.Combine(root, "receipt.txt"));
        Assert.Equal(first.Id, Submit(workspace, "epic-outcome-record", epic.Id, WriteJson(unmet), "unmet").Id);
        Assert.Contains("missing-receipt", EpicProgressReadModel.LoadEpic(workspace, epic.Id).Outcome.Reasons);
        Evidence(false); // Restore the same receipt bytes for the next explicit observation.
        var met = unmet with { Met = true, ExpectedObservationSequence = 1, SupersedesReceiptIds = [first.Id] };
        Tick(workspace, Submit(workspace, "epic-outcome-record", epic.Id, WriteJson(met), "met"));
        CheckViews(workspace, epic.Id, "proven", 1, "complete");
        Tick(workspace, Submit(workspace, "epic-outcome-define", epic.Id, WriteJson(Definition(1)), "new-policy"));
        CheckViews(workspace, epic.Id, "unknown", 2, "outcome evidence required");
        Assert.Contains("stale-version", EpicProgressReadModel.LoadEpic(workspace, epic.Id).Outcome.Reasons);
        Assert.Equal(2, new EpicPlanStore(workspace.PortfolioStorePath).LoadAsync(epic.Id).GetAwaiter().GetResult().Outcome!.Observations.Count);
    }

    [Fact]
    public void Malformed_missing_duplicate_and_unknown_inputs_do_not_change_portfolio_or_enqueue()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var epic = new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Epic").GetAwaiter().GetResult();
        var file = WriteJson(Definition());
        var before = File.ReadAllBytes(workspace.PortfolioStorePath);
        foreach (var json in new[] { "{}", "null", "{broken", "{\"schemaVersion\":1,\"expectedVersion\":0,\"displayText\":\"Outcome\",\"requirements\":[],\"actor\":\"owner\"}" })
        {
            var malformed = Path.Combine(root, "malformed.json");
            File.WriteAllText(malformed, json);
            Assert.Throws<ArgumentException>(() => Execute(workspace, "epic-outcome-define", epic.Id, "--text-file", malformed));
        }
        Assert.Throws<InvalidOperationException>(() => Execute(workspace, "epic-outcome-define", "absent", "--text-file", file));
        Assert.Throws<FileNotFoundException>(() => Execute(workspace, "epic-outcome-define", epic.Id, "--text-file", Path.Combine(root, "missing.json")));
        Assert.Throws<ArgumentException>(() => Execute(workspace, "epic-outcome-define", epic.Id, "--text-file", file, "--text-file", file));
        Assert.Throws<ArgumentException>(() => Execute(workspace, "epic-outcome-define", epic.Id, "--text-file", file, "--json", "--json"));
        Assert.Throws<ArgumentException>(() => Execute(workspace, "epic-outcome-define", epic.Id, "--text-file", file, "--unknown", "x"));
        Assert.Equal(before, File.ReadAllBytes(workspace.PortfolioStorePath));
        var first = Submit(workspace, "epic-outcome-define", epic.Id, file, "same-request");
        Assert.Throws<InvalidOperationException>(() => Submit(workspace, "epic-outcome-define", epic.Id,
            WriteJson(Definition() with { DisplayText = "Changed policy" }), "same-request"));
        Tick(workspace, first);
        Assert.Single(new EpicPlanStore(workspace.PortfolioStorePath).LoadAsync(epic.Id).GetAwaiter().GetResult().Outcome!.Revisions);
    }

    [Fact]
    public void Proposals_are_per_epic_read_only_and_do_not_infer_policy_from_narrative()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var a = portfolio.AddEpicAsync("A", description: "Everything is proven").GetAwaiter().GetResult();
        var b = portfolio.AddEpicAsync("B", description: "All goals landed").GetAwaiter().GetResult();
        var before = File.ReadAllBytes(workspace.PortfolioStorePath);
        using var json = JsonDocument.Parse(Execute(workspace, "epic-outcome-proposals", "--json"));
        var proposals = json.RootElement.GetProperty("payload").GetProperty("proposals").EnumerateArray().ToArray();
        Assert.Equal(new[] { a.Id, b.Id }.Order(), proposals.Select(proposal => proposal.GetProperty("epicId").GetString()!).Order());
        Assert.All(proposals, proposal =>
        {
            Assert.Equal("unknown", proposal.GetProperty("outcome").GetProperty("state").GetString());
            Assert.Equal("retain unknown pending owner decision", proposal.GetProperty("action").GetString());
            Assert.NotEmpty(proposal.GetProperty("ownerDecisionsRequired").EnumerateArray());
            Assert.Contains(proposal.GetProperty("epicId").GetString()!, proposal.GetProperty("defineCommand").GetString());
        });
        Assert.Equal(before, File.ReadAllBytes(workspace.PortfolioStorePath));
        Assert.Equal(0, EpicProgressReadModel.LoadEpic(workspace, a.Id).Outcome.BarVersion);
    }

    private void CheckViews(OrchestratorWorkspace workspace, string epicId, string state, long version, string next)
    {
        var row = EpicProgressReadModel.LoadEpic(workspace, epicId);
        Assert.Equal(state, row.Outcome.State.ToString().ToLowerInvariant());
        Assert.Equal(version, row.Outcome.BarVersion);
        Assert.Equal(next, EpicPlanNextStep.Compute(row.Plan!).Text);
        Assert.Equal("1 of 1 slices landed; next: complete", EpicPlanNextStep.Summary(row.Plan!.Items));
        Assert.Equal(1, row.BacklogDoneCount);
        Assert.Equal(0, row.BacklogOpenCount);
        Assert.Equal(1, row.LandedCount);
        Assert.Equal(1, row.GoalCount);
        foreach (var verb in new[] { "epic-list", "epic-show", "epic-plan" })
        {
            var arguments = verb == "epic-list" ? new[] { verb } : [verb, epicId];
            var text = Execute(workspace, arguments);
            Assert.Contains(row.Outcome.Render(), text);
            using var json = JsonDocument.Parse(Execute(workspace, [.. arguments, "--json"]));
            Assert.Equal("mcg.epic-progress", json.RootElement.GetProperty("schema").GetString());
            var projected = json.RootElement.GetProperty("payload").GetProperty("epics")[0];
            Assert.Equal(state, projected.GetProperty("outcome").GetProperty("state").GetString());
            Assert.Equal(version, projected.GetProperty("outcome").GetProperty("barVersion").GetInt64());
            Assert.Equal(next, projected.GetProperty("plan").GetProperty("nextStep").GetProperty("text").GetString());
        }
        var console = new WorkspaceEpicProgressSource(workspace).LoadAsync(null, default).GetAwaiter().GetResult().Single();
        Assert.Equal(JsonSerializer.Serialize(row.Outcome, EpicProgressAgentReadView.Options), JsonSerializer.Serialize(console.Outcome, EpicProgressAgentReadView.Options));
        Assert.Contains(row.Outcome.Render(), OwnerConsoleEpicFormatter.ListLines(new(OwnerConsoleEpicWindow.AllTime, null, [console]), 1000, epicId));
        Assert.Contains(row.Outcome.Render(), OwnerConsoleEpicPlanFormatter.Lines(new(row.Epic, [], [], [], [], row.Plan), 1000));
    }

    private OperatorIntentRecord Submit(OrchestratorWorkspace workspace, string verb, string epic, string file, string key)
    {
        using var json = JsonDocument.Parse(Execute(workspace, verb, epic, "--text-file", file, "--operator-actor", "owner", "--idempotency-key", key, "--json"));
        var payload = json.RootElement.GetProperty("payload");
        var id = payload.GetProperty("id").GetString()!;
        return SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory).GetAsync(id).GetAwaiter().GetResult()!;
    }

    private static void Tick(OrchestratorWorkspace workspace, OperatorIntentRecord intent)
    {
        Assert.NotEmpty(OperatorIntentCoordinator.CreateDefault(workspace).ExecuteWorkspacePending(new AgentOrchestratorKernel()));
        Assert.Equal(OperatorIntentStatus.Applied, SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory).GetAsync(intent.Id).GetAwaiter().GetResult()!.Status);
    }

    private EpicOutcomeEvidence Evidence(bool met)
    {
        var file = Path.Combine(root, "receipt.txt");
        File.WriteAllText(file, "Owner observation receipt");
        return new(1, 1, "service", 0, met, DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            new("file-sha256", "receipt.txt", "owner-receipt", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));
    }

    private string WriteJson<T>(T payload)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("n") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, EpicOutcomeContract.JsonOptions));
        return path;
    }

    private static string Execute(OrchestratorWorkspace workspace, params string[] args)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        // epic-plan uses the persistent transaction route; other commands retain the mutation guard.
        ITransactionalOrchestratorStateRepository repository = args[0] == "epic-plan"
            ? new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
            : new ProbeStateRepository(new AgentOrchestratorKernel());
        return CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(args,
            repository, workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
