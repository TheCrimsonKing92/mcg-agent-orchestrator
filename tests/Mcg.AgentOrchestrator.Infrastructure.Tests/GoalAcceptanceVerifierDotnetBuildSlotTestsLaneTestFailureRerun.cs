using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// The JobAccounting collection isolates stable build-slot leases; runners never launch a process.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsLaneTestFailureRerun : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Theory]
    [Xunit.InlineData("flake")]
    [Xunit.InlineData("confirmed-failure")]
    public async Task RunOwnedAsync_FailedLane_RetainsOrderedRunsAndFinalVerdict(string scenario)
    {
        var root = CreateTwoLaneShardManifestWorkspace(maxConcurrentShards: 2);
        var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["checks"]![0]!["runner"] = "mtp";
        manifest["engine"]!["mtpInvocations"] = JsonNode.Parse("""
            [{
              "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
              "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
              "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
              "arguments": ["{executable}", "--results-directory", "{resultsDirectory}",
                            "--report-trx-filename", "{trxFileName}"]
            }]
            """);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
        SetPartitionVerdictKeyHooks("tree-lane-rerun", "main-lane-rerun", "commit-lane-rerun");
        var calls = new ConcurrentQueue<(string Lane, string Trx, string Executable, string Worktree)>();
        var alphaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remainderRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var alphaRuns = 0;
        var buildCalls = 0;
        const string goal = "77777777777777777777777777777777";
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> Runner(
                string[] args, string worktree, CancellationToken token)
            {
                if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
                {
                    Interlocked.Increment(ref buildCalls);
                    var artifacts = GetArtifactsPath(args);
                    var output = Path.Combine(artifacts, "bin", "Mcg.AgentOrchestrator.Infrastructure.Tests", "debug");
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "Mcg.AgentOrchestrator.Infrastructure.Tests.dll"), "fake assembly");
                    File.WriteAllText(Path.Combine(output, "Mcg.AgentOrchestrator.Infrastructure.Tests" +
                        (OperatingSystem.IsWindows() ? ".exe" : string.Empty)), "fake apphost");
                    return new(0, "Build succeeded.");
                }

                if (!IsInfrastructurePartitionTestCall(args))
                    return new(0, string.Empty);
                var alpha = HasArgumentPair(args, "--filter-class", "*AlphaShardTests*");
                var run = alpha ? Interlocked.Increment(ref alphaRuns) : 1;
                if (!alpha)
                    await alphaStarted.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
                var trx = Path.Combine(args[Array.IndexOf(args, "--results-directory") + 1],
                    args[Array.IndexOf(args, "--report-trx-filename") + 1]);
                calls.Enqueue((alpha ? $"Alpha-{run}" : "Remainder-1", trx, args[1], worktree));
                if (alpha && run == 1)
                {
                    alphaStarted.SetResult();
                    await remainderRecorded.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
                }
                else if (!alpha)
                {
                    remainderRecorded.SetResult();
                }

                var fails = alpha && (run == 1 || scenario != "flake");
                WriteMtpTrx(args, fails ? MtpFailureFixturePath() : null);
                return new(fails ? 1 : 0, fails ? "Fixture test failure." : "Passed: 1");
            }

            AcceptanceVerificationResult? result = null;
            var console = await AsyncLocalConsoleRouter.Out.CaptureLocalAsync(async () =>
            {
                var verifier = new GoalAcceptanceVerifier(TestOverrides, Runner);
                using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                    TimeSpan.FromSeconds(60));
                result = await verifier.RunOwnedAsync(root, new GoalId(goal), changedFiles: null,
                    stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath), stableSlotLease: lease,
                    CancellationToken.None, new AcceptanceRunExecutionOptions());
            });

            Assert.NotNull(result);
            Assert.Equal(["Alpha-1", "Remainder-1", "Alpha-2"], calls.Select(c => c.Lane));
            Assert.Equal(1, buildCalls);
            var alphaCalls = calls.Where(c => c.Lane.StartsWith("Alpha-", StringComparison.Ordinal)).ToArray();
            Assert.Equal(alphaCalls[0].Executable, alphaCalls[1].Executable);
            Assert.Equal(alphaCalls[0].Worktree, alphaCalls[1].Worktree);
            Assert.NotEqual(alphaCalls[0].Trx, alphaCalls[1].Trx);
            var lane = Assert.Single(result.Checks!, c => c.Name == "infrastructure tests: Alpha");
            Assert.True(Assert.Single(result.Checks!, c => c.Name == "infrastructure tests: Remainder").Passed);
            var ledger = new AcceptanceLaneFlakeLedger(root);
            Assert.Equal(scenario == "flake", result.Passed);
            Assert.Equal(scenario == "flake", lane.Passed);
            var evidence = Assert.IsType<AcceptanceLaneRerunEvidence>(lane.LaneRerun);
            Assert.Equal(scenario, evidence.Outcome);
            var firstTrx = Assert.Single(evidence.FirstTestResultPaths);
            var rerunTrx = Assert.Single(evidence.RerunTestResultPaths);
            Assert.Equal(Path.GetFileName(alphaCalls[0].Trx), Path.GetFileName(firstTrx));
            Assert.Equal(Path.GetFileName(alphaCalls[1].Trx), Path.GetFileName(rerunTrx));
            Assert.NotEqual(firstTrx, rerunTrx);
            Assert.True(File.Exists(firstTrx), "First-run failure receipt was not retained.");
            Assert.True(File.Exists(rerunTrx), "Rerun receipt was not retained.");
            Assert.Equal(evidence.RerunTestResultPaths, lane.TestResultPaths);
            Assert.Equal(evidence.RerunFailingTestIdentities, lane.FailingTestIdentities);
            Assert.NotEmpty(evidence.FirstFailingTestIdentities);
            Assert.Equal("failing-trx", evidence.FirstPredicate);
            Assert.NotEqual(evidence.FirstInvocationId, evidence.RerunInvocationId);
            Assert.Contains("first_run=failed(failing-trx)", lane.ResultSummary);
            Assert.Contains(scenario == "flake" ? "rerun=passed" : "rerun=failed", lane.ResultSummary);
            if (scenario == "confirmed-failure")
                Assert.NotEmpty(evidence.RerunFailingTestIdentities);
            Assert.Contains(rerunTrx, result.TestResultPaths!);
            Assert.DoesNotContain(firstTrx, result.TestResultPaths!);
            var row = Assert.Single(ledger.Read(lane.Name));
            Assert.Equal(goal, row.GoalId);
            Assert.Equal(lane.TestResultAttemptId, row.AttemptId);
            Assert.Equal("tree-lane-rerun", row.CandidateTreeSha);
            Assert.Equal(scenario, row.Outcome);
            Assert.Equal(evidence.FirstInvocationId, row.FirstInvocationId);
            Assert.Equal(evidence.FirstTestResultPaths, row.FirstTestResultPaths);
            Assert.Equal(evidence.FirstFailingTestIdentities, row.FirstFailingTestIdentities);
            Assert.Equal(evidence.RerunInvocationId, row.RerunInvocationId);
            Assert.Equal(evidence.RerunTestResultPaths, row.RerunTestResultPaths);
            Assert.Equal(evidence.RerunFailingTestIdentities, row.RerunFailingTestIdentities);
            Assert.Contains($"LANE_RERUN lane=\"{lane.Name}\" outcome={scenario} " +
                $"first_invocation={evidence.FirstInvocationId} rerun_invocation={evidence.RerunInvocationId}", console);
        }
        finally
        {
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }
}
