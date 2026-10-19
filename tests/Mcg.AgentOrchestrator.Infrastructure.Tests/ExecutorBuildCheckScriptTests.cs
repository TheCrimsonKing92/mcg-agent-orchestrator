using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Real process contract. Every child environment, repository, worktree and artifact is fixture-owned.
[Collection("IsolatedProcessSpawningRemainderB")]
public sealed class ExecutorBuildCheckScriptTests : IDisposable
{
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Fact]
    public async Task Job_BuildCheck_PublishesBoundArtifactsWithoutRunningLane()
    {
        var sha = Seed();
        var result = await Run(Job(sha, build: true));
        Assert.True(result.Exit == 0, result.Error);
        var folder = Path.Combine(_root, "results", sha[..9], "worker-build-check");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "status.json")));
        var status = document.RootElement;
        Assert.Equal("completed", status.GetProperty("state").GetString());
        Assert.Equal(0, status.GetProperty("exitCode").GetInt32());
        Assert.Equal("build-check", status.GetProperty("kind").GetString());
        Assert.Equal(sha, status.GetProperty("commitSha").GetString());
        Assert.Equal(Git(Path.Combine(_root, "repo", "repo.git"), "rev-parse", sha + "^{tree}"), status.GetProperty("treeSha").GetString());
        foreach (var field in Job(sha, true))
            if (field.Key is not ("kind" or "projects")) Assert.Equal((string)field.Value, status.GetProperty(field.Key).GetString());
        var files = status.GetProperty("files").EnumerateArray().Select(f => f.GetString()!).ToArray();
        Assert.Equal(new[] { "build-output.txt", "worker-build-receipt.json" }, files);
        foreach (var file in files) Assert.True(File.Exists(Path.Combine(folder, file)), "Missing fetched build artifact: " + file);
        Assert.Contains(RemoteWorkerBuildCheckScenarioTests.Fixture.Pass, File.ReadAllText(Path.Combine(folder, "build-output.txt")));
        Assert.True(File.Exists(Path.Combine(folder, "heartbeat.txt")));
        Assert.True(status.GetProperty("phases").TryGetProperty("buildSeconds", out _));
        Assert.False(File.Exists(Path.Combine(_root, "lane-ran.json")));
        var work = Path.Combine(_root, "build", ".orchestrator-worktrees", "b-" + sha[..9]);
        Assert.Equal(sha, Git(work, "rev-parse", "HEAD"));
        Assert.Equal("HEAD", Git(work, "rev-parse", "--abbrev-ref", "HEAD"));
        // The helper stub rejects any argument list other than exactly these two project paths.
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "worker-build-receipt.json")));
        Assert.Equal(new[] { "src/Feature/Feature.csproj", "src/Other/Other.csproj" },
            receipt.RootElement.GetProperty("projects").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Job_NoKind_KeepsExistingLaneDispatch()
    {
        var sha = Seed();
        var job = Job(sha, false);
        var result = await Run(job);
        Assert.True(result.Exit == 0, result.Error);
        using var lane = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "lane-ran.json")));
        foreach (var name in new[] { "sha", "project", "filter", "lane" })
            Assert.Equal((string)job[name], lane.RootElement.GetProperty(name).GetString());
        Assert.False(Directory.Exists(Path.Combine(_root, "results")));
    }

    [Fact]
    public async Task Job_UnknownCandidate_PublishesFailureAndExitsNonzero()
    {
        Seed();
        var sha = new string('a', 40);
        var result = await Run(Job(sha, true));
        Assert.NotEqual(0, result.Exit);
        var path = Path.Combine(_root, "results", sha[..9], "worker-build-check", "status.json");
        Assert.True(File.Exists(path), result.Error);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("failed", document.RootElement.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("error").GetString()));
        Assert.False(File.Exists(Path.Combine(_root, "lane-ran.json")));
    }

    private static Dictionary<string, object> Job(string sha, bool build)
    {
        var job = new Dictionary<string, object>
        {
            ["sha"] = sha, ["project"] = "src/Feature/Feature.csproj;src/Other/Other.csproj", ["filter"] = "FullyQualifiedName~Feature|FullyQualifiedName~Other",
            ["lane"] = "worker-build-check", ["attemptId"] = "wbc-fixture", ["executorId"] = "mcg-exec-1",
            ["filterHash"] = "fixture-filter", ["mainSha"] = "", ["manifestIdentity"] = "worker-build-check:v1"
        };
        if (build) { job["kind"] = "build-check"; job["projects"] = new[] { "src/Feature/Feature.csproj", "src/Other/Other.csproj" }; }
        return job;
    }

    private string Seed()
    {
        var tools = Path.Combine(_root, "tools");
        Directory.CreateDirectory(tools);
        foreach (var name in new[] { "Invoke-ExecutorJob.ps1", "Invoke-ExecutorBuildCheck.ps1" })
        {
            var source = Path.Combine(VerifiedRepositoryRoot.Find(), "scripts", "executor", "tools", name);
            Assert.True(File.Exists(source), "Tracked executor script is required: " + name);
            File.Copy(source, Path.Combine(tools, name));
        }
        File.WriteAllText(Path.Combine(tools, "Invoke-ExecutorLane.ps1"), """
            param($Sha, $Project, $Filter, $Lane, $RunRoot, $AttemptId, $ExecutorId, $FilterHash, $MainSha, $ManifestIdentity)
            @{ sha=$Sha; project=$Project; filter=$Filter; lane=$Lane } | ConvertTo-Json -Compress |
                Set-Content -LiteralPath (Join-Path $RunRoot 'lane-ran.json')
            """);
        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(Path.Combine(seed, "scripts"));
        Git(seed, "init", "-q");
        Git(seed, "config", "user.name", "Build fixture");
        Git(seed, "config", "user.email", "fixture@mcg.invalid");
        File.WriteAllText(Path.Combine(seed, "scripts", "WorkerBuildReceipt.psm1"), """
            function Get-WorkerBuildRunRoot { param([string]$Root)
                Join-Path $env:MCG_DOTNET_ISOLATED_ROOT (Split-Path -Leaf $Root)
            }
            Export-ModuleMember -Function Get-WorkerBuildRunRoot
            """);
        File.WriteAllText(Path.Combine(seed, "scripts", "Invoke-WorkerBuildCheck.ps1"), """
            $ErrorActionPreference = 'Stop'
            if ($args.Count -ne 2 -or $args[0] -ne 'src/Feature/Feature.csproj' -or $args[1] -ne 'src/Other/Other.csproj') {
                throw 'Helper did not receive the exact build project arguments.'
            }
            Import-Module (Join-Path $PSScriptRoot 'WorkerBuildReceipt.psm1') -Force
            $root = Get-WorkerBuildRunRoot -Root (Split-Path -Parent $PSScriptRoot)
            New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts') | Out-Null
            @{ schemaVersion=1; buildOutcome='success'; projects=@($args) } | ConvertTo-Json -Compress |
                Set-Content -LiteralPath (Join-Path $root 'artifacts/worker-build-receipt.json')
            Write-Output 'PASS build: 0 errors (Invoke-WorkerBuildCheck) projects=1'
            exit 0
            """);
        Git(seed, "add", "-A");
        Git(seed, "commit", "-q", "-m", "stub build helper");
        Directory.CreateDirectory(Path.Combine(_root, "repo"));
        Git(_root, "clone", "-q", "--bare", seed, Path.Combine(_root, "repo", "repo.git"));
        return Git(seed, "rev-parse", "HEAD");
    }

    private static string Git(string root, params string[] args)
    {
        var result = GitCli.RunForRepository(root, args);
        Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
        return result.Output.Trim();
    }

    private async Task<(int Exit, string Error)> Run(Dictionary<string, object> job)
    {
        var path = Path.Combine(_root, "job.json");
        File.WriteAllText(path, JsonSerializer.Serialize(job));
        var start = new ProcessStartInfo(TestPowerShell.Executable)
        {
            WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        string[] allow = ["COMSPEC", "LOCALAPPDATA", "PATH", "PATHEXT", "PROGRAMDATA", "PROGRAMFILES",
            "PROGRAMFILES(X86)", "SYSTEMDRIVE", "SYSTEMROOT", "TEMP", "TMP", "WINDIR"];
        var required = start.Environment.Where(item => allow.Contains(item.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
        start.Environment.Clear();
        foreach (var item in required) start.Environment[item.Key] = item.Value;
        start.Environment["USERPROFILE"] = _root;
        start.Environment["MCG_DOTNET_ISOLATED_ROOT"] = Path.Combine(_root, "isolated-build");
        foreach (var arg in new[] { "-NoProfile", "-File", Path.Combine(_root, "tools", "Invoke-ExecutorJob.ps1"), "-JobPath", path, "-RunRoot", _root })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Missing event: executor job process exited");
        }
        await stdout;
        return (process.ExitCode, await stderr);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
