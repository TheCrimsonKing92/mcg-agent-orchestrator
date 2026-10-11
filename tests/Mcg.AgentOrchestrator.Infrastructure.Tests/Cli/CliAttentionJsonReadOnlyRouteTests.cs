using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliAttentionJsonReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData("attention", "--json")]
    [InlineData("attention", "list", "--json")]
    [InlineData("ATTENTION", "--json")]
    [InlineData("Attention", "LIST", "--json")]
    public async Task JsonFormsAreQueryOnlyAndSkipHydration(params string[] args)
    {
        Assert.True(CliAttentionJsonQueryCommand.IsAttentionJsonCommand(args));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
        var startup = await CliReadOnlyStartupHydration.PrepareStartupAsync(args, repository);
        Assert.False(startup.Hydrated);
        Assert.Null(startup.CurrentGoal);
        AssertNoWrites(repository);
    }

    [Theory]
    [InlineData("attention", "--json", "extra")]
    [InlineData("attention", "list", "--json", "extra")]
    [InlineData("--json", "attention")]
    [InlineData("attention", "show", "--json")]
    [InlineData("attention", "--JSON")]
    [InlineData("attention", "answer", "--json")]
    [InlineData("attention", "dismiss", "--json")]
    [InlineData("attention", "list", "--json", "--json")]
    public void OtherFormsDoNotGainJsonClassification(params string[] args)
    {
        Assert.False(CliAttentionJsonQueryCommand.IsAttentionJsonCommand(args));
        Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(args));
    }

    [Fact]
    public void HelpStaysOutsideJsonRouteAndUnsupportedJsonFlagsFail()
    {
        string[] help = ["attention", "--json", "--help"];
        Assert.False(CliAttentionJsonQueryCommand.IsAttentionJsonCommand(help));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(help));
        foreach (var args in new[]
        {
            new[] { "attention", "show", "abc10000", "--json" },
            new[] { "attention", "answer", "abc10000", "wait-id", "--json" },
            new[] { "attention", "dismiss", "--item", "item-id", "--json" },
            new[] { "attention", "--json", "extra" }
        })
            Assert.Contains("Unknown option '--json'", Assert.Throws<ArgumentException>(
                () => CliCommandHelp.ThrowIfInvalidFlags(args)).Message);
        var output = CaptureConsole(() => Assert.True(CliCommandHelp.TryPrintStartupHelp(["attention", "--help"])));
        Assert.Contains("attention [list] --json", output);
        Assert.Contains("emits the mcg.attention agent envelope", output);
    }

    [Theory]
    [InlineData(true, "attention")]
    [InlineData(true, "attention", "list")]
    [InlineData(true, "attention", "show")]
    [InlineData(false, "attention", "answer", "abc10000", "wait-id", "answer")]
    [InlineData(false, "attention", "dismiss", "abc10000")]
    [InlineData(false, "attention", "dismiss", "--item", "item-id")]
    public void ExistingFormsKeepClassificationAndRouting(bool readOnly, params string[] args)
    {
        Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(args));
        Assert.Equal(readOnly, CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.False(CliAttentionJsonQueryCommand.IsAttentionJsonCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly,
            CliCommandCapabilities.Classify(["attention", "--json"]));
        Assert.Equal(CliCommandCapability.QueryOnly,
            CliCommandCapabilities.Classify(["attention", "list", "--json"]));
    }

    [Theory]
    [InlineData(false, "attention", "--json")]
    [InlineData(false, "attention", "list", "--json")]
    [InlineData(true, "attention", "--json")]
    [InlineData(true, "attention", "list", "--json")]
    public async Task ParkedOrMissingStoreNeverDeclinesOrWrites(bool parked, params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            File.WriteAllText(Path.Combine(root, "sentinel.txt"), "unchanged bytes");
            var kernel = new AgentOrchestratorKernel(new FutureClock());
            var live = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Live wait");
            var wait = kernel.RequestHumanInput(live.Id, null, "Live question");
            CollaborationItemStore? store = null;
            CollaborationItem? parkedItem = null;
            HumanInputRequest? parkedWait = null;
            Goal? parkedGoal = null;
            if (parked)
            {
                parkedGoal = kernel.CreateGoal(new GoalId("abc20000bbbbbbbbbbbbbbbbbbbbbbbb"), "Parked wait");
                parkedWait = kernel.RequestHumanInput(parkedGoal.Id, null, "Parked question");
                kernel.ParkGoal(parkedGoal.Id, "Operator wait");
                Assert.True(parkedWait.IsCompleted);
                store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
                parkedItem = await store.RaiseAsync(CollaborationItemType.Decision, parkedGoal.Id.Value,
                    "Parked decision", "Keep raised", "parked-decision");
            }
            var database = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
            Assert.Equal(parked, File.Exists(database));
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var providers = new InMemoryModelProviderRegistry([]);
            Assert.False(CliAttentionQueryCommand.TryExecute(["attention"], repository, workspace,
                providers, null, ref agents, ref profiles, ref currentGoal));
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes);
            var startup = await CliReadOnlyStartupHydration.PrepareStartupAsync(args, repository);
            Assert.False(startup.Hydrated);
            var declines = 0;
            var output = CaptureConsole(() => Assert.False(CliReadOnlyStartupHydration.ExecuteStartupCommand(
                args, repository, workspace, ref agents, providers, ref profiles, ref currentGoal,
                hydrated: startup.Hydrated, onReadOnlyDeclined: () => declines++)));
            Assert.Equal(0, declines);
            Assert.Null(currentGoal);
            using var json = JsonDocument.Parse(output);
            Assert.Equal("mcg.attention", json.RootElement.GetProperty("schema").GetString());
            var payload = json.RootElement.GetProperty("payload");
            Assert.Equal(wait.Id.Value, Assert.Single(payload.GetProperty("waits").EnumerateArray())
                .GetProperty("id").GetString());
            Assert.Empty(payload.GetProperty("items").EnumerateArray());
            Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(parked, File.Exists(database));
            AssertNoWrites(repository);
            if (store is not null)
            {
                Assert.Equal(CollaborationItemStatus.Raised,
                    Assert.Single(await store.ListAsync(), item => item.Id == parkedItem!.Id).Status);
                var storedKernel = await repository.LoadGoalsAsync([parkedGoal!.Id]);
                var storedWait = Assert.Single(storedKernel.HumanInputRequests);
                Assert.True(storedWait.IsCompleted);
                Assert.Equal(parkedWait!.Answer, storedWait.Answer);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeededHumanOutputKeepsLiteralBytes(bool withWait)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel(new FutureClock());
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Human golden");
            string expected;
            if (withWait)
            {
                var wait = kernel.RequestHumanInput(goal.Id, null, "Literal question",
                    isAutoDefaultable: true, isDismissible: false, isAnswerRequired: true, isExternallyBlocked: false);
                // An item is also present: the human view still prints waits only.
                await store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value, "Hidden item", "Hidden body");
                expected = "\r\nHuman waits: 1 active wait(s)\r\n" +
                    $"  [SpecClarification] {wait.Id.Value[..8]} goal=abc10000 task=goal age=0s\r\n" +
                    "    flags: auto-defaultable=True; dismissible=False; answer-required=True; externally-blocked=False\r\n" +
                    "    question: Literal question\r\n" +
                    $"    resume: attention answer abc10000 {wait.Id.Value[..8]} <answer>\r\n\r\n";
            }
            else
            {
                await store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value, "Goal decision", "Goal body");
                await store.RaiseAsync(CollaborationItemType.Decision, null, "Cross decision", "Cross body");
                expected = "\r\nAttention queue: 2 open reach-up item(s)\r\n";
                foreach (var item in await store.GetAttentionQueueAsync())
                    expected += $"  [Decision] {item.Id[..8]} status=Raised goal={(item.GoalId is null ? "cross-goal" : "abc10000")}\r\n" +
                        $"    dismiss: attention dismiss --item {item.Id}\r\n" +
                        $"    subject: {item.Subject}\r\n    body: {item.Body}\r\n    raised: {item.RaisedAt:u}\r\n";
                expected += "\r\n";
            }
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var providers = new InMemoryModelProviderRegistry([]);
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            var jsonOutput = CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(["attention", "--json"], repository, workspace,
                    providers, null, ref agents, ref profiles, ref currentGoal, out var changed));
                Assert.False(changed);
            });
            using var json = JsonDocument.Parse(jsonOutput);
            var payload = json.RootElement.GetProperty("payload");
            Assert.Equal(withWait ? 1 : 0, payload.GetProperty("waits").GetArrayLength());
            Assert.Equal(withWait ? 1 : 2, payload.GetProperty("items").GetArrayLength());
            var read = CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(["attention"], repository, workspace,
                    providers, null, ref agents, ref profiles, ref currentGoal, out var changed));
                Assert.False(changed);
            });
            var legacy = CaptureConsole(() => Assert.False(CliCommandDispatcher.ExecuteCommand(
                ["attention"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal)));
            Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(read));
            Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(legacy));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void AssertNoWrites(ProbeStateRepository repository)
    {
        Assert.Equal(0, repository.FullLoadAttempts);
        Assert.Equal(0, repository.SaveAttempts);
        Assert.Equal(0, repository.MergeSaveAttempts);
        Assert.Equal(0, repository.MutationAttempts);
        Assert.Equal(0, repository.ListOutboxMessagesCount);
        Assert.Equal(0, repository.OutboxClaimAttempts);
    }

    private sealed class FutureClock : IClock
    {
        public DateTimeOffset UtcNow => new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
