using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each app child owns a private root and an allow-listed environment.
public sealed class CliStatusJsonNoStoreWriteTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AppCommand_PreservesAllBytesAndCreatesOnlyReadSidecars(
        bool withLabel, bool withClarification)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var id = new GoalId("abcdef12abcdef12abcdef12abcdef12");
            kernel.CreateGoal(id, "Read-only status", [new TaskSpec(TaskId.New(), "Status task", AgentRole.Developer)]);
            if (withLabel)
            {
                var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Read-only label");
                kernel.SetGoalSourceBacklogItemId(id, item.Id);
            }
            var collaborationPath = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
            if (withClarification)
            {
                await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                    .RaiseAsync(CollaborationItemType.Clarification, id.Value, "Open clarification", "Choose a value");
                var snapshot = kernel.ExportSnapshot();
                kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
                {
                    Goals = snapshot.Goals.Select(goal => goal with { Status = GoalStatus.Active }).ToArray()
                });
            }
            else
                Assert.False(File.Exists(collaborationPath));
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            File.WriteAllText(Path.Combine(root, "other-store.db"), "unrelated store sentinel");
            var before = Snapshot(root);
            Assert.Contains(Path.GetRelativePath(root, workspace.SqliteStatePath), before.Keys);

            var result = await RunProcessAsync(root, ["status", "abcdef12", "--json"]);

            Assert.True(result.ExitCode == 0, result.StandardError);
            Assert.Single(result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            using var json = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("mcg.status", json.RootElement.GetProperty("schema").GetString());
            var payload = json.RootElement.GetProperty("payload");
            Assert.Equal(id.Value, payload.GetProperty("goal").GetString());
            Assert.Equal(withLabel ? "Read-only label" : null, payload.GetProperty("label").GetString());
            Assert.Equal(withClarification ? "Active" : "Draft", payload.GetProperty("status").GetString());
            var after = Snapshot(root);
            foreach (var (path, hash) in before)
            {
                Assert.True(after.ContainsKey(path), $"Pre-existing file removed: {path}");
                Assert.Equal(hash, after[path]);
            }
            foreach (var path in after.Keys.Except(before.Keys))
            {
                Assert.True(path.EndsWith("-wal", StringComparison.Ordinal) || path.EndsWith("-shm", StringComparison.Ordinal),
                    $"Unexpected new file: {path}");
                Assert.True(before.ContainsKey(path[..^4]) && path[..^4].EndsWith(".db", StringComparison.Ordinal),
                    $"Sidecar has no pre-existing store: {path}");
            }
            if (!withClarification) Assert.False(File.Exists(collaborationPath));
            if (!withLabel) Assert.False(File.Exists(workspace.BacklogStorePath));
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
        start.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] =
            Path.Combine(root, ".orchestrator", "test-dotnet");
        return CliChildProcessRunner.RunAsync(start);
    }

    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
}
