using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTests : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact]
    public void EmitShardTimingProgressRecordsTypedLoadContextAndPreservesExistingFields()
    {
        var progress = new List<AcceptanceGateProgress>();
        using var gateProbe = GateLoadContextProbe.PushConcurrentGateCountProbe(() => 3);
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 5);
        using var cpuProbe = GateLoadContextProbe.PushHostCpuProbe(
            () => new GateLoadContextProbe.HostCpuSample(37.5, 16, 425));
        using var sink = GoalAcceptanceVerifier.PushGateProgressSink(progress.Add);
        var goalId = new GoalId("12345678123456781234567812345678");
        var elapsed = TimeSpan.FromSeconds(12);

        GoalAcceptanceVerifier.EmitShardTimingProgress(
            goalId,
            "shard-complete",
            "Process spawning",
            1,
            elapsed,
            3);

        var emitted = Assert.Single(progress);
        Assert.Equal(goalId.Value, emitted.GoalId);
        Assert.Equal("shard-complete", emitted.Phase);
        Assert.Equal("Process spawning", emitted.CurrentTarget);
        Assert.Equal(1, emitted.SlotIndex);
        Assert.Equal(elapsed, emitted.Elapsed);
        Assert.Null(emitted.PhaseBreakdown);
        var load = Assert.IsType<GateLoadContext>(emitted.LoadContext);
        AssertAvailable(load.ConcurrentShardCount, 3);
        AssertAvailable(load.HostCpuUtilizationPercent, 37.5);
        AssertAvailable(load.ProcessorCount, 16);
        AssertAvailable(load.CpuSampleWindowMilliseconds, 425);
        AssertAvailable(load.ConcurrentGateCount, 3);
        AssertAvailable(load.InFlightPaidWorkerDispatchCount, 5);
    }

    [Xunit.Fact]
    public void EmitShardTimingProgressDistinguishesAvailableZeroFromUnavailableCounts()
    {
        var progress = new List<AcceptanceGateProgress>();
        using var gateProbe = GateLoadContextProbe.PushConcurrentGateCountProbe(() => null);
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 0);
        using var cpuProbe = GateLoadContextProbe.PushHostCpuProbe(
            () => new GateLoadContextProbe.HostCpuSample(0, 8, 250));
        using var sink = GoalAcceptanceVerifier.PushGateProgressSink(progress.Add);

        GoalAcceptanceVerifier.EmitShardTimingProgress(
            null,
            "shards-complete",
            "0-infrastructure-shards",
            0,
            TimeSpan.Zero,
            0);

        var emitted = Assert.Single(progress);
        Assert.Equal("shards-complete", emitted.Phase);
        Assert.Equal("0-infrastructure-shards", emitted.CurrentTarget);
        Assert.Equal(0, emitted.SlotIndex);
        Assert.Equal(TimeSpan.Zero, emitted.Elapsed);
        Assert.Null(emitted.PhaseBreakdown);
        var load = Assert.IsType<GateLoadContext>(emitted.LoadContext);
        AssertAvailable(load.ConcurrentShardCount, 0);
        AssertAvailable(load.InFlightPaidWorkerDispatchCount, 0);
        AssertUnavailable(load.ConcurrentGateCount, "gate-count-unavailable");
        Assert.NotEqual(load.InFlightPaidWorkerDispatchCount.IsAvailable, load.ConcurrentGateCount.IsAvailable);
        Assert.NotEqual(load.InFlightPaidWorkerDispatchCount.Value, load.ConcurrentGateCount.Value);
    }

    [Xunit.Fact]
    public void ThrowingLoadProbeDoesNotEscapeOrPreventTimingRecord()
    {
        var progress = new List<AcceptanceGateProgress>();
        using var gateProbe = GateLoadContextProbe.PushConcurrentGateCountProbe(
            () => throw new InvalidOperationException("deterministic gate probe failure"));
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 4);
        using var cpuProbe = GateLoadContextProbe.PushHostCpuProbe(
            () => new GateLoadContextProbe.HostCpuSample(25, 8, 500));
        using var sink = GoalAcceptanceVerifier.PushGateProgressSink(progress.Add);

        var exception = Record.Exception(() => GoalAcceptanceVerifier.EmitShardTimingProgress(
            null,
            "shard-complete",
            "Cli",
            0,
            TimeSpan.FromSeconds(4),
            2));

        Assert.Null(exception);
        var emitted = Assert.Single(progress);
        Assert.Equal("Cli", emitted.CurrentTarget);
        Assert.Equal(TimeSpan.FromSeconds(4), emitted.Elapsed);
        var load = Assert.IsType<GateLoadContext>(emitted.LoadContext);
        AssertAvailable(load.ConcurrentShardCount, 2);
        AssertUnavailable(load.ConcurrentGateCount, "probe-error:InvalidOperationException");
        AssertAvailable(load.InFlightPaidWorkerDispatchCount, 4);
        AssertAvailable(load.HostCpuUtilizationPercent, 25);
    }

    [Xunit.Fact]
    public void LoadCaptureInvokesEachProbeOncePerLaneCompletion()
    {
        var gateCalls = 0;
        var workerCalls = 0;
        var cpuCalls = 0;
        using var gateProbe = GateLoadContextProbe.PushConcurrentGateCountProbe(() =>
        {
            gateCalls++;
            return 1;
        });
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() =>
        {
            workerCalls++;
            return 0;
        });
        using var cpuProbe = GateLoadContextProbe.PushHostCpuProbe(() =>
        {
            cpuCalls++;
            return new GateLoadContextProbe.HostCpuSample(25, 4, 500);
        });

        var load = GateLoadContextProbe.Capture(1);

        Assert.Equal(1, gateCalls);
        Assert.Equal(1, workerCalls);
        Assert.Equal(1, cpuCalls);
        AssertAvailable(load.ConcurrentShardCount, 1);
    }

    [Xunit.Fact]
    public void CpuSamplesBelowMinimumWindowOrWithNonFiniteUtilizationAreUnavailable()
    {
        using var gateProbe = GateLoadContextProbe.PushConcurrentGateCountProbe(() => 1);
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 0);
        using (GateLoadContextProbe.PushHostCpuProbe(
            () => new GateLoadContextProbe.HostCpuSample(25, 4, 199)))
        {
            AssertUnavailable(
                GateLoadContextProbe.Capture(1).HostCpuUtilizationPercent,
                "cpu-sample-invalid");
        }

        using (GateLoadContextProbe.PushHostCpuProbe(
            () => new GateLoadContextProbe.HostCpuSample(double.NaN, 4, 500)))
        {
            AssertUnavailable(
                GateLoadContextProbe.Capture(1).HostCpuUtilizationPercent,
                "cpu-sample-invalid");
        }
    }

    [Xunit.Fact]
    public void RealHostCpuProbeReturnsFiniteSampleOrExplicitUnavailableState()
    {
        using var gateProbe = GateLoadContextProbe.PushConcurrentGateCountProbe(() => 1);
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 0);

        var load = GateLoadContextProbe.Capture(1);

        Assert.Equal(load.HostCpuUtilizationPercent.IsAvailable, load.ProcessorCount.IsAvailable);
        Assert.Equal(load.HostCpuUtilizationPercent.IsAvailable, load.CpuSampleWindowMilliseconds.IsAvailable);
        if (load.HostCpuUtilizationPercent.IsAvailable)
        {
            Assert.True(double.IsFinite(load.HostCpuUtilizationPercent.Value!.Value));
            Assert.InRange(load.HostCpuUtilizationPercent.Value.Value, 0, 100);
            Assert.True(load.CpuSampleWindowMilliseconds.Value >= 200);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(load.HostCpuUtilizationPercent.UnavailableReason));
        }
    }

    [Xunit.Fact]
    public void ProductionGateProbeIncludesCallingGateWhenHeartbeatDirectoryIsReachable()
    {
        var heartbeatDirectory = Path.GetDirectoryName(GateHeartbeatArtifacts.GetStableSlotPath(0));
        Assert.False(string.IsNullOrWhiteSpace(heartbeatDirectory));
        Directory.CreateDirectory(heartbeatDirectory!);
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 0);
        using var cpuProbe = GateLoadContextProbe.PushHostCpuProbe(
            () => new GateLoadContextProbe.HostCpuSample(25, 4, 500));

        var sample = GateLoadContextProbe.Capture(1).ConcurrentGateCount;

        Assert.True(sample.IsAvailable);
        Assert.NotNull(sample.Value);
        Assert.True(sample.Value >= 1);
        Assert.Null(sample.UnavailableReason);
    }

    [Xunit.Fact]
    public void AcceptanceGateProgressRetainsOriginalPositionalContract()
    {
        var startedAt = new DateTimeOffset(2026, 8, 21, 1, 2, 3, TimeSpan.Zero);
        var observedAt = startedAt.AddSeconds(4);
        var progressedAt = startedAt.AddSeconds(3);
        var elapsed = TimeSpan.FromSeconds(4);
        var progress = new AcceptanceGateProgress(
            "goal",
            "phase",
            "target",
            1,
            2,
            3,
            startedAt,
            observedAt,
            progressedAt,
            elapsed,
            4,
            "heartbeat");

        Assert.Equal("goal", progress.GoalId);
        Assert.Equal("phase", progress.Phase);
        Assert.Equal("target", progress.CurrentTarget);
        Assert.Equal(1, progress.SlotIndex);
        Assert.Equal(2, progress.ProcessId);
        Assert.Equal(3, progress.ChildProcessId);
        Assert.Equal(startedAt, progress.StartedAt);
        Assert.Equal(observedAt, progress.LastObservedAt);
        Assert.Equal(progressedAt, progress.LastProgressAt);
        Assert.Equal(elapsed, progress.Elapsed);
        Assert.Equal(4, progress.OutputBytes);
        Assert.Equal("heartbeat", progress.HeartbeatPath);
        Assert.Null(progress.LoadContext);
        Assert.Null(progress.PhaseBreakdown);
    }

    [Xunit.Fact]
    public void ResolveDotnetTestRunner_DerivesRunnerFromProjectDeclaration()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-runner-tests", Guid.NewGuid().ToString("N"));
        const string project = "tests/Unlisted.Tests/Unlisted.Tests.csproj";
        var projectPath = Path.Combine(root, project);
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        try
        {
            File.WriteAllText(
                projectPath,
                "<Project><PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>");
            Assert.Equal("mtp", GoalAcceptanceVerifier.ResolveDotnetTestRunner(root, project));

            File.WriteAllText(
                projectPath,
                "<Project><PropertyGroup><UseMicrosoftTestingPlatformRunner>  FaLsE  </UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>");
            Assert.Equal("vstest", GoalAcceptanceVerifier.ResolveDotnetTestRunner(root, project));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public void ResolveDotnetTestRunner_DefaultsToMtpForAmbiguousOrUnreadableDeclarations()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-runner-tests", Guid.NewGuid().ToString("N"));
        const string project = "tests/Unlisted.Tests/Unlisted.Tests.csproj";
        var projectPath = Path.Combine(root, project);
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        try
        {
            var ambiguousProjects = new[]
            {
                "<Project><PropertyGroup /></Project>",
                "<Project>",
                "<Project><PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\"><UseMicrosoftTestingPlatformRunner>false</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>",
                "<Project><PropertyGroup><UseMicrosoftTestingPlatformRunner Condition=\"'$(Configuration)' == 'Debug'\">false</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>",
                "<Project><PropertyGroup><UseMicrosoftTestingPlatformRunner>false</UseMicrosoftTestingPlatformRunner><UseMicrosoftTestingPlatformRunner>false</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>",
                "<Project><PropertyGroup><UseMicrosoftTestingPlatformRunner>false</UseMicrosoftTestingPlatformRunner><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>",
                "<Project><PropertyGroup><UseMicrosoftTestingPlatformRunner>not-a-boolean</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>",
                "<Project><Choose><When Condition=\"'$(Configuration)' == 'Debug'\"><PropertyGroup><UseMicrosoftTestingPlatformRunner>false</UseMicrosoftTestingPlatformRunner></PropertyGroup></When></Choose></Project>"
            };
            foreach (var contents in ambiguousProjects)
            {
                File.WriteAllText(projectPath, contents);
                Assert.Equal("mtp", GoalAcceptanceVerifier.ResolveDotnetTestRunner(root, project));
            }

            Assert.Equal("mtp", GoalAcceptanceVerifier.ResolveDotnetTestRunner(root, "../Outside.Tests.csproj"));
            Assert.Equal("mtp", GoalAcceptanceVerifier.ResolveDotnetTestRunner(root, "tests/Missing.Tests.csproj"));
            Assert.Equal(
                "mtp",
                GoalAcceptanceVerifier.ResolveDotnetTestRunner(
                    root,
                    project,
                    _ => throw new IOException("deterministic unreadable-project probe")));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_pre_review_attempt_namespace_cannot_collide_with_acceptance")]
    public void GoalAcceptanceVerifierPreReviewAttemptNamespaceCannotCollideWithAcceptance()
    {
        var preReview = GoalAcceptanceVerifier.OwnerResultsAttemptRoot("pre-review");
        var acceptance = GoalAcceptanceVerifier.OwnerResultsAttemptRoot("gate");

        Assert.Equal("pre-review-evidence-attempts", preReview);
        Assert.Equal("acceptance-gate-attempts", acceptance);
        Assert.NotEqual(preReview, acceptance);
    }

    [Xunit.Theory(DisplayName = "GoalAcceptanceVerifier_decodes_legacy_console_bytes_without_replacement")]
    [Xunit.InlineData(0xFA)]
    [Xunit.InlineData(0xB7)]
    public void GoalAcceptanceVerifierDecodesLegacyConsoleBytesWithoutReplacement(int legacyByte)
    {
        var output = GoalAcceptanceVerifier.DecodeCapturedOutput(
            [.. Encoding.ASCII.GetBytes("case:"), (byte)legacyByte, (byte)legacyByte, (byte)legacyByte]);

        Assert.StartsWith("case:", output, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', output);
        Assert.DoesNotContain('\0', output);
        Assert.Equal(8, output.Length);
        Assert.Equal(output[5], output[6]);
        Assert.Equal(output[6], output[7]);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_prefers_strict_UTF8_over_legacy_console_decoding")]
    public void GoalAcceptanceVerifierPrefersStrictUtf8OverLegacyConsoleDecoding()
    {
        var output = GoalAcceptanceVerifier.DecodeCapturedOutput(Encoding.UTF8.GetBytes("case:···"));

        Assert.Equal("case:···", output);
        Assert.DoesNotContain("Â·", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_preserves_BOM_based_output_decoding")]
    public void GoalAcceptanceVerifierPreservesBomBasedOutputDecoding()
    {
        const string expected = "captured · output";
        var utf8 = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(expected)).ToArray();
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(expected)).ToArray();

        Assert.Equal(expected, GoalAcceptanceVerifier.DecodeCapturedOutput(utf8));
        Assert.Equal(expected, GoalAcceptanceVerifier.DecodeCapturedOutput(utf16));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_reads_legacy_capture_and_tolerates_missing_file")]
    public async Task GoalAcceptanceVerifierReadsLegacyCaptureAndToleratesMissingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-capture-{Guid.NewGuid():N}.out");
        try
        {
            await File.WriteAllBytesAsync(path, [0xFA, 0xFA, 0xFA]);

            var output = await GoalAcceptanceVerifier.ReadFileWithRetryAsync(path);

            Assert.DoesNotContain('\uFFFD', output);
            Assert.Equal(3, output.Length);
            Assert.Equal(
                string.Empty,
                await GoalAcceptanceVerifier.ReadFileWithRetryAsync(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Xunit.Fact(DisplayName = "LockAttribution_emits_LOCK_receipt_with_holder_identity")]
    public void LockAttributionEmitsLockReceiptWithHolderIdentity()
    {
        var lockedPath = Path.Combine(Path.GetTempPath(), "Mcg.AgentOrchestrator.App.dll");
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(1234, "dotnet", "dotnet test --artifacts-path slot-0", true)],
            "test");
        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() => LockAttribution.Attribute(lockedPath, "slot-0"));

            Assert.Contains("LOCK ", output, StringComparison.Ordinal);
            Assert.Contains($"path=\"{lockedPath}\"", output, StringComparison.Ordinal);
            Assert.Contains("holderPid=1234", output, StringComparison.Ordinal);
            Assert.Contains("holderName=\"dotnet\"", output, StringComparison.Ordinal);
            Assert.Contains("owned=true", output, StringComparison.Ordinal);
            Assert.Contains("commandLine=\"dotnet test --artifacts-path slot-0\"", output, StringComparison.Ordinal);
        }
        finally
        {
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_retries_build_lock_after_attribution_and_build_server_shutdown")]
    public async Task GoalAcceptanceVerifierRetriesBuildLockAfterAttributionAndBuildServerShutdown()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "app build", "type": "command", "command": "dotnet", "arguments": ["build", "src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var lockedPath = Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", "bin", "Debug", "net10.0", "Mcg.AgentOrchestrator.App.dll");
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(1, $"Csc error CS2012: Cannot open '{lockedPath}' for writing -- The process cannot access the file because it is being used by another process."),
            new(0, ""),
            new(0, "Build succeeded.")
        ]);
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(4321, "testhost", $"dotnet test --artifacts-path {root}", true)],
            "test");
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(responses.Dequeue());
            });

            var result = await verifier.RunAsync(root);

            Assert.True(result.Passed);
            Assert.Equal(4, calls.Count);
            Assert.True(calls[0].SequenceEqual(["dotnet", "build-server", "shutdown"]));
            Assert.Equal("dotnet", calls[1][0]);
            Assert.True(calls[2].SequenceEqual(["dotnet", "build-server", "shutdown"]));
            Assert.Equal("dotnet", calls[3][0]);
            var check = Assert.Single(result.Checks!);
            Assert.True(check.LockRemediationApplied);
            Assert.Contains("build artifact lock detected", check.ResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_does_not_treat_missing_build_artifact_as_a_lock")]
    public async Task GoalAcceptanceVerifierDoesNotTreatMissingBuildArtifactAsALock()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "app build", "type": "command", "command": "dotnet", "arguments": ["build", "Fake.csproj"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var missingPath = Path.Combine(root, "artifacts", "obj", "apphost.exe");
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(1, $"error MSB3030: Could not copy the file '{missingPath}' because it was not found.")
        ]);

        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(responses.Dequeue());
            });

            var result = await verifier.RunAsync(root);

            Assert.False(result.Passed);
            Assert.Equal(2, calls.Count);
            var check = Assert.Single(result.Checks!);
            Assert.False(check.LockRemediationApplied);
            Assert.Contains("was not found", check.OutputTail, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_waits_for_no_holder_acceptance_output_lock_then_retries")]
    public async Task GoalAcceptanceVerifierWaitsForNoHolderAcceptanceOutputLockThenRetries()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "app build", "type": "command", "command": "dotnet", "arguments": ["build", "Fake.csproj"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var lockedPath = Path.Combine(root, "artifacts", "Mcg.AgentOrchestrator.Core.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
        File.WriteAllText(lockedPath, "held");
        using var held = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var calls = new List<string[]>();
        var buildAttempts = 0;
        var delayCallsWhileHeld = new List<TimeSpan>();
        var timeProvider = new RecordingTimeProvider();
        var lockReleased = false;
        var previousWindow = GoalAcceptanceVerifier.TransientNoHolderBuildLockWaitWindow;
        var previousPoll = GoalAcceptanceVerifier.TransientNoHolderBuildLockPollInterval;
        var previousMaxCycles = GoalAcceptanceVerifier.TransientNoHolderBuildLockMaxRetryCycles;
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
            "handle64-timeout");
        GoalAcceptanceVerifier.TransientNoHolderBuildLockWaitWindow = TimeSpan.FromMilliseconds(30);
        GoalAcceptanceVerifier.TransientNoHolderBuildLockPollInterval = TimeSpan.FromMilliseconds(10);
        GoalAcceptanceVerifier.TransientNoHolderBuildLockMaxRetryCycles = 1;

        try
        {
            timeProvider.DelayRequested = delay =>
            {
                if (!lockReleased)
                {
                    delayCallsWhileHeld.Add(delay);
                    held.Dispose();
                    lockReleased = true;
                }
            };

            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                buildAttempts++;
                if (buildAttempts == 1)
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            }, timeProvider);

            AcceptanceVerificationResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(root).GetAwaiter().GetResult());

            Assert.NotNull(result);
            Assert.True(result!.Passed);
            Assert.True(result.Retried);
            Assert.Equal(2, buildAttempts);
            Assert.Equal(4, calls.Count);
            Assert.NotEmpty(delayCallsWhileHeld);
            Assert.Equal(TimeSpan.FromMilliseconds(10), Assert.Single(delayCallsWhileHeld));
            Assert.True(calls[0].SequenceEqual(["dotnet", "build-server", "shutdown"]));
            Assert.True(calls[2].SequenceEqual(["dotnet", "build-server", "shutdown"]));
            var check = Assert.Single(result.Checks!);
            Assert.True(check.LockRemediationApplied);
            Assert.Contains("build artifact lock detected", check.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("LOCK_TRANSIENT_WAIT ", output, StringComparison.Ordinal);
            Assert.Contains("released=true", output, StringComparison.Ordinal);
            Assert.Contains("LOCK_TRANSIENT_RETRY ", output, StringComparison.Ordinal);
            Assert.Contains("verdict=completed", output, StringComparison.Ordinal);
            Assert.Contains("build-lock=false", output, StringComparison.Ordinal);

            var stuckRoot = CreateManifestWorkspace("""
                {
                  "version": 1,
                  "checks": [
                    { "name": "app build", "type": "command", "command": "dotnet", "arguments": ["build", "Fake.csproj"] }
                  ],
                  "forbiddenChangedPathGlobs": []
                }
                """);
            var stuckPath = Path.Combine(stuckRoot, "artifacts", "Mcg.AgentOrchestrator.Core.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(stuckPath)!);
            File.WriteAllText(stuckPath, "held");
            using var stuckHold = new FileStream(stuckPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var stuckCalls = new List<string[]>();
            var stuckTimeProvider = new RecordingTimeProvider();
            var stuckBuildAttempts = 0;
            var stuckVerifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                stuckCalls.Add(args);
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                stuckBuildAttempts++;
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    1,
                    $"error CS2012: Cannot open '{stuckPath}' for writing because it is being used by another process."));
            }, stuckTimeProvider);

            BuildLockBlockedException? blocked = null;
            var stuckOutput = AsyncLocalConsoleRouter.Capture(() =>
                blocked = Assert.ThrowsAsync<BuildLockBlockedException>(() => stuckVerifier.RunAsync(stuckRoot))
                    .GetAwaiter()
                    .GetResult());

            Assert.NotNull(blocked);
            Assert.Equal(stuckPath, blocked!.Attribution.Path);
            Assert.Equal(1, stuckBuildAttempts);
            Assert.Single(stuckCalls.Where(call => !call.SequenceEqual(["dotnet", "build-server", "shutdown"])));
            Assert.Contains(stuckCalls, call => call.SequenceEqual(["dotnet", "build-server", "shutdown"]));
            Assert.Equal(3, stuckTimeProvider.Delays.Count);
            Assert.All(stuckTimeProvider.Delays, delay => Assert.Equal(TimeSpan.FromMilliseconds(10), delay));
            Assert.Contains("LOCK_TRANSIENT_WAIT ", stuckOutput, StringComparison.Ordinal);
            Assert.Contains("released=false", stuckOutput, StringComparison.Ordinal);
            Assert.Contains("LOCK_TRANSIENT_RETRY ", stuckOutput, StringComparison.Ordinal);
            Assert.Contains("verdict=wait-exhausted", stuckOutput, StringComparison.Ordinal);
            Assert.Contains("build-lock=true", stuckOutput, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.TransientNoHolderBuildLockWaitWindow = previousWindow;
            GoalAcceptanceVerifier.TransientNoHolderBuildLockPollInterval = previousPoll;
            GoalAcceptanceVerifier.TransientNoHolderBuildLockMaxRetryCycles = previousMaxCycles;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_no_holder_lock_wait_excludes_probe_time_and_blocks_after_window")]
    public async Task GoalAcceptanceVerifierNoHolderLockWaitExcludesProbeTimeAndBlocksAfterWindow()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "app build", "type": "command", "command": "dotnet", "arguments": ["build", "Fake.csproj"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var lockedPath = Path.Combine(root, "artifacts", "Mcg.AgentOrchestrator.Core.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
        File.WriteAllText(lockedPath, "held");
        using var held = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var previousWindow = GoalAcceptanceVerifier.TransientNoHolderBuildLockWaitWindow;
        var previousPoll = GoalAcceptanceVerifier.TransientNoHolderBuildLockPollInterval;
        var previousMaxCycles = GoalAcceptanceVerifier.TransientNoHolderBuildLockMaxRetryCycles;
        LockAttribution.AttributeForTests = (path, _) =>
        {
            Thread.Sleep(120);
            return new BuildLockAttribution(
                path,
                [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
                "handle64-timeout");
        };
        GoalAcceptanceVerifier.TransientNoHolderBuildLockWaitWindow = TimeSpan.FromMilliseconds(80);
        GoalAcceptanceVerifier.TransientNoHolderBuildLockPollInterval = TimeSpan.FromMilliseconds(10);
        GoalAcceptanceVerifier.TransientNoHolderBuildLockMaxRetryCycles = 1;

        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    1,
                    $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process."));
            });

            BuildLockBlockedException? blocked = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                blocked = Assert.ThrowsAsync<BuildLockBlockedException>(() => verifier.RunAsync(root)).GetAwaiter().GetResult());

            Assert.NotNull(blocked);
            Assert.Equal(lockedPath, blocked!.Attribution.Path);
            Assert.Contains("LOCK_TRANSIENT_WAIT ", output, StringComparison.Ordinal);
            Assert.Contains("released=false", output, StringComparison.Ordinal);
            Assert.Matches(@"probe-ms=(1[0-9]{2}|[2-9][0-9]{2,})", output);
            Assert.Matches(@"waited-ms=([8-9][0-9]|[1-9][0-9]{2,})", output);
            Assert.Contains("LOCK_TRANSIENT_RETRY ", output, StringComparison.Ordinal);
            Assert.Contains("verdict=wait-exhausted", output, StringComparison.Ordinal);
            Assert.Contains("build-lock=true", output, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.TransientNoHolderBuildLockWaitWindow = previousWindow;
            GoalAcceptanceVerifier.TransientNoHolderBuildLockPollInterval = previousPoll;
            GoalAcceptanceVerifier.TransientNoHolderBuildLockMaxRetryCycles = previousMaxCycles;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_blocks_with_LOCK_receipt_while_file_is_held_then_succeeds_after_release")]
    public async Task GoalAcceptanceVerifierBlocksWithLockReceiptWhileFileIsHeldThenSucceedsAfterRelease()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "app build", "type": "command", "command": "dotnet", "arguments": ["build", "Fake.csproj"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var lockedPath = Path.Combine(root, "artifacts", "Mcg.AgentOrchestrator.App.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
        File.WriteAllText(lockedPath, "held");
        using var held = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var calls = new List<string[]>();

        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(Environment.ProcessId, "testhost", $"test held {lockedPath}", false)],
            "test");
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                try
                {
                    using var opened = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"Access to the path '{lockedPath}' is denied.", ex);
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            BuildLockBlockedException? blocked = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                blocked = Assert.ThrowsAsync<BuildLockBlockedException>(() => verifier.RunAsync(root)).GetAwaiter().GetResult());

            Assert.NotNull(blocked);
            Assert.Equal(lockedPath, blocked!.Attribution.Path);
            Assert.Contains("LOCK ", output, StringComparison.Ordinal);
            Assert.Contains($"path=\"{lockedPath}\"", output, StringComparison.Ordinal);
            Assert.Contains($"holderPid={Environment.ProcessId}", output, StringComparison.Ordinal);
            Assert.Contains("holderName=\"testhost\"", output, StringComparison.Ordinal);
            Assert.True(calls.Count >= 3);

            held.Dispose();
            var result = await verifier.RunAsync(root);

            Assert.True(result.Passed);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_auto_runs_policy_required_browser_smoke")]
    public async Task GoalAcceptanceVerifierAutoRunsPolicyRequiredBrowserSmoke()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed."));
        });

        var result = await verifier.RunAsync(root, changedFiles: ["wwwroot/css/app.css"]);

        Assert.True(result.Passed);
        var browserCall = calls.Single(call => call.Contains(@".\scripts\Run-DashboardBrowserScript.ps1", StringComparer.OrdinalIgnoreCase));
        Assert.Equal("powershell", browserCall[0]);
        Assert.Contains(@".\scripts\dashboard-smoke.js", browserCall, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(result.Checks!, check => check.Name == "dashboard browser smoke" && check.Passed);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_blocks_forbidden_changed_paths_from_manifest")]
    public async Task GoalAcceptanceVerifierBlocksForbiddenChangedPathsFromManifest()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": [ "bin/**", ".scratch/**" ]
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "src/ok.cs\nbin/Debug/generated.dll\n")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        Assert.False(result.Passed);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(2, calls.Count);
        Assert.True(calls[1].SequenceEqual(["git", "diff", "--name-only", "main...HEAD"]));
        Assert.Equal(1, result.Checks!.Count);
        Assert.Equal("forbidden changed paths", result.Checks![0].Name);
        Assert.Contains("bin/Debug/generated.dll", result.OutputTail!, StringComparison.Ordinal);
    }

    [Xunit.Theory(DisplayName = "GoalAcceptanceVerifier_classifies_transient_testhost_abort_vs_real_failure")]
    [Xunit.InlineData("The active Test Run was aborted because the host process exited unexpectedly.", true)]
    [Xunit.InlineData("Test Run Aborted.\r\n   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs", true)]
    [Xunit.InlineData("Failed!  - Failed:     1, Passed:  1012, Skipped:     0, Total:  1013", false)]
    [Xunit.InlineData("Failed:     1, Passed:  1012. The active Test Run was aborted because the host process exited unexpectedly.", false)]
    [Xunit.InlineData("Build FAILED.\r\nerror CS1002: ; expected", false)]
    [Xunit.InlineData("Passed!  - Failed:     0, Passed:  1013, Skipped:     0, Total:  1013", false)]
    public void ClassifiesTransientTesthostAbortVsRealFailure(string output, bool expected)
    {
        Assert.Equal(expected, GoalAcceptanceVerifier.IsTransientTesthostAbort(output));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, "known-empty-candidate")]
    [Xunit.InlineData(true, "docs-tree-only-candidate")]
    public async Task ProvenNoTestCandidate_RunsNonDotnetChecks_AndRecordsReceipt(
        bool docsTreeOnly,
        string receiptName)
    {
        var root = CreateTrackedManifestShapeWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "non-dotnet check passed"));
        });

        try
        {
            IReadOnlyList<string> changedFiles = docsTreeOnly ? ["docs/operator.md"] : [];
            var result = await verifier.RunAsync(root, changedFiles: changedFiles);

            Assert.True(result.Passed);
            Assert.False(result.Skipped);
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.TestResultPaths ?? []);
            Assert.Contains(calls, call => call.SequenceEqual(["git", "diff", "--check"]));
            Assert.DoesNotContain(calls, IsDotnetShardInvocation);
            var receipt = Assert.Single(result.Checks!, check => check.Name == receiptName);
            Assert.True(receipt.Passed);
            Assert.Equal("dotnet-shards=omitted", receipt.ResultSummary);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task ProvenNoTestCandidate_FailingNonDotnetCheck_FailsGate()
    {
        var root = CreateTrackedManifestShapeWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            var failed = args.SequenceEqual(["git", "diff", "--check"]);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                failed ? 1 : 0,
                failed ? "whitespace error" : string.Empty));
        });

        try
        {
            var result = await verifier.RunAsync(root, changedFiles: ["docs/operator.md"]);

            Assert.False(result.Passed);
            Assert.False(result.Skipped);
            Assert.Equal(1, result.ExitCode);
            Assert.DoesNotContain(calls, IsDotnetShardInvocation);
            Assert.Contains(result.Checks!, check =>
                check.Name == "git diff whitespace" && !check.Passed);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("README.md", null)]
    [Xunit.InlineData("notes/example.md", null)]
    [Xunit.InlineData("src/Feature.cs", null)]
    [Xunit.InlineData("tests/FeatureTests.cs", null)]
    [Xunit.InlineData("docs/operator.md", "src/Feature.cs")]
    public async Task NonDocsTreeCandidate_ExecutesDotnetShard(string firstPath, string? secondPath)
    {
        var root = CreateTrackedManifestShapeWorkspace();
        SetManifestStructuralCoverage(root, enabled: false);
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                0,
                IsDotnetShardInvocation(args)
                    ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                    : string.Empty));
        });

        try
        {
            IReadOnlyList<string> changedFiles = secondPath is null
                ? [firstPath]
                : [firstPath, secondPath];
            await verifier.RunAsync(root, changedFiles: changedFiles);

            Assert.Contains(calls, IsDotnetShardInvocation);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task UnknownCandidate_ExecutesDotnetShard()
    {
        var root = CreateTrackedManifestShapeWorkspace();
        SetManifestStructuralCoverage(root, enabled: false);
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                0,
                IsDotnetShardInvocation(args)
                    ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                    : string.Empty));
        });

        try
        {
            await verifier.RunAsync(root, changedFiles: null);

            Assert.Contains(calls, IsDotnetShardInvocation);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" ")]
    [Xunit.InlineData("docs")]
    [Xunit.InlineData("/docs/operator.md")]
    [Xunit.InlineData("C:/repo/docs/operator.md")]
    [Xunit.InlineData("docs/../src/Feature.cs")]
    [Xunit.InlineData("docs//operator.md")]
    public void InvalidDocsPath_RunsDotnetShards(string path)
    {
        Assert.Equal(
            DotnetShardDisposition.RunDotnetShards,
            GoalAcceptanceVerifier.ClassifyDotnetShardDisposition([path]));
    }

    [Xunit.Fact]
    public void NoTestDispositions_TransformEffectivePlanIdentity()
    {
        var root = CreateTrackedManifestShapeWorkspace();
        try
        {
            var emptyIdentity = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(root, []);
            var docsIdentity = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                root,
                ["docs/operator.md"]);
            var codeIdentity = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                root,
                ["src/Feature.cs"]);

            Assert.Equal(
                DotnetShardDisposition.KnownEmptyCandidate,
                GoalAcceptanceVerifier.ClassifyDotnetShardDisposition([]));
            Assert.Equal(
                DotnetShardDisposition.DocsTreeOnlyCandidate,
                GoalAcceptanceVerifier.ClassifyDotnetShardDisposition(["docs/operator.md"]));
            Assert.Equal(
                DotnetShardDisposition.RunDotnetShards,
                GoalAcceptanceVerifier.ClassifyDotnetShardDisposition(null));
            var settings = AcceptanceGateEngineSettings.Load(root);
            Assert.False(GoalAcceptanceVerifier.StructuralCoverageApplies(settings, []));
            Assert.False(GoalAcceptanceVerifier.StructuralCoverageApplies(
                settings,
                ["docs/operator.md"]));
            Assert.NotEqual(emptyIdentity, docsIdentity);
            Assert.NotEqual(emptyIdentity, codeIdentity);
            Assert.NotEqual(docsIdentity, codeIdentity);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task DocsTreeCandidate_SkipsAdvisoryDotnet_ButRunsOtherAdvisories()
    {
        var root = CreateTrackedManifestShapeWorkspace();
        var orchestratorDirectory = Path.Combine(root, ".orchestrator");
        Directory.CreateDirectory(orchestratorDirectory);
        File.WriteAllText(Path.Combine(orchestratorDirectory, "marker.txt"), "present");
        File.WriteAllText(
            Path.Combine(orchestratorDirectory, "goal-acceptance-criteria.json"),
            """
            [
              { "name": "marker exists", "type": "file-exists", "path": ".orchestrator/marker.txt" },
              { "name": "advisory dotnet tests", "type": "dotnet-test" }
            ]
            """);
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, string.Empty));
        });

        try
        {
            var result = await verifier.RunAsync(root, changedFiles: ["docs/operator.md"]);

            Assert.True(result.Passed);
            Assert.DoesNotContain(calls, IsDotnetShardInvocation);
            Assert.Contains(result.Checks!, check =>
                check.Name == "marker exists" && check.Advisory && check.Passed);
            var advisorySkip = Assert.Single(result.Checks!, check =>
                check.Name == "advisory dotnet tests");
            Assert.True(advisorySkip.Advisory);
            Assert.Contains("advisory-skip=true", advisorySkip.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("docs-tree-only-candidate", advisorySkip.ResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    private static bool IsDotnetShardInvocation(string[] args) =>
        (args.Length >= 2 &&
            args[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            (args[1].Equals("build", StringComparison.OrdinalIgnoreCase) ||
                args[1].Equals("test", StringComparison.OrdinalIgnoreCase))) ||
        (args.Length > 0 &&
            args[0].Contains("Tests", StringComparison.OrdinalIgnoreCase) &&
            (args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_uses_25_minute_default_timeout_when_unconfigured")]
    public async Task GoalAcceptanceVerifierUses25MinuteDefaultTimeoutWhenUnconfigured()
    {
        var previous = SetAcceptanceTimeoutEnvironment(null);
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<(string[] Args, TimeSpan Timeout)>();
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, timeout, _) =>
            {
                calls.Add((args, timeout));
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            var result = await verifier.RunAsync(root);

            Assert.True(result.Passed);
            Assert.Equal(2, calls.Count);
            Assert.Equal(TimeSpan.FromMinutes(25), calls[0].Timeout);
            Assert.Equal(TimeSpan.FromMinutes(25), calls[1].Timeout);
        }
        finally
        {
            SetAcceptanceTimeoutEnvironment(previous);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_manifest_timeout_override_wins_over_global_default")]
    public async Task GoalAcceptanceVerifierManifestTimeoutOverrideWinsOverGlobalDefault()
    {
        var previous = SetAcceptanceTimeoutEnvironment("7");
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "slow custom check", "type": "command", "command": "custom-check", "arguments": ["--slow"], "timeoutMinutes": 2 }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<(string[] Args, TimeSpan Timeout)>();
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, timeout, _) =>
            {
                calls.Add((args, timeout));
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            var result = await verifier.RunAsync(root);

            Assert.True(result.Passed);
            Assert.Equal(2, calls.Count);
            Assert.Equal(TimeSpan.FromMinutes(7), calls[0].Timeout);
            Assert.Equal(TimeSpan.FromMinutes(2), calls[1].Timeout);
            Assert.Equal("custom-check", calls[1].Args[0]);
        }
        finally
        {
            SetAcceptanceTimeoutEnvironment(previous);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_validates_state_effect_proposal_schema")]
    public async Task GoalAcceptanceVerifierValidatesStateEffectProposalSchema()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "fast no-op", "type": "no-op" }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var proposalDirectory = Path.Combine(root, ".orchestrator-proposals");
            Directory.CreateDirectory(proposalDirectory);
            File.WriteAllText(Path.Combine(proposalDirectory, "backlog-add-missing-title.md"), """
                ---
                kind: backlog-add
                ---
                Body only is not enough.
                """);
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
                Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, string.Empty)));

            var result = await verifier.RunAsync(
                root,
                changedFiles: [".orchestrator-proposals/backlog-add-missing-title.md"]);

            Assert.False(result.Passed);
            var check = Assert.Single(result.Checks!.Where(check => check.Name == "state-effect proposal schema"));
            Assert.False(check.Passed);
            Assert.Contains("missing required field 'title'", check.OutputTail);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_hermetic_profile_creates_derived_temp_directory")]
    public void GoalAcceptanceVerifierHermeticProfileCreatesDerivedTempDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profileRoot = Path.Combine(Path.GetTempPath(), "mcg-hvp");
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(
            environment,
            Path.GetTempPath());

        var localAppData = Assert.Contains("LOCALAPPDATA", environment);
        Assert.False(string.IsNullOrWhiteSpace(localAppData));

        // LOCALAPPDATA must NOT be relocated into the hermetic profile. Everything Windows derives from it
        // moves when it does, and three separate gate defects in one day came from exactly that: the per-user
        // temp location %LOCALAPPDATA%\Temp disappeared, paths built through it outgrew MAX_PATH and failed
        // git object writes, and the PowerShell 7 execution alias under %LOCALAPPDATA%\Microsoft\WindowsApps
        // stopped resolving so callers degraded to Windows PowerShell 5.1. Credential and cache isolation is
        // carried by HOME/USERPROFILE/DOTNET_CLI_HOME/NUGET_PACKAGES instead.
        Assert.False(
            localAppData!.StartsWith(profileRoot, StringComparison.OrdinalIgnoreCase),
            $"LOCALAPPDATA must stay outside the hermetic profile root, but was {localAppData}");
        Assert.True(
            Directory.Exists(localAppData),
            $"LOCALAPPDATA must point at a real existing directory: {localAppData}");

        var appData = Assert.Contains("APPDATA", environment);
        Assert.False(
            appData!.StartsWith(profileRoot, StringComparison.OrdinalIgnoreCase),
            $"APPDATA must stay outside the hermetic profile root, but was {appData}");

        // The isolation that IS intended must still hold.
        var userProfile = Assert.Contains("USERPROFILE", environment);
        Assert.StartsWith(profileRoot, userProfile!, StringComparison.OrdinalIgnoreCase);

        // Git identity must survive the HOME/USERPROFILE redirect. git reads user.name/user.email from
        // $HOME/.gitconfig, and the profile root is empty, so without this a verification child has no
        // identity. Read-only git does not care, which is why it hid - but the pre-landing rebase replays
        // commits and git exits 128 the moment it needs one, failing every landing attempt with
        // "exit=128; stderr=Rebasing (1/3)".
        foreach (var identityVariable in new[]
                 {
                     "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL",
                 })
        {
            var value = Assert.Contains(identityVariable, environment);
            Assert.False(
                string.IsNullOrWhiteSpace(value),
                $"{identityVariable} must be set so a verification child can create commits");
        }

        // The identity must be DETERMINISTIC, not borrowed from the operator. Pointing GIT_CONFIG_GLOBAL at
        // the real ~/.gitconfig would also restore identity, but it would drag in every other global git
        // setting - the ambient-input class this whole function exists to forbid.
        Assert.DoesNotContain("GIT_CONFIG_GLOBAL", environment.Keys);
    }

    [Xunit.Fact(DisplayName = "Hermetic_per_user_folders_prefer_the_inherited_variables_over_the_known_folder")]
    public void HermeticPerUserFoldersPreferTheInheritedVariablesOverTheKnownFolder()
    {
        // The gate applies the hermetic environment at TWO nested levels: the conductor configures the
        // __acceptance-gate-attempt child (ConductorParallelAcceptanceAttempts.cs:1160), and that child -
        // already inside the hermetic environment, with USERPROFILE repointed - configures each lane
        // (GoalAcceptanceVerifier.cs:6016). At the inner level GetFolderPath expands
        // "%USERPROFILE%\AppData\Local" against the redirected profile, so the known-folder inputs below are
        // what the INNER level actually sees, while the variables still carry the real locations the outer
        // level pinned.
        const string RealAppData = @"C:\Users\real\AppData\Roaming";
        const string RealLocalAppData = @"C:\Users\real\AppData\Local";
        const string NestedAppData = @"C:\Temp\mcg-hvp\AppData\Roaming";
        const string NestedLocalAppData = @"C:\Temp\mcg-hvp\AppData\Local";

        var (appData, localAppData) = GoalAcceptanceVerifier.ResolvePerUserFolders(
            appDataVariable: RealAppData,
            localAppDataVariable: RealLocalAppData,
            appDataKnownFolder: NestedAppData,
            localAppDataKnownFolder: NestedLocalAppData);

        // Taking the known folder here is what pushed the per-user folders one level deeper per hop. Every
        // lane then inherited a nested LOCALAPPDATA, the per-user pwsh 7 install stopped resolving under it,
        // and the gate silently ran each lane under Windows PowerShell 5.1.
        Assert.Equal(RealAppData, appData);
        Assert.Equal(RealLocalAppData, localAppData);
    }

    [Xunit.Fact(DisplayName = "Hermetic_per_user_folders_fall_back_to_the_known_folder_at_the_outermost_level")]
    public void HermeticPerUserFoldersFallBackToTheKnownFolderAtTheOutermostLevel()
    {
        // At the outermost level there is no inherited value yet, so the known folder is correct there.
        const string KnownAppData = @"C:\Users\real\AppData\Roaming";
        const string KnownLocalAppData = @"C:\Users\real\AppData\Local";

        var (appData, localAppData) = GoalAcceptanceVerifier.ResolvePerUserFolders(
            appDataVariable: null,
            localAppDataVariable: "   ",
            appDataKnownFolder: KnownAppData,
            localAppDataKnownFolder: KnownLocalAppData);

        Assert.Equal(KnownAppData, appData);
        Assert.Equal(KnownLocalAppData, localAppData);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_bounds_generated_artifact_file_names")]
    public void GoalAcceptanceVerifierBoundsGeneratedArtifactFileNames()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-long-names-{Guid.NewGuid():N}");
        var environment = new DotnetBuildEnvironment(
            "goal-long-name",
            root,
            Path.Combine(root, "build-artifacts"),
            Path.Combine(root, "build-slots", "build-0.lock"),
            [],
            "goal-long-name");
        using var scope = GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(
            Path.Combine(root, "c012c6fc-pre-review-20260731222420370-2a0f855b951b461ab287ba73dfae1a28"));

        // The exact shape that crashed a real gate: a focused-evidence check is named after its whole filter
        // expression, so four target classes produced a 324-character file name. Windows rejected it with
        // ERROR_INVALID_NAME inside MTP's TRX writer, which does not handle it, and the test host died.
        const string CrashingCheckName =
            "reviewer focused evidence: infrastructure tests --filter " +
            "FullyQualifiedName~GoalWorktreeTests.RemoveCleanup|" +
            "FullyQualifiedName~GoalWorktreeTests.OrphanEphemeralSweep|" +
            "FullyQualifiedName~LandingExecutorTests|" +
            "FullyQualifiedName~CliCommandTests.GoalLifecycleCommands";
        const string SiblingCheckName = CrashingCheckName + "|FullyQualifiedName~OneMoreDistinguishingClass";

        var trxPath = GoalAcceptanceVerifier.ResolveTrxPathForTests(CrashingCheckName, environment);
        var heartbeatPath = GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(CrashingCheckName, environment);
        var siblingTrxPath = GoalAcceptanceVerifier.ResolveTrxPathForTests(SiblingCheckName, environment);
        var siblingHeartbeatPath =
            GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(SiblingCheckName, environment);

        var trxFileName = Path.GetFileName(trxPath);
        var heartbeatFileName = Path.GetFileName(heartbeatPath);

        // 255 is the NTFS component limit. Receipt preservation stages through "{path}.{guid:N}.tmp", so a
        // resolved name has to stay clear of the limit by that much rather than merely reach it.
        const int NtfsFileNameLimit = 255;
        const int StagingSuffixLength = 37;
        Assert.True(
            trxFileName.Length + StagingSuffixLength <= NtfsFileNameLimit,
            $"TRX file name is {trxFileName.Length} characters and must leave room for the staging suffix: {trxFileName}");
        Assert.True(
            heartbeatFileName.Length <= NtfsFileNameLimit,
            $"Heartbeat file name is {heartbeatFileName.Length} characters: {heartbeatFileName}");

        Assert.EndsWith(".trx", trxFileName, StringComparison.Ordinal);
        Assert.EndsWith(GateHeartbeatArtifacts.FileName, heartbeatFileName, StringComparison.Ordinal);

        // Truncation must not merge two checks onto one receipt; these two names share a 300-character prefix.
        Assert.NotEqual(trxPath, siblingTrxPath);
        Assert.NotEqual(heartbeatPath, siblingHeartbeatPath);
    }
}

public abstract class GoalAcceptanceVerifierTestBase
{
    protected static void AssertAvailable(GateLoadSample sample, double expected)
    {
        Assert.True(sample.IsAvailable);
        Assert.Equal(expected, sample.Value!.Value);
        Assert.Null(sample.UnavailableReason);
    }

    protected static void AssertUnavailable(GateLoadSample sample, string expectedReason)
    {
        Assert.False(sample.IsAvailable);
        Assert.Null(sample.Value);
        Assert.Equal(expectedReason, sample.UnavailableReason);
    }

    protected static string CreateManifestWorkspace(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-tests", Guid.NewGuid().ToString("N"));
        var manifestDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(manifestDirectory);
        File.WriteAllText(
            Path.Combine(manifestDirectory, "acceptance-manifest.json"),
            AcceptanceManifestTestDefaults.WithEngine(manifest));
        return root;
    }

    protected static string CreateTrackedManifestShapeWorkspace()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] },
                { "name": "core tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "dashboard tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj", "arguments": ["--verbosity", "minimal", "--filter-not-trait", "Category=HostIntegration"] }
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
        var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidOperationException("Expected tracked-shape manifest JSON.");
        manifest["engine"]!["enforceStructuralCoverage"] = true;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        return root;
    }

    protected static void SetManifestStructuralCoverage(string root, bool enabled)
    {
        var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidOperationException("Expected acceptance manifest JSON.");
        manifest["engine"]!["enforceStructuralCoverage"] = enabled;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
    }

    protected static string? SetAcceptanceTimeoutEnvironment(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(AcceptanceCheckTimeouts.EnvironmentVariable);
        Environment.SetEnvironmentVariable(AcceptanceCheckTimeouts.EnvironmentVariable, value);
        return previous;
    }

    protected static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                System.Threading.Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                ClearReadOnlyAttributes(path);
                System.Threading.Thread.Sleep(100);
            }
        }

        Directory.Delete(path, recursive: true);
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        try
        {
            var root = new DirectoryInfo(path);
            root.Attributes &= ~FileAttributes.ReadOnly;
            foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the next delete attempt surfaces any remaining failure.
        }
    }
}

internal sealed class RecordingTimeProvider : TimeProvider
{
    private long _currentTicks;
    private DateTimeOffset _utcNow = new(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Delays { get; } = [];

    public Action<TimeSpan>? DelayRequested { get; set; }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _currentTicks;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        _currentTicks += delay.Ticks;
        _utcNow += delay;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Advance(dueTime);
            Delays.Add(dueTime);
            DelayRequested?.Invoke(dueTime);
        }

        callback(state);
        return CompletedTimer.Instance;
    }

    private sealed class CompletedTimer : ITimer
    {
        public static readonly CompletedTimer Instance = new();

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}


internal static class AcceptanceManifestTestDefaults
{
    public static string WithEngine(
        string manifest,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var candidate = JsonNode.Parse(manifest)?.AsObject()
            ?? throw new InvalidOperationException("Expected acceptance manifest JSON object.");
        if (candidate.ContainsKey("engine"))
        {
            return candidate.ToJsonString();
        }

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath) ?? AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "config", "acceptance-manifest.json")))
        {
            directory = directory.Parent;
        }

        var trackedManifest = directory is not null
            ? Path.Combine(directory.FullName, "config", "acceptance-manifest.json")
            : throw new DirectoryNotFoundException(
                $"Could not locate tracked acceptance manifest from source path '{sourceFilePath}'.");
        var tracked = JsonNode.Parse(File.ReadAllText(trackedManifest))?.AsObject()
            ?? throw new InvalidOperationException("Expected tracked acceptance manifest JSON object.");
        var engine = tracked["engine"]?.DeepClone()?.AsObject()
            ?? throw new InvalidOperationException("Tracked acceptance manifest has no engine settings.");
        engine["enforceStructuralCoverage"] = false;
        engine["timeouts"] = new JsonObject();
        candidate["engine"] = engine;
        return candidate.ToJsonString();
    }
}

internal sealed class OptInRealAcceptanceVerifierFactAttribute : Xunit.FactAttribute
{
    private const string EnvironmentVariable = "MCG_RUN_REAL_ACCEPTANCE_VERIFIER_TESTS";

    public OptInRealAcceptanceVerifierFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.OrdinalIgnoreCase))
        {
            Skip = $"Set {EnvironmentVariable}=1 to run the opt-in real acceptance verifier smoke.";
        }
    }
}
