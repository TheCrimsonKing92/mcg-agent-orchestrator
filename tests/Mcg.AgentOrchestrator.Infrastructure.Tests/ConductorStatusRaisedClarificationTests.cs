using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private workspace/databases, injected owner and boot probes, local writers.
public sealed class ConductorStatusRaisedClarificationTests
{
    private const string Goal = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Topic = "unexpected-dependency-scope";

    [Fact]
    public async Task Status_AuthorHeldClarification_PrintsIncidentAndPreservesStore()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var kernel = CreateKernel();
        await Save(fixture.Workspace, kernel);
        var store = CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory);
        var item = await Raise(store);
        Hold(kernel, item.Id);
        await Save(fixture.Workspace, kernel);
        var goal = Assert.Single(kernel.Goals);
        var id = CliCommandHandlers.ClarificationId(item,
            CliCommandHandlers.AllClarificationsForGoal(store, goal));
        Assert.Equal(Topic, id);
        var path = StorePath(fixture.Workspace);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(path));

        var human = Status(fixture.Workspace);

        Assert.Contains("Pending clarifications: 1; human waits: 0", human);
        Assert.Contains($"  {Goal} | Draft | role=Researcher{Environment.NewLine}" +
            $"    clarification: id={id} hold=author-owner-question raised={item.RaisedAt:O} " +
            $"answer=attention answer aaaaaaaa {id} <answer>{Environment.NewLine}", human);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));

        using var json = JsonDocument.Parse(Status(fixture.Workspace, json: true));
        Assert.Equal(1, JsonGoals(json).GetProperty("pendingClarifications").GetInt32());
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_NoMatchingAuthorHold_PrintsAwaitingClarification(bool otherTarget)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var kernel = CreateKernel();
        var store = CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory);
        var item = await Raise(store);
        if (otherTarget) Hold(kernel, "different-clarification-id");
        await Save(fixture.Workspace, kernel);

        var human = Status(fixture.Workspace);

        Assert.Contains($"    clarification: id={Topic} hold=awaiting-clarification raised={item.RaisedAt:O} " +
            $"answer=attention answer aaaaaaaa {Topic} <answer>", human);
        Assert.Contains("Pending clarifications: 1; human waits: 0", human);
    }

    [Fact]
    public async Task Status_KernelAndStoreClarifications_SumsBothSources()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var kernel = CreateKernel();
        var second = kernel.CreateGoal(new GoalId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), "kernel clarification");
        kernel.RequestHumanInput(second.Id, null, "Which scope?", HumanWaitKind.SpecClarification);
        await Save(fixture.Workspace, kernel);
        await Raise(CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory));

        Assert.Contains("Pending clarifications: 2; human waits: 0", Status(fixture.Workspace));
        using var json = JsonDocument.Parse(Status(fixture.Workspace, json: true));
        Assert.Equal(2, JsonGoals(json).GetProperty("pendingClarifications").GetInt32());
        Assert.Equal(0, JsonGoals(json).GetProperty("humanWaits").GetInt32());
    }

    [Theory]
    [InlineData("resolved")]
    [InlineData("parked")]
    [InlineData("completed")]
    [InlineData("decision")]
    [InlineData("keyless")]
    public async Task Status_NonAttentionItems_LeavesBaselineCountUnchanged(string scenario)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var kernel = CreateKernel();
        if (scenario is "parked" or "completed")
        {
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal with
                {
                    Status = scenario == "parked" ? GoalStatus.Parked : GoalStatus.Completed
                }).ToArray()
            });
        }
        await Save(fixture.Workspace, kernel);
        var baseline = CountLine(Status(fixture.Workspace));
        var store = CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory);
        var item = await store.RaiseAsync(
            scenario == "decision" ? CollaborationItemType.Decision : CollaborationItemType.Clarification,
            Goal, "Q", "R", scenario switch
            {
                "decision" => "decision:scope",
                "keyless" => null,
                _ => $"spec-clarification:{Goal}:{Topic}"
            });
        if (scenario == "resolved") Assert.True(await store.TryResolveByIdAsync(item.Id, "answered"));

        var human = Status(fixture.Workspace);

        Assert.DoesNotContain("    clarification:", human);
        Assert.Equal(baseline, CountLine(human));
        Assert.Equal("Pending clarifications: 0; human waits: 0", baseline);
        if (scenario == "completed") Assert.DoesNotContain($"  {Goal} |", human);
        if (scenario == "parked") Assert.Contains($"  {Goal} | Parked |", human);
    }

    [Fact]
    public async Task Status_UnreadableStore_ReportsUnavailableWithoutFailing()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        await Save(fixture.Workspace, CreateKernel());
        var bytes = Encoding.ASCII.GetBytes("not a sqlite db!");
        Assert.Equal(16, bytes.Length);
        await File.WriteAllBytesAsync(StorePath(fixture.Workspace), bytes);

        var human = Status(fixture.Workspace);

        Assert.Contains("Pending clarifications: unavailable (collaboration items unreadable); human waits: 0", human);
        Assert.Contains($"  {Goal} | Draft | role=Researcher", human);
        Assert.DoesNotContain("    clarification:", human);
        using var json = JsonDocument.Parse(Status(fixture.Workspace, json: true));
        Assert.Equal(JsonValueKind.Null, JsonGoals(json).GetProperty("pendingClarifications").ValueKind);
        Assert.Equal(0, JsonGoals(json).GetProperty("humanWaits").GetInt32());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StorePath(fixture.Workspace)));
    }

    [Fact]
    public async Task Status_MissingStore_PreservesHumanOutputAndCreatesNoStore()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        await Save(fixture.Workspace, CreateKernel());
        Assert.False(File.Exists(StorePath(fixture.Workspace)));

        var human = Status(fixture.Workspace);

        Assert.Equal(string.Join(Environment.NewLine,
            "Conductor: stopped",
            "Since: unavailable",
            "Supervisor build: unavailable",
            "Generation: unavailable (no supervisor build recorded)",
            "Latest tick: unavailable",
            "Non-terminal goals: 1",
            $"  {Goal} | Draft | role=Researcher",
            "Pending clarifications: 0; human waits: 0",
            "Most recent landing: unavailable",
            "Worker adoption: unavailable",
            "Pending operator intents: unavailable (intent database missing)",
            "Owner directives: 0 active", ""), human);
        using var json = JsonDocument.Parse(Status(fixture.Workspace, json: true));
        Assert.Equal(0, JsonGoals(json).GetProperty("pendingClarifications").GetInt32());
        Assert.False(File.Exists(StorePath(fixture.Workspace)));
    }

    private static AgentOrchestratorKernel CreateKernel()
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId(Goal), "clarification incident");
        return kernel;
    }

    private static void Hold(AgentOrchestratorKernel kernel, string targetId)
    {
        var text = new ConductorAuthorOwnerQuestion(Goal, OperatorAnswerTargetKind.Clarification,
            targetId, "Q", "R", "model-ask-owner").Format();
        kernel.ObserveGoalHold(new GoalId(Goal), "author-owner-question", text,
            new DateTimeOffset(2026, 10, 25, 21, 13, 13, TimeSpan.Zero), TimeSpan.MaxValue,
            $"author-owner-question:Clarification:{targetId}");
    }

    private static async Task Save(OrchestratorWorkspace workspace, AgentOrchestratorKernel kernel) =>
        await InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);

    private static Task<CollaborationItem> Raise(CollaborationItemStore store) =>
        store.RaiseAsync(CollaborationItemType.Clarification, Goal, "Q", "R", $"spec-clarification:{Goal}:{Topic}");

    private static string StorePath(OrchestratorWorkspace workspace) =>
        Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");

    private static string CountLine(string human) =>
        Assert.Single(human.Split(Environment.NewLine), line => line.StartsWith("Pending clarifications:", StringComparison.Ordinal));

    private static JsonElement JsonGoals(JsonDocument json) => json.RootElement.GetProperty("payload").GetProperty("goals");

    private static string Status(OrchestratorWorkspace workspace, bool json = false)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(json ? ["conductor", "status", "--json"] : ["conductor", "status"],
            workspace, lockProbe: new ConductorVerbStartTests.FixedProbe(null),
            bootTime: () => DateTimeOffset.UnixEpoch, mainCommit: () => "def456", output: output, error: error));
        Assert.Equal("", error.ToString());
        return output.ToString();
    }
}
