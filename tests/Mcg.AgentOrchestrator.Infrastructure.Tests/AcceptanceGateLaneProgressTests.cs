using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fake commands, unique temporary workspaces, and local progress sinks.
public sealed class AcceptanceGateLaneProgressTests : GoalAcceptanceVerifierTestBase
{
    [Fact]
    public void PassedCheck_ReportsSettledVerdictFields()
    {
        var result = new AcceptanceCheckResult("cli tests", true, 0, null,
            DurationMilliseconds: 95000, GateHeartbeatPath: "attempt.cli-tests.gate-heartbeat.json");
        var progress = new List<AcceptanceGateProgress>();

        AcceptanceGateLaneProgress.CheckCompleted(null, result, progress.Add);

        var emitted = Assert.Single(progress);
        Assert.Equal("check-complete", emitted.Phase);
        Assert.Equal("cli tests", emitted.CurrentTarget);
        Assert.Equal(TimeSpan.FromSeconds(95), emitted.Elapsed);
        Assert.Equal(result.GateHeartbeatPath, emitted.HeartbeatPath);
        Assert.Null(emitted.ChildProcessId);
        Assert.Equal(0, emitted.OutputBytes);
        Assert.Equal("passed", emitted.Outcome);
        Assert.Equal(0, emitted.FailedTestCount);
    }

    [Fact]
    public void FailedCheck_OutcomeReachesCohortConductLog()
    {
        var directory = CreateTempDirectory();
        try
        {
            var progress = new List<AcceptanceGateProgress>();
            AcceptanceGateLaneProgress.CheckCompleted(null,
                new AcceptanceCheckResult("cli tests", false, 1, null,
                    FailingTestIdentities: ["Failure.One", "Failure.Two"]), progress.Add);
            AcceptanceCohortMemberBinding Member(char digit) => new(new GoalId(new string(digit, 32)),
                new string('a', 40), new string('b', 40), ["src/A.cs"], ["resource:a"], ChangeRiskTier.Behavior,
                ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");
            var members = new[] { Member('1'), Member('2') };
            var identity = AcceptanceCohortIdentity.Create(members, new string('c', 40), new string('d', 40), "manifest-v1");
            var path = Path.Combine(directory, "conduct-events.log");
            ConductorDriver.AppendCohortGateProgressEvents(new ConductEventLogWriter(path), identity, members, Assert.Single(progress));
            var events = File.ReadAllLines(path).Select(line => OwnerConsoleGateProgress.ParseEvent(line)!).ToArray();
            Assert.Equal(2, events.Length);
            Assert.All(events, item =>
            {
                Assert.Contains("phase=check-complete", item.Detail);
                Assert.EndsWith(" outcome=failed failed=2", item.Detail);
            });
        }
        finally { DeleteDirectoryWithRetry(directory); }
    }

    [Fact]
    public void RemoteProgress_StartNamesExecutorAndCompletionNamesOutcome()
    {
        var directory = CreateTempDirectory();
        try
        {
            var storage = new DotnetBuildStorageRoot(directory);
            var progress = new List<AcceptanceGateProgress>();
            AcceptanceGateLaneProgress.RemoteStarted(null, "lane", "fixture-executor", 2, storage, progress.Add);
            var started = Assert.Single(progress);
            Assert.Equal("remote-shard-started", started.Phase);
            Assert.Equal("lane", started.CurrentTarget);
            Assert.Equal("fixture-executor", started.Executor);
            Assert.Equal(TimeSpan.Zero, started.Elapsed);
            Assert.Equal(GateHeartbeatArtifacts.GetStableSlotPath(2, storage), started.HeartbeatPath);
            Assert.Equal(" executor=fixture-executor", started.ShardOutcomeTokens);
            Assert.Null(started.Outcome);

            GoalAcceptanceVerifier.EmitShardTimingProgress(null, "remote-shard-complete", "lane", 2,
                TimeSpan.FromSeconds(10), 1, storage, progress.Add,
                new AcceptanceCheckResult("lane", false, 1, null, FailingTestIdentities: ["Failure.One"]));
            Assert.Equal(" outcome=failed failed=1", progress[1].ShardOutcomeTokens);
            Assert.Empty((started with { Phase = "verification-check", Executor = null }).ShardOutcomeTokens);
            Assert.Empty((started with { Phase = "shards-complete", Executor = null }).ShardOutcomeTokens);
        }
        finally { DeleteDirectoryWithRetry(directory); }
    }

    [Fact]
    public void SinkFailure_IsBestEffortExceptCancellation()
    {
        var result = new AcceptanceCheckResult("cli tests", true, 0, null);
        AcceptanceGateLaneProgress.CheckCompleted(null, result, _ => throw new InvalidOperationException("fixture"));
        Assert.Throws<OperationCanceledException>(() =>
            AcceptanceGateLaneProgress.CheckCompleted(null, result, _ => throw new OperationCanceledException()));
        var storage = new DotnetBuildStorageRoot(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        AcceptanceGateLaneProgress.RemoteStarted(null, "lane", "executor", 0, storage,
            _ => throw new InvalidOperationException("fixture"));
        Assert.Throws<OperationCanceledException>(() => AcceptanceGateLaneProgress.RemoteStarted(
            null, "lane", "executor", 0, storage, _ => throw new OperationCanceledException()));
    }

    [Fact]
    public async Task CommandChecks_CompleteOnceBeforePhaseBreakdown()
    {
        var root = CreateManifestWorkspace("""
            { "version": 1, "checks": [
                { "name": "first seam", "type": "command", "command": "git", "arguments": ["first"] },
                { "name": "second seam", "type": "command", "command": "git", "arguments": ["second"] }
              ], "forbiddenChangedPathGlobs": [] }
            """);
        try
        {
            var progress = new List<AcceptanceGateProgress>();
            var verifier = new GoalAcceptanceVerifier((args, _, _) => Task.FromResult(
                new GoalAcceptanceVerifier.CommandResult(args.Contains("second") ? 1 : 0, string.Empty)));
            var result = await verifier.RunOwnedAsync(root, null, null, null, null, default,
                new AcceptanceRunExecutionOptions(ProgressSink: progress.Add));

            Assert.False(result.Passed);
            var first = Assert.Single(progress, item => item.Phase == "check-complete" && item.CurrentTarget == "first seam");
            var second = Assert.Single(progress, item => item.Phase == "check-complete" && item.CurrentTarget == "second seam");
            Assert.Equal("passed", first.Outcome);
            Assert.Equal("failed", second.Outcome);
            var breakdown = Assert.Single(progress, item => item.Phase == "gate-phase-breakdown");
            Assert.True(progress.IndexOf(first) < progress.IndexOf(breakdown));
            Assert.True(progress.IndexOf(second) < progress.IndexOf(breakdown));
        }
        finally { DeleteDirectoryWithRetry(root); }
    }
}
