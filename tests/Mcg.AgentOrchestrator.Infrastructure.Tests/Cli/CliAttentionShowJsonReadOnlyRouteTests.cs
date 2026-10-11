using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Private roots and explicit child environments; console capture stays in the shared console collection.
[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliAttentionShowJsonReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData("attention", "show", "--json")]
    [InlineData("attention", "show", "abc10000", "--json")]
    [InlineData("attention", "show", "--goal", "abc10000", "--json")]
    [InlineData("ATTENTION", "SHOW", "--json")]
    [InlineData("Attention", "Show", "--GOAL", "abc10000", "--json")]
    public async Task ExactJsonForms_AreQueryOnlyAndSkipHydration(params string[] args)
    {
        Assert.True(CliAttentionShowJsonQueryCommand.IsAttentionShowJsonCommand(args));
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
    [InlineData(true, "attention", "show")]
    [InlineData(true, "attention", "show", "abc10000")]
    [InlineData(true, "attention", "show", "--all")]
    [InlineData(true, "attention", "show", "--all", "--json")]
    [InlineData(true, "attention", "show", "--include-parked", "abc10000", "--json")]
    [InlineData(true, "attention", "show", "--json", "extra", "words")]
    [InlineData(true, "attention", "show", "--goal", "--json")]
    [InlineData(true, "attention", "show", "abc10000", "--JSON")]
    [InlineData(true, "attention", "show", "--unknown", "--json")]
    [InlineData(false, "attention", "answer", "abc10000", "wait-id", "answer")]
    [InlineData(false, "attention", "dismiss", "abc10000")]
    [InlineData(false, "attention", "dismiss", "--item", "item-id")]
    public void OtherForms_KeepClassificationAndRouting(bool readOnly, params string[] args)
    {
        Assert.False(CliAttentionShowJsonQueryCommand.IsAttentionShowJsonCommand(args));
        Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(args));
        Assert.Equal(readOnly, CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
    }

    [Fact]
    public void Help_ListsShowEnvelopeAndExcludesHistoryJson()
    {
        foreach (var args in new[]
        {
            new[] { "attention", "show", "--json", "--help" },
            new[] { "attention", "show", "--help", "--json" },
            new[] { "attention", "show", "-h", "--json" }
        })
        {
            Assert.False(CliAttentionShowJsonQueryCommand.IsAttentionShowJsonCommand(args));
            Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        }
        var output = CaptureConsole(() => Assert.True(CliCommandHelp.TryPrintStartupHelp(["attention", "--help"])));
        Assert.Contains("attention show [--goal] [<goal-id-prefix>] --json", CliCommandHelp.AttentionUsage);
        Assert.Contains("attention show [--goal] [<goal-id-prefix>] --json", output);
        Assert.Contains("mcg.attention-show agent envelope", output);
        Assert.Contains("--all history has no JSON form yet", output);
        foreach (var args in new[]
        {
            new[] { "attention", "show", "--all", "--json" },
            new[] { "attention", "show", "--include-parked", "abc10000", "--json" }
        })
            Assert.Contains("Unknown option '--json'", Assert.Throws<ArgumentException>(
                () => CliCommandHelp.ThrowIfInvalidFlags(args)).Message);
    }

    [Theory]
    [InlineData(false, "attention", "show", "--json")]
    [InlineData(false, "attention", "show", "abc10000", "--json")]
    [InlineData(false, "attention", "show", "--goal", "abc10000", "--json")]
    [InlineData(true, "attention", "show", "--json")]
    [InlineData(true, "attention", "show", "abc10000", "--json")]
    [InlineData(true, "attention", "show", "--goal", "abc10000", "--json")]
    [InlineData(true, "attention", "show", "abc20000", "--json")]
    [InlineData(true, "attention", "show", "--goal", "abc20000", "--json")]
    public async Task ParkedOrMissingStore_EmitsEnvelopeWithoutDeclinesOrWrites(bool parked, params string[] args)
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
            // Establish that these seeds exercise the human route's decline cases.
            Assert.False(CliAttentionQueryCommand.TryExecute(["attention", "show"], repository, workspace,
                providers, null, ref agents, ref profiles, ref currentGoal));
            string[] allowedReadSidecars = File.Exists(database) ? [database + "-wal", database + "-shm"] : [];
            var before = Snapshot(root, allowedReadSidecars);
            var startup = await CliReadOnlyStartupHydration.PrepareStartupAsync(args, repository);
            Assert.False(startup.Hydrated);
            var declines = 0;
            var output = CaptureConsole(() => Assert.False(CliReadOnlyStartupHydration.ExecuteStartupCommand(
                args, repository, workspace, ref agents, providers, ref profiles, ref currentGoal,
                hydrated: startup.Hydrated, onReadOnlyDeclined: () => declines++)));
            Assert.Equal(0, declines);
            Assert.Null(currentGoal);
            using var json = JsonDocument.Parse(output);
            Assert.Equal("mcg.attention-show", json.RootElement.GetProperty("schema").GetString());
            var payload = json.RootElement.GetProperty("payload");
            var scopedToParked = args.Contains("abc20000");
            Assert.Equal(args.Length == 3 ? null : scopedToParked ? parkedGoal!.Id.Value : live.Id.Value,
                payload.GetProperty("goalId").GetString());
            Assert.Equal(JsonValueKind.Array, payload.GetProperty("waits").ValueKind);
            Assert.Equal(JsonValueKind.Array, payload.GetProperty("items").ValueKind);
            if (scopedToParked)
                Assert.Empty(payload.GetProperty("waits").EnumerateArray());
            else
                Assert.Equal(wait.Id.Value, Assert.Single(payload.GetProperty("waits").EnumerateArray())
                    .GetProperty("id").GetString());
            Assert.Empty(payload.GetProperty("items").EnumerateArray());
            var after = Snapshot(root, allowedReadSidecars);
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var (path, hash) in before) Assert.Equal(hash, after[path]);
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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvalidPrefix_MatchesHumanProcessErrorAndEmitsNoEnvelope(bool ambiguous, bool explicitGoal)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "First goal");
            if (ambiguous)
                kernel.CreateGoal(new GoalId("abc10000bbbbbbbbbbbbbbbbbbbbbbbb"), "Second goal");
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            var prefix = ambiguous ? "abc10000" : "missing";
            string[] humanArgs = explicitGoal ? ["attention", "show", "--goal", prefix] : ["attention", "show", prefix];
            // Absent database proves the human invocation takes the writer path.
            Assert.False(File.Exists(Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db")));
            var human = await RunProcessAsync(root, humanArgs);
            Assert.Equal(1, human.ExitCode);
            Assert.Equal(string.Empty, human.StandardOutput);
            var expectedError = ambiguous
                ? "InvalidOperationException: Goal prefix 'abc10000' is ambiguous (2 matches).\r\n"
                : CliAttentionNextGoldenFixtures.UnknownAttentionGoal.Stderr;
            Assert.Equal(expectedError, human.StandardError);
            var json = await RunProcessAsync(root, [.. humanArgs, "--json"]);
            Assert.Equal(human.ExitCode, json.ExitCode);
            Assert.Equal(Encoding.UTF8.GetBytes(human.StandardOutput), Encoding.UTF8.GetBytes(json.StandardOutput));
            Assert.Equal(Encoding.UTF8.GetBytes(human.StandardError), Encoding.UTF8.GetBytes(json.StandardError));
            Assert.Equal(string.Empty, json.StandardOutput);
            Assert.DoesNotContain("mcg.attention-show", json.StandardOutput);
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task<CliChildProcessResult> RunProcessAsync(string root, string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root
        };
        start.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP", "DOTNET_ROOT",
            "USERPROFILE", "LOCALAPPDATA", "APPDATA" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = root;
        return CliChildProcessRunner.RunAsync(start);
    }

    private static Dictionary<string, string> Snapshot(string root, IReadOnlyCollection<string> allowedReadSidecars) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !allowedReadSidecars.Contains(path))
            .ToDictionary(path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

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
