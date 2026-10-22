using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildCheckRunHoldTests
{
    [Fact]
    public async Task SuccessfulBuild_PublishesPendingHold_WithTreeHeadAndOwner()
    {
        var result = await RunFixtureAsync();
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("PASS build:", result.Stdout);
        const string prefix = "OBSERVED fixture: ";
        var line = Assert.Single(result.Stdout.Split('\n').Where(l => l.StartsWith(prefix, StringComparison.Ordinal)));
        using var document = JsonDocument.Parse(line[prefix.Length..]);
        var observed = document.RootElement;
        var file = Assert.Single(observed.GetProperty("holdFiles").EnumerateArray());
        Assert.Matches(@"^build-slot-holds\.jsonl\.pending-[a-f0-9]{32}\.jsonl$", file.GetProperty("name").GetString()!);
        var content = file.GetProperty("content").GetString()!;
        Assert.Single(content.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var entry = JsonDocument.Parse(content);
        var r = entry.RootElement;
        Assert.Equal("LEASE_HELD", r.GetProperty("event").GetString());
        Assert.Equal("released", r.GetProperty("decision").GetString());
        Assert.Equal("goal-manual", r.GetProperty("leaseId").GetString());
        Assert.Equal("build-0", r.GetProperty("slot").GetString());
        var reason = r.GetProperty("reason").GetString()!;
        Assert.StartsWith("holder=worker-build-check:goal-manual:run-", reason);
        Assert.Matches(@"tree=wbc1:git-tree:[a-f0-9]{40}(?: |$)", reason);
        Assert.Contains("head=" + observed.GetProperty("fixtureHead").GetString(), reason);
        Assert.Contains("configuration=Debug outcome=success", reason);
        Assert.Matches(@"acquiredAt=\S+ heldMs=\d+ waitMs=\d+", reason);
        Assert.Equal(observed.GetProperty("holder").GetProperty("holderLabel").GetString(), reason.Split(' ')[0][7..]);
        Assert.Equal(observed.GetProperty("holder").GetProperty("ownerProcessId").GetInt32(), r.GetProperty("processId").GetInt32());
        Assert.True(file.GetProperty("runExists").GetBoolean());
        Assert.Contains("worker-build-logs", r.GetProperty("triggerPath").GetString()!);
        var log = Assert.Single(file.GetProperty("logs").EnumerateArray()).GetString()!;
        Assert.Contains("Compilation request One (net10.0), PathToTool=x", log);
        Assert.Contains("Time Elapsed 00:00:01.50", log);
        Assert.True(observed.GetProperty("canonicalReceiptExists").GetBoolean());
        Assert.Equal("success", observed.GetProperty("receipt").GetProperty("buildOutcome").GetString());
        Assert.True(observed.GetProperty("holderRemoved").GetBoolean());
        Assert.True(observed.GetProperty("lockReacquired").GetBoolean());
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunFixtureAsync()
    {
        var root = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable, WorkingDirectory = root,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Fixtures", "WorkerBuildCheckRunHold", "Invoke-Fixture.ps1") })
            startInfo.ArgumentList.Add(argument);
        // Hang-only launcher bound must exceed the fixture's 270 s child guard plus setup under gate load.
        var capture = await Task.Run(() => TestChildProcessCapture.Run(startInfo,
            hangBound: TimeSpan.FromSeconds(600), start: info =>
            {
                var process = Process.Start(info);
                process?.StandardInput.Close();
                return process;
            }));
        return (capture.ExitCode, capture.Stdout, capture.Stderr);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root)) return root;
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
