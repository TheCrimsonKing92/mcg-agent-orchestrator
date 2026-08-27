using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

public abstract class GoalAcceptanceVerifierDotnetBuildSlotTests : GoalAcceptanceVerifierTestBase
{
    private protected static void SetPartitionVerdictKeyHooks(string candidateTreeSha, string mainSha, string verifyingCommitSha)
    {
        GoalAcceptanceVerifier.ResolvePartitionVerdictCandidateTreeShaForTests = _ => candidateTreeSha;
        GoalAcceptanceVerifier.ResolvePartitionVerdictMainShaForTests = _ => mainSha;
        GoalAcceptanceVerifier.ResolvePartitionVerdictVerifyingCommitShaForTests = _ => verifyingCommitSha;
    }

    private protected static async Task<AcceptanceVerificationResult> RunTwoLaneShardScenarioAsync(
        int maxConcurrentShards,
        Func<string[], string, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> runner,
        string goalId)
    {
        var root = CreateTwoLaneShardManifestWorkspace(maxConcurrentShards);
        try
        {
            var verifier = new GoalAcceptanceVerifier(runner);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            return await verifier.RunAsync(
                root,
                new GoalId(goalId),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    private protected static string CreateTwoLaneShardManifestWorkspace(int maxConcurrentShards) =>
        CreateManifestWorkspace($$"""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": {{maxConcurrentShards}},
                "infrastructureTestLanes": [
                  { "name": "Alpha", "filter": "FullyQualifiedName~AlphaShardTests" },
                  {
                    "name": "Remainder",
                    "filter": "FullyQualifiedName!~AlphaShardTests&Category!=HostIntegration"
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--verbosity", "minimal"]
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);

    private protected static string CreateTwoLanePartitionVerdictManifestWorkspace() =>
        CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "infrastructureTestLanes": [
                  { "name": "Cli", "filter": "FullyQualifiedName~CliCommandTests" },
                  {
                    "name": "Remainder",
                    "filter": "FullyQualifiedName!~CliCommandTests&Category!=HostIntegration"
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--verbosity", "minimal"]
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);

    private protected static string ResolveLaneFilter(string root, string laneName) =>
        AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes
            .Single(lane => lane.Name.Equals(laneName, StringComparison.Ordinal))
            .Filter;

    private protected static string CreateRealProcessShardManifestWorkspace() =>
        CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 1,
                "infrastructureTestLanes": [
                  {
                    "name": "Real process alpha",
                    "filter": "FullyQualifiedName~ShardProbeAlphaTests"
                  }
                ],
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/Mcg.AgentOrchestrator.RealProcessShardProbe/{configuration}/Mcg.AgentOrchestrator.RealProcessShardProbe{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/Mcg.AgentOrchestrator.RealProcessShardProbe/{configuration}/Mcg.AgentOrchestrator.RealProcessShardProbe.exe",
                    "arguments": [
                      "{executable}",
                      "--no-ansi",
                      "--progress",
                      "off",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx",
                      "--report-trx-filename",
                      "{trxFileName}",
                      "--long-running",
                      "120"
                    ]
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--verbosity", "minimal"]
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);

    internal static async Task SynchronizeRealProcessShardSmokeAsync(
        string ownSignalVariable,
        string peerSignalVariable)
    {
        var ownSignalPath = Environment.GetEnvironmentVariable(ownSignalVariable);
        if (string.IsNullOrWhiteSpace(ownSignalPath))
        {
            return;
        }

        var peerSignalPath = Environment.GetEnvironmentVariable(peerSignalVariable);
        Assert.False(string.IsNullOrWhiteSpace(peerSignalPath));
        File.WriteAllText(ownSignalPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await WaitForShardSignalAsync(peerSignalPath!);
    }

    private protected static async Task WaitForShardSignalAsync(string signalPath)
    {
        if (File.Exists(signalPath))
        {
            return;
        }

        var signalDirectory = Path.GetDirectoryName(signalPath)
            ?? throw new InvalidOperationException($"Shard signal path has no directory: '{signalPath}'.");
        using var watcher = new FileSystemWatcher(signalDirectory, Path.GetFileName(signalPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite
        };
        var signalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FileSystemEventHandler onSignal = (_, _) => signalObserved.TrySetResult();
        watcher.Created += onSignal;
        watcher.Changed += onSignal;
        try
        {
            watcher.EnableRaisingEvents = true;
            if (File.Exists(signalPath))
            {
                return;
            }

            try
            {
                await signalObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                Assert.True(
                    File.Exists(signalPath),
                    $"Peer shard did not reach the real-process event gate '{signalPath}'.");
            }
        }
        finally
        {
            watcher.Created -= onSignal;
            watcher.Changed -= onSignal;
        }
    }

    private protected static async Task<GoalAcceptanceVerifier.CommandResult> RunRealShardProcessAsync(
        string[] args,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string> environmentVariables,
        Action<System.Diagnostics.Process> onStarted,
        CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = args[0],
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in args.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var (name, value) in environmentVariables)
        {
            startInfo.Environment[name] = value;
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(process.Start(), $"Failed to start real shard process '{args[0]}'.");
        process.StandardInput.Close();
        onStarted(process);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None);
            var timedOutOutput = string.Join(Environment.NewLine, await stdout, await stderr);
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new GoalAcceptanceVerifier.CommandResult(
                process.ExitCode,
                timedOutOutput,
                TimedOut: true,
                Timeout: timeout,
                Elapsed: elapsed.Elapsed);
        }

        return new GoalAcceptanceVerifier.CommandResult(
            process.ExitCode,
            string.Join(Environment.NewLine, await stdout, await stderr),
            Elapsed: elapsed.Elapsed);
    }

    private protected static int StableSlotIndex(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            path,
            @"(?:slot-|build-)(?<slot>\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success
            ? int.Parse(match.Groups["slot"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Expected build-pool path, got '{path}'.");
    }

    private protected static void ResetPartitionVerdictKeyHooks()
    {
        GoalAcceptanceVerifier.ResolvePartitionVerdictCandidateTreeShaForTests = null;
        GoalAcceptanceVerifier.ResolvePartitionVerdictMainShaForTests = null;
        GoalAcceptanceVerifier.ResolvePartitionVerdictVerifyingCommitShaForTests = null;
    }

    private protected static int CountInfrastructurePartitionTestCalls(IEnumerable<string[]> calls) =>
        calls.Count(IsInfrastructurePartitionTestCall);

    private protected static int CountChangeScopedInfrastructureTestLanes(string root) =>
        AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count(
            lane => !lane.RequiresBuildSystemChange);

    // A partition shard for Infrastructure.Tests appears as exactly one command per shard, in one of
    // two runner shapes depending on how the check was synthesized:
    //   * runner=mtp (impact-plan / policy-synthesized checks): the managed test assembly through the
    //     shared dotnet host, with a
    //     translated class filter (--filter-class / --filter-not-class). The preceding `dotnet build`
    //     call carries no class filter and is excluded.
    //   * runner=vstest (manifest-loaded checks that omit an explicit runner): `dotnet test <csproj>`
    //     with a raw `--filter`.
    // Either way there is one matching call per shard, so assertions derive counts from the lane schema.
    private protected static bool IsInfrastructurePartitionTestCall(string[] args) =>
        (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests") &&
            (args.Contains("--filter-class") || args.Contains("--filter-not-class"))) ||
        (args.Length > 2 &&
            args[0] == "dotnet" &&
            args[1] == "test" &&
            args[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj" &&
            args.Contains("--filter"));

    private protected static bool IsMtpExecutableCall(string[] args, string projectName) =>
        args.Length > 1 &&
        args[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
        args[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileNameWithoutExtension(args[1]).Equals(projectName, StringComparison.OrdinalIgnoreCase);

    private protected static void AssertArgumentPair(string[] args, string option, string value) =>
        Assert.True(HasArgumentPair(args, option, value), $"Expected {option} {value}.");

    private protected static bool HasArgumentPair(string[] args, string option, string value)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(option, StringComparison.Ordinal) &&
                args[index + 1].Equals(value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private protected static void WriteMtpTrx(string[] args)
    {
        WriteMtpTrx(args, sourcePath: null);
    }

    private protected static void WriteMtpTrx(
        string[] args,
        int? executedTestCount,
        IReadOnlyList<string>? executedTestIdentities = null)
    {
        if (executedTestCount is null)
        {
            WriteMtpTrx(args);
            return;
        }

        var resultsDirectoryIndex = Array.IndexOf(args, "--results-directory");
        var trxFileIndex = Array.IndexOf(args, "--report-trx-filename");
        Assert.True(resultsDirectoryIndex >= 0);
        Assert.True(resultsDirectoryIndex + 1 < args.Length);
        Assert.True(trxFileIndex >= 0);
        Assert.True(trxFileIndex + 1 < args.Length);
        Directory.CreateDirectory(args[resultsDirectoryIndex + 1]);
        var destinationPath = Path.Combine(args[resultsDirectoryIndex + 1], args[trxFileIndex + 1]);
        var identities = executedTestIdentities ?? [];
        var definitions = identities.Select((identity, index) =>
        {
            var separator = identity.LastIndexOf('.');
            var className = separator > 0 ? identity[..separator] : identity;
            var methodName = separator > 0 ? identity[(separator + 1)..] : "Executed";
            return new XElement(
                "UnitTest",
                new XAttribute("id", $"test-{index}"),
                new XAttribute("name", identity),
                new XElement(
                    "TestMethod",
                    new XAttribute("className", className),
                    new XAttribute("name", methodName)));
        });
        var results = identities.Select((identity, index) =>
            new XElement(
                "UnitTestResult",
                new XAttribute("testId", $"test-{index}"),
                new XAttribute("testName", identity),
                new XAttribute("outcome", "Passed")));
        new XDocument(
            new XElement(
                "TestRun",
                new XElement("TestDefinitions", definitions),
                new XElement("Results", results),
                new XElement(
                    "ResultSummary",
                    new XAttribute("outcome", "Completed"),
                    new XElement(
                        "Counters",
                        new XAttribute("total", Math.Max(1, executedTestCount.Value)),
                        new XAttribute("executed", executedTestCount.Value),
                        new XAttribute("passed", executedTestCount.Value),
                        new XAttribute("failed", 0),
                        new XAttribute("notExecuted", 0)))))
            .Save(destinationPath);
    }

    private protected static void WriteMtpTrx(string[] args, string? sourcePath)
    {
        var resultsDirectoryIndex = Array.IndexOf(args, "--results-directory");
        var trxFileIndex = Array.IndexOf(args, "--report-trx-filename");
        Assert.True(resultsDirectoryIndex >= 0);
        Assert.True(resultsDirectoryIndex + 1 < args.Length);
        Assert.True(trxFileIndex >= 0);
        Assert.True(trxFileIndex + 1 < args.Length);
        Directory.CreateDirectory(args[resultsDirectoryIndex + 1]);
        var destinationPath = Path.Combine(args[resultsDirectoryIndex + 1], args[trxFileIndex + 1]);
        if (sourcePath is null)
        {
            File.WriteAllText(destinationPath, "<TestRun />");
        }
        else
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private protected static string MtpFailureFixturePath() =>
        Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "TestData",
            "Fixtures",
            "mtp-xunit-v3-failures.trx.xml");

    private protected static void CorruptPartitionCacheKey(string root, string partitionId)
    {
        var path = Path.Combine(
            root,
            ".orchestrator",
            "goal-operations",
            "12345678123456781234567812345678.jsonl");
        var lines = SharedJsonlFile.ReadAllLines(path);
        var updated = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var record = JsonNode.Parse(lines[index])?.AsObject()
                ?? throw new InvalidOperationException("Expected partition verdict journal entry.");
            if (!string.Equals(record["partitionId"]?.GetValue<string>(), partitionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            record["partitionFilterHash"] = "stale";
            record["partitionVerdictCacheKey"] = record["partitionVerdictCacheKey"]?.GetValue<string>() + "-stale";
            lines[index] = record.ToJsonString();
            updated = true;
            break;
        }

        if (!updated)
        {
            throw new InvalidOperationException("Expected partition verdict journal record.");
        }

        var payload = Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, lines) + Environment.NewLine);
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1);
        stream.Write(payload);
    }

    private protected static void AssertIsolatedTestCommand(string[] args)
    {
        Assert.Equal("dotnet", args[0]);
        Assert.Equal("test", args[1]);
        Assert.True(args.Any(arg => arg.Equals("--artifacts-path", StringComparison.Ordinal)));
        Assert.False(args.Any(arg => arg.Equals("--disable-build-servers", StringComparison.Ordinal)));
        Assert.False(args.Any(arg => arg.Equals("-p:UseSharedCompilation=false", StringComparison.Ordinal)));
        Assert.True(args.Any(arg => arg.StartsWith("-maxcpucount:", StringComparison.Ordinal) && !arg.Equals("-maxcpucount:1", StringComparison.Ordinal)));
        var isolatedRoot = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        Assert.False(string.IsNullOrWhiteSpace(isolatedRoot));
        Assert.True(
            Path.GetFullPath(GetArtifactsPath(args)).StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(isolatedRoot)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    private protected static string GetArtifactsPath(string[] args)
    {
        var artifactsPathIndex = Array.IndexOf(args, "--artifacts-path");
        Assert.True(artifactsPathIndex >= 0);
        Assert.True(artifactsPathIndex + 1 < args.Length);
        return args[artifactsPathIndex + 1];
    }

    private protected static void WriteProjectArtifacts(string artifactsPath, string project, string content)
    {
        var projectName = Path.GetFileNameWithoutExtension(project);
        var binPath = Path.Combine(artifactsPath, "bin", projectName, "debug_net10.0");
        var objPath = Path.Combine(artifactsPath, "obj", projectName, "debug_net10.0");
        Directory.CreateDirectory(binPath);
        Directory.CreateDirectory(objPath);
        File.WriteAllText(Path.Combine(binPath, "cache.txt"), content);
        File.WriteAllText(Path.Combine(objPath, "cache.obj"), content);
    }

    private protected static void TryDeleteStableSlotHeartbeat(int slotIndex)
    {
        var stablePath = GateHeartbeatArtifacts.GetStableSlotPath(slotIndex);
        try { File.Delete(stablePath); } catch { }
        try
        {
            var directory = Path.GetDirectoryName(stablePath) ?? ".";
            var pattern =
                $"{Path.GetFileNameWithoutExtension(stablePath)}-*{Path.GetExtension(stablePath)}";
            foreach (var path in Directory.EnumerateFiles(directory, pattern))
            {
                try { File.Delete(path); } catch { }
            }
        }
        catch { }
    }

    private protected static System.Diagnostics.Process StartSleepProcess()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "powershell";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        }
        else
        {
            startInfo.FileName = "sleep";
            startInfo.ArgumentList.Add("30");
        }

        return System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start sleep process.");
    }

    private protected static string CreateStandardManifestWorkspace() =>
        CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] },
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "full dotnet tests", "type": "dotnet-test", "project": "Mcg.AgentOrchestrator.sln", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);

    private protected static string CreateCheckedInManifestShapeWorkspace() =>
        CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] },
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "dashboard tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj", "arguments": ["--verbosity", "minimal", "--filter-not-trait", "Category=HostIntegration"] }
              ],
              "forbiddenChangedPathGlobs": [
                "bin/**",
                "obj/**",
                ".scratch/**",
                ".orchestrator-prototype/**",
                "TestResults/**",
                "playwright-report/**"
              ]
            }
            """);

    private protected static string CreateExtractedProjectManifestWorkspace() =>
        CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] },
                { "name": "core tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "provider environment tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "cli tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);

    private protected static string CreatePartitionedInfrastructureManifestWorkspace() =>
        CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);

}
