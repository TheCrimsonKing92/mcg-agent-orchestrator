using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildCheckFreeSlotTests
{
    [Xunit.Theory]
    [Xunit.InlineData("HashedSlotBusy", "artifacts-build-1")]
    [Xunit.InlineData("HashedSlotFree", "artifacts")]
    public async Task WorkerBuildCheckUsesFirstFreeSlotAndRecordsItsHolder(string scenario, string artifactsDirectory)
    {
        var result = await RunFixtureAsync(scenario);
        Assert.True(result.ExitCode == 0, result.Combined);
        Assert.Contains("PASS build:", result.Stdout, StringComparison.Ordinal);
        const string observationPrefix = "OBSERVED fixture: ";
        var observation = Assert.Single(result.Stdout.Split('\n')
            .Where(line => line.StartsWith(observationPrefix, StringComparison.Ordinal)));
        using var document = JsonDocument.Parse(observation[observationPrefix.Length..]);
        var observed = document.RootElement;

        Assert.Equal(0, observed.GetProperty("helperExitCode").GetInt32());
        var artifactsPath = observed.GetProperty("artifactsPath").GetString()!;
        Assert.EndsWith(Path.Combine("goals", "manual", artifactsDirectory), artifactsPath, StringComparison.OrdinalIgnoreCase);
        var holder = observed.GetProperty("holder");
        Assert.Equal(JsonValueKind.Object, holder.ValueKind);
        Assert.Equal(1, holder.GetProperty("version").GetInt32());
        Assert.Equal("goal-manual", holder.GetProperty("leaseId").GetString());
        Assert.Equal("goal-manual", holder.GetProperty("slotOwnerToken").GetString());
        Assert.True(holder.GetProperty("ownerProcessId").GetInt32() > 0);
        Assert.Equal(artifactsPath, holder.GetProperty("artifactsPath").GetString());
        Assert.False(string.IsNullOrWhiteSpace(holder.GetProperty("machineName").GetString()));
        Assert.Equal(TimeSpan.Zero, holder.GetProperty("acquiredAt").GetDateTimeOffset().Offset);
        Assert.True(observed.GetProperty("canonicalReceiptExists").GetBoolean());
        Assert.Equal("success", observed.GetProperty("receipt").GetProperty("buildOutcome").GetString());
        Assert.True(observed.GetProperty("holderRemoved").GetBoolean());
        if (scenario == "HashedSlotFree")
            Assert.True(observed.GetProperty("lockReacquired").GetBoolean());
    }

    private static async Task<ProcessResult> RunFixtureAsync(string scenario)
    {
        var repositoryRoot = FindRepositoryRoot();
        var fixturePath = Path.Combine(repositoryRoot, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "Fixtures", "WorkerBuildCheckFreeSlot", "Invoke-Fixture.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", fixturePath, "-Scenario", scenario
        })
            startInfo.ArgumentList.Add(argument);

        // Hang-only guard exceeds the fixture's per-child guard; no elapsed-time verdict.
        var capture = await Task.Run(() => TestChildProcessCapture.Run(
            startInfo, hangBound: TimeSpan.FromSeconds(300), start: info =>
            {
                var process = Process.Start(info);
                process?.StandardInput.Close();
                return process;
            }));
        return new ProcessResult(capture.ExitCode, capture.Stdout, capture.Stderr);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
    {
        public string Combined => Stdout + Environment.NewLine + Stderr;
    }
}
