using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierTestsGateChildReap : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string CheckName = "core tests";
    private static readonly GoalId Goal = new("c0dec0dec0dec0dec0dec0dec0dec0de");

    internal sealed class FakeGateChildReapSeam : IGateChildReapSeam, IRecordedGateChild
    {
        internal int ProcessId { get; } = 424242;
        internal bool Available { get; set; } = true;
        internal bool KillResult { get; set; } = true;
        internal bool ExitConfirmed { get; set; } = true;
        internal bool Waited { get; private set; }
        internal TimeSpan? WaitBudget { get; private set; }
        internal List<string> Events { get; } = [];

        public IRecordedGateChild? TryOpen(int processId, DateTimeOffset recordedStartedAt)
        {
            Events.Add("open");
            Assert.Equal(ProcessId, processId);
            Assert.NotEqual(default, recordedStartedAt);
            return Available ? this : null;
        }

        public bool Kill()
        {
            Events.Add("kill");
            return KillResult;
        }

        public bool WaitForExit(TimeSpan budget)
        {
            Events.Add("wait");
            Waited = true;
            WaitBudget = budget;
            return ExitConfirmed;
        }

        public void Dispose() { }
    }

    [Xunit.Fact]
    public void WithinAttemptRerunReapsRecordedChildThroughSeamBeforeSecondInvocation()
    {
        var fake = new FakeGateChildReapSeam();
        var (result, output, invocations) = RunScenario(fake, rerun: true);

        Assert.False(result.Passed);
        Assert.True(result.Retried);
        Assert.Equal(2, invocations);
        Assert.Equal(["open", "kill", "wait", "invocation-1"], fake.Events);
        Assert.Equal(TimeSpan.FromSeconds(30), fake.WaitBudget);
        Assert.Single(output.Split('\n'), line => line.Contains("GATE_CHILD_REAP check=\"core tests\" pid=424242 outcome=reaped reason=exit-confirmed", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void WithinAttemptRerunDoesNotLaunchOverStillAliveRecordedChild()
    {
        var fake = new FakeGateChildReapSeam { ExitConfirmed = false };
        var budget = TimeSpan.FromSeconds(7);
        var (result, output, invocations) = RunScenario(fake, rerun: true, budget: budget);

        Assert.False(result.Passed);
        Assert.True(result.Retried);
        Assert.Equal(1, invocations);
        Assert.Equal(budget, fake.WaitBudget);
        var line = Assert.Single(output.Split('\n'), part => part.Contains("outcome=still-alive", StringComparison.Ordinal)).Trim();
        Assert.Contains("reason=exit-unconfirmed-within-budget", line, StringComparison.Ordinal);
        Assert.Contains(result.Checks, check =>
            AcceptanceFailureClassifications.IsEnvironmentalApparatus(check.FailureClassification) &&
            check.ResultSummary?.Contains(line, StringComparison.Ordinal) == true);
    }

    [Xunit.Fact]
    public void KillHelperFailureStillConfirmsExitThroughHandleWait()
    {
        var fake = new FakeGateChildReapSeam { KillResult = false };
        var (result, output, invocations) = RunScenario(fake, rerun: true);

        Assert.Equal(2, invocations);
        Assert.Equal(["open", "kill", "wait", "invocation-1"], fake.Events);
        Assert.Contains("outcome=reaped reason=exit-confirmed", output, StringComparison.Ordinal);
        Assert.True(result.Retried);
    }

    [Xunit.Theory]
    [Xunit.InlineData("no-heartbeat")]
    [Xunit.InlineData("unreadable-heartbeat")]
    [Xunit.InlineData("no-child-pid")]
    [Xunit.InlineData("slot-mismatch")]
    [Xunit.InlineData("goal-mismatch")]
    [Xunit.InlineData("stale-snapshot")]
    [Xunit.InlineData("artifacts-path-mismatch")]
    [Xunit.InlineData("not-running")]
    public void NotApplicableReapConditionEmitsReasonAndLaunches(string reason)
    {
        var fake = new FakeGateChildReapSeam { Available = false };
        var (result, output, invocations) = RunScenario(fake, rerun: false, reason: reason);

        Assert.True(result.Passed);
        Assert.Equal(1, invocations);
        Assert.Contains($"outcome=not-applicable reason={reason}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("kill", fake.Events);
    }

    [Xunit.Fact]
    public void RecordedStartTimeRejectsReusedPid()
    {
        var heartbeatStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.False(DefaultGateChildReapSeam.IsRecordedChild(heartbeatStart.UtcDateTime.AddSeconds(1), heartbeatStart));
        Assert.True(DefaultGateChildReapSeam.IsRecordedChild(heartbeatStart.UtcDateTime.AddSeconds(-1), heartbeatStart));
    }

    private (AcceptanceVerificationResult Result, string Output, int Invocations) RunScenario(
        FakeGateChildReapSeam fake, bool rerun, TimeSpan? budget = null, string? reason = null)
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": { "maxConcurrentShards": 1, "partitionVerdictFullRerunEveryN": 1 },
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~SampleTests"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var storage = new DotnetBuildStorageRoot(Path.Combine(root, ".orchestrator", "test-dotnet"));
        var previousPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var prefix = Path.Combine(root, ".orchestrator", "reap-attempt");
        var invocations = 0;
        TestOverrides.BuildStorageRootForTests = storage;
        TestOverrides.GateChildReapSeamForTests = fake;
        TestOverrides.GateChildExitConfirmationBudget = budget;
        SetPartitionVerdictKeyHooks("tree-reap", "main-reap", "commit-reap");

        void WriteHeartbeat(string? notApplicableReason)
        {
            if (notApplicableReason == "no-heartbeat")
                return;

            var environment = DotnetBuildEnvironmentManager.ResolveGoalEnvironment(Goal, storage);
            var path = GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
                CheckName, environment, stableSlotIndex: null, attemptResultsPrefix: prefix);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (notApplicableReason == "unreadable-heartbeat")
            {
                File.WriteAllText(path, "{");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            GateHeartbeatArtifacts.Write(path, new GateHeartbeatSnapshot(
                notApplicableReason == "goal-mismatch" ? "11111111111111111111111111111111" : Goal.Value,
                "verification-check", CheckName,
                notApplicableReason == "slot-mismatch" ? 1 : null,
                fake.ProcessId,
                notApplicableReason == "no-child-pid" ? null : fake.ProcessId,
                notApplicableReason == "stale-snapshot" ? "completed" : "running",
                now,
                notApplicableReason == "stale-snapshot" ? DateTimeOffset.UnixEpoch : now,
                now, 0, 0, 0,
                $"dotnet test --artifacts-path {(notApplicableReason == "artifacts-path-mismatch" ? "other-artifacts" : environment.ArtifactsPath)}"));
        }

        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                if (arguments.Length < 2 || !arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));

                if (arguments[1].Equals("build", StringComparison.OrdinalIgnoreCase))
                {
                    if (!rerun)
                        WriteHeartbeat(reason);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                if (!arguments[1].Equals("test", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));

                var current = invocations++;
                if (rerun && current == 0)
                    WriteHeartbeat(null);
                if (current > 0)
                    fake.Events.Add($"invocation-{current}");
                if (!rerun || current > 0)
                    GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineDiscovery.WriteVstestTrx(arguments, "SampleTests.Passes");
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    rerun && current == 0 ? 1 : 0,
                    rerun && current == 0 ? "Test runner exited before producing TRX." : "Passed: 1"));
            });

            AcceptanceVerificationResult? result = null;
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, prefix);
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(root, Goal, stableSlotIndex: reason == "slot-mismatch" ? 0 : null)
                    .GetAwaiter().GetResult());
            return (Assert.IsType<AcceptanceVerificationResult>(result), output, invocations);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previousPrefix);
            PerUserGoalRootLeakProbe.DrainRegistrationReports(storage);
            DeleteDirectoryWithRetry(root);
        }
    }
}
