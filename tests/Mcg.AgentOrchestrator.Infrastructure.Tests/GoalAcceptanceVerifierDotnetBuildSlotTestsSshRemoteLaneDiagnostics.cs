using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsSshRemoteLaneDiagnostics : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutorFailureCapturesTailBeforeLocalVerdict(bool localRed)
    {
        using var scenario = CreateScenario();
        scenario.FailedStatus = true;
        scenario.RedLane = localRed ? 0 : null;
        scenario.Error = "executor-checkout-failed\r\n  " + new string('e', 300);
        var result = await scenario.RunAsync();
        Assert.Equal(!localRed, result.Passed);
        Assert.Contains(0, scenario.LocalStarts);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.Unreachable, row.Outcome);
        Assert.Equal("executor", row.FaultOwner);
        var attempt = Assert.IsType<RemoteLaneAttemptDetail>(row.Attempt);
        AssertSubmitAndPoll(attempt, "failed");
        Assert.Equal(scenario.Error, attempt.LastStatus!.Value.GetProperty("error").GetString());
        Assert.Contains(scenario.Error, attempt.ExceptionMessage);
        var call = Assert.Single(scenario.Calls.Where(call => call.Arguments.Any(arg => arg.EndsWith("/runner.log", StringComparison.Ordinal))));
        var destination = call.Arguments[^1];
        Assert.Equal(Path.GetFileName(destination), destination);
        Assert.DoesNotContain('\\', destination);
        Assert.DoesNotContain('/', destination);
        Assert.InRange(destination.Length, 1, 16);
        Assert.Equal(TimeSpan.FromSeconds(30), call.Bound);
        Assert.Equal(Path.Combine(call.Directory, "runner-tail.log"), attempt.RunnerLogPath);
        Assert.Equal(Scenario.RunnerLines.Skip(300), File.ReadAllLines(attempt.RunnerLogPath!));
        Assert.InRange(new FileInfo(attempt.RunnerLogPath!).Length, 1L, 32L * 1024);
        Assert.Empty(Directory.GetFiles(call.Directory, "*.part"));
        AssertEvents(scenario);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("timeout")]
    [InlineData("exception")]
    public async Task RunnerCaptureFailurePreservesOutcomeAndFallback(string failure)
    {
        using var scenario = CreateScenario();
        scenario.FailedStatus = true;
        scenario.RunnerLogFailure = failure == "exit";
        scenario.RunnerLogTimeout = failure == "timeout";
        scenario.RunnerLogThrows = failure == "exception";
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Contains(0, scenario.LocalStarts);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.Unreachable, row.Outcome);
        Assert.Equal("executor", row.FaultOwner);
        var attempt = Assert.IsType<RemoteLaneAttemptDetail>(row.Attempt);
        Assert.Null(attempt.RunnerLogPath);
        var step = Assert.Single(attempt.Steps.Where(step => step.Name == "runner-log"));
        if (failure == "exit") Assert.Equal(1, step.ExitCode);
        if (failure == "timeout") Assert.True(step.TimedOut);
        if (failure == "exception") Assert.Contains("runner capture exception", step.StderrTail);
        Assert.True(File.Exists(step.StdoutPath));
        Assert.True(File.Exists(step.StderrPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(step.StdoutPath!)!, "*.part"));
        AssertEvents(scenario);
    }

    [Fact]
    public async Task AcceptedAttemptKeepsFetchTimelineWithoutRunnerCapture()
    {
        using var scenario = CreateScenario();
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.DoesNotContain(0, scenario.LocalStarts);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
        Assert.Null(row.FaultOwner);
        var attempt = Assert.IsType<RemoteLaneAttemptDetail>(row.Attempt);
        AssertSubmitAndPoll(attempt, "completed");
        var fetch = Assert.Single(attempt.Fetches);
        Assert.Equal("fetch-1", fetch.Name);
        Assert.Equal(0, fetch.ExitCode);
        Assert.False(fetch.TimedOut);
        Assert.Null(attempt.RunnerLogPath);
        Assert.DoesNotContain(scenario.Calls, call => call.Arguments.Any(arg => arg.EndsWith("/runner.log", StringComparison.Ordinal)));
        AssertEvents(scenario);
    }

    [Fact]
    public async Task FailedTrxFetchKeepsTransportOutputAndFallsBack()
    {
        using var scenario = CreateScenario();
        scenario.FetchFailure = true;
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Contains(0, scenario.LocalStarts);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.Unreachable, row.Outcome);
        var fetch = Assert.Single(row.Attempt!.Fetches);
        Assert.Equal(1, fetch.ExitCode);
        Assert.Equal("fetch stderr", fetch.StderrTail);
        Assert.Equal("fetch stdout", File.ReadAllText(fetch.StdoutPath!));
        Assert.Equal("fetch stderr", File.ReadAllText(fetch.StderrPath!));
        AssertEvents(scenario);
    }

    private static void AssertSubmitAndPoll(RemoteLaneAttemptDetail attempt, string state)
    {
        Assert.Equal(new[] { "push", "job-copy", "trigger" }, attempt.Steps.Select(step => step.Name));
        Assert.All(attempt.Steps, step => Assert.Equal(0, step.ExitCode));
        Assert.Equal(1, attempt.Poll!.PollCount);
        Assert.Equal(0, attempt.Poll.FailedPollCount);
        Assert.Equal(state, attempt.Poll.LastState);
    }

    private static void AssertEvents(Scenario scenario)
    {
        var rows = scenario.Health();
        Assert.Equal(rows.Count, scenario.Events.Count);
        foreach (var pair in rows.Zip(scenario.Events))
        {
            var row = pair.First;
            var line = pair.Second;
            Assert.StartsWith("REMOTE_LANE ", line);
            Assert.Contains($"executor={row.ExecutorId}", line);
            Assert.Contains($"attempt={row.GateAttemptId}", line);
            Assert.Contains($"lane=\"{row.Lane}\"", line);
            Assert.Contains($"outcome={JsonNamingPolicy.KebabCaseLower.ConvertName(row.Outcome.ToString())}", line);
            var reason = line[(line.IndexOf("reason=", StringComparison.Ordinal) + 7)..];
            Assert.InRange(reason.Length, 1, 200);
            Assert.DoesNotMatch(@"\s{2,}|[\t\r\n]", reason);
            var expected = string.IsNullOrWhiteSpace(row.Reason) ? "none" : Regex.Replace(row.Reason, @"\s+", " ").Trim();
            Assert.Equal(expected.Length > 200 ? expected[..200] : expected, reason);
        }
    }

    private Scenario CreateScenario()
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var root = CreateManifestWorkspace($$"""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 3,
                "infrastructureTestLanes": {{JsonSerializer.Serialize(Enumerable.Range(0, 3).Select(index => new { name = $"Lane {index}", filter = Filter(index) }))}},
                "mtpInvocations": [{
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                  "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                  "arguments": ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}"]
                }]
              },
              "checks": [{
                "name": "infrastructure tests", "type": "dotnet-test", "runner": "mtp",
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "timeoutMinutes": 2
              }],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var source = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(source);
        foreach (var index in Enumerable.Range(0, 3)) File.WriteAllText(Path.Combine(source, $"Lane{index}Tests.cs"),
            $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
        var scenario = new Scenario(this, root);
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = Path.Combine(root, "executors.json");
        File.WriteAllText(TestOverrides.RemoteLaneExecutorConfigurationPathForTests, JsonSerializer.Serialize(new
        {
            executors = new[] { new { id = "fixture-executor", transport = "ssh", runnerAlias = "runner", adminAlias = "admin", remoteRepository = "C:/repo/bare.git" } },
            lanes = new[] { Lane(0) }
        }));
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        TestOverrides.RemoteLaneTransportRunnerForTests = scenario.Transport;
        TestOverrides.RemoteLaneGitRunnerForTests = scenario.Git;
        TestOverrides.OnRemoteLaneEventForTests = scenario.Events.Enqueue;
        return scenario;
    }
    private static string Lane(int index) => $"infrastructure tests: Lane {index}";
    private static string Filter(int index) => $"FullyQualifiedName~Lane{index}Tests";

    private sealed class Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsSshRemoteLaneDiagnostics owner, string root) : IDisposable
    {
        internal readonly ConcurrentQueue<int> LocalStarts = new();
        internal readonly GoalId Goal = new(Guid.NewGuid().ToString("N"));
        internal readonly ConcurrentQueue<string> Events = new();
        internal readonly ConcurrentQueue<(string[] Arguments, string Directory, TimeSpan Bound)> Calls = new();
        internal bool FailedStatus, RunnerLogFailure, RunnerLogTimeout, RunnerLogThrows, FetchFailure;
        internal string Error = "executor-checkout-failed";
        internal static string[] RunnerLines => Enumerable.Range(1, 500).Select(index => $"runner line {index}").ToArray();
        internal string Tree = "candidate-tree";
        internal int? RedLane;
        internal int Pushes, Triggers, Fetches;
        private JsonElement _job;
        internal GitCli.GitResult Git(string directory, int bound, string[] args)
        { Interlocked.Increment(ref Pushes); return new(0, "", ""); }
        internal Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] args, string directory, TimeSpan bound, CancellationToken token)
        {
            Calls.Enqueue((args, directory, bound));
            if (args[0] == SshRemoteLaneExecutor.SshPath)
            { Interlocked.Increment(ref Triggers); return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "")); }
            if (args[3].EndsWith(".json", StringComparison.Ordinal) && !args[3].Contains(':'))
            {
                using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, args[3])));
                _job = job.RootElement.Clone();
            }
            else if (args.Any(arg => arg.EndsWith("/status.json", StringComparison.Ordinal)))
            {
                var destination = Path.Combine(directory, args[^1]);
                Directory.CreateDirectory(destination);
                var fields = new[] { "attemptId", "executorId", "filterHash", "mainSha", "manifestIdentity", "lane" }
                    .ToDictionary(name => name, name => (object?)_job.GetProperty(name).GetString());
                fields["state"] = FailedStatus ? "failed" : "completed"; fields["error"] = Error; fields["commitSha"] = _job.GetProperty("sha").GetString();
                fields["treeSha"] = Tree; fields["exitCode"] = 0; fields["trx"] = new[] { "remote-lane.trx" };
                File.WriteAllText(Path.Combine(destination, "status.json"), JsonSerializer.Serialize(fields));
                File.WriteAllText(Path.Combine(destination, "heartbeat.txt"), "executor-time");
            }
            else
            {
                Interlocked.Increment(ref Fetches);
                if (args.Any(arg => arg.EndsWith("/runner.log", StringComparison.Ordinal)))
                {
                    if (RunnerLogThrows) throw new IOException("runner capture exception");
                    if (RunnerLogFailure || RunnerLogTimeout)
                        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(RunnerLogFailure ? 1 : 0,
                            "runner stdout", RunnerLogTimeout, Stderr: "runner stderr"));
                    File.WriteAllLines(Path.Combine(directory, args[^1]), RunnerLines);
                }
                else
                {
                    if (FetchFailure) return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "fetch stdout", Stderr: "fetch stderr"));
                    WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", args[^1]], 1, ["Lane0Tests.Executes"]);
                }
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
        }
        internal async Task<AcceptanceVerificationResult> RunAsync()
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var attemptId = Guid.NewGuid().ToString("N");
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            await using var executionOwner = new AcceptanceAttemptExecutionOwner(new AcceptanceAttemptIdentity(
                attemptId, Goal.Value, root, "candidate-tree", "main-sha", "verifying-commit",
                Path.Combine(root, ".orchestrator", "attempts", attemptId, "result"), slot, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(root), default);
            return await new GoalAcceptanceVerifier(owner.TestOverrides, Runner).RunOwnedAsync(root, Goal, null, slot, lease, executionOwner);
        }
        private Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                var index = Enumerable.Range(0, 3).Single(index => args.Any(argument => argument.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                LocalStarts.Enqueue(index);
                if (RedLane == index) WriteMtpTrx(args, MtpFailureFixturePath());
                else WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(RedLane == index ? 1 : 0, RedLane == index ? "Failed: 1" : "Passed: 1"));
            }
            if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
            {
                const string project = "Mcg.AgentOrchestrator.Infrastructure.Tests";
                var output = Path.Combine(GetArtifactsPath(args), "bin", project, "debug");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, project + ".dll"), "fixture");
                File.WriteAllText(Path.Combine(output, project + ".exe"), "fixture");
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        }
        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() => RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(root));
        public void Dispose()
        { DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal); DeleteDirectoryWithRetry(root); }
    }
}
