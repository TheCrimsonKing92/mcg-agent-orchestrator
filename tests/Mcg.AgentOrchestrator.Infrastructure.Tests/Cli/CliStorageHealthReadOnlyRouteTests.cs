using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel safe: each test owns a private temporary root; console capture is AsyncLocal.
public sealed class CliStorageHealthReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData("storage-health")]
    [InlineData("storage-health", "--json")]
    [InlineData("STORAGE-HEALTH", "--json")]
    public void ServedForms_MissingDirectory_UseQueryOnlyWithoutState(params string[] args)
    {
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Assert.False(Directory.Exists(workspace.OrchestratorDirectory));
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            var stdout = Run(args, workspace, repository);
            if (args.Contains("--json"))
            {
                using var document = JsonDocument.Parse(stdout);
                var report = document.RootElement;
                var classes = report.GetProperty("classes").EnumerateArray().ToArray();
                Assert.Equal(OrchestratorArtifactRegistry.Entries.Count, classes.Length);
                Assert.All(classes, row => Assert.Equal("absent", row.GetProperty("status").GetString()));
                Assert.Empty(report.GetProperty("unregistered").EnumerateArray());
                var totals = report.GetProperty("totals");
                foreach (var field in new[] { "bytes", "files", "over", "unregistered", "unreadable" })
                    Assert.Equal(0, totals.GetProperty(field).GetInt64());
                Assert.Equal(OrchestratorArtifactRegistry.Entries.Count, totals.GetProperty("classes").GetInt32());
                Assert.True(totals.GetProperty("elapsed_ms").GetDouble() >= 0);
            }
            else Assert.Contains("totals\tbytes=0\tfiles=0", stdout);
            AssertNoStateCalls(repository);
            Assert.Empty(Directory.GetFileSystemEntries(root));
            Assert.False(Directory.Exists(workspace.OrchestratorDirectory));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SeededFiles_TableAndJson_ReportIdenticalValuesWithoutWrites()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Seed(workspace.OrchestratorDirectory);
            var before = Snapshot(workspace.OrchestratorDirectory);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            var table = Run(["storage-health"], workspace, repository);
            AssertUnchanged(workspace.OrchestratorDirectory, before);
            var stdout = Run(["storage-health", "--json"], workspace, repository);
            AssertUnchanged(workspace.OrchestratorDirectory, before);
            using var document = JsonDocument.Parse(stdout);
            var report = document.RootElement;
            var classes = report.GetProperty("classes").EnumerateArray().ToArray();
            Assert.Equal(OrchestratorArtifactRegistry.Entries.Count, classes.Length);
            Assert.Equal(classes.OrderByDescending(row => row.GetProperty("bytes").GetInt64())
                .ThenBy(row => row.GetProperty("pattern").GetString(), StringComparer.Ordinal)
                .Select(row => row.GetProperty("pattern").GetString()), classes.Select(row => row.GetProperty("pattern").GetString()));
            var logs = Assert.Single(classes, row => row.GetProperty("pattern").GetString() == "logs");
            AssertSize(logs, 150, 2);
            AssertSize(Assert.Single(classes, row => row.GetProperty("pattern").GetString() == "state.db"), 15, 2);
            Assert.Equal("absent", Assert.Single(classes, row => row.GetProperty("pattern").GetString() == "acceptance-gate-attempts").GetProperty("status").GetString());
            var unregistered = Assert.Single(report.GetProperty("unregistered").EnumerateArray());
            Assert.Equal("odd-dir", unregistered.GetProperty("name").GetString());
            AssertSize(unregistered, 7, 1);
            AssertSize(report.GetProperty("totals"), 172, 5);
            Assert.Equal(1, report.GetProperty("totals").GetProperty("unregistered").GetInt32());
            Assert.Equal(0, report.GetProperty("totals").GetProperty("unreadable").GetInt64());
            AssertTableMatchesJson(table, report);
            AssertNoStateCalls(repository);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Build_CustomBudgets_DistinguishesOverOkEqualAndAbsent()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Seed(workspace.OrchestratorDirectory);
            var before = Snapshot(workspace.OrchestratorDirectory);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            Run(["storage-health"], workspace, repository);
            var entries = new[] {
                OrchestratorArtifactRegistry.Classify("logs")! with { BudgetBytes = 120 },
                OrchestratorArtifactRegistry.Classify("state.db")! with { BudgetBytes = 100 },
                OrchestratorArtifactRegistry.Classify("prompts")!
            };
            var report = CliStorageHealthQueryCommand.Build(workspace.OrchestratorDirectory, entries);
            Assert.Equal(new[] { "logs", "state.db", "prompts" }, report.Classes.Select(row => row.Pattern));
            Assert.Equal(new[] { "over", "ok", "absent" }, report.Classes.Select(row => row.Status));
            Assert.Equal(1, report.Totals.Over);
            Assert.Equal(3, report.Totals.Classes);
            Assert.Equal(172, report.Totals.Bytes);
            Assert.Equal(5, report.Totals.Files);
            var equalBudget = CliStorageHealthQueryCommand.Build(workspace.OrchestratorDirectory,
                [entries[0] with { BudgetBytes = 150 }, entries[1]]);
            Assert.Equal("ok", Assert.Single(equalBudget.Classes, row => row.Pattern == "logs").Status);
            AssertUnchanged(workspace.OrchestratorDirectory, before);
            AssertNoStateCalls(repository);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Arguments_UnknownFlagThrowsWithUsage_HelpAndRetiredVerbDecline()
    {
        var error = Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["storage-health", "--bogus"]));
        Assert.Contains(CliCommandHelp.StorageHealthUsage, error.Message);
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["storage-health", "--help"]));
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["storage-health", "-h"]));
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["lane-reuse-shadow"]));
        Assert.DoesNotContain("lane-reuse-shadow", CliArgumentParser.RecognizedCommands);
    }

    [Fact]
    public void InvalidArguments_ReadOnlyRunner_PrintsUsageWithoutEffects()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            var stdout = "";
            var stderr = AsyncLocalConsoleRouter.CaptureError(() => stdout = Run(["storage-health", "--bogus"], workspace, repository, false));
            Assert.Equal("", stdout);
            Assert.Equal(CliCommandHelp.StorageHealthUsage + Environment.NewLine, stderr);
            Assert.Empty(Directory.GetFileSystemEntries(root));
            AssertNoStateCalls(repository);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void AssertTableMatchesJson(string table, JsonElement report)
    {
        var lines = table.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t')).ToArray();
        var classLines = lines.Where(fields => fields[0] == "class").ToArray();
        var classes = report.GetProperty("classes").EnumerateArray().ToArray();
        Assert.Equal(classes.Select(row => row.GetProperty("pattern").GetString()), classLines.Select(fields => fields[1]));
        foreach (var (row, fields) in classes.Zip(classLines)) AssertTokens(row, fields, 2, "pattern");
        var unknownLines = lines.Where(fields => fields[0] == "unregistered").ToArray();
        var unknown = report.GetProperty("unregistered").EnumerateArray().ToArray();
        Assert.Equal(unknown.Select(row => row.GetProperty("name").GetString()), unknownLines.Select(fields => fields[1]));
        foreach (var (row, fields) in unknown.Zip(unknownLines)) AssertTokens(row, fields, 2, "name");
        AssertTokens(report.GetProperty("totals"), Assert.Single(lines, fields => fields[0] == "totals"), 1, null);
    }

    private static void AssertTokens(JsonElement row, string[] fields, int skip, string? name)
    {
        var tokens = fields.Skip(skip).Select(field => field.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
        Assert.Equal(row.EnumerateObject().Count() - (name is null ? 0 : 1), tokens.Count);
        foreach (var property in row.EnumerateObject())
        {
            if (property.Name == name) continue;
            if (property.Name == "elapsed_ms")
            {
                Assert.True(property.Value.GetDouble() >= 0);
                Assert.True(double.Parse(tokens[property.Name], CultureInfo.InvariantCulture) >= 0);
            }
            else Assert.Equal(property.Value.ValueKind == JsonValueKind.Null ? "-" : property.Value.ToString(), tokens[property.Name]);
        }
    }

    private static void AssertSize(JsonElement row, long bytes, long files)
    {
        Assert.Equal(bytes, row.GetProperty("bytes").GetInt64());
        Assert.Equal(files, row.GetProperty("files").GetInt64());
    }

    private static void Seed(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
        Directory.CreateDirectory(Path.Combine(directory, "odd-dir"));
        File.WriteAllBytes(Path.Combine(directory, "logs", "a.log"), new byte[100]);
        File.WriteAllBytes(Path.Combine(directory, "logs", "b.log"), new byte[50]);
        File.WriteAllBytes(Path.Combine(directory, "state.db"), new byte[10]);
        File.WriteAllBytes(Path.Combine(directory, "state.db-wal"), new byte[5]);
        File.WriteAllBytes(Path.Combine(directory, "odd-dir", "x.bin"), new byte[7]);
    }

    private static Dictionary<string, (long Length, DateTime Written)> Snapshot(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path,
            path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));

    private static void AssertUnchanged(string root, Dictionary<string, (long Length, DateTime Written)> before)
    {
        var after = Snapshot(root);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, value) in before) Assert.Equal(value, after[path]);
    }

    private static string Run(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository repository, bool requireNoErrors = true)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        var stdout = "";
        void Execute() => stdout = CaptureConsole(() =>
        {
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
            Assert.False(changed);
        });
        if (requireNoErrors) Assert.Equal("", AsyncLocalConsoleRouter.CaptureError(Execute));
        else Execute();
        Assert.Null(goal);
        return stdout;
    }

    private static void AssertNoStateCalls(ProbeStateRepository repository)
    {
        Assert.Empty(repository.ObservedWriteOperationTags);
        Assert.Equal(0, repository.FullLoadAttempts + repository.LoadGoalsCount + repository.LoadGoalCount +
            repository.ListGoalMetadataCount + repository.SaveAttempts + repository.MergeSaveAttempts +
            repository.MutationAttempts + repository.ListOutboxMessagesCount + repository.OutboxClaimAttempts);
    }
}
