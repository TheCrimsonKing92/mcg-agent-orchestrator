using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Each child process has a private store/heartbeat root and a pinned provider environment.
public sealed class CliGateStatusJsonReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData("gate-status", "--json")]
    [InlineData("GATE-STATUS", "--json")]
    public void ExactJsonForm_IsQueryOnlyAndReadOnly(params string[] args)
    {
        Assert.True(CliGateStatusJsonQueryCommand.IsGateStatusJsonCommand(args));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        CliCommandHelp.ThrowIfInvalidFlags(args);
        Assert.Equal("Usage: gate-status [--json]", CliCommandHelp.GateStatusUsage);
    }

    [Theory]
    [InlineData("gate-status")]
    [InlineData("gate-status", "--json", "extra")]
    [InlineData("gate-status", "--jsonx")]
    [InlineData("gate-status", "--JSON")]
    public void OtherForms_KeepExecutionClassification(params string[] args)
    {
        Assert.False(CliGateStatusJsonQueryCommand.IsGateStatusJsonCommand(args));
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(args));
    }

    [Fact]
    public void HelpAndInvalidFlags_DoNotEnterJsonRoute()
    {
        Assert.False(CliGateStatusJsonQueryCommand.IsGateStatusJsonCommand(["gate-status", "--json", "--help"]));
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["gate-status", "--jsonx"]));
    }

    [Fact]
    public async Task AppCommand_EmitsEnvelopeWithoutChangingFilesOrCreatingStores()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            File.WriteAllText(Path.Combine(root, "other-store.db"), "unrelated store sentinel");
            var storage = new DotnetBuildStorageRoot(Path.Combine(root, ".orchestrator", "test-dotnet"));
            var now = DateTimeOffset.UtcNow;
            const string goalId = "abcdef12abcdef12abcdef12abcdef12";
            GateHeartbeatArtifacts.Write(GateHeartbeatArtifacts.GetStableSlotPath(0, storage),
                new GateHeartbeatSnapshot(goalId, "verification-check", "WorkerDispatchTests", 0, 111,
                    222, "running", now.AddSeconds(-31), now.AddSeconds(-1), now, 12, 3, 15));
            var before = Snapshot(root, allowReadSidecars: true);
            var allBefore = Snapshot(root, allowReadSidecars: false);

            var result = await RunProcessAsync(root, storage, ["gate-status", "--json"]);

            Assert.True(result.ExitCode == 0, result.StandardError);
            Assert.Single(result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            using var json = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("mcg.gate-status", json.RootElement.GetProperty("schema").GetString());
            var live = Assert.Single(json.RootElement.GetProperty("payload").GetProperty("slots").EnumerateArray(),
                slot => slot.GetProperty("slot").GetInt32() == 0);
            Assert.Equal(goalId, live.GetProperty("goal").GetString());
            Assert.Equal(30d, live.GetProperty("elapsedSeconds").GetDouble());
            AssertSnapshotsEqual(before, Snapshot(root, allowReadSidecars: true));
            var allAfter = Snapshot(root, allowReadSidecars: false);
            foreach (var (path, hash) in allBefore)
            {
                Assert.True(allAfter.ContainsKey(path), $"Pre-existing file removed: {path}");
                Assert.Equal(hash, allAfter[path]);
            }
            foreach (var path in allAfter.Keys.Except(allBefore.Keys))
            {
                Assert.True(path.EndsWith("-wal", StringComparison.Ordinal) || path.EndsWith("-shm", StringComparison.Ordinal),
                    $"Unexpected new file: {path}");
                Assert.True(allBefore.ContainsKey(path[..^4]) && path[..^4].EndsWith(".db", StringComparison.Ordinal),
                    $"Sidecar has no pre-existing store: {path}");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task<CliChildProcessResult> RunProcessAsync(string root, DotnetBuildStorageRoot storage, string[] args)
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
        start.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = storage.RootPath;
        return CliChildProcessRunner.RunAsync(start);
    }

    private static Dictionary<string, string> Snapshot(string root, bool allowReadSidecars) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !allowReadSidecars || !((path.EndsWith(".db-wal", StringComparison.Ordinal) ||
                path.EndsWith(".db-shm", StringComparison.Ordinal)) && File.Exists(path[..^4])))
            .ToDictionary(path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static void AssertSnapshotsEqual(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        Assert.Equal(before.Keys.OrderBy(key => key, StringComparer.Ordinal), after.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var (path, hash) in before) Assert.Equal(hash, after[path]);
    }
}
