using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;

public sealed class CliOwnerDigestCommandTests
{
    [Fact]
    public async Task VerbPrintsTableAndJsonWithoutWritingStores()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var before = HashFiles(fixture.Workspace.OrchestratorDirectory);
        var walBefore = HashWalFiles(fixture.Workspace.OrchestratorDirectory);
        var table = await RunAsync(fixture, false);
        Assert.Equal(0, table.ExitCode);
        Assert.Contains("Goal | Landed UTC", table.Output, StringComparison.Ordinal);
        Assert.Contains("correct=1 escapes=1", table.Output, StringComparison.Ordinal);
        Assert.Contains("Non-landed goals with interventions in window: 1", table.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, table.Error);

        var json = await RunAsync(fixture, true);
        Assert.Equal(0, json.ExitCode);
        using var parsed = JsonDocument.Parse(json.Output);
        Assert.Equal(2, parsed.RootElement.GetProperty("totals").GetProperty("landedGoals").GetInt32());
        Assert.Equal(3, parsed.RootElement.GetProperty("totals").GetProperty("interventions").GetProperty("total").GetInt32());
        Assert.Equal("not tracked", parsed.RootElement.GetProperty("reverts").GetString());
        Assert.Equal(string.Empty, json.Error);
        Assert.Equal(before, HashFiles(fixture.Workspace.OrchestratorDirectory));
        foreach (var (path, hash) in HashWalFiles(fixture.Workspace.OrchestratorDirectory))
            Assert.True(hash == EmptyFileHash ||
                (walBefore.TryGetValue(path, out var original) && hash == original),
                $"SQLite WAL file changed and is not empty: {path}");
    }

    private static readonly string EmptyFileHash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));

    private static Dictionary<string, string> HashFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> HashWalFiles(string root) =>
        Directory.EnumerateFiles(root, "*-wal", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.OrdinalIgnoreCase);

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        OwnerDigestTestFixture fixture, bool json)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = fixture.Workspace.RootDirectory
        };
        start.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
        start.ArgumentList.Add("owner-digest");
        start.ArgumentList.Add("--since");
        start.ArgumentList.Add(OwnerDigestTestFixture.Start.ToString("O"));
        start.ArgumentList.Add("--until");
        start.ArgumentList.Add(OwnerDigestTestFixture.End.ToString("O"));
        if (json) start.ArgumentList.Add("--json");
        start.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = fixture.Workspace.RootDirectory;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start owner digest process.");
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }
}
