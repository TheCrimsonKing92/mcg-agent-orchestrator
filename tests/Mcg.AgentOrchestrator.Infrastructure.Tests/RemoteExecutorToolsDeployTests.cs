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
        var deploy = Create((args, directory, bound, token) =>
        {
            calls.Add(args);
            Assert.Equal("C:/fixture", directory.Replace('\\', '/'));
            Assert.Equal(TimeSpan.FromMinutes(2), bound);
            Assert.Equal(CancellationToken.None, token);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                "{\"previous\":\"A\",\"backup\":\"tools.bak-x\",\"readback\":\"B\"}"));
        });
        var result = deploy.Deploy(Entry, Source, "tools.bak-x");
        Assert.Equal(new ExecutorToolsDeployResult("deployed", null, "A", "B", "tools.bak-x", null), result);
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes", "-r", "tools-staging-B", "admin:D:/runner/" }, calls[0]);
        Assert.Equal(new[] { SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "admin",
            "powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand" }, calls[1][..^1]);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(calls[1][^1]));
        foreach (var text in new[] { "'tools-staging-B'", "'tools.bak-x'", "$expected = 'B'", "runner-state.json",
            "Get-ExecutorToolsDigest.ps1", "Set-ScriptSet $bak $tools", "[IO.File]::WriteAllBytes($manifest, $original)",
            "$files.Name -notcontains $file.Name", "finally", "Remove-Item -LiteralPath $staging -Recurse -Force" })
            Assert.Contains(text, script);
        Assert.True(script.IndexOf("if ($staged -ne $expected)", StringComparison.Ordinal) <
            script.IndexOf("Copy-Item -LiteralPath $tools", StringComparison.Ordinal));
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
        var deploy = Create((_, _, _, _) => Task.FromResult(++calls == 1
            ? new GoalAcceptanceVerifier.CommandResult(0, "")
            : new GoalAcceptanceVerifier.CommandResult(exit, output, timedOut) { Stderr = "fixture-error" }));
        var result = deploy.Deploy(Entry, Source, "tools.bak-x");
        Assert.Equal(2, calls);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Equal("fixture-error", result.ErrorTail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyFailureNeverRunsSsh(bool timeout)
    {
        var calls = 0;
        var deploy = Create((_, _, _, _) => { calls++; return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "", timeout)); });
        Assert.Equal("copy-failed", deploy.Deploy(Entry, Source, "tools.bak-x").Reason);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void ThrownTransportMapsToTransportError(int throwAt, bool asynchronous)
    {
        var calls = 0;
        var deploy = Create((_, _, _, _) => ++calls != throwAt
            ? Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""))
            : asynchronous ? Task.FromException<GoalAcceptanceVerifier.CommandResult>(new IOException("fixture")) : throw new IOException("fixture"));
        Assert.Equal("transport-error", deploy.Deploy(Entry, Source, "tools.bak-x").Reason);
        Assert.Equal(throwAt, calls);
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
