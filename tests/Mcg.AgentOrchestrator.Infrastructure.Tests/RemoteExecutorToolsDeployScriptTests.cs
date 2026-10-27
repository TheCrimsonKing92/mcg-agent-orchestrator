using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Real PowerShell contract. Every process, file, and environment value is fixture-owned.
[Collection("IsolatedProcessSpawning")]
public sealed class RemoteExecutorToolsDeployScriptTests
{
    [Fact]
    public async Task ActualScriptVerifiesSwapRetainsBackupAndPreservesUnrelatedFiles()
    {
        using var fixture = new Fixture();
        var before = ExecutorToolsDigest.Compute(fixture.Tools);
        var expected = fixture.Expected;
        var result = await fixture.Deploy();
        Assert.Equal(0, result.Exit);
        Assert.Null(result.Error);
        Assert.Equal(expected, result.Readback);
        Assert.Equal(expected, ExecutorToolsDigest.Compute(fixture.Tools));
        Assert.Equal(before, ExecutorToolsDigest.Compute(fixture.Backup));
        Assert.Equal(expected, ExecutorToolsDigest.ReadManifest(File.ReadAllText(fixture.Manifest)));
        Assert.False(File.Exists(Path.Combine(fixture.Tools, "old.ps1")));
        Assert.True(File.Exists(Path.Combine(fixture.Tools, "candidate.ps1")));
        fixture.AssertCleanAndUnrelated();
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task FreshRunnerStateRefusesSwapWithoutTouchingJobs(string state)
    {
        using var fixture = new Fixture();
        var statePath = Path.Combine(fixture.Root, "runner-state.json");
        var text = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["utc"] = DateTimeOffset.UtcNow.ToString("O"), ["queued"] = state == "queued" ? new[] { "job" } : [],
            ["running"] = state == "running" ? new[] { "job" } : []
        });
        File.WriteAllText(statePath, text);
        var before = ExecutorToolsDigest.Compute(fixture.Tools);
        var result = await fixture.Deploy();
        Assert.Equal("runner-busy", result.Error);
        Assert.Equal(before, ExecutorToolsDigest.Compute(fixture.Tools));
        Assert.Equal(fixture.OriginalManifest, File.ReadAllText(fixture.Manifest));
        Assert.Equal(text, File.ReadAllText(statePath));
        Assert.False(Directory.Exists(fixture.Backup));
        fixture.AssertCleanAndUnrelated();
    }

    [Theory]
    [InlineData("staged-digest-mismatch")]
    [InlineData("backup-exists")]
    public async Task AdmissionFailureLeavesLiveToolsAndAuthoritativeManifestIntact(string error)
    {
        using var fixture = new Fixture();
        if (error == "backup-exists")
        {
            Directory.CreateDirectory(fixture.Backup);
            File.WriteAllText(Path.Combine(fixture.Backup, "operator.txt"), "retained");
        }
        var before = ExecutorToolsDigest.Compute(fixture.Tools);
        var result = await fixture.Deploy(error == "staged-digest-mismatch" ? new string('0', 64) : null);
        Assert.Equal(error, result.Error);
        Assert.Equal(before, ExecutorToolsDigest.Compute(fixture.Tools));
        Assert.Equal(fixture.OriginalManifest, File.ReadAllText(fixture.Manifest));
        if (error == "backup-exists") Assert.Equal("retained", File.ReadAllText(Path.Combine(fixture.Backup, "operator.txt")));
        else Assert.False(Directory.Exists(fixture.Backup));
        fixture.AssertCleanAndUnrelated();
    }

    [Fact]
    public async Task InstalledDigestFailureRestoresExactScriptsAndManifestAndRetainsBackup()
    {
        using var fixture = new Fixture();
        File.AppendAllText(Path.Combine(fixture.Staging, "Get-ExecutorToolsDigest.ps1"),
            "\r\nif ((Split-Path $Path -Leaf) -eq 'tools') { throw 'fixture installed readback failure' }\r\n");
        var before = ExecutorToolsDigest.Compute(fixture.Tools);
        var result = await fixture.Deploy();
        Assert.Equal("swap-failed", result.Error);
        Assert.Equal("tools.bak-fixture", result.Backup);
        Assert.Equal(before, ExecutorToolsDigest.Compute(fixture.Tools));
        Assert.Equal(before, ExecutorToolsDigest.Compute(fixture.Backup));
        Assert.Equal(fixture.OriginalManifest, File.ReadAllText(fixture.Manifest));
        Assert.False(File.Exists(Path.Combine(fixture.Tools, "candidate.ps1")));
        fixture.AssertCleanAndUnrelated();
    }

    [Fact]
    public async Task RollbackFailureReportsRecoveryBackupWithoutAdvertisingSuccess()
    {
        using var fixture = new Fixture();
        // Fault injection is confined to this fixture's child PowerShell session.
        File.AppendAllText(Path.Combine(fixture.Staging, "Get-ExecutorToolsDigest.ps1"),
            "\r\nif ((Split-Path $Path -Leaf) -eq 'tools') { function global:Copy-Item { throw 'fixture rollback failure' }; throw 'fixture readback failure' }\r\n");
        var before = ExecutorToolsDigest.Compute(fixture.Tools);
        var result = await fixture.Deploy();
        Assert.Equal("rollback-failed", result.Error);
        Assert.Null(result.Readback);
        Assert.Equal("tools.bak-fixture", result.Backup);
        Assert.Equal(before, ExecutorToolsDigest.Compute(fixture.Backup));
        fixture.AssertCleanAndUnrelated();
    }

    [Theory]
    [InlineData("../tools")]
    [InlineData("tools")]
    [InlineData("D:/outside")]
    [InlineData("tools-deploy-other-attempt")]
    public async Task CleanupRejectsNonAttemptPathsWithoutDeletingSentinels(string attempt)
    {
        using var fixture = new Fixture();
        var result = await fixture.RunCleanup(attempt);
        Assert.NotEqual(0, result.Exit);
        Assert.True(Directory.Exists(fixture.Staging));
        Assert.True(Directory.Exists(fixture.Tools));
        fixture.AssertUnrelated();
    }

    [Fact]
    public async Task CleanupRejectsReparsePointAndPreservesItsTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Container, "outside");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "sentinel.txt");
        File.WriteAllText(sentinel, "outside");
        var junction = Path.Combine(fixture.Staging, "link");
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = $"/c mklink /J \"{junction}\" \"{target}\""
        };
        var link = await Execute(start);
        Assert.True(link.Exit == 0, link.Output + link.Stderr);
        try
        {
            var result = await fixture.RunCleanup(fixture.Attempt);
            Assert.NotEqual(0, result.Exit);
            Assert.Contains("unsafe-link", result.Stderr);
            Assert.Equal("outside", File.ReadAllText(sentinel));
            Assert.True(Directory.Exists(fixture.Staging));
            fixture.AssertUnrelated();
        }
        finally { Directory.Delete(junction); }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Container { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal string Root { get; }
        internal string Attempt { get; } = "tools-deploy-" + Guid.NewGuid().ToString("N");
        internal string Staging => Path.Combine(Root, Attempt + ".staging");
        internal string Tools => Path.Combine(Root, "tools");
        internal string Backup => Path.Combine(Root, "tools.bak-fixture");
        internal string Manifest => Path.Combine(Root, "tools-digest.json");
        internal string OriginalManifest { get; } = "{\"digest\":\"" + new string('a', 64) + "\"}\r\n";
        internal string Expected => ExecutorToolsDigest.Compute(Staging)!;
        private string ScriptPath => Path.Combine(Root, Attempt + ".ps1");
        internal Fixture()
        {
            Root = Path.Combine(Container, "executor root 検証");
            Directory.CreateDirectory(Tools);
            Directory.CreateDirectory(Staging);
            var digestScript = Path.Combine(VerifiedRepositoryRoot.Find(), "scripts", "executor", "tools", "Get-ExecutorToolsDigest.ps1");
            File.Copy(digestScript, Path.Combine(Tools, "Get-ExecutorToolsDigest.ps1"));
            File.Copy(digestScript, Path.Combine(Staging, "Get-ExecutorToolsDigest.ps1"));
            File.WriteAllText(Path.Combine(Tools, "old.ps1"), "# old bytes\r\n");
            File.WriteAllText(Path.Combine(Staging, "candidate.ps1"), "# committed candidate fixture\r\n");
            File.WriteAllText(Manifest, OriginalManifest);
            Directory.CreateDirectory(Path.Combine(Root, "queue"));
            File.WriteAllText(Path.Combine(Root, "queue", "job.json"), "existing-job");
            File.WriteAllText(Path.Combine(Tools, "unrelated.txt"), "retained");
            File.WriteAllText(Path.Combine(Root, "tools.bak-operator.txt"), "retained");
            Directory.CreateDirectory(Path.Combine(Root, "tools-deploy-" + new string('f', 32) + ".staging"));
            File.WriteAllText(Path.Combine(Root, "tools-deploy-" + new string('f', 32) + ".staging", "other.txt"), "retained");
        }
        internal async Task<(int Exit, string? Error, string? Readback, string? Backup)> Deploy(string? expected = null)
        {
            File.WriteAllText(ScriptPath, RemoteExecutorToolsDeploy.Script(Root, Attempt, expected ?? Expected, "tools.bak-fixture"), new UTF8Encoding(true));
            var result = await RunScript(ScriptPath);
            Assert.True(result.Exit == 0, result.Stderr);
            using var json = JsonDocument.Parse(result.Output.Trim().TrimStart('\uFEFF'));
            string? Read(string key) => json.RootElement.TryGetProperty(key, out var value) ? value.GetString() : null;
            return (result.Exit, Read("error"), Read("readback"), Read("backup"));
        }
        internal Task<(int Exit, string Output, string Stderr)> RunCleanup(string attempt)
        {
            var path = Path.Combine(Container, "cleanup.ps1");
            File.WriteAllText(path, RemoteExecutorToolsDeploy.CleanupScript(Root, attempt), new UTF8Encoding(true));
            return RunScript(path);
        }
        private Task<(int Exit, string Output, string Stderr)> RunScript(string path)
        {
            var start = new ProcessStartInfo(TestPowerShell.Executable)
            {
                WorkingDirectory = Container, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            string[] allow = ["COMSPEC", "LOCALAPPDATA", "PATH", "PATHEXT", "PROGRAMDATA", "PROGRAMFILES",
                "PROGRAMFILES(X86)", "SYSTEMDRIVE", "SYSTEMROOT", "TEMP", "TMP", "WINDIR"];
            var required = start.Environment.Where(item => allow.Contains(item.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
            start.Environment.Clear();
            foreach (var item in required) start.Environment[item.Key] = item.Value;
            start.Environment["USERPROFILE"] = Container;
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path })
                start.ArgumentList.Add(arg);
            return Execute(start);
        }
        internal void AssertCleanAndUnrelated()
        {
            Assert.False(Directory.Exists(Staging));
            Assert.False(File.Exists(ScriptPath));
            AssertUnrelated();
        }
        internal void AssertUnrelated()
        {
            Assert.Equal("existing-job", File.ReadAllText(Path.Combine(Root, "queue", "job.json")));
            Assert.Equal("retained", File.ReadAllText(Path.Combine(Tools, "unrelated.txt")));
            Assert.Equal("retained", File.ReadAllText(Path.Combine(Root, "tools.bak-operator.txt")));
            Assert.Equal("retained", File.ReadAllText(Path.Combine(Root, "tools-deploy-" + new string('f', 32) + ".staging", "other.txt")));
        }
        public void Dispose() => Directory.Delete(Container, true);
    }

    private static async Task<(int Exit, string Output, string Stderr)> Execute(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell contract process did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Missing event: deployment script process exit.");
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}
