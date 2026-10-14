using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsWorkerBuildNoBuildBridge
{
    private const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RealHelpersReuseWorkerBuildWithoutCompilation(bool fallbackSlot)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new RealBridgeFixture();
        // Obtain the repair command from the real no-build failure and execute it verbatim.
        var missing = fixture.Test();
        AssertRejected(missing);
        var repair = Assert.Single(missing.Stdout.Split('\n').Where(line => line.StartsWith("Sanctioned build: ", StringComparison.Ordinal)));
        var repairScript = Path.Combine(fixture.Root, "repair.ps1");
        File.WriteAllText(repairScript, repair["Sanctioned build: ".Length..]);

        var lockPath = Path.Combine(fixture.IsolatedRoot, "build-slots", "build-0.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using var slot = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (fallbackSlot) slot.Lock(0, 1);
        TestChildProcessCapture.Result build;
        try { build = fixture.Shell("-File", repairScript); }
        finally { if (fallbackSlot) slot.Unlock(0, 1); }
        Assert.True(build.ExitCode == 0, build.Stdout + build.Stderr);
        Assert.Contains("PASS build:", build.Stdout, StringComparison.Ordinal);
        Assert.True(WorkerBuildReceipt.Evaluate(fixture.ReceiptPath, fixture.Root).Matches,
            "The additive receipt must remain usable by dispatch without a worker start.");
        using var receipt = JsonDocument.Parse(File.ReadAllText(fixture.ReceiptPath));
        var artifacts = receipt.RootElement.GetProperty("artifactsPath").GetString()!;
        Assert.EndsWith(fallbackSlot ? "artifacts-build-1" : "artifacts", artifacts, StringComparison.Ordinal);
        var logs = Directory.GetFiles(artifacts, "*.log", SearchOption.AllDirectories);
        Assert.NotEmpty(logs);
        var logHashes = logs.ToDictionary(path => path, Sha256);

        var first = fixture.Test();
        var second = fixture.Test();
        var firstSummary = AssertRealPass(first);
        var secondSummary = AssertRealPass(second);
        Assert.NotEqual(firstSummary.GetProperty("resultsDirectory").GetString(), secondSummary.GetProperty("resultsDirectory").GetString());
        Assert.NotEqual(firstSummary.GetProperty("retainedEvidenceDirectory").GetString(), secondSummary.GetProperty("retainedEvidenceDirectory").GetString());
        foreach (var summary in new[] { firstSummary, secondSummary })
        {
            var directory = summary.GetProperty("resultsDirectory").GetString()!;
            var retained = summary.GetProperty("retainedEvidenceDirectory").GetString()!;
            Assert.All(summary.GetProperty("trxPaths").EnumerateArray(), trx => Assert.True(File.Exists(Path.Combine(retained, Path.GetFileName(trx.GetString()!)))));
            var evidencePath = Path.Combine(retained, "run-identity.json");
            using var evidence = JsonDocument.Parse(File.ReadAllText(evidencePath));
            var selection = Assert.Single(evidence.RootElement.GetProperty("selections").EnumerateArray());
            Assert.All(evidence.RootElement.GetProperty("trx").EnumerateArray(), trx => Assert.True(trx.GetProperty("total").GetInt32() > 0));
            var assembly = selection.GetProperty("launchedAssembly").GetString()!;
            Assert.StartsWith(directory + Path.DirectorySeparatorChar, assembly, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".dll", assembly, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(artifacts + Path.DirectorySeparatorChar, selection.GetProperty("selectedDirectory").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(logs.Order(), Directory.GetFiles(artifacts, "*.log", SearchOption.AllDirectories).Order());
        foreach (var pair in logHashes) Assert.Equal(pair.Value, Sha256(pair.Key));
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing", "worker-build-receipt.json")]
    [Xunit.InlineData("malformed", "sanctioned worker output")]
    [Xunit.InlineData("legacy", "sanctioned worker output")]
    [Xunit.InlineData("source", "tree digest is stale")]
    [Xunit.InlineData("project-file", "tree digest is stale")]
    [Xunit.InlineData("referenced-source", "tree digest is stale")]
    [Xunit.InlineData("foreign", "worktree/configuration mismatch")]
    [Xunit.InlineData("configuration", "worktree/configuration mismatch")]
    [Xunit.InlineData("project", "does not cover requested project")]
    [Xunit.InlineData("escape", "outside the sanctioned goal boundary")]
    [Xunit.InlineData("properties", "unsupported output properties")]
    [Xunit.InlineData("tfm", "targetFramework")]
    [Xunit.InlineData("rid", "runtimeIdentifier")]
    [Xunit.InlineData("closure", "checksum mismatch")]
    [Xunit.InlineData("detail-missing", "build receipt is missing")]
    [Xunit.InlineData("ambiguous", "Ambiguous verified no-build outputs")]
    public void RejectsUnboundWorkerOutputBeforeLaunch(string mutation, string reason)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var receiptPath = PrepareWorkerOutput(sandbox, keepDeclared: mutation == "ambiguous");
        var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!.AsObject();
        var output = Path.Combine(receipt["artifactsPath"]!.GetValue<string>(), "bin", "Mcg.AgentOrchestrator.Infrastructure.Tests", "debug");
        var detailPath = Path.Combine(output, ".mcg-build-receipt.txt");
        switch (mutation)
        {
            case "missing": File.Delete(receiptPath); break;
            case "malformed": File.WriteAllText(receiptPath, "{"); break;
            case "legacy": receipt.Remove("artifactsPath"); receipt.Remove("buildProperties"); break;
            case "source": File.WriteAllText(Path.Combine(sandbox.Root, "Feature.cs"), "// edited"); break;
            case "project-file": File.AppendAllText(Path.Combine(sandbox.Root, Project), "\n<!-- edit -->"); break;
            case "referenced-source": File.WriteAllText(Path.Combine(sandbox.Root, "Dependency.cs"), "// edited"); break;
            case "foreign": receipt["worktreeRoot"] = sandbox.LocalApplicationDataRoot; break;
            case "configuration": receipt["configuration"] = "Release"; break;
            case "project": receipt["projects"] = new JsonArray("Other.csproj"); break;
            case "escape": receipt["artifactsPath"] = sandbox.Root; break;
            case "properties": receipt["buildProperties"]!["UseArtifactsOutput"] = "false"; break;
            case "tfm": File.WriteAllText(detailPath, File.ReadAllText(detailPath).Replace("K\ttargetFramework\tnet10.0", "K\ttargetFramework\tnet9.0", StringComparison.Ordinal)); break;
            case "rid": File.WriteAllText(detailPath, File.ReadAllText(detailPath).Replace("K\truntimeIdentifier\t", "K\truntimeIdentifier\twin-x64", StringComparison.Ordinal)); break;
            case "detail-missing": File.Delete(detailPath); break;
            case "closure": File.AppendAllText(Path.Combine(output, "receipt-source.cs"), "// changed"); break;
            case "ambiguous":
                var payload = Path.Combine(output, "receipt-source.cs");
                File.AppendAllText(payload, "// divergent closure");
                var detail = File.ReadAllText(detailPath);
                var oldHash = detail.Split('\n').Single(line => line.StartsWith($"F\tclosure\t{payload}\t", StringComparison.Ordinal)).Trim().Split('\t')[3];
                File.WriteAllText(detailPath, detail.Replace($"F\tclosure\t{payload}\t{oldHash}", $"F\tclosure\t{payload}\t{Sha256(payload)}", StringComparison.Ordinal));
                break;
        }
        if (mutation is not "missing" and not "malformed") File.WriteAllText(receiptPath, receipt.ToJsonString());
        var result = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);
        AssertRejected(new(result.ExitCode, result.Stdout, result.Stderr));
        Assert.Contains(reason, result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(sandbox.ArgumentLog), "No rejected output may reach the managed test host.");
    }

    [Xunit.Theory]
    [Xunit.InlineData("closure", "Selected no-build closure changed before copying")]
    [Xunit.InlineData("receipt", "Selected worker build receipt changed before copying")]
    [Xunit.InlineData("source", "Selected worker build identity changed while copying")]
    public void SnapshotRejectsIdentityChangedAfterSelection(string mutation, string reason)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        PrepareWorkerOutput(sandbox);
        var script = Path.Combine(sandbox.LocalApplicationDataRoot, "copy.ps1");
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module '{{Quote(Path.Combine(sandbox.Root, "scripts", "MtpTestRunner.psm1"))}}' -Force
            $module = Get-Module MtpTestRunner
            & $module {
                $root = '{{Quote(sandbox.Root)}}'
                $run = '{{Quote(sandbox.ResultsRoot)}}'
                $invocation = [pscustomobject]@{ project = '{{Project}}'; executablePathTemplate = 'bin/{projectName}/{configuration}/{projectName}{executableExtension}' }
                $selection = Select-MtpVerifiedBuildOutput -RepositoryRoot $root -Invocation $invocation -Configuration Debug -DotnetPath '{{Quote(sandbox.RunnerPath)}}' -RunDirectory $run
                switch ('{{mutation}}') {
                    'closure' { Add-Content -LiteralPath (Join-Path $selection.Directory 'receipt-source.cs') -Value 'changed after selection' }
                    'receipt' { Add-Content -LiteralPath $selection.WorkerProvenance.Path -Value 'changed after selection' }
                    'source' { Add-Content -LiteralPath (Join-Path $root 'Feature.cs') -Value 'changed after selection' }
                }
                try {
                    Copy-MtpStableNoBuildClosure -SourceDirectory $selection.Directory -PublishedDirectory (Join-Path $run 'copied') -ManagedAssemblyLeaf 'Mcg.AgentOrchestrator.Infrastructure.Tests.dll' -ProjectName 'Mcg.AgentOrchestrator.Infrastructure.Tests' -BuildSelection $selection -RepositoryRoot $root -RunDirectory $run
                    throw 'Copy unexpectedly authorized replaced closure.'
                }
                catch {
                    if ($_.Exception.Message -notlike '*{{reason}}*') { throw }
                    Write-Output 'REJECTED selected closure replacement'
                }
                if (Test-Path (Join-Path $run 'copied')) { throw 'Rejected closure was published.' }
            }
            """);
        var start = sandbox.SandboxPowerShellStartInfo();
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        var result = TestChildProcessCapture.Run(start);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("REJECTED selected closure replacement", result.Stdout, StringComparison.Ordinal);
    }

    private static string PrepareWorkerOutput(MtpTestRunnerScriptTests.ScriptSandbox sandbox, bool keepDeclared = false)
    {
        var assembly = sandbox.CreateManagedAssemblyPlaceholder();
        var declared = Path.GetDirectoryName(assembly)!;
        var marker = System.Text.RegularExpressions.Regex.Match(sandbox.Root.Replace('\\', '/'), @"/\.orchestrator-worktrees/([^/]+)");
        var goal = marker.Success ? marker.Groups[1].Value : "manual";
        var artifacts = Path.Combine(sandbox.LocalApplicationDataRoot, "worker-builds", "goals", goal, "artifacts");
        var output = Path.Combine(artifacts, "bin", "Mcg.AgentOrchestrator.Infrastructure.Tests", "debug");
        Directory.CreateDirectory(output);
        foreach (var file in Directory.GetFiles(declared)) File.Copy(file, Path.Combine(output, Path.GetFileName(file)));
        var detailPath = Path.Combine(output, ".mcg-build-receipt.txt");
        var detail = File.ReadAllText(detailPath);
        var sourceLine = detail.Split('\n').Single(line => line.StartsWith("S\tsource\t", StringComparison.Ordinal));
        detail = detail.Replace(declared + Path.DirectorySeparatorChar, output + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        var copiedSourceLine = detail.Split('\n').Single(line => line.StartsWith("S\tsource\t", StringComparison.Ordinal));
        detail = detail.Replace(copiedSourceLine, sourceLine, StringComparison.Ordinal);
        File.WriteAllText(detailPath, detail);
        if (!keepDeclared)
            foreach (var file in Directory.GetFiles(declared).Where(file => Path.GetFileName(file) != "receipt-source.cs")) File.Delete(file);

        var runner = Path.Combine(sandbox.Root, "scripts", "FakeRunner.ps1");
        var script = File.ReadAllText(runner);
        var beforePayload = "    $open = [char]123";
        Assert.Contains(beforePayload, script, StringComparison.Ordinal);
        script = script.Replace(beforePayload, $$"""
                $workerProperty = @($Arguments | Where-Object { $_ -like '-p:ArtifactsPath=*' })
                if ($workerProperty.Count -eq 1) {
                    $targetPath = Join-Path $workerProperty[0].Substring('-p:ArtifactsPath='.Length) 'bin\Mcg.AgentOrchestrator.Infrastructure.Tests\debug\Mcg.AgentOrchestrator.Infrastructure.Tests.dll'
                }
            {{beforePayload}}
            """, StringComparison.Ordinal);
        File.WriteAllText(runner, script);
        File.WriteAllText(Path.Combine(sandbox.Root, ".gitignore"), "bin/\nrunner-arguments.txt*\n");
        File.WriteAllText(Path.Combine(sandbox.Root, "Feature.cs"), "// feature");
        File.WriteAllText(Path.Combine(sandbox.Root, "Dependency.cs"), "// dependency");
        InitializeGit(sandbox.Root);
        Assert.True(WorktreeTreeDigest.TryCompute(sandbox.Root, out var digest, out var failure), failure);
        var receiptPath = Path.Combine(artifacts, "worker-build-receipt.json");
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, treeDigest = digest, digestAlgorithm = "wbc1:git-tree", buildOutcome = "success",
            generatedAtUtc = DateTimeOffset.UtcNow, worktreeRoot = sandbox.Root, configuration = "Debug", projects = new[] { Project },
            artifactsPath = artifacts, buildProperties = new { UseArtifactsOutput = "true", ArtifactsPath = artifacts, BuildInParallel = "false" }
        }));
        return receiptPath;
    }

    private static JsonElement AssertRealPass(TestChildProcessCapture.Result result)
    {
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("NO-BUILD BUILD RECEIPT SELECTED - sanctioned worker output", result.Stdout, StringComparison.Ordinal);
        var summary = Summary(result);
        Assert.NotEmpty(summary.GetProperty("executedTestNames").EnumerateArray());
        Assert.NotEmpty(summary.GetProperty("trxPaths").EnumerateArray());
        return summary;
    }

    private static void AssertRejected(TestChildProcessCapture.Result result)
    {
        Assert.True(result.ExitCode == 24, result.Stdout + result.Stderr);
        var summary = Summary(result);
        Assert.Empty(summary.GetProperty("executedTestNames").EnumerateArray());
        Assert.Empty(summary.GetProperty("trxPaths").EnumerateArray());
    }

    private static JsonElement Summary(TestChildProcessCapture.Result result)
    {
        const string prefix = "MTP_TERMINAL_SUMMARY ";
        var line = Assert.Single(result.Stdout.Split('\n').Where(line => line.StartsWith(prefix, StringComparison.Ordinal)));
        using var document = JsonDocument.Parse(line[prefix.Length..]);
        return document.RootElement.Clone();
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string Sha256(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private static void InitializeGit(string root)
    {
        foreach (var args in new[]
        {
            new[] { "init", "--initial-branch=fixture" }, new[] { "add", "-A" },
            new[] { "-c", "user.name=Bridge Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=", "commit", "-m", "fixture" }
        })
        {
            var start = StartInfo(root, "git", args);
            foreach (var key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" }) start.Environment.Remove(key);
            var result = TestChildProcessCapture.Run(start);
            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        }
    }

    private static ProcessStartInfo StartInfo(string root, string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private sealed class RealBridgeFixture : IDisposable
    {
        private readonly string _parent;
        private readonly string _resultsRoot;
        public string Root { get; }
        public string IsolatedRoot { get; }
        public string ReceiptPath => Path.Combine(IsolatedRoot, "goals", "manual", "artifacts", "worker-build-receipt.json");

        public RealBridgeFixture()
        {
            // A private Git repo outside the goal worktree makes 'manual' slot choice explicit.
            _parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "mcg-bridge-" + Guid.NewGuid().ToString("N"));
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // Use the same required Low results boundary as the real managed helper.
            _resultsRoot = Path.Combine(localAppData, "Temp", "Low", "mcg-tests", "bridge-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(_parent, "repo");
            IsolatedRoot = Path.Combine(_parent, "outputs");
            Directory.CreateDirectory(Path.Combine(Root, "scripts"));
            Directory.CreateDirectory(Path.Combine(Root, Path.GetDirectoryName(Project)!));
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            var candidate = MtpTestRunnerScriptTests.RepositoryRoot();
            foreach (var file in new[] { "Invoke-WorkerBuildCheck.ps1", "WorkerBuildReceipt.psm1", "MtpTestRunner.psm1", "Invoke-TestSummary.ps1" })
                File.Copy(Path.Combine(candidate, "scripts", file), Path.Combine(Root, "scripts", file));
            foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets", "global.json" })
                File.Copy(Path.Combine(candidate, file), Path.Combine(Root, file));
            var manager = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs";
            Directory.CreateDirectory(Path.Combine(Root, Path.GetDirectoryName(manager)!));
            File.Copy(Path.Combine(candidate, manager), Path.Combine(Root, manager));
            File.WriteAllText(Path.Combine(Root, Project), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject><OutputType>Exe</OutputType><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup>
                  <ItemGroup><PackageReference Include="xunit.v3.mtp-v2" Version="3.2.2" /><PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.3.2" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(Root, Path.GetDirectoryName(Project)!, "BridgeCanary.cs"), "public class BridgeCanary { [Xunit.Fact] public void Executes() { Xunit.Assert.True(true); } }");
            File.WriteAllText(Path.Combine(Root, ".gitignore"), "**/bin/\n**/obj/\nrepair.ps1\n");
            File.WriteAllText(Path.Combine(Root, "config", "acceptance-manifest.json"), JsonSerializer.Serialize(new
            {
                engine = new
                {
                    infrastructureTestLanes = Array.Empty<object>(),
                    localTestPartitions = Array.Empty<object>(),
                    mtpInvocations = new[] { new
                    {
                        project = Project, executablePathTemplate = "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                        firewallExecutablePathTemplate = "bin/{projectName}/{configuration}/{projectName}.exe",
                        arguments = new[] { "{executable}", "--no-ansi", "--progress", "off", "--results-directory", "{resultsDirectory}", "--report-trx", "--report-trx-filename", "{trxFileName}" }
                    } }
                }
            }));
            InitializeGit(Root);
        }

        public TestChildProcessCapture.Result Shell(params string[] args)
        {
            var start = StartInfo(Root, WorkerShell.Executable, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", .. args]);
            start.Environment["MCG_DOTNET_ISOLATED_ROOT"] = IsolatedRoot;
            // Hang-only guard names process termination, never a performance verdict.
            return TestChildProcessCapture.Run(start, hangBound: TimeSpan.FromMinutes(2));
        }

        public TestChildProcessCapture.Result Test() => Shell("-File", Path.Combine(Root, "scripts", "Invoke-TestSummary.ps1"),
            "-Target", Project, "-NoBuild", "-Filter", "FullyQualifiedName~BridgeCanary", "-ResultsRoot", _resultsRoot);

        public void Dispose() => ScriptSandboxCleanup.DeleteOrThrow([_parent, _resultsRoot]);
    }
}
