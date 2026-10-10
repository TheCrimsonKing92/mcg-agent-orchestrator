using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsLaneEarlyStop : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task ConfirmedFailure_StopsPendingLanesAndCancelsRunningLanes()
    {
        var root = CreateManifestWorkspace("""{"version":1,"checks":[],"forbiddenChangedPathGlobs":[]}""");
        ConfigureWorkspace(root, 4, 3);
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
        SetPartitionVerdictKeyHooks("early-stop-tree", "early-stop-main", "early-stop-commit");
        var running = Signal();
        var cancelled = new ConcurrentQueue<int>();
        var runningCount = 0;
        var starts = new ConcurrentQueue<int>();
        var confirmer = -1;
        var alphaRuns = 0;
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string _, CancellationToken token)
            {
                if (!IsInfrastructurePartitionTestCall(args)) return BuildResult(args);
                var index = LaneIndex(args, 4);
                var first = Interlocked.CompareExchange(ref confirmer, index, -1);
                if (first == -1 || index == confirmer)
                {
                    if (Interlocked.Increment(ref alphaRuns) == 1) starts.Enqueue(index);
                    await Event(running.Task, "sibling local lane started", token);
                    WriteMtpTrx(args, MtpFailureFixturePath());
                    return new(1, "Fixture test failure.");
                }
                starts.Enqueue(index);
                if (Interlocked.Increment(ref runningCount) == 2) running.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled.Enqueue(index);
                    throw;
                }
                throw new InvalidOperationException("Blocked lane returned without cancellation.");
            }

            AcceptanceVerificationResult? result = null;
            var console = await AsyncLocalConsoleRouter.Out.CaptureLocalAsync(async () =>
            {
                using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
                using var cancellation = new CancellationTokenSource();
                var gate = new GoalAcceptanceVerifier(TestOverrides, Runner).RunOwnedAsync(root, GoalId.New(), null,
                    StableSlotIndex(lease.Environment.ArtifactsPath), lease, cancellation.Token, new AcceptanceRunExecutionOptions());
                try
                {
                    await Event(gate, "gate completed after cancelling both running siblings");
                    result = await gate;
                }
                finally
                {
                    await cancellation.CancelAsync();
                    try { await Event(gate, "gate cleanup"); }
                    catch (OperationCanceledException) { }
                }
            });

            Assert.NotNull(result);
            Assert.False(result.Passed);
            Assert.Equal(2, alphaRuns);
            Assert.Equal(2, cancelled.Count);
            Assert.Equal(starts.Where(index => index != confirmer).Order(), cancelled.Order());
            Assert.Equal(3, starts.Count);
            var stopped = Assert.Single(result.Checks!, check => check.Name == AcceptanceLaneEarlyStop.ReceiptName);
            Assert.Contains($"confirming_lane=\"{Lane(confirmer)}\"", stopped.ResultSummary);
            Assert.Contains("coverage=partial lanes_stopped=3", stopped.ResultSummary);
            foreach (var index in cancelled) Assert.DoesNotContain(result.Checks!, check => check.Name == Lane(index));
            Assert.Single(console.Split('\n'), line => line.Contains("phase=lane-early-stop", StringComparison.Ordinal));
            Assert.Contains("lanes_stopped=3", console);
            // Every launched local invocation traverses the existing gate child-reap seam.
            foreach (var index in cancelled) Assert.Contains($"GATE_CHILD_REAP check=\"{Lane(index)}\"", console);
        }
        finally { ResetPartitionVerdictKeyHooks(); DeleteDirectoryWithRetry(root); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("flake")]
    [Xunit.InlineData("not-rerun-predicate")]
    [Xunit.InlineData("rerun-switch-off")]
    public async Task NonConfirmedFailure_DoesNotStopSiblings(string scenario)
    {
        var root = CreateManifestWorkspace("""{"version":1,"checks":[],"forbiddenChangedPathGlobs":[]}""");
        ConfigureWorkspace(root, 3, 2);
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = scenario != "rerun-switch-off";
        SetPartitionVerdictKeyHooks("no-stop-tree", "no-stop-main", "no-stop-commit");
        var starts = new ConcurrentQueue<int>();
        var alphaRuns = 0;
        var confirmer = -1;
        try
        {
            Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string _, CancellationToken token)
            {
                if (!IsInfrastructurePartitionTestCall(args)) return Task.FromResult(BuildResult(args));
                token.ThrowIfCancellationRequested();
                var index = LaneIndex(args, 3);
                var first = Interlocked.CompareExchange(ref confirmer, index, -1);
                var alpha = first == -1 || index == confirmer;
                var run = alpha ? Interlocked.Increment(ref alphaRuns) : 1;
                if (run == 1) starts.Enqueue(index);
                var fails = alpha && (run == 1 || scenario != "flake");
                // Missing TRX has its own apparatus-loss stop. A valid green TRX plus a nonzero
                // exit isolates a failure that carries no test-failure rerun marker.
                WriteMtpTrx(args, fails && scenario != "not-rerun-predicate" ? MtpFailureFixturePath() : null);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(fails ? 1 : 0, fails ? "Fixture failure." : "Passed: 1"));
            }

            AcceptanceVerificationResult? result = null;
            var console = await AsyncLocalConsoleRouter.Out.CaptureLocalAsync(async () =>
            {
                using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
                result = await new GoalAcceptanceVerifier(TestOverrides, Runner).RunOwnedAsync(root, GoalId.New(), null,
                    StableSlotIndex(lease.Environment.ArtifactsPath), lease, CancellationToken.None, new AcceptanceRunExecutionOptions());
            });
            Assert.NotNull(result);
            Assert.Equal(scenario == "flake", result.Passed);
            Assert.Equal(3, starts.Distinct().Count());
            foreach (var index in Enumerable.Range(0, 3))
                Assert.Single(result.Checks!, check => check.Name == Lane(index));
            var alpha = Assert.Single(result.Checks!, check => check.Name == Lane(confirmer));
            if (scenario == "flake") Assert.Equal(AcceptanceLaneRerunEvidence.Flake, alpha.LaneRerun?.Outcome);
            else Assert.Null(alpha.LaneRerun);
            Assert.DoesNotContain(result.Checks!, check => check.Name == AcceptanceLaneEarlyStop.ReceiptName);
            Assert.DoesNotContain("phase=lane-early-stop", console);
        }
        finally { ResetPartitionVerdictKeyHooks(); DeleteDirectoryWithRetry(root); }
    }

    [Xunit.Fact]
    public async Task FocusedEvidenceBatchFailure_DoesNotEmitLaneStopOrLoseSiblingVerdicts()
    {
        var root = CreateManifestWorkspace("""{"version":1,"checks":[],"forbiddenChangedPathGlobs":[]}""");
        TestOverrides.ResolveShardCoreBudgetForTests = () => 1;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
        var calls = new List<string>();
        try
        {
            Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string _, CancellationToken token)
            {
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Core.Tests"))
                {
                    calls.Add("Core");
                    WriteMtpTrx(args, 1, ["EarlyStopFocusedSiblingTests.Executes"]);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
                }
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    calls.Add("Infrastructure");
                    WriteMtpTrx(args, MtpFailureFixturePath());
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "Fixture test failure."));
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            }
            FocusedEvidenceRunResult? result = null;
            var console = await AsyncLocalConsoleRouter.Out.CaptureLocalAsync(async () =>
                result = await new GoalAcceptanceVerifier(TestOverrides, Runner).RunFocusedEvidenceAsync(root, GoalId.New(),
                    "Core.Tests: EarlyStopFocusedSiblingTests; Infrastructure.Tests: EarlyStopFocusedFailureTests"));
            Assert.NotNull(result);
            Assert.True(result.Accepted);
            Assert.False(result.Passed);
            Assert.Equal(["Core", "Infrastructure"], calls);
            Assert.Equal(2, result.Checks.Count);
            Assert.Contains(result.Checks, check => check.Passed);
            Assert.Contains(result.Checks, check => !check.Passed);
            Assert.All(result.Checks, check => Assert.Null(check.LaneRerun));
            Assert.DoesNotContain(result.Checks, check => check.Name == AcceptanceLaneEarlyStop.ReceiptName);
            Assert.DoesNotContain("phase=lane-early-stop", console);
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    internal static string Lane(int index) => $"infrastructure tests: EarlyStop{index}";
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Event(Task task, string name, CancellationToken token = default)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30), token); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
    internal static int LaneIndex(string[] args, int count) => Enumerable.Range(0, count)
        .Single(index => args.Any(arg => arg.Contains($"EarlyStop{index}Tests", StringComparison.Ordinal)));

    internal static GoalAcceptanceVerifier.CommandResult BuildResult(string[] args)
    {
        if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
        {
            const string project = "Mcg.AgentOrchestrator.Infrastructure.Tests";
            var output = Path.Combine(GetArtifactsPath(args), "bin", project, "debug");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, project + ".dll"), "fixture assembly");
            File.WriteAllText(Path.Combine(output, project + (OperatingSystem.IsWindows() ? ".exe" : "")), "fixture apphost");
        }
        return new(0, "Build succeeded.");
    }

    internal static void ConfigureWorkspace(string root, int count, int concurrency, bool remote = false)
    {
        var path = Path.Combine(root, "config", "acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        manifest["engine"]!["maxConcurrentShards"] = concurrency;
        manifest["engine"]!["infrastructureTestLanes"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, count).Select(index => new
        {
            name = $"EarlyStop{index}", filter = $"FullyQualifiedName~EarlyStop{index}Tests",
            // In the remote scenario, only lane zero is eligible for offload.
            exclusiveResourceKeys = remote && index > 0 ? new[] { "early-stop-resource-" + index } : Array.Empty<string>()
        }));
        manifest["checks"] = JsonNode.Parse("""
            [{"name":"infrastructure tests","type":"dotnet-test","runner":"mtp",
              "project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj","timeoutMinutes":2}]
            """);
        manifest["engine"]!["mtpInvocations"] = JsonNode.Parse("""
            [{"project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
              "executablePathTemplate":"bin/{projectName}/{configuration}/{projectName}{executableExtension}",
              "firewallExecutablePathTemplate":"bin/{projectName}/{configuration}/{projectName}.exe",
              "arguments":["{executable}","--results-directory","{resultsDirectory}","--report-trx-filename","{trxFileName}"]}]
            """);
        File.WriteAllText(path, manifest.ToJsonString());
        var source = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(source);
        foreach (var index in Enumerable.Range(0, count))
            File.WriteAllText(Path.Combine(source, $"EarlyStop{index}Tests.cs"),
                $"public class EarlyStop{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
    }
}
