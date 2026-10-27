using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteExecutorToolsDeployTests
{
    private static readonly RemoteLaneExecutorEntry Entry = new("one", 60, "ssh", "runner", "admin", RunRoot: "D:/runner");
    private static readonly ExecutorToolsSource Source = new(new string('a', 40), "C:/fixture/tools-staging-B", "B");

    [Fact]
    public void DeployUsesAdminCopyThenBoundedSwapAndReturnsReceipt()
    {
        var calls = new List<string[]>();
        var directories = new List<string>();
        var bounds = new List<TimeSpan>();
        byte[]? uploaded = null;
        var deploy = Create((args, directory, bound, token) =>
        {
            calls.Add(args);
            directories.Add(directory);
            bounds.Add(bound);
            if (calls.Count == 2) uploaded = File.ReadAllBytes(Path.Combine(directory, args[^2]));
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                "{\"previous\":\"A\",\"backup\":\"tools.bak-x\",\"readback\":\"B\"}"));
        });
        var result = deploy.Deploy(Entry, Source, "tools.bak-x");
        Assert.Equal(new ExecutorToolsDeployResult("deployed", null, "A", "B", "tools.bak-x", null), result);
        Assert.Equal(4, calls.Count);
        var name = calls[1][^2];
        var attempt = Path.GetFileNameWithoutExtension(name);
        Assert.Matches("^tools-deploy-[a-f0-9]{32}$", attempt);
        Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes", "-r", Source.Folder, $"admin:D:/runner/{attempt}.staging" }, calls[0]);
        Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes", name, $"admin:D:/runner/{name}" }, calls[1]);
        Assert.Equal(new[] { SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "admin",
            "powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", $"\"D:/runner/{name}\"" }, calls[2]);
        Assert.Equal(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(
            RemoteExecutorToolsDeploy.Script(Entry.RunRoot!, attempt, Source.Digest, "tools.bak-x"))), uploaded!);
        var script = Encoding.UTF8.GetString(uploaded!);
        foreach (var text in new[] { "'tools.bak-x'", "$expected = 'B'", "runner-state.json",
            "Get-ExecutorToolsDigest.ps1", "Set-ScriptSet $bak $tools", "[IO.File]::WriteAllBytes($manifest, $original)",
            "$files.Name -notcontains $file.Name", "finally", "Remove-Attempt" })
            Assert.Contains(text, script);
        Assert.True(script.IndexOf("if ($staged -ne $expected)", StringComparison.Ordinal) <
            script.IndexOf("Copy-Item -LiteralPath $tools", StringComparison.Ordinal));
        Assert.Equal(RemoteExecutorToolsDeploy.CleanupScript(Entry.RunRoot!, attempt),
            Encoding.Unicode.GetString(Convert.FromBase64String(calls[3][^1])));
        Assert.Equal(new[] { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30) }, bounds);
        Assert.False(Directory.Exists(directories[0]));
    }

    [Theory]
    [InlineData("D:/runner")]
    [InlineData("D:/Executor roots/検証 runner")]
    public void ActualUploadedScriptUsesBoundedProductionCommands(string runRoot)
    {
        if (!OperatingSystem.IsWindows()) return;
        var commands = new List<string[]>();
        var composed = new List<string>();
        var deploy = Create((args, directory, _, _) =>
        {
            commands.Add(args);
            composed.Add(GoalAcceptanceVerifier.BuildAcceptanceProcessStartInfo(args, directory,
                @"\\.\pipe\mcg-acceptance-stdout-01234567890123456789012345678901",
                @"\\.\pipe\mcg-acceptance-stderr-01234567890123456789012345678901").Arguments);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "{\"readback\":\"B\"}"));
        });
        var result = deploy.Deploy(Entry with { RunRoot = runRoot }, Source with { Folder = "C:/Main tools/検証/tools-staging-B" }, "tools.bak-x");
        Assert.Equal("deployed", result.Outcome); // Fails against the former oversized inline script transport.
        Assert.Equal(4, composed.Count);
        Assert.All(composed, command => Assert.InRange(command.Length, 1, 8191));
        Assert.Contains(SshRemoteLaneExecutor.ScpPath.Replace("\"", "\\\"", StringComparison.Ordinal), composed[0]);
        Assert.Contains("-File", commands[2]);
        Assert.DoesNotContain("-EncodedCommand", commands[2]);
    }

    [Theory]
    [InlineData(0, false, "{\"readback\":\"C\"}", "readback-mismatch")]
    [InlineData(0, false, "{\"error\":\"runner-busy\"}", "remote-runner-busy")]
    [InlineData(0, false, "{\"error\":\"staged-digest-mismatch\"}", "remote-staged-digest-mismatch")]
    [InlineData(0, false, "{\"error\":\"backup-exists\"}", "remote-backup-exists")]
    [InlineData(0, false, "{\"error\":\"swap-failed\"}", "remote-swap-failed")]
    [InlineData(255, false, "{}", "unreachable")]
    [InlineData(0, false, "", "unreachable")]
    [InlineData(-1, true, "", "timed-out")]
    [InlineData(1, false, "{}", "remote-exit-1")]
    [InlineData(0, false, "not-json", "output-unparsable")]
    [InlineData(0, false, "{}", "output-unparsable")]
    public void SwapFailureSignalsRetainSpecificReasons(int exit, bool timedOut, string output, string reason)
    {
        var calls = 0;
        var deploy = Create((_, _, _, _) => Task.FromResult(++calls == 3
            ? new GoalAcceptanceVerifier.CommandResult(exit, output, timedOut) { Stderr = "fixture-error" }
            : new GoalAcceptanceVerifier.CommandResult(0, "")));
        var result = deploy.Deploy(Entry, Source, "tools.bak-x");
        Assert.Equal(4, calls);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Equal("fixture-error", result.ErrorTail);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void CopyFailureNeverRunsSwapAndCleansPartialUpload(int failAt, bool timeout)
    {
        var calls = new List<string[]>();
        var deploy = Create((args, _, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(calls.Count == failAt ? 1 : 0, "", calls.Count == failAt && timeout));
        });
        Assert.Equal(failAt == 1 ? "copy-failed" : "script-copy-failed", deploy.Deploy(Entry, Source, "tools.bak-x").Reason);
        Assert.Equal(failAt + 1, calls.Count);
        Assert.DoesNotContain(calls, args => args.Contains("-File"));
        Assert.Equal("-EncodedCommand", calls[^1][^2]);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void ThrownTransportMapsToTransportError(int throwAt, bool asynchronous)
    {
        var calls = 0;
        var deploy = Create((_, _, _, _) => ++calls != throwAt
            ? Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""))
            : asynchronous ? Task.FromException<GoalAcceptanceVerifier.CommandResult>(new IOException("fixture")) : throw new IOException("fixture"));
        Assert.Equal("transport-error", deploy.Deploy(Entry, Source, "tools.bak-x").Reason);
        Assert.Equal(throwAt + 1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupFailureCannotReportSuccessOrErasePrimaryFailure(bool primaryFailed)
    {
        var calls = 0;
        var result = Create((_, _, _, _) => Task.FromResult(++calls == 4
            ? new GoalAcceptanceVerifier.CommandResult(255, "")
            : new GoalAcceptanceVerifier.CommandResult(0, primaryFailed ? "{\"error\":\"swap-failed\",\"backup\":\"tools.bak-x\"}" : "{\"readback\":\"B\",\"backup\":\"tools.bak-x\"}")))
            .Deploy(Entry, Source, "tools.bak-x");
        Assert.Equal(4, calls);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal(primaryFailed ? "remote-swap-failed" : "cleanup-failed", result.Reason);
        Assert.Equal("tools.bak-x", result.Backup);
        Assert.Contains("cleanup-failed", result.Detail);
    }

    [Theory]
    [InlineData("D:/runner/../other")]
    [InlineData("D:/runner%SECRET%")]
    [InlineData("D:/runner\" & whoami")]
    public void UnsafeRootFailsBeforeAnyTransfer(string path)
    {
        var calls = 0;
        var result = Create((_, _, _, _) => { calls++; return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "")); })
            .Deploy(Entry with { RunRoot = path }, Source, "tools.bak-x");
        Assert.Equal("unsafe-path", result.Reason);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("failed")]
    public void ManifestReadRemovesStaleDataAndValidatesNewFile(string mode)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var digest = new string('a', 64);
            var path = Path.Combine(root, ExecutorToolsDigest.ManifestFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(new { digest }));
            var calls = new List<(string[] Args, string Directory, TimeSpan Bound, CancellationToken Token, bool Stale)>();
            var deploy = Create((args, directory, bound, token) =>
            {
                calls.Add((args, directory, bound, token, File.Exists(path)));
                if (mode != "missing") File.WriteAllText(path, mode == "invalid" ? "broken" : JsonSerializer.Serialize(new { digest }));
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(mode == "failed" ? 255 : 0, ""));
            });
            Assert.Equal(mode == "valid" ? digest : null, deploy.ReadDeployedDigest(Entry, root));
            // Assert outside the transport: the reader deliberately catches callback exceptions.
            var call = Assert.Single(calls);
            Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes", "admin:D:/runner/tools-digest.json", "tools-digest.json" }, call.Args);
            Assert.Equal(root, call.Directory);
            Assert.Equal(TimeSpan.FromSeconds(30), call.Bound);
            Assert.Equal(CancellationToken.None, call.Token);
            Assert.False(call.Stale);
        }
        finally { Directory.Delete(root, true); }
    }

    private static RemoteExecutorToolsDeploy Create(Func<string[], string, TimeSpan, CancellationToken,
        Task<GoalAcceptanceVerifier.CommandResult>> transport) => new(transport, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
}
