using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTests : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_scrubs_launch_context_env_so_the_gate_verdict_is_hermetic")]
    public void GoalAcceptanceVerifierScrubsLaunchContextEnvSoTheGateVerdictIsHermetic()
    {
        var environment = new System.Collections.Specialized.StringDictionary
        {
            // Handoff vars leak in from a handoff-spawned conductor and make CLI grandchildren skip
            // startup cleanup, which silently greened a real backlog-list regression on some runs.
            ["MCG_ORCHESTRATOR_HANDOFF_READY_PATH"] = @"C:\ready.txt",
            ["MCG_ORCHESTRATOR_HANDOFF_TOKEN"] = "token-1",
            ["mcg_orchestrator_handoff_incumbent_pid"] = "4242",
            [WorkerSandboxOptions.EnabledVariable] = "1",
            [WorkerSandboxOptions.AccountVariable] = "worker",
            // Unrelated inputs the suite legitimately needs must survive.
            ["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = @"C:\repo",
            ["PATH"] = @"C:\windows"
        };

        GoalAcceptanceVerifier.ScrubNonHermeticEnvironment(environment);

        Assert.Empty(environment.Keys
            .Cast<string>()
            .Where(name => name.StartsWith(
                GoalAcceptanceVerifier.HandoffEnvironmentVariablePrefix,
                StringComparison.OrdinalIgnoreCase)));
        Assert.False(environment.ContainsKey(WorkerSandboxOptions.EnabledVariable));
        Assert.False(environment.ContainsKey(WorkerSandboxOptions.AccountVariable));
        Assert.Equal(@"C:\repo", environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"]);
        Assert.Equal(@"C:\windows", environment["PATH"]);
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

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_skips_dotnet_tests_for_docs_only_default_plan")]
    public async Task GoalAcceptanceVerifierSkipsDotnetTestsForDocsOnlyDefaultPlan()
    {
        var root = CreateManifestWorkspace("""
            {
              "engine": {
                "maxConcurrentShards": 1,
                "enforceStructuralCoverage": true
              },
              "checks": [
                { "name": "test impact: no build required", "type": "no-op" }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, "")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        try
        {
            var result = await verifier.RunAsync(root, changedFiles: ["README.md"]);

            Assert.True(result.Passed);
            Assert.Equal(1, calls.Count);
            Assert.True(calls[0].SequenceEqual(["dotnet", "build-server", "shutdown"]));
            var check = Xunit.Assert.Single(result.Checks!);
            Assert.Equal("test impact: no build required", check.Name);
            Xunit.Assert.Null(check.ExitCode);
            Xunit.Assert.Null(result.ArtifactsPath);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

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
}

public abstract class GoalAcceptanceVerifierTestBase
{
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
                System.Threading.Thread.Sleep(100);
            }
        }

        Directory.Delete(path, recursive: true);
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

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTests : GoalAcceptanceVerifierTestBase
{
    private const string CheckedInCliLaneFilter =
        "FullyQualifiedName~CliCommandTests&FullyQualifiedName!~CliCommandTestsGoalLifecycleCommands&FullyQualifiedName!~CliCommandTestsPersistentRunnerCommands&FullyQualifiedName!~CliCommandTestsSubscriptionDispatchCommands&FullyQualifiedName!~CliCommandTestsTerminalSweepCommands";
    private const string CheckedInGoalAcceptanceVerifierLaneFilter =
        "FullyQualifiedName~AcceptanceGateEngineSettingsTests|FullyQualifiedName~GoalAcceptanceVerifierTests|FullyQualifiedName~RealProcessShardAlphaSmokeTests|FullyQualifiedName~RealProcessShardBetaSmokeTests|FullyQualifiedName~WorkerDispatchJobAccountingTests";
    private const string CheckedInGoalAcceptanceBuildSlotsLaneFilter =
        "FullyQualifiedName~GoalAcceptanceVerifierDotnetBuildSlotTests";

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_gate_heartbeat_surfaces_hung_child_without_process_inspection")]
    public async Task GoalAcceptanceVerifierGateHeartbeatSurfacesHungChildWithoutProcessInspection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "hung gate receipt", "type": "command", "command": "powershell", "arguments": ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], "timeoutMinutes": 1 }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var previousHeartbeat = GoalAcceptanceVerifier.HeartbeatInterval;
        var previousProgress = GoalAcceptanceVerifier.ProgressInterval;
        var progress = new List<AcceptanceGateProgress>();
        try
        {
            TryDeleteStableSlotHeartbeat(0);
            GoalAcceptanceVerifier.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
            GoalAcceptanceVerifier.ProgressInterval = TimeSpan.FromMilliseconds(200);
            var verifier = new GoalAcceptanceVerifier();
            var goalId = new GoalId("feedfacefeedfacefeedfacefeedface");
            using var sink = GoalAcceptanceVerifier.PushGateProgressSink(progress.Add);
            using var cts = new CancellationTokenSource();
            var run = verifier.RunAsync(root, goalId, stableSlotIndex: 0, cancellationToken: cts.Token);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!progress.Any(item => item.CurrentTarget == "hung gate receipt") &&
                   DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            var heartbeatPath = Assert.Single(
                progress
                    .Where(item => item.CurrentTarget == "hung gate receipt")
                    .Select(item => item.HeartbeatPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

            Assert.True(File.Exists(heartbeatPath), $"Missing attempt heartbeat '{heartbeatPath}'.");
            var snapshot = JsonSerializer.Deserialize<GateHeartbeatSnapshot>(
                File.ReadAllText(heartbeatPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(snapshot);
            Assert.Equal("feedfacefeedfacefeedfacefeedface", snapshot!.GoalId);
            Assert.Equal("verification-check", snapshot.Phase);
            Assert.Equal("hung gate receipt", snapshot.CurrentTarget);
            Assert.True(snapshot.ChildPid.HasValue || snapshot.State is "completed" or "timed-out");
            Assert.True(DateTimeOffset.UtcNow - snapshot.LastProgressAt >= TimeSpan.Zero);
            Assert.Contains(progress, item => item.GoalId == goalId.Value && item.CurrentTarget == "hung gate receipt");
        }
        finally
        {
            GoalAcceptanceVerifier.HeartbeatInterval = previousHeartbeat;
            GoalAcceptanceVerifier.ProgressInterval = previousProgress;
            try { DeleteDirectoryWithRetry(root); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_gate_heartbeat_mirrors_are_run_scoped_and_terminal_cleanup_preserves_sibling")]
    public void GoalAcceptanceVerifierGateHeartbeatMirrorsAreRunScopedAndTerminalCleanupPreservesSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-gate-status-mirror-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var firstGoalId = new GoalId("abcdef01abcdef01abcdef01abcdef01");
        var secondGoalId = new GoalId("12345678123456781234567812345678");
        const int buildSlot = 0;
        var environment = new DotnetBuildEnvironment(
            "goal-mirror",
            root,
            Path.Combine(root, "artifacts"),
            Path.Combine(root, "build-slots", $"build-{buildSlot}.lock"),
            [],
            "goal-mirror",
            BuildPermitIndex: buildSlot);
        var stdoutPath = Path.Combine(root, "run.out");
        var stderrPath = Path.Combine(root, "run.err");
        var childPid = Environment.ProcessId;

        try
        {
            TryDeleteStableSlotHeartbeat(buildSlot);

            // The attempt-results prefix is exactly what made gate-status structurally blind: it forces the
            // PRIMARY heartbeat onto an attempt-scoped path that GateHeartbeatArtifacts.ReadStableSlots never
            // reads.
            string firstPrimaryPath;
            string? firstMirrorPath;
            using (GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(
                Path.Combine(root, "attempt-owner-a")))
            {
                (firstPrimaryPath, firstMirrorPath) = GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                    "infrastructure lane",
                    firstGoalId,
                    environment,
                    childPid,
                    stdoutPath,
                    stderrPath);
            }

            var expectedStableSlotPath = GateHeartbeatArtifacts.GetStableSlotPath(buildSlot);

            // Blindness precondition: with the attempt prefix active the primary heartbeat is attempt-scoped
            // and is NOT the stable slot path gate-status reads.
            Assert.StartsWith(
                Path.Combine(root, "attempt-owner"),
                firstPrimaryPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(expectedStableSlotPath, firstPrimaryPath);

            Assert.Equal(
                GateHeartbeatArtifacts.GetRunScopedStableSlotPath(buildSlot, firstPrimaryPath),
                firstMirrorPath);
            Assert.True(
                File.Exists(firstMirrorPath),
                $"Run-scoped heartbeat mirror missing: {firstMirrorPath}");

            string secondPrimaryPath;
            string? secondMirrorPath;
            using (GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(
                Path.Combine(root, "attempt-owner-b")))
            {
                (secondPrimaryPath, secondMirrorPath) = GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                    "infrastructure lane",
                    secondGoalId,
                    environment,
                    childPid + 1,
                    stdoutPath,
                    stderrPath);
            }
            Assert.NotEqual(firstMirrorPath, secondMirrorPath);

            var runningSlots = GateHeartbeatArtifacts.ReadStableSlots()
                .Where(status => status.SlotIndex == buildSlot)
                .ToArray();
            Assert.Equal(2, runningSlots.Length);
            Assert.Contains(runningSlots, status =>
                status.Snapshot?.GoalId == firstGoalId.Value &&
                status.Snapshot.State == "running");
            Assert.Contains(runningSlots, status =>
                status.Snapshot?.GoalId == secondGoalId.Value &&
                status.Snapshot.State == "running");

            using (GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(
                Path.Combine(root, "attempt-owner-a")))
            {
                GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                    "infrastructure lane",
                    firstGoalId,
                    environment,
                    childPid,
                    stdoutPath,
                    stderrPath,
                    finalState: "completed");
            }

            Assert.False(File.Exists(firstMirrorPath));
            var remainingSlot = GateHeartbeatArtifacts.ReadStableSlots()
                .Single(status => status.SlotIndex == buildSlot);
            Assert.Equal(secondMirrorPath, remainingSlot.Path);
            Assert.Equal("running", remainingSlot.Snapshot?.State);
            Assert.Equal(secondGoalId.Value, remainingSlot.Snapshot?.GoalId);

            using (GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(
                Path.Combine(root, "attempt-owner-b")))
            {
                GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                    "infrastructure lane",
                    secondGoalId,
                    environment,
                    childPid,
                    stdoutPath,
                    stderrPath,
                    finalState: "completed");
            }

            string? orphanMirrorPath;
            using (GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(
                Path.Combine(root, "attempt-owner-orphan")))
            {
                (_, orphanMirrorPath) = GoalAcceptanceVerifier.WriteGateHeartbeatBeatForTests(
                    "infrastructure lane",
                    secondGoalId,
                    environment,
                    int.MaxValue,
                    stdoutPath,
                    stderrPath);
            }

            Assert.NotNull(orphanMirrorPath);
            Assert.True(File.Exists(orphanMirrorPath));
            var afterOrphanPrune = GateHeartbeatArtifacts.ReadStableSlots()
                .Single(status => status.SlotIndex == buildSlot);
            Assert.Equal("missing", afterOrphanPrune.UnavailableReason);
            Assert.False(File.Exists(orphanMirrorPath));
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(buildSlot);
            try { DeleteDirectoryWithRetry(root); } catch { }
        }
    }

    [OptInRealAcceptanceVerifierFact(DisplayName = "GoalAcceptanceVerifier_real_runner_smoke_is_opt_in")]
    [Xunit.Trait("Category", "AcceptanceOptIn")]
    public async Task GoalAcceptanceVerifierRealRunnerSmokeIsOptIn()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "dotnet info smoke", "type": "command", "command": "dotnet", "arguments": ["--info"], "timeoutMinutes": 1 }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier();

            var result = await verifier.RunAsync(root);

            Assert.True(result.Passed, result.OutputTail);
            Assert.Contains(result.Checks!, check => check.Name == "dotnet info smoke" && check.Passed);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_invokes_dotnet_test_with_isolated_script_arguments")]
    public async Task GoalAcceptanceVerifierInvokesDotnetTestWithIsolatedScriptArguments()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "full dotnet tests", "type": "dotnet-test", "project": "Mcg.AgentOrchestrator.sln", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            });

            var goalId = new GoalId("abcdef12abcdef12abcdef12abcdef12");
            var result = await verifier.RunAsync(root, goalId);

            Assert.True(result.Passed);
            Assert.Equal(2, calls.Count);
            Assert.True(calls[0].SequenceEqual(["dotnet", "build-server", "shutdown"]));

            var dotnetArgs = calls[1];
            AssertIsolatedTestCommand(dotnetArgs);
            Assert.Equal("Mcg.AgentOrchestrator.sln", dotnetArgs[2]);
            Assert.Contains("--filter", dotnetArgs);
            Assert.Contains("FullyQualifiedName!~DashboardHostTests&Category!=HostIntegration", dotnetArgs);
            Assert.Contains("--blame-hang-timeout", dotnetArgs);
            Assert.Contains("120s", dotnetArgs);
            Assert.Equal("goal-abcdef12", result.Checks!.Single().LeaseId);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_focused_evidence_uses_slot_routed_mtp_executable")]
    public async Task GoalAcceptanceVerifierFocusedEvidenceUsesSlotRoutedMtpExecutable()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunFocusedEvidenceAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                "Infrastructure.Tests: ConductorDriverTests,GoalAcceptanceVerifierTests",
                stableSlotIndex: 0);

            Assert.True(result.Accepted);
            Assert.True(result.Passed);
            // The focused-evidence target projects are MTP, so the run must route through the MTP
            // executable, NOT `dotnet test` (which fails against MTP projects on .NET 10).
            Assert.DoesNotContain(calls, call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test");
            var mtpCall = calls.Single(call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"));
            Assert.Contains("--report-trx", mtpCall);
            Assert.Contains("--filter-class", mtpCall);
            Assert.Equal("goal-abcdef12", result.Checks.Single().LeaseId);
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_mtp_runner_uses_slot_executable_trx_filters_and_coexists_with_vstest")]
    public async Task GoalAcceptanceVerifierMtpRunnerUsesSlotExecutableTrxFiltersAndCoexistsWithVstest()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal", "--filter", "FullyQualifiedName~GoalLifecycleTests&FullyQualifiedName!~SlowCoreTests&Category!=HostIntegration"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Core.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3."));
                }

                return Task.FromResult(args.Length >= 2 && args[0] == "dotnet" && args[1] == "test"
                    ? new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2.")
                    : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                stableSlotIndex: 0);

            Assert.True(result.Passed);
            var artifactsPath = DotnetBuildEnvironmentManager.GoalArtifactsPath(
                new GoalId("abcdef12abcdef12abcdef12abcdef12"));
            Assert.Contains(calls, call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "build");
            Assert.Contains(calls, call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj");
            Assert.DoesNotContain(calls, call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2].Contains("Mcg.AgentOrchestrator.Core.Tests", StringComparison.Ordinal));

            var mtpCall = calls.Single(call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests"));
            Assert.Equal(Path.Combine(artifactsPath, "bin", "Mcg.AgentOrchestrator.Core.Tests", "debug", "Mcg.AgentOrchestrator.Core.Tests.exe"), mtpCall[0]);
            AssertArgumentPair(mtpCall, "--filter-class", "*GoalLifecycleTests*");
            AssertArgumentPair(mtpCall, "--filter-not-class", "*SlowCoreTests*");
            Assert.Contains("--filter-not-trait", mtpCall);
            Assert.Contains("Category=HostIntegration", mtpCall);
            Assert.Contains("--results-directory", mtpCall);
            Assert.Contains("--report-trx", mtpCall);
            Assert.Contains("--report-trx-filename", mtpCall);
            Assert.Single(result.Checks!.Single(check => check.Name == "core tests").TestResultPaths!);
            Assert.True(File.Exists(result.Checks!.Single(check => check.Name == "core tests").TestResultPaths!.Single()));
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_extracts_all_failed_tests_from_real_mtp_trx_fixture")]
    public void GoalAcceptanceVerifierExtractsAllFailedTestsFromRealMtpTrxFixture()
    {
        var fixturePath = MtpFailureFixturePath();

        var evidence = GoalAcceptanceVerifier.ExtractTrxFailureEvidence(fixturePath);

        Assert.Equal(
            [
                "[FAIL] Retry evidence includes failed test names: Assert.Contains() Failure: Sub-string not found",
                "[FAIL] MTP shard preserves theory display name(value: 42): Expected shard count to be 2, but found 1"
            ],
            evidence);
        Assert.DoesNotContain(evidence, line =>
            line.Contains("Passing MTP test is not surfaced", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_failed_mtp_shard_surfaces_fixture_test_names_and_messages")]
    public async Task GoalAcceptanceVerifierFailedMtpShardSurfacesFixtureTestNamesAndMessages()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests: Cli", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal", "--filter", "FullyQualifiedName~CliCommandTests"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var previousPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(root, "TestResults", "mtp-fixture"));
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    WriteMtpTrx(args, MtpFailureFixturePath());
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        "Failed! - Failed: 2, Passed: 1, Skipped: 0, Total: 3."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, stableSlotIndex: 0);

            Assert.False(result.Passed);
            var output = Assert.Single(result.Checks!).OutputTail;
            Assert.Contains(
                "[FAIL] Retry evidence includes failed test names: Assert.Contains() Failure: Sub-string not found",
                output,
                StringComparison.Ordinal);
            Assert.Contains(
                "[FAIL] MTP shard preserves theory display name(value: 42): Expected shard count to be 2, but found 1",
                output,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Passing MTP test is not surfaced", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previousPrefix);
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Theory(DisplayName = "GoalAcceptanceVerifier_failed_mtp_shard_explains_missing_or_empty_trx")]
    [Xunit.InlineData(false, "failed — no TRX produced (shard was killed or crashed before reporter flushed)")]
    [Xunit.InlineData(true, "failed — TRX found but contained no failure records (process may have exited before tests ran)")]
    public async Task GoalAcceptanceVerifierFailedMtpShardExplainsMissingOrEmptyTrx(
        bool writeEmptyTrx,
        string expectedEvidence)
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests: Cli", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal", "--filter", "FullyQualifiedName~CliCommandTests"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var previousPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(root, "TestResults", writeEmptyTrx ? "empty-trx" : "missing-trx"));
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    if (writeEmptyTrx)
                    {
                        WriteMtpTrx(args);
                    }

                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, stableSlotIndex: 0);

            Assert.False(result.Passed);
            Assert.Contains(
                $"[FAIL] infrastructure tests: Cli: {expectedEvidence}",
                Assert.Single(result.Checks!).OutputTail,
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previousPrefix);
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_failed_mtp_shard_does_not_reuse_stale_trx")]
    public async Task GoalAcceptanceVerifierFailedMtpShardDoesNotReuseStaleTrx()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests: Cli", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal", "--filter", "FullyQualifiedName~CliCommandTests"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var previousPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        try
        {
            var attemptPrefix = Path.Combine(root, "TestResults", "reused-stable-slot");
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                attemptPrefix);
            var staleTrxPath = $"{attemptPrefix}.infrastructure-tests-cli.trx";
            Directory.CreateDirectory(Path.GetDirectoryName(staleTrxPath)!);
            File.Copy(MtpFailureFixturePath(), staleTrxPath);
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        "Test process exited before the reporter flushed."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, stableSlotIndex: 0);

            Assert.False(result.Passed);
            var output = Assert.Single(result.Checks!).OutputTail;
            Assert.Contains(
                "[FAIL] infrastructure tests: Cli: failed — no TRX produced (shard was killed or crashed before reporter flushed)",
                output,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Retry evidence includes failed test names", output, StringComparison.Ordinal);
            Assert.False(File.Exists(staleTrxPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previousPrefix);
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_vstest_failure_stdout_remains_unchanged")]
    public async Task GoalAcceptanceVerifierVstestFailureStdoutRemainsUnchanged()
    {
        const string failureOutput =
            "[xUnit.net 00:00:01.23]     Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]\n" +
            "Assert.True() Failure";
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
                Task.FromResult(args.Length >= 2 && args[0] == "dotnet" && args[1] == "test"
                    ? new GoalAcceptanceVerifier.CommandResult(1, failureOutput)
                    : new GoalAcceptanceVerifier.CommandResult(0, "")));

            var result = await verifier.RunAsync(root);

            Assert.False(result.Passed);
            Assert.Equal(failureOutput, Assert.Single(result.Checks!).OutputTail);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_mtp_runner_omits_total_session_timeout")]
    public async Task GoalAcceptanceVerifierMtpRunnerOmitsTotalSessionTimeout()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Core.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                stableSlotIndex: 0);

            Assert.True(result.Passed);
            var mtpCall = calls.Single(call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests"));
            Assert.DoesNotContain(mtpCall, argument => argument.Equals("--timeout", StringComparison.OrdinalIgnoreCase));
            var longRunningIndex = Array.IndexOf(mtpCall, "--long-running");
            Assert.True(longRunningIndex >= 0);
            Assert.True(longRunningIndex + 1 < mtpCall.Length);
            Assert.Equal("120", mtpCall[longRunningIndex + 1]);
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_mtp_runner_infrastructure_remainder_shard_uses_slot_executable")]
    public async Task GoalAcceptanceVerifierMtpRunnerInfrastructureRemainderShardUsesSlotExecutable()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(args.Length >= 3 &&
                    args[0] == "dotnet" &&
                    args[1] == "test" &&
                    args[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                    ? new GoalAcceptanceVerifier.CommandResult(1, "unexpected vstest fallback")
                    : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                stableSlotIndex: 0);

            Assert.True(result.Passed);
            var shardChecks = result.Checks!
                .Where(check => check.Name.StartsWith("infrastructure tests: ", StringComparison.Ordinal))
                .ToArray();
            var mtpCalls = calls
                .Where(call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                .ToArray();
            Assert.Equal(shardChecks.Length, mtpCalls.Length);
            Assert.Contains(mtpCalls, call => HasArgumentPair(call, "--filter-class", "*CliCommandTests*"));
            Assert.Contains(mtpCalls, call =>
                HasArgumentPair(call, "--filter-not-class", "*CliCommandTests*") &&
                HasArgumentPair(call, "--filter-not-class", "*GoalAcceptanceVerifierTests*"));
            Assert.DoesNotContain(calls, call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj");
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_dotnet_test_without_runner_uses_vstest")]
    public async Task GoalAcceptanceVerifierDotnetTestWithoutRunnerUsesVstest()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(args.Length >= 2 && args[0] == "dotnet" && args[1] == "test"
                    ? new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")
                    : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                stableSlotIndex: 0);

            Assert.True(result.Passed);
            Assert.Contains(calls, call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj" &&
                call.Contains("--no-build"));
            Assert.DoesNotContain(calls, call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests"));
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_dotnet_test_unknown_runner_fails_loudly")]
    public async Task GoalAcceptanceVerifierDotnetTestUnknownRunnerFailsLoudly()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "runner": "typo", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "should not run tests"));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                stableSlotIndex: 0);

            Assert.False(result.Passed);
            var check = Assert.Single(result.Checks!);
            Assert.False(check.Passed);
            Assert.Contains("unrecognized runner 'typo'", check.OutputTail);
            Assert.DoesNotContain(calls, call => call.Length > 1 && call[0] == "dotnet" && call[1] == "test");
            Assert.DoesNotContain(calls, call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests"));
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_mtp_runner_without_build_phase_builds_project_before_executable")]
    public async Task GoalAcceptanceVerifierMtpRunnerWithoutBuildPhaseBuildsProjectBeforeExecutable()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var goalId = new GoalId("cccccccccccccccccccccccccccccccc");
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Core.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, goalId);

            Assert.True(result.Passed);
            var buildIndex = calls.FindIndex(call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "build" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj");
            var mtpIndex = calls.FindIndex(call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests"));
            Assert.True(buildIndex >= 0);
            Assert.True(mtpIndex > buildIndex);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_rejects_unbounded_focused_evidence_request")]
    public async Task GoalAcceptanceVerifierRejectsUnboundedFocusedEvidenceRequest()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "should not run"));
            });

            var result = await verifier.RunFocusedEvidenceAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                "Infrastructure.Tests: all");

            Assert.False(result.Accepted);
            Assert.False(result.Passed);
            Assert.Contains("unbounded evidence request rejected", result.Summary);
            Assert.Empty(result.Checks);
            Assert.Empty(calls);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_builds_once_and_runs_later_tests_no_build")]
    public async Task GoalAcceptanceVerifierSlotGateBuildsOnceAndRunsLaterTestsNoBuild()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "first tests", "type": "dotnet-test", "project": "tests/First.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "second tests", "type": "dotnet-test", "project": "tests/Second.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var firstTestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var verifier = new GoalAcceptanceVerifier(async (args, _, _) =>
            {
                calls.Add(args);
                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "test" && args.Contains("tests/First.Tests.csproj"))
                {
                    firstTestStarted.SetResult();
                    await releaseFirstTest.Task.ConfigureAwait(false);
                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.");
                }

                return new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length >= 2 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : "Build succeeded.");
            });

            var run = verifier.RunAsync(root, new GoalId("11112222333344445555666677778888"), stableSlotIndex: 0);
            await firstTestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Task.Delay(100);
            Assert.DoesNotContain(calls, call => call.Length >= 3 && call[0] == "dotnet" && call[1] == "build" && call[2] == "tests/Second.Tests.csproj");

            releaseFirstTest.SetResult();
            var result = await run;

            Assert.True(result.Passed);
            var buildCalls = calls
                .Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "build")
                .ToArray();
            var firstTestIndex = calls.FindIndex(call => call.Length >= 3 && call[0] == "dotnet" && call[1] == "test" && call[2] == "tests/First.Tests.csproj");
            var secondTestIndex = calls.FindIndex(call => call.Length >= 3 && call[0] == "dotnet" && call[1] == "test" && call[2] == "tests/Second.Tests.csproj");
            Assert.Single(buildCalls);
            Assert.Equal("tests/First.Tests.csproj", buildCalls[0][2]);
            Assert.True(firstTestIndex > 0);
            Assert.True(secondTestIndex > firstTestIndex);
            Assert.DoesNotContain(calls, call => call.Length >= 3 && call[0] == "dotnet" && call[1] == "build" && call[2] == "tests/Second.Tests.csproj");
            Assert.All(calls.Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test"), call => Assert.Contains("--no-build", call));
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_retry_reaps_recorded_heartbeat_child_before_rebuild")]
    public void GoalAcceptanceVerifierSlotGateRetryReapsRecordedHeartbeatChildBeforeRebuild()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var buildAttempts = 0;
        var sleeper = StartSleepProcess();
        var goalId = new GoalId("99998888777766665555444433332222");
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
            "handle64-timeout");

        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    buildAttempts++;
                    if (buildAttempts == 1)
                    {
                        var artifactsPath = GetArtifactsPath(args);
                        var heartbeatEnvironment =
                            DotnetBuildEnvironmentManager.ResolveGoalEnvironment(goalId);
                        var heartbeatPath =
                            GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
                                "core tests",
                                heartbeatEnvironment,
                                stableSlotIndex: 0);
                        GateHeartbeatArtifacts.Write(
                            heartbeatPath,
                            new GateHeartbeatSnapshot(
                                "99998888777766665555444433332222",
                                "verification-check",
                                "core tests",
                                0,
                                sleeper.Id,
                                sleeper.Id,
                                "running",
                                DateTimeOffset.UtcNow,
                                DateTimeOffset.UtcNow,
                                DateTimeOffset.UtcNow,
                                0,
                                0,
                                0,
                                $"dotnet test --artifacts-path {artifactsPath}"));
                        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                            1,
                            $"error CS2012: Cannot open '{Path.Combine(artifactsPath, "bin", "Core.dll")}' for writing because it is being used by another process."));
                    }

                    Assert.True(sleeper.HasExited);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            });

            AcceptanceVerificationResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(root, goalId, stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());

            Assert.NotNull(result);
            Assert.True(result!.Passed);
            Assert.True(result.Retried);
            Assert.Equal(2, buildAttempts);
            Assert.Contains("LOCK_CONTEXT ", output, StringComparison.Ordinal);
            Assert.Contains("heartbeat_child_alive=true", output, StringComparison.Ordinal);
            Assert.Contains($"holderPid={sleeper.Id}", output, StringComparison.Ordinal);
            Assert.Contains("source=handle64-timeout+gate-context", output, StringComparison.Ordinal);
            Assert.Contains(calls, call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test" && call.Contains("--no-build"));
        }
        finally
        {
            LockAttribution.AttributeForTests = null;
            if (!sleeper.HasExited)
            {
                try { sleeper.Kill(entireProcessTree: true); } catch { }
            }

            sleeper.Dispose();
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_lock_context_keeps_fast_exit_child_output_evidence")]
    public void GoalAcceptanceVerifierSlotGateLockContextKeepsFastExitChildOutputEvidence()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var buildAttempts = 0;
        var goalId = new GoalId("aaaabbbbccccddddeeeeffff00001111");
        var stdoutPath = Path.Combine(Path.GetTempPath(), $"mcg-acc-test-{Guid.NewGuid():N}.out");
        var stderrPath = Path.Combine(Path.GetTempPath(), $"mcg-acc-test-{Guid.NewGuid():N}.err");
        File.WriteAllText(stdoutPath, "fast child stdout");
        File.WriteAllText(stderrPath, "fast child stderr");
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
            "handle64-timeout");

        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    buildAttempts++;
                    if (buildAttempts == 1)
                    {
                        var artifactsPath = GetArtifactsPath(args);
                        var lockedPath = Path.Combine(artifactsPath, "bin", "Core.dll");
                        var heartbeatEnvironment =
                            DotnetBuildEnvironmentManager.ResolveGoalEnvironment(goalId);
                        var heartbeatPath =
                            GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
                                "core tests",
                                heartbeatEnvironment,
                                stableSlotIndex: 0);
                        GateHeartbeatArtifacts.Write(
                            heartbeatPath,
                            new GateHeartbeatSnapshot(
                                "aaaabbbbccccddddeeeeffff00001111",
                                "verification-check",
                                "core tests",
                                0,
                                999999,
                                999999,
                                "completed",
                                DateTimeOffset.UtcNow.AddSeconds(-18),
                                DateTimeOffset.UtcNow,
                                DateTimeOffset.UtcNow,
                                new FileInfo(stdoutPath).Length,
                                new FileInfo(stderrPath).Length,
                                new FileInfo(stdoutPath).Length + new FileInfo(stderrPath).Length,
                                $"dotnet test --artifacts-path {artifactsPath}",
                                1,
                                stdoutPath,
                                stderrPath));
                        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                            1,
                            $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process.",
                            StdoutPath: stdoutPath,
                            StderrPath: stderrPath,
                            Elapsed: TimeSpan.FromSeconds(18),
                            StdoutBytes: new FileInfo(stdoutPath).Length,
                            StderrBytes: new FileInfo(stderrPath).Length));
                    }

                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            });

            AcceptanceVerificationResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(root, goalId, stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());

            Assert.NotNull(result);
            Assert.True(result!.Passed);
            Assert.True(result.Retried);
            Assert.Equal(2, buildAttempts);
            Assert.Contains("LOCK_CONTEXT ", output, StringComparison.Ordinal);
            Assert.Contains("exit_code=1", output, StringComparison.Ordinal);
            Assert.Contains("elapsed_ms=18000", output, StringComparison.Ordinal);
            Assert.Contains("stdout_bytes=17", output, StringComparison.Ordinal);
            Assert.Contains("stderr_bytes=17", output, StringComparison.Ordinal);
            Assert.Contains($"stdout={stdoutPath}", output, StringComparison.Ordinal);
            Assert.Contains($"stderr={stderrPath}", output, StringComparison.Ordinal);
            Assert.Contains("heartbeat_state=completed", output, StringComparison.Ordinal);
            Assert.Contains("heartbeat_child_alive=false", output, StringComparison.Ordinal);
            Assert.Contains("heartbeat_output_bytes=34", output, StringComparison.Ordinal);
        }
        finally
        {
            LockAttribution.AttributeForTests = null;
            TryDeleteStableSlotHeartbeat(0);
            try { File.Delete(stdoutPath); } catch { }
            try { File.Delete(stderrPath); } catch { }
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_records_job_resource_receipt")]
    public async Task GoalAcceptanceVerifierSlotGateRecordsJobResourceReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "missing project gate", "type": "dotnet-test", "project": "MissingProject.csproj", "arguments": ["--verbosity", "minimal"], "timeoutMinutes": 1 }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier();
            var result = await verifier.RunAsync(
                root,
                new GoalId("24682468246824682468246824682468"),
                stableSlotIndex: 0);

            Assert.False(result.Passed);
            var check = Assert.Single(result.Checks!);
            Assert.Equal("missing project gate", check.Name);
            Assert.True(check.ResultSummary?.Contains("RESOURCE phase=gate", StringComparison.Ordinal) == true);
            Assert.True(check.ResultSummary?.Contains("cpu_ms=", StringComparison.Ordinal) == true);
            Assert.True(check.ResultSummary?.Contains("peak_mem_bytes=", StringComparison.Ordinal) == true);
            Assert.True(check.ResultSummary?.Contains("io_bytes=", StringComparison.Ordinal) == true);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_timeout_records_job_resource_receipt")]
    public async Task GoalAcceptanceVerifierSlotGateTimeoutRecordsJobResourceReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "slow build gate", "type": "command", "command": "dotnet", "arguments": ["build", "SlowGate.csproj", "--nologo"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var originalTimeout = AppContext.GetData(AcceptanceCheckTimeouts.AppContextKey);
        AppContext.SetData(AcceptanceCheckTimeouts.AppContextKey, TimeSpan.FromMilliseconds(500));
        try
        {
            File.WriteAllText(Path.Combine(root, "SlowGate.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <Target Name="SleepBeforeBuild" BeforeTargets="CoreCompile">
                    <Exec Command="powershell -NoProfile -ExecutionPolicy Bypass -Command &quot;Start-Sleep -Seconds 30&quot;" />
                  </Target>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "Program.cs"), "Console.WriteLine(\"slow gate\");");

            var verifier = new GoalAcceptanceVerifier();
            var result = await verifier.RunAsync(
                root,
                new GoalId("24682468246824682468246824682468"),
                stableSlotIndex: 0);

            Assert.False(result.Passed);
            var check = Assert.Single(result.Checks!);
            Assert.StartsWith("acceptance-check-timeout: slow-build-gate", check.Name, StringComparison.Ordinal);
            Assert.True(check.ResultSummary?.Contains("RESOURCE phase=gate", StringComparison.Ordinal) == true);
            Assert.True(check.ResultSummary?.Contains("cpu_ms=", StringComparison.Ordinal) == true);
            Assert.True(check.ResultSummary?.Contains("peak_mem_bytes=", StringComparison.Ordinal) == true);
            Assert.True(check.ResultSummary?.Contains("io_bytes=", StringComparison.Ordinal) == true);
        }
        finally
        {
            AppContext.SetData(AcceptanceCheckTimeouts.AppContextKey, originalTimeout);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partitions_checked_in_infrastructure_manifest_check")]
    public async Task GoalAcceptanceVerifierPartitionsCheckedInInfrastructureManifestCheck()
    {
        var calls = new List<string[]>();
        var root = CreateCheckedInManifestShapeWorkspace();
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : ""));
            });

            var result = await verifier.RunAsync(root);

            Assert.True(result.Passed);
            var checks = result.Checks ?? throw new InvalidOperationException("Expected acceptance checks.");
            string[] expectedInfrastructureChecks =
            [
                "infrastructure tests: Cli",
                "infrastructure tests: Worker shell",
                "infrastructure tests: Worker sandbox planner",
                "infrastructure tests: Dashboard validation",
                "infrastructure tests: Conduct watch sweep scoping",
                "infrastructure tests: Goal lifecycle commands",
                "infrastructure tests: Goal worktree cleanup",
                "infrastructure tests: Worker profiles",
                "infrastructure tests: Worker dispatch fixtures",
                "infrastructure tests: Process spawning",
                "infrastructure tests: Chaos gate",
                "infrastructure tests: Dotnet build slots",
                "infrastructure tests: Goal acceptance verifier",
                "infrastructure tests: Goal acceptance build slots",
                "infrastructure tests: Provider environment",
                "infrastructure tests: Remainder balance A",
                "infrastructure tests: Remainder balance B",
                "infrastructure tests: Remainder"
            ];
            Assert.Equal(
                expectedInfrastructureChecks.Order(StringComparer.Ordinal),
                checks
                    .Where(check => check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal))
                    .Select(check => check.Name)
                    .Order(StringComparer.Ordinal));
            Assert.False(checks.Any(check => check.Name == "infrastructure tests"));

            var infrastructureCalls = calls
                .Where(call => call.Length > 2 &&
                    call[0] == "dotnet" &&
                    call[1] == "test" &&
                    call[2].EndsWith("Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", StringComparison.Ordinal))
                .ToList();

            Assert.DoesNotContain(infrastructureCalls, call => !call.Contains("--filter"));
            Assert.Contains(infrastructureCalls, call => call.Contains(CheckedInCliLaneFilter));
            Assert.Contains(infrastructureCalls, call => call.Contains(CheckedInGoalAcceptanceVerifierLaneFilter));
            Assert.Contains(infrastructureCalls, call => call.Contains(CheckedInGoalAcceptanceBuildSlotsLaneFilter));
            Assert.DoesNotContain(infrastructureCalls, call => call.Contains("FullyQualifiedName~DashboardHostTests&Category!=HostIntegration"));
            Assert.Contains(infrastructureCalls, call =>
                call.Any(argument =>
                    argument.Contains("FullyQualifiedName!~GoalAcceptanceVerifierTests", StringComparison.Ordinal) &&
                    argument.Contains("FullyQualifiedName!~DashboardRenderingTests", StringComparison.Ordinal) &&
                    argument.Contains("Category!=HostIntegration", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_builds_solution_once_for_partitioned_targets")]
    public async Task GoalAcceptanceVerifierSlotGateBuildsSolutionOnceForPartitionedTargets()
    {
        var calls = new List<string[]>();
        var root = CreateCheckedInManifestShapeWorkspace();
        File.WriteAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln"), string.Empty);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("12345678123456781234567812345678"),
                changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"],
                stableSlotIndex: 0);

            Assert.True(result.Passed);
            var buildCalls = calls
                .Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "build")
                .ToArray();
            var testCalls = calls
                .Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test")
                .ToArray();
            Assert.Single(buildCalls);
            Assert.Contains("Mcg.AgentOrchestrator.sln", buildCalls[0], StringComparer.OrdinalIgnoreCase);
            Assert.All(testCalls, call => Assert.Contains("--no-build", call));
            Assert.DoesNotContain(testCalls, call => call.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
            Assert.DoesNotContain(calls.SkipWhile(call => call != buildCalls[0]).Skip(1),
                call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "build");
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_concurrent_shards_preserve_sequential_mixed_verdicts")]
    public async Task GoalAcceptanceVerifierConcurrentShardsPreserveSequentialMixedVerdicts()
    {
        GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = () => 2;
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        SetPartitionVerdictKeyHooks("tree-concurrency", "main-concurrency", "commit-concurrency");
        var alphaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remainderFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionOrder = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var activeShardWorkers = 0;
        var peakShardWorkers = 0;
        try
        {
            static Task<GoalAcceptanceVerifier.CommandResult> RunSequentialFixedVerdict(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (args.Length > 0 && args[0] == "dotnet")
                {
                    if (args.Length >= 2 && args[1] == "build")
                    {
                        var executable = Path.Combine(
                            GetArtifactsPath(args),
                            "bin",
                            "Mcg.AgentOrchestrator.Infrastructure.Tests",
                            "debug",
                            "Mcg.AgentOrchestrator.Infrastructure.Tests.exe");
                        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                        File.WriteAllText(executable, "deterministic shard fixture");
                    }

                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                WriteMtpTrx(args);
                var filterClassIndex = Array.IndexOf(args, "--filter-class");
                var alpha = filterClassIndex >= 0 &&
                    args[filterClassIndex + 1].Contains("AlphaShardTests", StringComparison.Ordinal);
                return Task.FromResult(alpha
                    ? new GoalAcceptanceVerifier.CommandResult(7, "alpha failed deterministically")
                    : new GoalAcceptanceVerifier.CommandResult(0, "passed deterministically"));
            }

            async Task<GoalAcceptanceVerifier.CommandResult> RunConcurrentFixedVerdict(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (args.Length > 0 && args[0] == "dotnet")
                {
                    if (args.Length >= 2 && args[1] == "build")
                    {
                        var executable = Path.Combine(
                            GetArtifactsPath(args),
                            "bin",
                            "Mcg.AgentOrchestrator.Infrastructure.Tests",
                            "debug",
                            "Mcg.AgentOrchestrator.Infrastructure.Tests.exe");
                        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                        File.WriteAllText(executable, "deterministic shard fixture");
                    }

                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                WriteMtpTrx(args);
                var active = Interlocked.Increment(ref activeShardWorkers);
                int observedPeak;
                do
                {
                    observedPeak = Volatile.Read(ref peakShardWorkers);
                }
                while (active > observedPeak &&
                       Interlocked.CompareExchange(ref peakShardWorkers, active, observedPeak) != observedPeak);

                var filterClassIndex = Array.IndexOf(args, "--filter-class");
                var alpha = filterClassIndex >= 0 &&
                    args[filterClassIndex + 1].Contains("AlphaShardTests", StringComparison.Ordinal);
                try
                {
                    if (alpha)
                    {
                        alphaStarted.TrySetResult();
                        await remainderFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        completionOrder.Enqueue("Alpha");
                        return new GoalAcceptanceVerifier.CommandResult(7, "alpha failed deterministically");
                    }

                    await alphaStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    completionOrder.Enqueue("Remainder");
                    remainderFinished.TrySetResult();
                    return new GoalAcceptanceVerifier.CommandResult(0, "passed deterministically");
                }
                finally
                {
                    Interlocked.Decrement(ref activeShardWorkers);
                }
            }

            static async Task<AcceptanceVerificationResult> RunScenarioAsync(
                int maxConcurrentShards,
                Func<string[], string, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> runner,
                string goalId)
            {
                var root = CreateManifestWorkspace($$"""
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
                        ],
                        "mtpInvocations": [
                          {
                            "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                            "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                            "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                            "arguments": [
                              "{executable}",
                              "--no-ansi",
                              "--progress",
                              "off",
                              "--results-directory",
                              "{resultsDirectory}",
                              "--report-trx",
                              "--report-trx-filename",
                              "{trxFileName}"
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

            var sequential = await RunScenarioAsync(
                1,
                RunSequentialFixedVerdict,
                "11111111111111111111111111111111");
            var concurrent = await RunScenarioAsync(
                2,
                RunConcurrentFixedVerdict,
                "22222222222222222222222222222222");
            var sequentialShards = sequential.Checks!
                .Where(check => check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal))
                .Select(check => (check.Name, check.Passed, check.ExitCode, check.OutputTail))
                .ToArray();
            var concurrentShards = concurrent.Checks!
                .Where(check => check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal))
                .Select(check => (check.Name, check.Passed, check.ExitCode, check.OutputTail))
                .ToArray();

            Assert.False(sequential.Passed);
            Assert.False(concurrent.Passed);
            Assert.Equal(sequential.ExitCode, concurrent.ExitCode);
            Assert.Equal(sequential.OutputTail, concurrent.OutputTail);
            Assert.Equal(sequentialShards, concurrentShards);
            Assert.Equal(2, peakShardWorkers);
            Assert.Equal(new[] { "Remainder", "Alpha" }, completionOrder);
            Assert.Collection(
                concurrentShards,
                alpha =>
                {
                    Assert.Equal("infrastructure tests: Alpha", alpha.Name);
                    Assert.False(alpha.Passed);
                    Assert.Equal(7, alpha.ExitCode);
                    Assert.NotNull(alpha.OutputTail);
                    Assert.StartsWith("alpha failed deterministically", alpha.OutputTail);
                    Assert.Contains(
                        "[FAIL] infrastructure tests: Alpha:",
                        alpha.OutputTail,
                        StringComparison.Ordinal);
                },
                remainder =>
                {
                    Assert.Equal("infrastructure tests: Remainder", remainder.Name);
                    Assert.True(remainder.Passed);
                    Assert.Equal(0, remainder.ExitCode);
                    Assert.Null(remainder.OutputTail);
                });
        }
        finally
        {
            GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = null;
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_exclusive_resource_key_order_is_exact_and_declaration_independent")]
    public void GoalAcceptanceVerifierExclusiveResourceKeyOrderIsExactAndDeclarationIndependent()
    {
        string[] expected = ["alpha-only", "Beta-only", "shared-a"];

        Assert.Equal(
            expected,
            GoalAcceptanceVerifier.OrderExclusiveResourceKeys(
                [" shared-a ", "Beta-only", "alpha-only"]));
        Assert.Equal(
            expected,
            GoalAcceptanceVerifier.OrderExclusiveResourceKeys(
                ["alpha-only", "Beta-only", " shared-a "]));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_exclusive_resource_waiters_do_not_block_disjoint_shards")]
    public async Task GoalAcceptanceVerifierExclusiveResourceWaitersDoNotBlockDisjointShards()
    {
        GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = () => 2;
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        SetPartitionVerdictKeyHooks("tree-resources", "main-resources", "commit-resources");
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  {
                    "name": "Conflict alpha",
                    "filter": "FullyQualifiedName~ConflictAlphaTests",
                    "estimatedSerialSeconds": 100,
                    "exclusiveResourceKeys": [ "shared-a", "alpha-only" ]
                  },
                  {
                    "name": "Conflict beta",
                    "filter": "FullyQualifiedName~ConflictBetaTests",
                    "estimatedSerialSeconds": 90,
                    "exclusiveResourceKeys": [ "shared-a", "beta-only" ]
                  },
                  {
                    "name": "Disjoint",
                    "filter": "FullyQualifiedName~DisjointTests",
                    "estimatedSerialSeconds": 80,
                    "exclusiveResourceKeys": [ "disjoint" ]
                  }
                ],
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [
                      "{executable}",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx-filename",
                      "{trxFileName}"
                    ]
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var firstConflictStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disjointStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstConflictFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeExecutions = 0;
        var peakExecutions = 0;
        var activeConflicts = 0;
        var peakConflicts = 0;
        var conflictEntries = 0;
        var disjointOverlappedConflict = 0;
        try
        {
            static void RecordPeak(ref int peak, int active)
            {
                int observed;
                do
                {
                    observed = Volatile.Read(ref peak);
                }
                while (active > observed &&
                       Interlocked.CompareExchange(ref peak, active, observed) != observed);
            }

            async Task<GoalAcceptanceVerifier.CommandResult> RunShardAsync(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (args.Length > 0 && args[0] == "dotnet")
                {
                    if (args.Length >= 2 && args[1] == "build")
                    {
                        var executable = Path.Combine(
                            GetArtifactsPath(args),
                            "bin",
                            "Mcg.AgentOrchestrator.Infrastructure.Tests",
                            "debug",
                            "Mcg.AgentOrchestrator.Infrastructure.Tests.exe");
                        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                        File.WriteAllText(executable, "deterministic shard fixture");
                    }

                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                WriteMtpTrx(args);
                var filterIndex = Array.IndexOf(args, "--filter-class");
                Assert.True(filterIndex >= 0 && filterIndex + 1 < args.Length);
                var filter = args[filterIndex + 1];
                var isConflict = filter.Contains("Conflict", StringComparison.Ordinal);
                var active = Interlocked.Increment(ref activeExecutions);
                RecordPeak(ref peakExecutions, active);
                var conflictOrdinal = 0;
                if (isConflict)
                {
                    var activeConflictCount = Interlocked.Increment(ref activeConflicts);
                    RecordPeak(ref peakConflicts, activeConflictCount);
                    conflictOrdinal = Interlocked.Increment(ref conflictEntries);
                }

                try
                {
                    if (conflictOrdinal == 1)
                    {
                        firstConflictStarted.TrySetResult();
                        await disjointStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    else if (conflictOrdinal == 2)
                    {
                        Assert.True(
                            firstConflictFinished.Task.IsCompleted,
                            "The second conflicting shard entered before the first released its resource keys.");
                    }
                    else
                    {
                        await firstConflictStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        if (!firstConflictFinished.Task.IsCompleted)
                        {
                            Interlocked.Exchange(ref disjointOverlappedConflict, 1);
                        }

                        disjointStarted.TrySetResult();
                        await firstConflictFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }

                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }
                finally
                {
                    if (isConflict)
                    {
                        Interlocked.Decrement(ref activeConflicts);
                        if (conflictOrdinal == 1)
                        {
                            firstConflictFinished.TrySetResult();
                        }
                    }

                    Interlocked.Decrement(ref activeExecutions);
                }
            }

            var verifier = new GoalAcceptanceVerifier(RunShardAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var result = await verifier.RunAsync(
                root,
                new GoalId("44444444444444444444444444444444"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease);

            Assert.True(result.Passed);
            Assert.Equal(2, conflictEntries);
            Assert.Equal(1, peakConflicts);
            Assert.Equal(2, peakExecutions);
            Assert.Equal(1, disjointOverlappedConflict);
        }
        finally
        {
            GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = null;
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_cancellation_while_waiting_for_execution_slot_does_not_over_release")]
    public async Task GoalAcceptanceVerifierCancellationWhileWaitingForExecutionSlotDoesNotOverRelease()
    {
        GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = () => 2;
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        SetPartitionVerdictKeyHooks(
            "tree-execution-wait-cancel",
            "main-execution-wait-cancel",
            "commit-execution-wait-cancel");
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  {
                    "name": "Holder alpha",
                    "filter": "FullyQualifiedName~HolderAlphaTests",
                    "estimatedSerialSeconds": 100
                  },
                  {
                    "name": "Holder beta",
                    "filter": "FullyQualifiedName~HolderBetaTests",
                    "estimatedSerialSeconds": 90
                  },
                  {
                    "name": "Resource waiter",
                    "filter": "FullyQualifiedName~ResourceWaiterTests",
                    "estimatedSerialSeconds": 80,
                    "exclusiveResourceKeys": [ "shared" ]
                  }
                ],
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [
                      "{executable}",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx-filename",
                      "{trxFileName}"
                    ]
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var bothHoldersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderCount = 0;
        var waiterRan = 0;
        using var cancellation = new CancellationTokenSource();
        GoalAcceptanceVerifier.OnInfrastructureShardResourcesAcquiredForTests = checkName =>
        {
            if (checkName.EndsWith(": Resource waiter", StringComparison.Ordinal))
            {
                waiterParked.TrySetResult();
            }
        };
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> RunShardAsync(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (args.Length > 0 && args[0] == "dotnet")
                {
                    if (args.Length >= 2 && args[1] == "build")
                    {
                        var executable = Path.Combine(
                            GetArtifactsPath(args),
                            "bin",
                            "Mcg.AgentOrchestrator.Infrastructure.Tests",
                            "debug",
                            "Mcg.AgentOrchestrator.Infrastructure.Tests.exe");
                        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                        File.WriteAllText(executable, "deterministic shard fixture");
                    }

                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                var filterIndex = Array.IndexOf(args, "--filter-class");
                Assert.True(filterIndex >= 0 && filterIndex + 1 < args.Length);
                if (args[filterIndex + 1].Contains("Holder", StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref holderCount) == 2)
                    {
                        bothHoldersStarted.TrySetResult();
                    }

                    WriteMtpTrx(args);
                    await releaseHolders.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }

                Interlocked.Exchange(ref waiterRan, 1);
                WriteMtpTrx(args);
                return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
            }

            var verifier = new GoalAcceptanceVerifier(RunShardAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var verification = verifier.RunAsync(
                root,
                new GoalId("77777777777777777777777777777777"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease,
                cancellationToken: cancellation.Token);

            await bothHoldersStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await waiterParked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            releaseHolders.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => verification.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, Volatile.Read(ref waiterRan));
            Assert.False(
                verification.Exception?.Flatten().InnerExceptions
                    .Any(exception => exception is SemaphoreFullException) ?? false,
                "Cancellation over-released the execution-slot semaphore.");
        }
        finally
        {
            releaseHolders.TrySetResult();
            GoalAcceptanceVerifier.OnInfrastructureShardResourcesAcquiredForTests = null;
            GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = null;
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Theory(DisplayName = "GoalAcceptanceVerifier_releases_exclusive_resources_after_shard_fault_or_cancellation")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task GoalAcceptanceVerifierReleasesExclusiveResourcesAfterShardFaultOrCancellation(
        bool cancelFirstShard)
    {
        GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = () => 2;
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        SetPartitionVerdictKeyHooks(
            $"tree-release-{cancelFirstShard}",
            $"main-release-{cancelFirstShard}",
            $"commit-release-{cancelFirstShard}");
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  {
                    "name": "Faulting",
                    "filter": "FullyQualifiedName~FaultingTests",
                    "estimatedSerialSeconds": 100,
                    "exclusiveResourceKeys": [ "shared" ]
                  },
                  {
                    "name": "Waiting",
                    "filter": "FullyQualifiedName~WaitingTests",
                    "estimatedSerialSeconds": 90,
                    "exclusiveResourceKeys": [ "shared" ]
                  }
                ],
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [
                      "{executable}",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx-filename",
                      "{trxFileName}"
                    ]
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var faultingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFaulting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> RunShardAsync(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (args.Length > 0 && args[0] == "dotnet")
                {
                    if (args.Length >= 2 && args[1] == "build")
                    {
                        var executable = Path.Combine(
                            GetArtifactsPath(args),
                            "bin",
                            "Mcg.AgentOrchestrator.Infrastructure.Tests",
                            "debug",
                            "Mcg.AgentOrchestrator.Infrastructure.Tests.exe");
                        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                        File.WriteAllText(executable, "deterministic shard fixture");
                    }

                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                var filterIndex = Array.IndexOf(args, "--filter-class");
                Assert.True(filterIndex >= 0 && filterIndex + 1 < args.Length);
                var faulting = args[filterIndex + 1].Contains("FaultingTests", StringComparison.Ordinal);
                if (faulting)
                {
                    faultingStarted.TrySetResult();
                    await releaseFaulting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (cancelFirstShard)
                    {
                        throw new OperationCanceledException("Synthetic shard cancellation.");
                    }

                    throw new InvalidOperationException("Synthetic shard fault.");
                }

                waitingStarted.TrySetResult();
                WriteMtpTrx(args);
                return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
            }

            var verifier = new GoalAcceptanceVerifier(RunShardAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var verification = verifier.RunAsync(
                root,
                new GoalId(cancelFirstShard
                    ? "55555555555555555555555555555555"
                    : "66666666666666666666666666666666"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease);

            await faultingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseFaulting.TrySetResult();
            if (cancelFirstShard)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => verification);
            }

            Assert.True(
                waitingStarted.Task.IsCompleted,
                "The waiting shard did not acquire the resource released by the faulted shard.");
        }
        finally
        {
            GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = null;
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_concurrent_shards_start_longest_estimated_lanes_first")]
    public async Task GoalAcceptanceVerifierConcurrentShardsStartLongestEstimatedLanesFirst()
    {
        GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = () => 2;
        SetPartitionVerdictKeyHooks("tree-estimates", "main-estimates", "commit-estimates");
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  {
                    "name": "Light first",
                    "filter": "FullyQualifiedName~LightFirstTests",
                    "estimatedSerialSeconds": 1
                  },
                  {
                    "name": "Heavy alpha",
                    "filter": "FullyQualifiedName~HeavyAlphaTests",
                    "estimatedSerialSeconds": 100
                  },
                  {
                    "name": "Light second",
                    "filter": "FullyQualifiedName~LightSecondTests",
                    "estimatedSerialSeconds": 2
                  },
                  {
                    "name": "Heavy beta",
                    "filter": "FullyQualifiedName~HeavyBetaTests",
                    "estimatedSerialSeconds": 99
                  }
                ],
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [
                      "{executable}",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx-filename",
                      "{trxFileName}"
                    ]
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var heavyAlphaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heavyBetaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startOrder = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> RunEstimatedShardAsync(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (args.Length > 0 && args[0] == "dotnet")
                {
                    if (args.Length >= 2 && args[1] == "build")
                    {
                        var executable = Path.Combine(
                            GetArtifactsPath(args),
                            "bin",
                            "Mcg.AgentOrchestrator.Infrastructure.Tests",
                            "debug",
                            "Mcg.AgentOrchestrator.Infrastructure.Tests.exe");
                        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                        File.WriteAllText(executable, "deterministic shard fixture");
                    }

                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                WriteMtpTrx(args);
                var filterIndex = Array.IndexOf(args, "--filter-class");
                Assert.True(filterIndex >= 0 && filterIndex + 1 < args.Length);
                var filter = args[filterIndex + 1];
                startOrder.Enqueue(filter);
                if (filter.Contains("HeavyAlphaTests", StringComparison.Ordinal))
                {
                    heavyAlphaStarted.TrySetResult();
                    await heavyBetaStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else if (filter.Contains("HeavyBetaTests", StringComparison.Ordinal))
                {
                    heavyBetaStarted.TrySetResult();
                    await heavyAlphaStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }

                return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
            }

            var verifier = new GoalAcceptanceVerifier(RunEstimatedShardAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var result = await verifier.RunAsync(
                root,
                new GoalId("33333333333333333333333333333333"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease);

            Assert.True(result.Passed);
            var firstWave = startOrder.Take(2).ToArray();
            Assert.Equal(2, firstWave.Length);
            Assert.Contains(firstWave, filter => filter.Contains("HeavyAlphaTests", StringComparison.Ordinal));
            Assert.Contains(firstWave, filter => filter.Contains("HeavyBetaTests", StringComparison.Ordinal));
            Assert.Equal(
                [
                    "infrastructure tests: Light first",
                    "infrastructure tests: Heavy alpha",
                    "infrastructure tests: Light second",
                    "infrastructure tests: Heavy beta"
                ],
                result.Checks!
                    .Where(check => check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal))
                    .Select(check => check.Name));
        }
        finally
        {
            GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = null;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_trusted_manifest_git_read_drains_large_output")]
    public void GoalAcceptanceVerifierTrustedManifestGitReadDrainsLargeOutput()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();

        var trustedManifest = GoalAcceptanceVerifier.ResolveGitText(
            repositoryRoot,
            "show",
            "main:config/acceptance-manifest.json");

        Assert.NotNull(trustedManifest);
        Assert.True(
            trustedManifest.Length > 4096,
            $"Expected the trusted manifest fixture to exceed a Windows pipe buffer; actual length was {trustedManifest.Length}.");
        Assert.Contains("\"maxConcurrentShards\"", trustedManifest, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_real_process_shards_keep_receipts_in_attempt_artifacts_after_releasing_build_lease")]
    public async Task GoalAcceptanceVerifierRealProcessShardsKeepReceiptsInAttemptArtifactsAfterReleasingSlots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = () => 2;
        var root = CreateRealProcessShardManifestWorkspace();
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var invocations = new System.Collections.Concurrent.ConcurrentQueue<string[]>();
        var executablePaths = new System.Collections.Concurrent.ConcurrentBag<string>();
        var resultsDirectories = new System.Collections.Concurrent.ConcurrentBag<string>();
        var testProcessIds = new System.Collections.Concurrent.ConcurrentBag<int>();
        var signalDirectory = Path.Combine(root, ".shard-smoke");
        Directory.CreateDirectory(signalDirectory);
        var alphaSignalPath = Path.Combine(signalDirectory, "alpha.signal");
        var betaSignalPath = Path.Combine(signalDirectory, "beta.signal");
        var ambientAttemptPrefix = Path.Combine(root, "ambient-gate-attempt", "not-a-slot");
        var previousAttemptPrefix = Environment.GetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        IReadOnlyDictionary<string, string> shardEnvironment = new Dictionary<string, string>
        {
            ["MCG_SHARD_SMOKE_ALPHA_SIGNAL"] = alphaSignalPath,
            ["MCG_SHARD_SMOKE_BETA_SIGNAL"] = betaSignalPath
        };
        DotnetBuildEnvironmentLease? primaryLease = null;
        DotnetBuildEnvironment? primaryEnvironment = null;
        var primaryBuildPermit = -1;
        try
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                ambientAttemptPrefix);
            var verifier = new GoalAcceptanceVerifier(async (args, _, timeout, cancellationToken) =>
            {
                invocations.Enqueue(args);
                var isTest = IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests");
                if (isTest)
                {
                    Assert.True(
                        DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(primaryBuildPermit),
                        "MTP execution must not retain the build-pool lease.");
                    executablePaths.Add(args[0]);
                    var resultsDirectoryIndex = Array.IndexOf(args, "--results-directory");
                    Assert.InRange(resultsDirectoryIndex, 0, args.Length - 2);
                    resultsDirectories.Add(args[resultsDirectoryIndex + 1]);
                }

                return await RunRealShardProcessAsync(
                    args,
                    repositoryRoot,
                    timeout,
                    shardEnvironment,
                    process =>
                    {
                        if (isTest)
                        {
                            testProcessIds.Add(process.Id);
                        }
                    },
                    cancellationToken);
            });

            primaryLease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            primaryEnvironment = primaryLease.Environment;
            var primarySlot = StableSlotIndex(primaryLease.Environment.ArtifactsPath);
            primaryBuildPermit = primarySlot;
            var run = verifier.RunAsync(
                root,
                stableSlotIndex: primarySlot,
                stableSlotLease: primaryLease);
            var result = await run;
            Assert.True(
                result.Passed,
                string.Join(
                    " | ",
                    result.Checks!
                        .Where(check => !check.Passed)
                        .Select(check => $"{check.Name}: exit={check.ExitCode}; output={check.OutputTail}")));
            Assert.True(File.Exists(alphaSignalPath));
            Assert.True(File.Exists(betaSignalPath));
            Assert.Equal(2, testProcessIds.Distinct().Count());
            var invocationArray = invocations.ToArray();
            Assert.Single(invocationArray, call =>
                call.Length >= 2 &&
                call[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                call[1].Equals("build", StringComparison.OrdinalIgnoreCase));
            var firstShardIndex = Array.FindIndex(
                invocationArray,
                call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"));
            Assert.True(firstShardIndex >= 0);
            Assert.DoesNotContain(
                invocationArray.Skip(firstShardIndex),
                call => call.Length > 0 && call[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase));
            Assert.Single(executablePaths.Distinct(StringComparer.OrdinalIgnoreCase));
            Assert.All(executablePaths, path => Assert.True(File.Exists(path), $"Missing prebuilt MTP executable '{path}'."));
            var attemptResultsDirectory = Path.GetDirectoryName(ambientAttemptPrefix)!;
            Assert.All(
                resultsDirectories,
                path => Assert.Equal(attemptResultsDirectory, path, ignoreCase: true));
            Assert.All(
                result.TestResultPaths!,
                path => Assert.StartsWith(
                    attemptResultsDirectory + Path.DirectorySeparatorChar,
                    path,
                    StringComparison.OrdinalIgnoreCase));
            Assert.All(
                Enumerable.Range(0, DotnetBuildEnvironmentManager.BuildConcurrencySlotCount)
                    .Where(slot => slot != primarySlot),
                slot => Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(slot)));

            primaryLease.Dispose();
            primaryLease = null;
            Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(primarySlot));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                previousAttemptPrefix);
            primaryLease?.Dispose();
            if (primaryEnvironment is not null)
            {
                DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(primaryEnvironment);
            }

            GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = null;
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_shard_receipts_use_attempt_artifacts_instead_of_releasable_slot")]
    public void GoalAcceptanceVerifierShardReceiptsUseAttemptArtifactsInsteadOfReleasableSlot()
    {
        var attemptDirectory = Path.Combine(
            Path.GetTempPath(),
            $"mcg-shard-attempt-{Guid.NewGuid():N}");
        var previousPrefix = Environment.GetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var environment = new DotnetBuildEnvironment(
            "run-slot-1",
            Path.Combine(Path.GetTempPath(), "slot-1"),
            Path.Combine(Path.GetTempPath(), "slot-1", "artifacts"),
            Path.Combine(Path.GetTempPath(), "slot-1", "lease.lock"),
            [],
            "slot-1");
        try
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(attemptDirectory, "attempt-123"));

            var resultsDirectory =
                GoalAcceptanceVerifier.ResolveInfrastructureShardResultsDirectory(environment);

            Assert.Equal(attemptDirectory, resultsDirectory, ignoreCase: true);
            Assert.False(
                resultsDirectory.StartsWith(
                    environment.ArtifactsPath,
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                previousPrefix);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_concurrent_shards_use_distinct_attempt_heartbeat_files")]
    public void GoalAcceptanceVerifierConcurrentShardsUseDistinctAttemptHeartbeatFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-shard-heartbeats-{Guid.NewGuid():N}");
        var environment = new DotnetBuildEnvironment(
            "goal-heartbeat",
            root,
            Path.Combine(root, "build-artifacts"),
            Path.Combine(root, "build-slots", "build-0.lock"),
            [],
            "goal-heartbeat");
        using var scope = GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(
            Path.Combine(root, "attempt-owner"));

        var first = GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
            "infrastructure lane alpha",
            environment,
            stableSlotIndex: 0);
        var second = GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
            "infrastructure lane beta",
            environment,
            stableSlotIndex: 0);
        var punctuationFirst = GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
            "infrastructure lane alpha+beta",
            environment,
            stableSlotIndex: 0);
        var punctuationSecond = GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
            "infrastructure lane alpha beta",
            environment,
            stableSlotIndex: 0);

        Assert.NotEqual(first, second);
        Assert.NotEqual(punctuationFirst, punctuationSecond);
        Assert.StartsWith(root + Path.DirectorySeparatorChar, first, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(root + Path.DirectorySeparatorChar, second, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(environment.ArtifactsPath, first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(environment.ArtifactsPath, second, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_owner_results_fallback_never_roots_in_goal_worktree")]
    public void GoalAcceptanceVerifierOwnerResultsFallbackNeverRootsInGoalWorktree()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), $"mcg-owner-root-{Guid.NewGuid():N}");
        var worktreePath = Path.Combine(repositoryRoot, ".orchestrator-worktrees", "abc12345");

        var resolved = GoalAcceptanceVerifier.ResolveOwnerResultsRepositoryRoot(worktreePath);

        Assert.Equal(repositoryRoot, resolved, ignoreCase: true);
        Assert.False(resolved.StartsWith(worktreePath, StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_concurrent_gate_evidence_and_operator_owners_do_not_cross_contaminate")]
    public async Task GoalAcceptanceVerifierConcurrentGateEvidenceAndOperatorOwnersDoNotCrossContaminate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-owner-chaos-{Guid.NewGuid():N}");
        var attemptA = Path.Combine(root, "attempt-a");
        var attemptB = Path.Combine(root, "attempt-b");
        var operatorDirectory = Path.Combine(root, "operator");
        var executionLockPath = Path.Combine(root, "build-slots", "build-0.lock");
        var gateBuild = new DotnetBuildEnvironment(
            "gate-chaos",
            Path.Combine(root, "gate-build"),
            Path.Combine(root, "gate-build", "artifacts"),
            executionLockPath,
            [],
            "gate-chaos",
            BuildPermitIndex: 0);
        var operatorBuild = new DotnetBuildEnvironment(
            "operator-chaos",
            Path.Combine(root, "operator-build"),
            Path.Combine(root, "operator-build", "artifacts"),
            executionLockPath,
            [],
            "operator-chaos",
            BuildPermitIndex: 0);
        var environment = new DotnetBuildEnvironment(
            "goal-chaos",
            root,
            Path.Combine(root, "build-artifacts"),
            Path.Combine(root, "build-slots", "build-0.lock"),
            [],
            "goal-chaos");
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim(false);
        var faults = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var custodyRefusals = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var activeBuildOwners = 0;
        var maximumConcurrentBuildOwners = 0;
        try
        {
            static string Trx(string id, string testName) =>
                $"<TestRun><TestDefinitions><UnitTest id=\"{id}\" name=\"{testName}\"><TestMethod className=\"ChaosTests\" name=\"{testName[(testName.LastIndexOf('.') + 1)..]}\" /></UnitTest></TestDefinitions><Results><UnitTestResult testId=\"{id}\" testName=\"{testName}\" outcome=\"Passed\" /></Results></TestRun>";

            static void RecordMaximum(ref int location, int value)
            {
                var observed = Volatile.Read(ref location);
                while (value > observed)
                {
                    var prior = Interlocked.CompareExchange(ref location, value, observed);
                    if (prior == observed)
                    {
                        return;
                    }

                    observed = prior;
                }
            }

            Task<(string Path, long Length, string Hash, TestCoverageInvariantResult Coverage)> RunOwnerAsync(
                string directory,
                string testName,
                bool ownsBuildPermit) => Task.Run(() =>
            {
                DotnetBuildEnvironmentLease? lease = null;
                try
                {
                    if (ownsBuildPermit)
                    {
                        lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                            DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                                gateBuild,
                                TimeSpan.Zero)).Lease;
                        var concurrent = Interlocked.Increment(ref activeBuildOwners);
                        RecordMaximum(ref maximumConcurrentBuildOwners, concurrent);
                    }

                    using var scope = GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(
                        Path.Combine(directory, "owner"));
                    var resolved = GoalAcceptanceVerifier.ResolveInfrastructureShardResultsDirectory(environment);
                    Directory.CreateDirectory(resolved);
                    var receipt = Path.Combine(resolved, "lane.trx");
                    File.WriteAllText(receipt, Trx(Guid.NewGuid().ToString("N"), testName));
                    var length = new FileInfo(receipt).Length;
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(receipt)));
                    ready.Signal();
                    release.Wait();

                    if (!receipt.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        custodyRefusals.Enqueue($"receipt escaped owner directory: {receipt}");
                    }

                    var coverage = TestCoverageInvariant.Evaluate(
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { testName },
                        [new TestPartitionCoverage(
                            testName,
                            Completed: true,
                            TestResultPaths: [receipt],
                            AttemptId: Path.GetFileName(directory))],
                        currentAttemptId: Path.GetFileName(directory));
                    return (receipt, length, hash, coverage);
                }
                catch (Exception ex)
                {
                    faults.Enqueue(ex.ToString());
                    throw;
                }
                finally
                {
                    if (ownsBuildPermit)
                    {
                        Interlocked.Decrement(ref activeBuildOwners);
                    }

                    lease?.Dispose();
                }
            });

            var ownerA = RunOwnerAsync(attemptA, "ChaosTests.AttemptA", ownsBuildPermit: true);
            var ownerB = RunOwnerAsync(attemptB, "ChaosTests.AttemptB", ownsBuildPermit: false);
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "Concurrent owners did not reach the event gate.");
            var operatorBlocked = Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(operatorBuild, TimeSpan.Zero));
            Assert.Contains(
                operatorBlocked.BusySlots,
                slot => slot.SlotIndex == gateBuild.BuildPermitIndex);
            Directory.CreateDirectory(operatorDirectory);
            var operatorReceipt = Path.Combine(operatorDirectory, "operator.trx");
            File.WriteAllText(operatorReceipt, Trx("operator", "ChaosTests.Operator"));
            release.Set();

            var receipts = await Task.WhenAll(ownerA, ownerB);
            using (var operatorLease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                    operatorBuild,
                    TimeSpan.FromSeconds(2))).Lease)
            {
                var concurrent = Interlocked.Increment(ref activeBuildOwners);
                RecordMaximum(ref maximumConcurrentBuildOwners, concurrent);
                Interlocked.Decrement(ref activeBuildOwners);
            }

            Assert.StartsWith(attemptA + Path.DirectorySeparatorChar, receipts[0].Path, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(attemptB + Path.DirectorySeparatorChar, receipts[1].Path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(receipts, receipt => receipt.Path.StartsWith(operatorDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.All(receipts, receipt =>
            {
                Assert.True(File.Exists(receipt.Path));
                Assert.Equal(receipt.Length, new FileInfo(receipt.Path).Length);
                Assert.Equal(
                    receipt.Hash,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(receipt.Path))));
                Assert.True(receipt.Coverage.Passed, receipt.Coverage.Summary);
                Assert.Single(receipt.Coverage.ExecutedTests);
            });
            Assert.DoesNotContain(
                "ChaosTests.AttemptB",
                TestCoverageInvariant.ReadRecordedTests([receipts[0].Path]));
            Assert.DoesNotContain(
                "ChaosTests.AttemptA",
                TestCoverageInvariant.ReadRecordedTests([receipts[1].Path]));
            Assert.True(File.Exists(operatorReceipt));
            Assert.False(operatorReceipt.StartsWith(attemptA, StringComparison.OrdinalIgnoreCase));
            Assert.False(operatorReceipt.StartsWith(attemptB, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, maximumConcurrentBuildOwners);
            Assert.Empty(faults);
            Assert.Empty(custodyRefusals);
        }
        finally
        {
            release.Set();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partition_verdict_cache_reuses_green_partitions_on_reroll")]
    public async Task GoalAcceptanceVerifierPartitionVerdictCacheReusesGreenPartitionsOnReroll()
    {
        var root = CreateCheckedInManifestShapeWorkspace();
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        var remainderRuns = 0;
        var previousPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        SetPartitionVerdictKeyHooks("tree-a", "main-a", "commit-a");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsInfrastructurePartitionTestCall(args) && args.Any(arg => arg.Contains("FullyQualifiedName!~GoalAcceptanceVerifierTests", StringComparison.Ordinal)))
                {
                    remainderRuns++;
                    return Task.FromResult(remainderRuns < 3
                        ? new GoalAcceptanceVerifier.CommandResult(1, "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1.")
                        : new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : ""));
            });

            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(root, ".orchestrator", "attempt-one"));
            var first = await verifier.RunAsync(root, goalId);
            Assert.False(first.Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));
            Assert.False(File.Exists(Path.Combine(root, ".orchestrator", "acceptance-partition-verdicts.json")));
            var sharedJournal = GoalOperationJournal.Read(root, goalId);
            Assert.Contains(sharedJournal.Entries, entry =>
                entry.GoalId == goalId &&
                entry.Operation == "acceptance:partition-verdict" &&
                entry.PartitionId == "cli");

            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(root, ".orchestrator", "attempt-two"));
            var second = await verifier.RunAsync(root, goalId);
            Assert.False(second.Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count + 1, CountInfrastructurePartitionTestCalls(calls));
            var secondReceipt = Assert.Single(second.Checks!, check => check.Name == "infrastructure partition verdict cache");
            Assert.Contains("source_attempt_id=attempt-one", secondReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("{partition_id=remainder,verdict=RED}", secondReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("aggregate_verdict=RED", secondReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("verifying_commit_sha=commit-a", secondReceipt.ResultSummary, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(root, ".orchestrator", "attempt-three"));
            var third = await verifier.RunAsync(root, goalId);
            Assert.True(third.Passed);
            Assert.Equal(
                AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count + 2,
                CountInfrastructurePartitionTestCalls(calls));
            var thirdReceipt = Assert.Single(third.Checks!, check => check.Name == "infrastructure partition verdict cache");
            Assert.Contains("{partition_id=remainder,verdict=GREEN}", thirdReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("aggregate_verdict=GREEN", thirdReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("before_reroll_wall_time=20-25m", thirdReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("after_reroll_wall_time=2-7m", thirdReceipt.ResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previousPrefix);
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partition_verdict_cache_records_later_partitions_after_early_failure")]
    public async Task GoalAcceptanceVerifierPartitionVerdictCacheRecordsLaterPartitionsAfterEarlyFailure()
    {
        var root = CreateCheckedInManifestShapeWorkspace();
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        SetPartitionVerdictKeyHooks("tree-a", "main-a", "commit-a");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsInfrastructurePartitionTestCall(args) &&
                    args.Contains(CheckedInCliLaneFilter))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : ""));
            });

            var result = await verifier.RunAsync(root, goalId);

            Assert.False(result.Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));
            var receipt = Assert.Single(result.Checks!, check => check.Name == "infrastructure partition verdict cache");
            Assert.Contains("{partition_id=cli,verdict=RED}", receipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("{partition_id=remainder,verdict=GREEN}", receipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("aggregate_verdict=RED", receipt.ResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_within_attempt_rerun_tolerates_flaky_partition")]
    public async Task GoalAcceptanceVerifierWithinAttemptRerunToleratesFlakyPartition()
    {
        var root = CreateCheckedInManifestShapeWorkspace();
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        var cliRuns = 0;
        SetPartitionVerdictKeyHooks("tree-b", "main-b", "commit-b");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsInfrastructurePartitionTestCall(args) &&
                    args.Contains(CheckedInCliLaneFilter))
                {
                    cliRuns++;
                    // Intermittent flake: fail the first run, pass the within-attempt re-run.
                    return Task.FromResult(cliRuns < 2
                        ? new GoalAcceptanceVerifier.CommandResult(1, "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1.")
                        : new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : ""));
            });

            var result = await verifier.RunAsync(root, goalId);

            Assert.True(result.Passed);
            Assert.Equal(2, cliRuns);
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_within_attempt_rerun_tolerates_flaky_mtp_partition")]
    public async Task GoalAcceptanceVerifierWithinAttemptRerunToleratesFlakyMtpPartition()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests: Cli", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal", "--filter", "FullyQualifiedName~CliCommandTests"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var goalId = new GoalId("dddddddddddddddddddddddddddddddd");
        var calls = new List<string[]>();
        var mtpRuns = 0;
        SetPartitionVerdictKeyHooks("tree-mtp", "main-mtp", "commit-mtp");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    mtpRuns++;
                    if (mtpRuns == 1)
                    {
                        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1."));
                    }

                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, goalId);

            Assert.True(result.Passed);
            Assert.True(result.Retried);
            Assert.Equal(2, mtpRuns);
            var partition = Assert.Single(
                result.Checks!,
                check => check.Name.Equals("infrastructure tests: Cli", StringComparison.Ordinal));
            Assert.Equal(1, partition.TestResultRunOrdinal);
            Assert.False(string.IsNullOrWhiteSpace(partition.TestResultAttemptId));
            Assert.DoesNotContain(calls, call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj");
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partition_verdict_cache_aggregate_ignores_unrelated_check_failures")]
    public async Task GoalAcceptanceVerifierPartitionVerdictCacheAggregateIgnoresUnrelatedCheckFailures()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "post partition command", "type": "command", "command": "git", "arguments": ["diff", "--check"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        SetPartitionVerdictKeyHooks("tree-a", "main-a", "commit-a");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.Length > 1 && args[0] == "git" && args[1] == "diff")
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "unrelated failure"));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : ""));
            });

            var result = await verifier.RunAsync(root, goalId);

            Assert.False(result.Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));
            var receipt = Assert.Single(result.Checks!, check => check.Name == "infrastructure partition verdict cache");
            Assert.Contains("aggregate_verdict=GREEN", receipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("{partition_id=cli,verdict=GREEN}", receipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("post partition command", result.Checks!.Single(check => !check.Passed).Name, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partition_verdict_cache_invalidates_on_candidate_or_main_sha_change")]
    public async Task GoalAcceptanceVerifierPartitionVerdictCacheInvalidatesOnCandidateOrMainShaChange()
    {
        var root = CreateCheckedInManifestShapeWorkspace();
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        SetPartitionVerdictKeyHooks("tree-a", "main-a", "commit-a");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : ""));
            });

            Assert.True((await verifier.RunAsync(root, goalId)).Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));

            SetPartitionVerdictKeyHooks("tree-b", "main-a", "commit-b");
            Assert.True((await verifier.RunAsync(root, goalId)).Passed);
            Assert.Equal(
                AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count * 2,
                CountInfrastructurePartitionTestCalls(calls));

            SetPartitionVerdictKeyHooks("tree-b", "main-b", "commit-c");
            Assert.True((await verifier.RunAsync(root, goalId)).Passed);
            Assert.Equal(
                AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count * 3,
                CountInfrastructurePartitionTestCalls(calls));
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partition_verdict_cache_filter_hash_change_runs_only_changed_partition")]
    public async Task GoalAcceptanceVerifierPartitionVerdictCacheFilterHashChangeRunsOnlyChangedPartition()
    {
        var root = CreateCheckedInManifestShapeWorkspace();
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        SetPartitionVerdictKeyHooks("tree-a", "main-a", "commit-a");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : ""));
            });

            Assert.True((await verifier.RunAsync(root, goalId)).Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));
            CorruptPartitionCacheKey(root, "cli");

            var second = await verifier.RunAsync(root, goalId);
            Assert.True(second.Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count + 1, CountInfrastructurePartitionTestCalls(calls));
            var receipt = Assert.Single(second.Checks!, check => check.Name == "infrastructure partition verdict cache");
            Assert.Contains("{partition_id=cli,verdict=GREEN}", receipt.ResultSummary, StringComparison.Ordinal);
            Assert.DoesNotContain("{partition_id=goal-acceptance-verifier,verdict=GREEN}", receipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("partition_id=goal-acceptance-verifier,source_attempt_id=", receipt.ResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partition_verdict_cache_backstop_forces_full_rerun")]
    public async Task GoalAcceptanceVerifierPartitionVerdictCacheBackstopForcesFullRerun()
    {
        var root = CreateCheckedInManifestShapeWorkspace();
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        var failFirstPartitionOnForcedRerun = false;
        var previousBackstop = GoalAcceptanceVerifier.PartitionVerdictFullRerunEveryN;
        SetPartitionVerdictKeyHooks("tree-a", "main-a", "commit-a");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        GoalAcceptanceVerifier.PartitionVerdictFullRerunEveryN = 2;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (failFirstPartitionOnForcedRerun &&
                    IsInfrastructurePartitionTestCall(args) &&
                    args.Contains(CheckedInCliLaneFilter))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : ""));
            });

            Assert.True((await verifier.RunAsync(root, goalId)).Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));

            var second = await verifier.RunAsync(root, goalId);
            Assert.True(second.Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));
            Assert.Contains("reroll_attempt_count=1", second.Checks!.Single(check => check.Name == "infrastructure partition verdict cache").ResultSummary, StringComparison.Ordinal);

            failFirstPartitionOnForcedRerun = true;
            var third = await verifier.RunAsync(root, goalId);
            Assert.False(third.Passed);
            Assert.Equal(
                AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count * 2,
                CountInfrastructurePartitionTestCalls(calls));
            var thirdReceipt = Assert.Single(third.Checks!, check => check.Name == "infrastructure partition verdict cache");
            Assert.Contains("forced_full_rerun=true", thirdReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("reroll_attempt_count=0", thirdReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("{partition_id=cli,verdict=RED}", thirdReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("aggregate_verdict=RED", thirdReceipt.ResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictFullRerunEveryN = previousBackstop;
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_uses_base_build_cache_for_unchanged_projects")]
    public async Task GoalAcceptanceVerifierSlotGateUsesBaseBuildCacheForUnchangedProjects()
    {
        var calls = new List<string[]>();
        var root = CreateCheckedInManifestShapeWorkspace();
        var cacheRoot = Path.Combine(root, "base-cache");
        var seedArtifacts = Path.Combine(root, "seed-artifacts");
        var cache = new DotnetBaseBuildCache(cacheRoot);
        File.WriteAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln"), string.Empty);
        var mainSha = new string('a', 40);
        string[] restoredProjects =
        [
            "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj",
            "src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj",
            "src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj",
            "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj"
        ];
        foreach (var project in restoredProjects)
        {
            WriteProjectArtifacts(seedArtifacts, project, $"cached:{project}");
        }

        cache.Publish(mainSha, seedArtifacts, restoredProjects);
        GoalAcceptanceVerifier.ResolveBaseBuildMainShaForTests = _ => mainSha;
        GoalAcceptanceVerifier.BaseBuildCacheForTests = cache;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]) ||
                    args.Length > 0 && args[0] == "git")
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    var artifactsPath = GetArtifactsPath(args);
                    Assert.DoesNotContain(cacheRoot, artifactsPath, StringComparison.OrdinalIgnoreCase);
                    WriteProjectArtifacts(
                        artifactsPath,
                        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                        "changed");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "test")
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            AcceptanceVerificationResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(
                    root,
                    new GoalId("12345678123456781234567812345678"),
                    changedFiles: ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ChaosGateNoWorkerResultTests.cs"],
                    stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());

            Assert.NotNull(result);
            Assert.True(result!.Passed);
            var buildCalls = calls
                .Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "build")
                .ToArray();
            Assert.Single(buildCalls);
            Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", buildCalls[0]);
            Assert.DoesNotContain("Mcg.AgentOrchestrator.sln", buildCalls[0], StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(calls, call => call.Any(arg => arg.Contains(cacheRoot, StringComparison.OrdinalIgnoreCase)));
            var artifactsPath = GetArtifactsPath(buildCalls[0]);
            Assert.True(File.Exists(Path.Combine(artifactsPath, "bin", "Mcg.AgentOrchestrator.Core", "debug_net10.0", "cache.txt")));
            Assert.Contains("BASE_BUILD_CACHE ", output, StringComparison.Ordinal);
            Assert.Contains($"main_sha={mainSha}", output, StringComparison.Ordinal);
            Assert.Contains("build_phase_ms=", output, StringComparison.Ordinal);
            Assert.Contains("Core=hit", output, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Tests=changed", output, StringComparison.Ordinal);
            Assert.Contains("built_projects=Infrastructure.Tests", output, StringComparison.Ordinal);
            Assert.Contains(result.Checks!, check =>
                check.ResultSummary?.Contains("base-build-cache", StringComparison.Ordinal) == true &&
                check.ResultSummary.Contains($"main_sha={mainSha}", StringComparison.Ordinal) &&
                check.ResultSummary.Contains("build_phase_ms=", StringComparison.Ordinal));
        }
        finally
        {
            GoalAcceptanceVerifier.ResolveBaseBuildMainShaForTests = null;
            GoalAcceptanceVerifier.BaseBuildCacheForTests = null;
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_uses_base_build_cache_for_verifier_source_scope")]
    public async Task GoalAcceptanceVerifierSlotGateUsesBaseBuildCacheForVerifierSourceScope()
    {
        var calls = new List<string[]>();
        var root = CreateCheckedInManifestShapeWorkspace();
        var cacheRoot = Path.Combine(root, "base-cache");
        var seedArtifacts = Path.Combine(root, "seed-artifacts");
        var cache = new DotnetBaseBuildCache(cacheRoot);
        File.WriteAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln"), string.Empty);
        var mainSha = new string('c', 40);
        string[] restoredProjects =
        [
            "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj",
            "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj"
        ];
        foreach (var project in restoredProjects)
        {
            WriteProjectArtifacts(seedArtifacts, project, $"cached:{project}");
        }

        cache.Publish(mainSha, seedArtifacts, restoredProjects);
        GoalAcceptanceVerifier.ResolveBaseBuildMainShaForTests = _ => mainSha;
        GoalAcceptanceVerifier.BaseBuildCacheForTests = cache;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]) ||
                    args.Length > 0 && args[0] == "git")
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    var artifactsPath = GetArtifactsPath(args);
                    Assert.DoesNotContain(cacheRoot, artifactsPath, StringComparison.OrdinalIgnoreCase);
                    WriteProjectArtifacts(artifactsPath, args[2], $"changed:{args[2]}");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "test")
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            AcceptanceVerificationResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(
                    root,
                    new GoalId("12345678123456781234567812345678"),
                    changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"],
                    stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());

            Assert.NotNull(result);
            Assert.True(result!.Passed);
            var buildCalls = calls
                .Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "build")
                .ToArray();
            Assert.Equal(3, buildCalls.Length);
            Assert.All(buildCalls, call => Assert.DoesNotContain("Mcg.AgentOrchestrator.sln", call, StringComparer.OrdinalIgnoreCase));
            Assert.Contains(buildCalls, call => call.Contains("src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj"));
            Assert.Contains(buildCalls, call => call.Contains("src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj"));
            Assert.Contains(buildCalls, call => call.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
            Assert.DoesNotContain(calls, call => call.Any(arg => arg.Contains(cacheRoot, StringComparison.OrdinalIgnoreCase)));
            var artifactsPath = GetArtifactsPath(buildCalls[0]);
            Assert.True(File.Exists(Path.Combine(artifactsPath, "bin", "Mcg.AgentOrchestrator.Core", "debug_net10.0", "cache.txt")));
            Assert.Contains("BASE_BUILD_CACHE ", output, StringComparison.Ordinal);
            Assert.Contains($"main_sha={mainSha}", output, StringComparison.Ordinal);
            Assert.Contains("Core=hit", output, StringComparison.Ordinal);
            Assert.Contains("Core.Tests=hit", output, StringComparison.Ordinal);
            Assert.Contains("Infrastructure=changed", output, StringComparison.Ordinal);
            Assert.Contains("App=changed", output, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Tests=changed", output, StringComparison.Ordinal);
            Assert.Contains("built_projects=Infrastructure,App,Infrastructure.Tests", output, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.ResolveBaseBuildMainShaForTests = null;
            GoalAcceptanceVerifier.BaseBuildCacheForTests = null;
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_base_build_cache_receipts_show_structural_cold_then_warm_attempts")]
    public async Task GoalAcceptanceVerifierBaseBuildCacheReceiptsShowStructuralColdThenWarmAttempts()
    {
        var calls = new List<string[]>();
        var root = CreateCheckedInManifestShapeWorkspace();
        var cacheRoot = Path.Combine(root, "base-cache");
        var cache = new DotnetBaseBuildCache(cacheRoot);
        File.WriteAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln"), string.Empty);
        var mainSha = new string('b', 40);
        string[] cacheableProjects =
        [
            "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj",
            "src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj",
            "src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj",
            "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
        ];

        GoalAcceptanceVerifier.ResolveBaseBuildMainShaForTests = _ => mainSha;
        GoalAcceptanceVerifier.BaseBuildCacheForTests = cache;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]) ||
                    args.Length > 0 && args[0] == "git")
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                if (args.Length >= 3 && args[0] == "dotnet" && args[1] == "build")
                {
                    var artifactsPath = GetArtifactsPath(args);
                    if (args[2].EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var project in cacheableProjects)
                        {
                            WriteProjectArtifacts(artifactsPath, project, $"cold:{project}");
                        }
                    }
                    else
                    {
                        WriteProjectArtifacts(artifactsPath, args[2], $"warm:{args[2]}");
                    }

                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "test")
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            static long ExtractBuildPhaseMilliseconds(string output)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    output,
                    @"build_phase_ms=(\d+)",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                Assert.True(match.Success, output);
                return long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            var firstOutput = AsyncLocalConsoleRouter.Capture(() =>
                verifier.RunAsync(
                    root,
                    new GoalId("12345678123456781234567812345678"),
                    changedFiles: ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ChaosGateNoWorkerResultTests.cs"],
                    stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());
            var firstBuildArtifactsPath = GetArtifactsPath(calls.First(call =>
                call.Length >= 3 && call[0] == "dotnet" && call[1] == "build"));
            var staleRestoredProjectFile = Path.Combine(
                firstBuildArtifactsPath,
                "bin",
                "Mcg.AgentOrchestrator.Core",
                "debug_net10.0",
                "stale-extra.txt");
            File.WriteAllText(staleRestoredProjectFile, "stale");
            var secondOutput = AsyncLocalConsoleRouter.Capture(() =>
                verifier.RunAsync(
                    root,
                    new GoalId("12345678123456781234567812345678"),
                    changedFiles: ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ChaosGateNoWorkerResultTests.cs"],
                    stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());

            var firstBuildPhaseMs = ExtractBuildPhaseMilliseconds(firstOutput);
            var secondBuildPhaseMs = ExtractBuildPhaseMilliseconds(secondOutput);
            Console.WriteLine(
                $"BASE_BUILD_CACHE_MEASUREMENT main_sha={mainSha} cold_build_phase_ms={firstBuildPhaseMs} warm_build_phase_ms={secondBuildPhaseMs}");
            var buildCalls = calls
                .Where(call => call.Length >= 3 && call[0] == "dotnet" && call[1] == "build")
                .ToArray();
            Assert.Equal(2, buildCalls.Length);
            Assert.Contains("Mcg.AgentOrchestrator.sln", buildCalls[0], StringComparer.OrdinalIgnoreCase);
            Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", buildCalls[1]);
            Assert.DoesNotContain("Mcg.AgentOrchestrator.sln", buildCalls[1], StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(calls, call => call.Any(arg => arg.Contains(cacheRoot, StringComparison.OrdinalIgnoreCase)));
            Assert.Contains($"main_sha={mainSha}", firstOutput, StringComparison.Ordinal);
            Assert.Contains($"main_sha={mainSha}", secondOutput, StringComparison.Ordinal);
            Assert.Contains("build_phase_ms=", firstOutput, StringComparison.Ordinal);
            Assert.Contains("build_phase_ms=", secondOutput, StringComparison.Ordinal);
            Assert.Contains("Core=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("App=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("Core.Tests=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Tests=changed", firstOutput, StringComparison.Ordinal);
            Assert.Contains("built_projects=Core,Infrastructure,App,Core.Tests,Infrastructure.Tests", firstOutput, StringComparison.Ordinal);
            Assert.Contains("Core=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("App=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("Core.Tests=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Tests=changed", secondOutput, StringComparison.Ordinal);
            Assert.Contains("built_projects=Infrastructure.Tests", secondOutput, StringComparison.Ordinal);
            Assert.False(File.Exists(staleRestoredProjectFile));
            Assert.True(firstBuildPhaseMs >= 0, $"Expected non-negative cold build phase receipt; cold={firstBuildPhaseMs}ms");
            Assert.True(secondBuildPhaseMs >= 0, $"Expected non-negative warm build phase receipt; warm={secondBuildPhaseMs}ms");
        }
        finally
        {
            GoalAcceptanceVerifier.ResolveBaseBuildMainShaForTests = null;
            GoalAcceptanceVerifier.BaseBuildCacheForTests = null;
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_prefers_completed_red_test_verdict_after_transient_build_lock")]
    public void GoalAcceptanceVerifierSlotGatePrefersCompletedRedTestVerdictAfterTransientBuildLock()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Infra.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        File.WriteAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln"), string.Empty);
        var calls = new List<string[]>();
        var buildAttempts = 0;
        var testAttempts = 0;
        var fakeTimeProvider = new RecordingTimeProvider();
        var leaseTimeStartedAt = fakeTimeProvider.GetUtcNow();
        var slotEnvironment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        var leasePrepareAttempts = 0;
        var previousWindow = GoalAcceptanceVerifier.TransientNoHolderBuildLockWaitWindow;
        var previousPoll = GoalAcceptanceVerifier.TransientNoHolderBuildLockPollInterval;
        var previousMaxCycles = GoalAcceptanceVerifier.TransientNoHolderBuildLockMaxRetryCycles;
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == slotEnvironment.ExecutionLockPath &&
                Interlocked.Increment(ref leasePrepareAttempts) <= 2)
            {
                var lockedPath = Path.Combine(current.ArtifactsPath, "bin", "Core.Tests.dll");
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
            "handle64-timeout");
        GoalAcceptanceVerifier.TransientNoHolderBuildLockWaitWindow = TimeSpan.FromSeconds(2);
        GoalAcceptanceVerifier.TransientNoHolderBuildLockPollInterval = TimeSpan.FromMilliseconds(10);
        GoalAcceptanceVerifier.TransientNoHolderBuildLockMaxRetryCycles = 1;

        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    buildAttempts++;
                    if (buildAttempts == 1)
                    {
                        var artifactsPath = GetArtifactsPath(args);
                        var lockedPath = Path.Combine(artifactsPath, "bin", "Core.Tests.dll");
                        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
                        File.WriteAllText(lockedPath, "transiently reported by compiler");
                        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                            1,
                            $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process."));
                    }

                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "test")
                {
                    testAttempts++;
                    return Task.FromResult(testAttempts == 1
                        ? new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 519, Skipped: 0, Total: 519.")
                        : new GoalAcceptanceVerifier.CommandResult(
                            1,
                            "Failed! - Failed: 1, Passed: 1980, Skipped: 0, Total: 1981.\n" +
                            "Red.Namespace.FailingTest failed\n" +
                            "error CS2012: Cannot open 'C:\\mcg-dotnet-isolated\\slots\\slot-0\\artifacts\\bin\\Core.Tests.dll' for writing because it is being used by another process."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            }, fakeTimeProvider, fakeTimeProvider.Advance);

            AcceptanceVerificationResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(
                    root,
                    new GoalId("11112222333344445555666677778888"),
                    stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());

            Assert.NotNull(result);
            Assert.False(result!.Passed);
            Assert.True(result.Retried);
            Assert.True(leasePrepareAttempts >= 3);
            Assert.True(fakeTimeProvider.GetUtcNow() - leaseTimeStartedAt >= TimeSpan.FromMilliseconds(200));
            Assert.Equal(2, buildAttempts);
            Assert.Equal(2, testAttempts);
            Assert.Equal(1, result.ExitCode);
            Assert.NotNull(result.OutputTail);
            Assert.Contains("Red.Namespace.FailingTest", result.OutputTail!, StringComparison.Ordinal);
            Assert.Contains("LOCK_TRANSIENT_WAIT ", output, StringComparison.Ordinal);
            Assert.Contains("released=true", output, StringComparison.Ordinal);
            Assert.Matches(@"waited-ms=([0-9]+)", output);
            Assert.Contains("LOCK_TRANSIENT_RETRY ", output, StringComparison.Ordinal);
            Assert.Contains("verdict=completed", output, StringComparison.Ordinal);
            Assert.Contains("build-lock=false", output, StringComparison.Ordinal);
            Assert.DoesNotContain("verdict=blocked", output, StringComparison.Ordinal);
            Assert.All(calls.Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test"),
                call => Assert.Contains("--no-build", call));
        }
        finally
        {
            GoalAcceptanceVerifier.TransientNoHolderBuildLockWaitWindow = previousWindow;
            GoalAcceptanceVerifier.TransientNoHolderBuildLockPollInterval = previousPoll;
            GoalAcceptanceVerifier.TransientNoHolderBuildLockMaxRetryCycles = previousMaxCycles;
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_self_heals_once_on_CS2012_file_lock_and_returns_passed")]
    public async Task GoalAcceptanceVerifierSelfHealsOnceOnCs2012FileLockAndReturnsPassed()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),                                                                                 // build-server shutdown
            new(1, "error CS2012: Cannot open 'Core.dll' for writing because it is being used by another process."),
            new(0, ""),                                                                                 // build-server shutdown (retry)
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")                              // dotnet test - retry passes
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var goalId = new GoalId("abcd1234abcd1234abcd1234abcd1234");
        var result = await verifier.RunAsync("C:\\fake\\worktree", goalId);

        Assert.True(result.Passed);
        Assert.True(result.Retried);
        Assert.True(result.OutputTail is null);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(4, calls.Count);
        Assert.True(calls[2].SequenceEqual(["dotnet", "build-server", "shutdown"]));
        AssertIsolatedTestCommand(calls[1]);
        AssertIsolatedTestCommand(calls[3]);
        Assert.Equal(GetArtifactsPath(calls[1]), GetArtifactsPath(calls[3]));
        Assert.True(result.ArtifactsPath is not null);
        Assert.Contains(Path.Combine("goals", "abcd1234", "artifacts"), result.ArtifactsPath!, StringComparison.OrdinalIgnoreCase);
        var check = result.Checks!.Single(item => item.Name == "dotnet test");
        Assert.Equal("goal-acceptance-verifier", check.BrokerName);
        Assert.Equal("goal-abcd1234", check.LeaseId);
        Assert.True(check.DurationMilliseconds is >= 0);
        Assert.True(check.LockRemediationApplied);
        Assert.True(check.ResultSummary?.Contains("build artifact lock detected; holder attributed; remediation retried", StringComparison.Ordinal) == true);
        Assert.True(check.ResultSummary?.Contains("Failed: 0", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_dotnet_test_adds_attempt_trx_logger_and_records_advisory_missing_trx")]
    public void GoalAcceptanceVerifierDotnetTestAddsAttemptTrxLoggerAndRecordsAdvisoryMissingTrx()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj" }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var goalId = new GoalId("feedfacefeedfacefeedfacefeedface");
        var attemptDirectory = Path.Combine(Path.GetTempPath(), $"mcg-trx-attempt-{Guid.NewGuid():N}");
        var attemptPrefix = Path.Combine(attemptDirectory, "feedface-0-20260717120000000");
        Directory.CreateDirectory(attemptDirectory);
        var previousPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var calls = new List<string[]>();
        try
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, attemptPrefix);
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            });

            AcceptanceVerificationResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(root, goalId).GetAwaiter().GetResult());

            Assert.NotNull(result);
            Assert.True(result!.Passed);
            var testCall = calls.Single(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test");
            var expectedTrx = Path.Combine(attemptDirectory, "feedface-0-20260717120000000.core-tests.trx");
            Assert.Contains("--logger", testCall);
            Assert.Contains($"trx;LogFileName={Path.GetFileName(expectedTrx)}", testCall);
            Assert.Contains("--results-directory", testCall);
            Assert.Contains(attemptDirectory, testCall);
            Assert.Contains(expectedTrx, result.TestResultPaths!);
            Assert.Contains(expectedTrx, result.Checks!.Single().TestResultPaths!);
            Assert.Contains("TRX_TELEMETRY_UNAVAILABLE", output, StringComparison.Ordinal);
            Assert.True(result.OutputTail is null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previousPrefix);
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(attemptDirectory);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_self_heals_once_on_compiler_lock_and_returns_failed_when_retry_also_fails")]
    public void GoalAcceptanceVerifierSelfHealsOnceOnCompilerLockAndReturnsFailedWhenRetryAlsoFails()
    {
        var calls = new List<string[]>();
        var lockedPath = Path.Combine("C:\\fake\\worktree", "obj", "Core.dll");
        var previousMaxCycles = GoalAcceptanceVerifier.TransientNoHolderBuildLockMaxRetryCycles;
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(1, "MSB3491: Could not write lines to file because it is being used by another process."),
            new(0, ""),
            new(1, $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process."),
            new(1, $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process.")
        ]);

        try
        {
            GoalAcceptanceVerifier.TransientNoHolderBuildLockMaxRetryCycles = 2;
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(responses.Dequeue());
            });

            BuildLockBlockedException? blocked = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                blocked = Assert.ThrowsAsync<BuildLockBlockedException>(() => verifier.RunAsync("C:\\fake\\worktree"))
                    .GetAwaiter()
                    .GetResult());

            Assert.NotNull(blocked);
            Assert.Equal(lockedPath, blocked!.Attribution.Path);
            Assert.Equal(5, calls.Count);
            AssertIsolatedTestCommand(calls[1]);
            AssertIsolatedTestCommand(calls[3]);
            AssertIsolatedTestCommand(calls[4]);
            Assert.Contains("LOCK_TRANSIENT_WAIT ", output, StringComparison.Ordinal);
            Assert.Contains("max-cycles=2", output, StringComparison.Ordinal);
            Assert.Contains("cycle=2", output, StringComparison.Ordinal);
            Assert.Contains("LOCK_TRANSIENT_RETRY ", output, StringComparison.Ordinal);
            Assert.Contains("verdict=blocked", output, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.TransientNoHolderBuildLockMaxRetryCycles = previousMaxCycles;
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_blocks_when_owned_process_kill_retry_still_reports_lock")]
    public async Task GoalAcceptanceVerifierBlocksWhenOwnedProcessKillRetryStillReportsLock()
    {
        var calls = new List<string[]>();
        var killed = new List<int>();
        var lockedPath = Path.Combine("C:\\fake\\worktree", "obj", "Core.dll");
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(1, $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process."),
            new(0, ""),
            new(1, $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process."),
            new(1, $"MSB3491: Could not write lines to file '{lockedPath}' because it is being used by another process.")
        ]);
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(987654, "testhost", $"dotnet test --artifacts-path {path}", true)],
            "test");
        WorkerProcessJobs.TryKillPidTree = pid =>
        {
            killed.Add(pid);
            return true;
        };

        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(responses.Dequeue());
            });

            var blocked = await Assert.ThrowsAsync<BuildLockBlockedException>(() => verifier.RunAsync("C:\\fake\\worktree"));

            Assert.Equal(lockedPath, blocked.Attribution.Path);
            Assert.Equal([987654], killed);
            Assert.Equal(5, calls.Count);
            AssertIsolatedTestCommand(calls[1]);
            AssertIsolatedTestCommand(calls[3]);
            AssertIsolatedTestCommand(calls[4]);
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_does_not_self_heal_non_lock_failure")]
    public async Task GoalAcceptanceVerifierDoesNotSelfHealNonLockFailure()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(1, "error CS2012: Cannot open 'Core.dll' for writing")
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync("C:\\fake\\worktree");

        Assert.False(result.Passed);
        Assert.False(result.Retried);
        Assert.True(result.OutputTail is not null);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(2, calls.Count);
        AssertIsolatedTestCommand(calls[1]);
        Assert.False(result.Checks!.Single(item => item.Name == "dotnet test").LockRemediationApplied);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_returns_failed_check_with_timeout_diagnostics")]
    public async Task GoalAcceptanceVerifierReturnsFailedCheckWithTimeoutDiagnostics()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "focused CLI infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": [ "--filter", "CliHelpTests", "--verbosity", "minimal" ] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(calls.Count == 1
                    ? new GoalAcceptanceVerifier.CommandResult(0, "")
                    : new GoalAcceptanceVerifier.CommandResult(
                        -1,
                        "restore complete\nstill running infrastructure tests",
                        TimedOut: true,
                        CommandLine: "dotnet test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                        StdoutPath: "C:\\temp\\acceptance.out",
                        StderrPath: "C:\\temp\\acceptance.err",
                        Timeout: TimeSpan.FromMinutes(10)));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcd1234abcd1234abcd1234abcd1234"));

            Assert.False(result.Passed);
            Assert.Equal(-1, result.ExitCode);
            Assert.Equal(2, calls.Count);
            var check = Assert.Single(result.Checks!.Where(check => !check.Passed));
            Assert.Equal("acceptance-check-timeout: focused-cli-infrastructure-tests elapsed=10m budget=10m", check.Name);
            Assert.False(check.Passed);
            Assert.Equal("elapsed=10m budget=10m", check.ResultSummary);
            Assert.Equal(result.ArtifactsPath, check.ArtifactsPath);
            var outputTail = result.OutputTail ?? string.Empty;
            Assert.True(outputTail.Contains("Verification command timed out after elapsed=10m budget=10m.", StringComparison.Ordinal), outputTail);
            Assert.True(outputTail.Contains("Command: dotnet test", StringComparison.Ordinal), outputTail);
            Assert.True(outputTail.Contains("stdout: C:\\temp\\acceptance.out", StringComparison.Ordinal), outputTail);
            Assert.True(outputTail.Contains("stderr: C:\\temp\\acceptance.err", StringComparison.Ordinal), outputTail);
            Assert.True(outputTail.Contains("still running infrastructure tests", StringComparison.Ordinal), outputTail);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_returns_passed_without_retry_on_first_time_pass")]
    public async Task GoalAcceptanceVerifierReturnsPassedWithoutRetryOnFirstTimePass()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "Test run succeeded.")
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync("C:\\fake\\worktree");

        Assert.True(result.Passed);
        Assert.False(result.Retried);
        Assert.True(result.OutputTail is null);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, calls.Count);
        AssertIsolatedTestCommand(calls[1]);
        Assert.Equal(1, result.Checks!.Count);
        Assert.Equal("dotnet test", result.Checks![0].Name);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_selects_infrastructure_tests_from_default_plan")]
    public async Task GoalAcceptanceVerifierSelectsInfrastructureTestsFromDefaultPlan()
    {
        var calls = new List<string[]>();
        var root = CreatePartitionedInfrastructureManifestWorkspace();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            return Task.FromResult(args.Length >= 3 &&
                args[0] == "dotnet" &&
                args[1] == "test" &&
                args[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                ? new GoalAcceptanceVerifier.CommandResult(1, "unexpected vstest fallback")
                : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs"]);

        Assert.True(result.Passed);
        var infrastructureCalls = calls
            .Where(IsInfrastructurePartitionTestCall)
            .ToArray();
        var laneCount = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count;
        Assert.Equal(laneCount, infrastructureCalls.Length);
        Assert.DoesNotContain(calls, call => call.Contains("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", StringComparer.Ordinal));
        foreach (var call in infrastructureCalls)
        {
            Assert.True(
                call.Contains("--filter-class") || call.Contains("--filter-not-class"),
                "Each infrastructure shard must carry a translated MTP class filter.");
            Assert.Contains("--report-trx", call);
        }

        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Worker profiles");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Remainder");
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.Passed &&
            check.ResultSummary == $"covered by {laneCount} partitioned checks");
        DeleteDirectoryWithRetry(root);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_runs_focused_infrastructure_filter_for_cli_only_changes")]
    public async Task GoalAcceptanceVerifierRunsFocusedInfrastructureFilterForCliOnlyChanges()
    {
        var root = CreateStandardManifestWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Focused CLI tests passed. Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2."));
            }

            return Task.FromResult(args.Length >= 3 &&
                args[0] == "dotnet" &&
                args[1] == "test" &&
                args[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                ? new GoalAcceptanceVerifier.CommandResult(1, "unexpected vstest fallback")
                : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.Tasks.cs"]);

        Assert.True(result.Passed);
        // shutdown + whitespace git-diff + focused shard (dotnet build + MTP executable); no extra runs.
        Assert.Equal(4, calls.Count);
        var focusedCall = Assert.Single(calls, call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"));
        Assert.DoesNotContain(focusedCall, argument => argument.Contains("Mcg.AgentOrchestrator.sln", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--filter-class", focusedCall);
        AssertArgumentPair(focusedCall, "--filter-class", "*CliHelpTests*");
        AssertArgumentPair(focusedCall, "--filter-class", "*CliCommandTests*");
        Assert.False(focusedCall.Any(argument => argument.Contains("FundamentalAliasTests", StringComparison.Ordinal)));
        Assert.DoesNotContain(focusedCall, argument => argument.Contains("DashboardHostTests", StringComparison.Ordinal));
        Assert.Contains(result.Checks!, check => check.Name == "focused CLI infrastructure tests");
        Assert.Contains(result.Checks!, check =>
            check.Name == "core tests" &&
            check.ResultSummary?.Contains("skipped: no changed file in dependency closure", StringComparison.Ordinal) == true);
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.ResultSummary?.Contains("covered by: focused CLI infrastructure tests", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_skips_core_policy_shard_for_infrastructure_test_only_scope")]
    public async Task GoalAcceptanceVerifierSkipsCorePolicyShardForInfrastructureTestOnlyScope()
    {
        var root = CreateCheckedInManifestShapeWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Focused CLI tests passed. Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            }

            return Task.FromResult(args.Length >= 3 &&
                args[0] == "dotnet" &&
                args[1] == "test" &&
                args[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                ? new GoalAcceptanceVerifier.CommandResult(1, "unexpected vstest fallback")
                : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles: ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProfileTests.cs"]);

        Assert.True(result.Passed);
        // shutdown + whitespace git-diff + focused shard (dotnet build + MTP executable) + the two
        // tamper-guard git diffs (name-only, then the per-file unified diff); the core policy shard is skipped.
        Assert.Equal(6, calls.Count);
        Assert.DoesNotContain(calls, call => call.Contains("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", StringComparer.OrdinalIgnoreCase));
        var focusedCall = Assert.Single(calls, call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"));
        Assert.Contains("--filter-class", focusedCall);
        AssertArgumentPair(focusedCall, "--filter-class", "*WorkerProfileTests*");
        Assert.DoesNotContain(
            calls,
            call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests") &&
                !call.Contains("--filter-class") &&
                !call.Contains("--filter-not-class"));
        var coreReceipt = result.Checks!.Single(check => check.Name == "core tests");
        Assert.True(coreReceipt.Passed);
        Assert.Contains("skipped: no changed file in dependency closure", coreReceipt.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("changed projects: Infrastructure.Tests", coreReceipt.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("dependency closure: Infrastructure.Tests", coreReceipt.ResultSummary, StringComparison.Ordinal);
        var infrastructureReceipt = result.Checks.Single(check => check.Name == "infrastructure tests");
        Assert.Contains("covered by: focused changed infrastructure tests", infrastructureReceipt.ResultSummary, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_runs_union_filter_for_multiple_mapped_app_subsystems")]
    public async Task GoalAcceptanceVerifierRunsUnionFilterForMultipleMappedAppSubsystems()
    {
        var root = CreateStandardManifestWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Focused App tests passed. Passed! - Failed: 0, Passed: 6, Skipped: 0, Total: 6."));
            }

            return Task.FromResult(args.Length >= 3 &&
                args[0] == "dotnet" &&
                args[1] == "test" &&
                args[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                ? new GoalAcceptanceVerifier.CommandResult(1, "unexpected vstest fallback")
                : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles:
            [
                "src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.Tasks.cs",
                "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.OperatorShell.cs"
            ]);

        Assert.True(result.Passed);
        var call = Assert.Single(calls, candidate => IsMtpExecutableCall(candidate, "Mcg.AgentOrchestrator.Infrastructure.Tests"));
        Assert.DoesNotContain(calls, candidate => candidate.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
        // The parenthesized union filter translates to repeated MTP class filters (union) plus the
        // per-group Category exclusion (intersection); grouping parens carry no token-level meaning.
        AssertArgumentPair(call, "--filter-class", "*CliCommandTests*");
        AssertArgumentPair(call, "--filter-class", "*CliHelpTests*");
        AssertArgumentPair(call, "--filter-class", "*DashboardRenderingTests*");
        AssertArgumentPair(call, "--filter-class", "*DashboardHostTests*");
        AssertArgumentPair(call, "--filter-not-trait", "Category=HostIntegration");
        AssertArgumentPair(call, "--filter-class", "*DashboardValidationHarnessTests*");
        Assert.Contains(result.Checks!, check => check.Name == "focused CLI+dashboard infrastructure tests");
        var infrastructureReceipt = Assert.Single(result.Checks!, check => check.Name == "infrastructure tests");
        Assert.True(infrastructureReceipt.Passed);
        Assert.Contains(
            "covered by: focused CLI+dashboard infrastructure tests",
            infrastructureReceipt.ResultSummary ?? "",
            StringComparison.Ordinal);
        Assert.Contains(
            "changed file in dependency closure",
            infrastructureReceipt.ResultSummary ?? "",
            StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_safety_valves_force_full_policy_shards")]
    public async Task GoalAcceptanceVerifierSafetyValvesForceFullPolicyShards()
    {
        var root = CreateCheckedInManifestShapeWorkspace();

        static async Task AssertFullShardRunAsync(string root, IReadOnlyList<string> changedFiles)
        {
            var calls = new List<string[]>();
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Infrastructure partition passed."
                        : ""));
            });

            var result = await verifier.RunAsync(root, changedFiles: changedFiles);

            Assert.True(result.Passed);
            Assert.Equal("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", calls[2][2]);
            var infrastructureCalls = calls
                .Where(call => call.Length > 2 &&
                    call[0] == "dotnet" &&
                    call[1] == "test" &&
                    call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
                .ToArray();
            var laneCount = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count;
            Assert.Equal(laneCount, infrastructureCalls.Length);
            foreach (var call in infrastructureCalls)
                Assert.Contains("--filter", call);
            Assert.DoesNotContain(infrastructureCalls, call => !call.Contains("--filter"));
            Assert.DoesNotContain(result.Checks!, check =>
                check.ResultSummary?.Contains("skipped: no changed file in dependency closure", StringComparison.Ordinal) == true);
            Assert.Contains(result.Checks!, check =>
                check.Name == "infrastructure tests" &&
                check.Passed &&
                check.ResultSummary == $"covered by {laneCount} partitioned checks");
            Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Remainder");
        }

        await AssertFullShardRunAsync(
            root,
            [
                "scripts/Invoke-IsolatedDotnet.ps1"
            ]);
        await AssertFullShardRunAsync(
            root,
            [
                "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj"
            ]);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_runs_manifest_command_checks_before_dotnet_tests")]
    public async Task GoalAcceptanceVerifierRunsManifestCommandChecksBeforeDotnetTests()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "whitespace", "type": "command", "command": "git", "arguments": [ "diff", "--check" ] },
                { "name": "focused tests", "type": "dotnet-test", "project": "tests/Example.Tests.csproj", "arguments": [ "--filter", "Example", "--verbosity", "minimal" ] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, ""),
            new(0, "Tests passed.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        Assert.True(result.Passed);
        Assert.Equal(3, calls.Count);
        Assert.True(calls[1].SequenceEqual(["git", "diff", "--check"]));
        Assert.Equal("dotnet", calls[2][0]);
        Assert.Equal("test", calls[2][1]);
        Assert.Equal("tests/Example.Tests.csproj", calls[2][2]);
        Assert.True(calls[2].Contains("--filter", StringComparer.Ordinal));
        Assert.Equal(2, result.Checks!.Count);
        Assert.Equal("whitespace", result.Checks[0].Name);
        Assert.Equal("focused tests", result.Checks[1].Name);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_brokers_dotnet_manifest_command_checks")]
    public async Task GoalAcceptanceVerifierBrokersDotnetManifestCommandChecks()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "custom dotnet", "type": "command", "command": "dotnet", "arguments": [ "test", "tests/Example.Tests.csproj", "--verbosity", "minimal" ] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root, new GoalId("13572468135724681357246813572468"));

        Assert.True(result.Passed);
        Assert.Equal(2, calls.Count);
        Assert.Equal("dotnet", calls[1][0]);
        Assert.Equal("test", calls[1][1]);
        AssertIsolatedTestCommand(calls[1]);
        var check = Xunit.Assert.Single(result.Checks!);
        Assert.Equal("custom dotnet", check.Name);
        Assert.Equal("goal-13572468", check.LeaseId);
        Assert.Equal("goal-acceptance-verifier", check.BrokerName);
        Assert.True(check.ResultSummary?.Contains("Failed: 0", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_defers_granular_csproj_checks_to_solution_wide_run")]
    public async Task GoalAcceptanceVerifierDefersGranularCsprojChecksToSolutionWideRun()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] },
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Infra.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "full dotnet tests", "type": "dotnet-test", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, ""),
            new(0, "Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        Assert.True(result.Passed);
        Assert.Equal(3, calls.Count);
        Assert.True(calls[1].SequenceEqual(["git", "diff", "--check"]));
        AssertIsolatedTestCommand(calls[2]);
        Assert.False(calls[2].Any(a => a.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(4, result.Checks!.Count);

        var fullCheck = result.Checks.Single(c => c.Name == "full dotnet tests");
        Assert.True(fullCheck.Passed);
        Assert.True(fullCheck.ResultSummary?.Contains("Passed:", StringComparison.Ordinal) == true);

        var coreCheck = result.Checks.Single(c => c.Name == "core tests");
        Assert.True(coreCheck.Passed);
        Assert.Equal("covered by: full dotnet tests", coreCheck.ResultSummary);
        Assert.Equal(fullCheck.ArtifactsPath, coreCheck.ArtifactsPath);
        Assert.Equal(fullCheck.LeaseId, coreCheck.LeaseId);

        var infraCheck = result.Checks.Single(c => c.Name == "infrastructure tests");
        Assert.True(infraCheck.Passed);
        Assert.Equal("covered by: full dotnet tests", infraCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_failing_solution_run_marks_deferred_checks_failed")]
    public async Task GoalAcceptanceVerifierFailingSolutionRunMarksDeferredChecksFailed()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] },
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Infra.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "full dotnet tests", "type": "dotnet-test", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, ""),
            new(1, "Failed! - Failed: 2, Passed: 3, Skipped: 0, Total: 5.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        Assert.False(result.Passed);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(3, calls.Count);
        Assert.Equal(4, result.Checks!.Count);

        var fullCheck = result.Checks.Single(c => c.Name == "full dotnet tests");
        Assert.False(fullCheck.Passed);

        var coreCheck = result.Checks.Single(c => c.Name == "core tests");
        Assert.False(coreCheck.Passed);
        Assert.Equal(1, coreCheck.ExitCode);
        Assert.Equal("covered by: full dotnet tests", coreCheck.ResultSummary);
        Assert.Equal(fullCheck.OutputTail, coreCheck.OutputTail);

        var infraCheck = result.Checks.Single(c => c.Name == "infrastructure tests");
        Assert.False(infraCheck.Passed);
        Assert.Equal("covered by: full dotnet tests", infraCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_defers_sln_project_check_using_sln_file_content")]
    public async Task GoalAcceptanceVerifierDefersSlnProjectCheckUsingSlnFileContent()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "extra tests", "type": "dotnet-test", "project": "tests/NotInSolution.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "full dotnet tests", "type": "dotnet-test", "project": "Fake.sln", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        File.WriteAllText(Path.Combine(root, "Fake.sln"),
            "Project(\"{FAE04EC0}\") = \"Core.Tests\", \"tests/Core.Tests.csproj\", \"{GUID}\"\nEndProject\n");

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3."),
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        Assert.True(result.Passed);
        // shutdown + sln run + extra tests (not in solution)
        Assert.Equal(3, calls.Count);
        Assert.Equal(3, result.Checks!.Count);

        Assert.True(result.Checks.Single(c => c.Name == "full dotnet tests").Passed);
        Assert.Equal("covered by: full dotnet tests",
            result.Checks.Single(c => c.Name == "core tests").ResultSummary);
        // "extra tests" ran for real (not synthesized), so ResultSummary is from actual output
        Assert.False(result.Checks.Single(c => c.Name == "extra tests").ResultSummary
            ?.StartsWith("covered by:", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_injects_policy_required_checks_missing_from_manifest")]
    public async Task GoalAcceptanceVerifierInjectsPolicyRequiredChecksMissingFromManifest()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Core.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5."));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
        });

        // Core source file -> impact plan requires only "core tests".
        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Core/Application/Foo.cs"]);

        Assert.True(result.Passed);
        // shutdown + whitespace git-diff + the injected core shard (dotnet build + MTP executable).
        Assert.Equal(4, calls.Count);
        Assert.True(calls[1].SequenceEqual(["git", "diff", "--check"]));
        // Core.Tests is an MTP project, so the injected "core tests" check runs the MTP executable
        // (preceded by a dotnet build), never the VSTest `dotnet test` runner.
        Assert.Single(calls, call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests"));
        Assert.DoesNotContain(calls, call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test");
        Assert.Equal(2, result.Checks!.Count);
        Assert.Equal("whitespace", result.Checks[0].Name);
        Assert.True(result.Checks.Any(c => c.Name == "core tests" && c.Passed));
        Assert.False(result.Checks.Any(c => c.Name == "infrastructure tests"));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_runs_core_and_dependent_infrastructure_shards_for_core_scope")]
    public async Task GoalAcceptanceVerifierRunsCoreAndDependentInfrastructureShardsForCoreScope()
    {
        var root = CreateCheckedInManifestShapeWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                0,
                args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                    ? "Tests passed."
                    : ""));
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Core/Application/Foo.cs"]);

        Assert.True(result.Passed);
        Assert.Equal(
            AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count + 4,
            calls.Count);
        Assert.Equal("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", calls[2][2]);
        var infrastructureCalls = calls
            .Where(call => call.Length > 2 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
            .ToArray();
        var laneCount = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count;
        Assert.Equal(laneCount, infrastructureCalls.Length);
        foreach (var call in infrastructureCalls)
            Assert.Contains("--filter", call);
        Assert.DoesNotContain(calls, call => call.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
        Assert.Contains(result.Checks!, check => check.Name == "core tests");
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.Passed &&
            check.ResultSummary == $"covered by {laneCount} partitioned checks");
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_substitutes_solution_check_with_partitioned_infrastructure_checks_for_infra_scope")]
    public async Task GoalAcceptanceVerifierSubstitutesSolutionCheckWithPartitionedInfrastructureChecksForInfraScope()
    {
        var root = CreateStandardManifestWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Infrastructure partition passed. Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            return Task.FromResult(args.Length >= 3 &&
                args[0] == "dotnet" &&
                args[1] == "test" &&
                args[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                ? new GoalAcceptanceVerifier.CommandResult(1, "unexpected vstest fallback")
                : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workers/Foo.cs"]);

        Assert.True(result.Passed);
        var infrastructureCalls = calls
            .Where(IsInfrastructurePartitionTestCall)
            .ToArray();
        var laneCount = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count;
        Assert.Equal(laneCount, infrastructureCalls.Length);
        Assert.DoesNotContain(calls, call => call.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
        foreach (var call in infrastructureCalls)
            Assert.True(
                call.Contains("--filter-class") || call.Contains("--filter-not-class"),
                "Each infrastructure shard must carry a translated MTP class filter.");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Goal acceptance verifier");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Remainder");
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.Passed &&
            check.ResultSummary == $"covered by {laneCount} partitioned checks");
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_substitutes_solution_check_with_union_for_core_and_infra_scope")]
    public async Task GoalAcceptanceVerifierSubstitutesSolutionCheckWithUnionForCoreAndInfraScope()
    {
        var root = CreateStandardManifestWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Tests passed. Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            // The substituted infrastructure shards must route through the MTP executable; a
            // `dotnet test` against the Infrastructure.Tests project would be a VSTest regression.
            if (args.Length >= 3 &&
                args[0] == "dotnet" &&
                args[1] == "test" &&
                args[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "unexpected vstest fallback"));
            }

            // The manifest "core tests" check carries no explicit runner, so it stays on VSTest
            // `dotnet test`; only synthesized/policy checks are promoted to runner=mtp.
            return Task.FromResult(args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                ? new GoalAcceptanceVerifier.CommandResult(0, "Tests passed.")
                : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles:
            [
                "src/Mcg.AgentOrchestrator.Core/Application/Foo.cs",
                "src/Mcg.AgentOrchestrator.Infrastructure/Workers/Foo.cs"
            ]);

        Assert.True(result.Passed);
        // The manifest "core tests" check has no explicit runner, so it runs via VSTest `dotnet test`.
        Assert.Equal("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", calls[2][2]);
        Assert.DoesNotContain(calls, call => call.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
        var infrastructureCalls = calls
            .Where(IsInfrastructurePartitionTestCall)
            .ToArray();
        var laneCount = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count;
        Assert.Equal(laneCount, infrastructureCalls.Length);
        // The substituted infrastructure shards are MTP, so every shard carries a translated class filter.
        foreach (var call in infrastructureCalls)
            Assert.True(
                IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests") &&
                (call.Contains("--filter-class") || call.Contains("--filter-not-class")),
                "Each infrastructure shard must run the MTP executable with a translated class filter.");
        Assert.Contains(result.Checks!, check => check.Name == "core tests");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Cli");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Remainder");
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.Passed &&
            check.ResultSummary == $"covered by {laneCount} partitioned checks");
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_keeps_solution_check_for_build_security_broad_or_disabled_scopes")]
    public async Task GoalAcceptanceVerifierKeepsSolutionCheckForBuildSecurityBroadOrDisabledScopes()
    {
        var root = CreateStandardManifestWorkspace();
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, ""),
            new(0, "Full tests passed.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root, changedFiles: ["Directory.Build.props"]);

        Assert.True(result.Passed);
        Assert.Contains("Mcg.AgentOrchestrator.sln", calls[2], StringComparer.OrdinalIgnoreCase);
        Assert.True(result.Checks!.Any(check => check.Name == "full dotnet tests"));
        Assert.True(result.Checks.Any(check => check.ResultSummary == "covered by: full dotnet tests"));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_change_scoped_kill_switch_keeps_solution_check")]
    public async Task GoalAcceptanceVerifierChangeScopedKillSwitchKeepsSolutionCheck()
    {
        var previous = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED");
        Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", "0");
        try
        {
            var root = CreateStandardManifestWorkspace();
            var calls = new List<string[]>();
            var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
                new(0, ""),
                new(0, ""),
                new(0, "Full tests passed.")
            ]);
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(responses.Dequeue());
            });

            var result = await verifier.RunAsync(
                root,
                changedFiles: ["src/Mcg.AgentOrchestrator.Core/Application/Foo.cs"]);

            Assert.True(result.Passed);
            Assert.Contains("Mcg.AgentOrchestrator.sln", calls[2], StringComparer.OrdinalIgnoreCase);
            Assert.True(result.Checks!.Any(check => check.Name == "full dotnet tests"));
            Assert.True(result.Checks.Any(check => check.ResultSummary == "covered by: full dotnet tests"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", previous);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_full_shards_override_runs_all_policy_shards")]
    public async Task GoalAcceptanceVerifierFullShardsOverrideRunsAllPolicyShards()
    {
        var previous = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS");
        Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", "1");
        try
        {
            var root = CreateCheckedInManifestShapeWorkspace();
            var calls = new List<string[]>();
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Tests passed."
                        : ""));
            });

            var result = await verifier.RunAsync(
                root,
                changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workers/Foo.cs"]);

            Assert.True(result.Passed);
            Assert.Equal("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", calls[2][2]);
            var infrastructureCalls = calls
                .Where(call => call.Length > 2 &&
                    call[0] == "dotnet" &&
                    call[1] == "test" &&
                    call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
                .ToArray();
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, infrastructureCalls.Length);
            Assert.DoesNotContain(result.Checks!, check =>
                check.ResultSummary?.Contains("skipped: no changed file in dependency closure", StringComparison.Ordinal) == true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", previous);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_dedupes_policy_injection_against_equivalent_manifest_check")]
    public async Task GoalAcceptanceVerifierDedupesPolicyInjectionAgainstEquivalentManifestCheck()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),                                                        // build-server shutdown
            new(0, "Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5.")    // core tests (manifest)
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        // Touching core source -> impact plan produces "core tests" (already in manifest).
        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Core/Application/Foo.cs"]);

        Assert.True(result.Passed);
        Assert.Equal(2, calls.Count);
        // "core tests" runs exactly once (not duplicated by injection)
        Assert.Equal(1, result.Checks!.Count(c => c.Name == "core tests"));
        Assert.Equal(0, result.Checks!.Count(c => c.Name == "infrastructure tests"));
        Assert.True(result.Checks.All(c => c.Passed));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_records_policy_alias_for_equivalent_manifest_command")]
    public async Task GoalAcceptanceVerifierRecordsPolicyAliasForEquivalentManifestCommand()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "renamed core coverage", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5."));
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Core/Application/Foo.cs"]);

        Assert.True(result.Passed);
        Assert.Equal(2, calls.Count);
        Assert.Equal(1, result.Checks!.Count(c => c.Name == "renamed core coverage"));
        var policyAlias = result.Checks.Single(c => c.Name == "core tests");
        Assert.True(policyAlias.Passed);
        Assert.Equal("covered by: renamed core coverage", policyAlias.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_failing_injected_policy_check_blocks_merge")]
    public async Task GoalAcceptanceVerifierFailingInjectedPolicyCheckBlocksMerge()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),                                                            // build-server shutdown
            new(1, "Failed! - Failed: 2, Passed: 3, Skipped: 0, Total: 5.")       // core tests (injected, fails)
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        // Core source file → impact plan requires "core tests", which gets injected and fails
        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Core/Application/Foo.cs"]);

        Assert.False(result.Passed);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(2, calls.Count);
        // "core tests" ran (injected) and failed; "infrastructure tests" was not reached
        Assert.True(result.Checks!.Any(c => c.Name == "core tests" && !c.Passed));
        Assert.False(result.Checks.Any(c => c.Name == "infrastructure tests"));
    }

    private static void SetPartitionVerdictKeyHooks(string candidateTreeSha, string mainSha, string verifyingCommitSha)
    {
        GoalAcceptanceVerifier.ResolvePartitionVerdictCandidateTreeShaForTests = _ => candidateTreeSha;
        GoalAcceptanceVerifier.ResolvePartitionVerdictMainShaForTests = _ => mainSha;
        GoalAcceptanceVerifier.ResolvePartitionVerdictVerifyingCommitShaForTests = _ => verifyingCommitSha;
    }

    private static async Task<AcceptanceVerificationResult> RunTwoLaneShardScenarioAsync(
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

    private static string CreateTwoLaneShardManifestWorkspace(int maxConcurrentShards) =>
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

    private static string CreateRealProcessShardManifestWorkspace() =>
        CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  {
                    "name": "Real process alpha",
                    "filter": "FullyQualifiedName~RealProcessShardAlphaSmokeTests"
                  },
                  {
                    "name": "Real process beta",
                    "filter": "FullyQualifiedName~RealProcessShardBetaSmokeTests"
                  }
                ],
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
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

    private static async Task WaitForShardSignalAsync(string signalPath)
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

    private static async Task<GoalAcceptanceVerifier.CommandResult> RunRealShardProcessAsync(
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

    private static int StableSlotIndex(string path)
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

    private static void ResetPartitionVerdictKeyHooks()
    {
        GoalAcceptanceVerifier.ResolvePartitionVerdictCandidateTreeShaForTests = null;
        GoalAcceptanceVerifier.ResolvePartitionVerdictMainShaForTests = null;
        GoalAcceptanceVerifier.ResolvePartitionVerdictVerifyingCommitShaForTests = null;
    }

    private static int CountInfrastructurePartitionTestCalls(IEnumerable<string[]> calls) =>
        calls.Count(IsInfrastructurePartitionTestCall);

    // A partition shard for Infrastructure.Tests appears as exactly one command per shard, in one of
    // two runner shapes depending on how the check was synthesized:
    //   * runner=mtp (impact-plan / policy-synthesized checks): the self-contained executable with a
    //     translated class filter (--filter-class / --filter-not-class). The preceding `dotnet build`
    //     call carries no class filter and is excluded.
    //   * runner=vstest (manifest-loaded checks that omit an explicit runner): `dotnet test <csproj>`
    //     with a raw `--filter`.
    // Either way there is one matching call per shard, so assertions derive counts from the lane schema.
    private static bool IsInfrastructurePartitionTestCall(string[] args) =>
        (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests") &&
            (args.Contains("--filter-class") || args.Contains("--filter-not-class"))) ||
        (args.Length > 2 &&
            args[0] == "dotnet" &&
            args[1] == "test" &&
            args[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj" &&
            args.Contains("--filter"));

    private static bool IsMtpExecutableCall(string[] args, string projectName) =>
        args.Length > 0 &&
        Path.GetFileNameWithoutExtension(args[0]).Equals(projectName, StringComparison.OrdinalIgnoreCase);

    private static void AssertArgumentPair(string[] args, string option, string value) =>
        Assert.True(HasArgumentPair(args, option, value), $"Expected {option} {value}.");

    private static bool HasArgumentPair(string[] args, string option, string value)
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

    private static void WriteMtpTrx(string[] args)
    {
        WriteMtpTrx(args, sourcePath: null);
    }

    private static void WriteMtpTrx(string[] args, string? sourcePath)
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

    private static string MtpFailureFixturePath() =>
        Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "TestData",
            "Fixtures",
            "mtp-xunit-v3-failures.trx.xml");

    private static void CorruptPartitionCacheKey(string root, string partitionId)
    {
        var path = Path.Combine(
            root,
            ".orchestrator",
            "goal-operations",
            "12345678123456781234567812345678.jsonl");
        var lines = File.ReadAllLines(path);
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

        File.WriteAllLines(path, lines);
    }

    private static void AssertIsolatedTestCommand(string[] args)
    {
        Assert.Equal("dotnet", args[0]);
        Assert.Equal("test", args[1]);
        Assert.True(args.Any(arg => arg.Equals("--artifacts-path", StringComparison.Ordinal)));
        Assert.False(args.Any(arg => arg.Equals("--disable-build-servers", StringComparison.Ordinal)));
        Assert.False(args.Any(arg => arg.Equals("-p:UseSharedCompilation=false", StringComparison.Ordinal)));
        Assert.True(args.Any(arg => arg.StartsWith("-maxcpucount:", StringComparison.Ordinal) && !arg.Equals("-maxcpucount:1", StringComparison.Ordinal)));
        Assert.Contains("mcg-dotnet-isolated", GetArtifactsPath(args), StringComparison.Ordinal);
    }

    private static string GetArtifactsPath(string[] args)
    {
        var artifactsPathIndex = Array.IndexOf(args, "--artifacts-path");
        Assert.True(artifactsPathIndex >= 0);
        Assert.True(artifactsPathIndex + 1 < args.Length);
        return args[artifactsPathIndex + 1];
    }

    private static void WriteProjectArtifacts(string artifactsPath, string project, string content)
    {
        var projectName = Path.GetFileNameWithoutExtension(project);
        var binPath = Path.Combine(artifactsPath, "bin", projectName, "debug_net10.0");
        var objPath = Path.Combine(artifactsPath, "obj", projectName, "debug_net10.0");
        Directory.CreateDirectory(binPath);
        Directory.CreateDirectory(objPath);
        File.WriteAllText(Path.Combine(binPath, "cache.txt"), content);
        File.WriteAllText(Path.Combine(objPath, "cache.obj"), content);
    }

    private static void TryDeleteStableSlotHeartbeat(int slotIndex)
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

    private static System.Diagnostics.Process StartSleepProcess()
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

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_advisory_grep_absent_failure_does_not_affect_passed")]
    public async Task GoalAcceptanceVerifierAdvisoryGrepAbsentFailureDoesNotAffectPassed()
    {
        var root = CreateAdvisoryWorkspace("""
            [
              {
                "name": "grep confirms no path still calls `new JsonSerializerOptions`",
                "type": "grep-absent",
                "pattern": "new JsonSerializerOptions"
              }
            ]
            """);

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),                                                         // build-server shutdown
            new(0, "Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5."),   // dotnet test
            new(0, "src/Foo.cs:JsonSerializerOptions opts = new JsonSerializerOptions();") // git grep (pattern found)
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        // Suite passed, advisory grep-absent FAILED (pattern found)
        Assert.True(result.Passed);
        var advisoryCheck = result.Checks!.Single(c => c.Advisory);
        Assert.False(advisoryCheck.Passed);
        Assert.True(advisoryCheck.Advisory);
        Assert.True(advisoryCheck.Name.Contains("new JsonSerializerOptions", StringComparison.Ordinal));
        // grep was the last call
        Assert.Equal("git", calls.Last()[0]);
        Assert.Equal("grep", calls.Last()[1]);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_advisory_grep_absent_passes_when_pattern_absent")]
    public async Task GoalAcceptanceVerifierAdvisoryGrepAbsentPassesWhenPatternAbsent()
    {
        var root = CreateAdvisoryWorkspace("""
            [
              {
                "name": "grep confirms no `OldClass` remains",
                "type": "grep-absent",
                "pattern": "OldClass"
              }
            ]
            """);

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),   // build-server shutdown
            new(0, "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2."), // dotnet test
            new(1, "")    // git grep exit 1 = pattern not found
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        Assert.True(result.Passed);
        var advisoryCheck = result.Checks!.Single(c => c.Advisory);
        Assert.True(advisoryCheck.Passed);
        Assert.Equal("pattern absent", advisoryCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_advisory_file_exists_check_reports_pass_and_fail")]
    public async Task GoalAcceptanceVerifierAdvisoryFileExistsCheckReportsPassAndFail()
    {
        // Use a file that does NOT yet exist in the workspace
        var root = CreateAdvisoryWorkspace("""
            [
              {
                "name": "`.orchestrator/output.txt` is produced",
                "type": "file-exists",
                "path": ".orchestrator/output.txt"
              }
            ]
            """);

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),  // build-server shutdown
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")  // dotnet test
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        // First run: .orchestrator/output.txt does not exist
        var resultMissing = await verifier.RunAsync(root);
        Assert.True(resultMissing.Passed);
        var missingCheck = resultMissing.Checks!.Single(c => c.Advisory);
        Assert.False(missingCheck.Passed);
        Assert.Equal("file not found", missingCheck.ResultSummary);

        // Create the file and verify it now passes
        var responses2 = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")
        ]);
        File.WriteAllText(Path.Combine(root, ".orchestrator", "output.txt"), "done");

        var verifier2 = new GoalAcceptanceVerifier((args, _, _) =>
        {
            return Task.FromResult(responses2.Dequeue());
        });

        var resultPresent = await verifier2.RunAsync(root);
        Assert.True(resultPresent.Passed);
        var presentCheck = resultPresent.Checks!.Single(c => c.Advisory);
        Assert.True(presentCheck.Passed);
        Assert.Equal("file exists", presentCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_advisory_command_exit_runs_and_reports")]
    public async Task GoalAcceptanceVerifierAdvisoryCommandExitRunsAndReports()
    {
        var root = CreateAdvisoryWorkspace("""
            [
              {
                "name": "`dotnet build Fake.sln -c Release` clean, no new warnings.",
                "type": "command-exit",
                "command": "dotnet build Fake.sln -c Release"
              }
            ]
            """);

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),   // build-server shutdown
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."),   // dotnet test
            new(0, "Build succeeded.")  // dotnet build (advisory command-exit)
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        Assert.True(result.Passed);
        var advisoryCheck = result.Checks!.Single(c => c.Advisory);
        Assert.True(advisoryCheck.Passed);
        Assert.True(advisoryCheck.Advisory);

        // Advisory command was the last call
        var lastCall = calls.Last();
        Assert.Equal("dotnet", lastCall[0]);
        Assert.Equal("build", lastCall[1]);
        Assert.Equal("Fake.sln", lastCall[2]);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_advisory_checks_run_even_when_suite_fails")]
    public async Task GoalAcceptanceVerifierAdvisoryChecksRunEvenWhenSuiteFails()
    {
        var root = CreateAdvisoryWorkspace("""
            [
              {
                "name": "`.orchestrator/marker.txt` exists",
                "type": "file-exists",
                "path": ".orchestrator/marker.txt"
              }
            ]
            """);
        File.WriteAllText(Path.Combine(root, ".orchestrator", "marker.txt"), "exists");

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),   // build-server shutdown
            new(1, "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1.")  // dotnet test FAILS
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        // Overall failed because suite failed
        Assert.False(result.Passed);
        // But advisory check still ran and passed
        var advisoryCheck = result.Checks!.Single(c => c.Advisory);
        Assert.True(advisoryCheck.Passed);
        Assert.Equal("file exists", advisoryCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_loads_no_advisory_checks_when_criteria_file_missing")]
    public async Task GoalAcceptanceVerifierLoadsNoAdvisoryChecksWhenCriteriaFileMissing()
    {
        var calls = new List<string[]>();

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                0,
                args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                    ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                    : ""));
        });

        // Use a plain worktree with no criteria file
        var result = await verifier.RunAsync("C:\\fake\\worktree");

        Assert.True(result.Passed);
        Assert.True(result.Checks is null || !result.Checks.Any(c => c.Advisory));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_flags_deleted_fact_method")]
    public async Task GoalAcceptanceVerifierTestTamperGuardFlagsDeletedFactMethod()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,5 +10,0 @@",
            "-    [Xunit.Fact(DisplayName = \"some test\")]",
            "-    public void SomeTest()",
            "-    {",
            "-        Assert.True(something);",
            "-    }"
        ]);

        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await RunTamperGuardAsync(
            verifier,
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"]);

        // Suite still passed — advisory does not gate
        Assert.True(result.Passed);
        Assert.Equal(0, result.ExitCode);

        // Tamper check is advisory and failed
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.False(tamperCheck.Passed);
        Assert.True(tamperCheck.OutputTail is not null);
        Assert.Contains("FooTests.cs", tamperCheck.OutputTail!, StringComparison.Ordinal);

        // Git diff was the last call
        var lastCall = calls.Last();
        Assert.Equal("git", lastCall[0]);
        Assert.Equal("diff", lastCall[1]);
        Assert.True(lastCall.Any(a => a.Equals("main...HEAD", StringComparison.Ordinal)));
        Assert.True(lastCall.Any(a => a.Equals("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_passes_when_assertions_added")]
    public async Task GoalAcceptanceVerifierTestTamperGuardPassesWhenAssertionsAdded()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,0 +10,5 @@",
            "+    [Xunit.Fact(DisplayName = \"new test\")]",
            "+    public void NewTest()",
            "+    {",
            "+        Assert.True(something);",
            "+    }"
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await RunTamperGuardAsync(
            verifier,
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"]);

        Assert.True(result.Passed);
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.True(tamperCheck.Passed);
        Assert.Equal("no test degradation detected", tamperCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_allows_mechanical_test_split")]
    public async Task GoalAcceptanceVerifierTestTamperGuardAllowsMechanicalTestSplit()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,5 +10,0 @@",
            "-    [Xunit.Fact(DisplayName = \"some test\")]",
            "-    public void SomeTest()",
            "-    {",
            "-        Assert.True(something);",
            "-    }",
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs",
            "--- /dev/null",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs",
            "@@ -0,0 +1,5 @@",
            "+    [Xunit.Fact(DisplayName = \"some test\")]",
            "+    public void SomeTest()",
            "+    {",
            "+        Assert.True(something);",
            "+    }"
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await RunTamperGuardAsync(
            verifier,
            [
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs"
            ]);

        Assert.True(result.Passed);
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.True(tamperCheck.Passed);
        Assert.Equal("no test degradation detected", tamperCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_flags_diff_wide_net_removal")]
    public async Task GoalAcceptanceVerifierTestTamperGuardFlagsDiffWideNetRemoval()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,10 +10,0 @@",
            "-    [Xunit.Fact(DisplayName = \"first test\")]",
            "-    public void FirstTest()",
            "-    {",
            "-        Assert.True(first);",
            "-    }",
            "-    [Xunit.Fact(DisplayName = \"second test\")]",
            "-    public void SecondTest()",
            "-    {",
            "-        Assert.True(second);",
            "-    }",
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs",
            "--- /dev/null",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs",
            "@@ -0,0 +1,5 @@",
            "+    [Xunit.Fact(DisplayName = \"first test\")]",
            "+    public void FirstTest()",
            "+    {",
            "+        Assert.True(first);",
            "+    }"
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await RunTamperGuardAsync(
            verifier,
            [
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs"
            ]);

        Assert.True(result.Passed);
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.False(tamperCheck.Passed);
        Assert.Equal("2 test degradation signal(s)", tamperCheck.ResultSummary);
        Assert.Contains("diff-wide net -1 assertion(s) removed", tamperCheck.OutputTail!, StringComparison.Ordinal);
        Assert.Contains("diff-wide 1 test method(s) removed", tamperCheck.OutputTail!, StringComparison.Ordinal);
        Assert.Contains("FooTests.cs: assertions -2/+0, tests -2/+0", tamperCheck.OutputTail!, StringComparison.Ordinal);
        Assert.Contains("SplitFooTests.cs: assertions -0/+1, tests -0/+1", tamperCheck.OutputTail!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_flags_tautology_assertion")]
    public async Task GoalAcceptanceVerifierTestTamperGuardFlagsTautologyAssertion()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -12,1 +12,1 @@",
            "-        Assert.True(something);",
            "+        Assert.True(true);"
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await RunTamperGuardAsync(
            verifier,
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"]);

        Assert.True(result.Passed);
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.False(tamperCheck.Passed);
        Assert.True(tamperCheck.OutputTail is not null);
        Assert.Contains("tautology", tamperCheck.OutputTail!, StringComparison.OrdinalIgnoreCase);
        var tautologyAssertion = "Assert." + "True(true)";
        Assert.Contains(tautologyAssertion, tamperCheck.OutputTail!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_flags_tautology_across_files")]
    public async Task GoalAcceptanceVerifierTestTamperGuardFlagsTautologyAcrossFiles()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,5 +10,0 @@",
            "-    [Xunit.Fact(DisplayName = \"some test\")]",
            "-    public void SomeTest()",
            "-    {",
            "-        Assert.True(something);",
            "-    }",
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs",
            "--- /dev/null",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs",
            "@@ -0,0 +1,5 @@",
            "+    [Xunit.Fact(DisplayName = \"some test\")]",
            "+    public void SomeTest()",
            "+    {",
            "+        Assert.True(true);",
            "+    }"
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await RunTamperGuardAsync(
            verifier,
            [
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SplitFooTests.cs"
            ]);

        Assert.True(result.Passed);
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.False(tamperCheck.Passed);
        Assert.Equal("1 test degradation signal(s)", tamperCheck.ResultSummary);
        Assert.Contains("SplitFooTests.cs", tamperCheck.OutputTail!, StringComparison.Ordinal);
        Assert.Contains("tautology", tamperCheck.OutputTail!, StringComparison.OrdinalIgnoreCase);
        var tautologyAssertion = "Assert." + "True(true)";
        Assert.Contains(tautologyAssertion, tamperCheck.OutputTail!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_absent_when_no_test_files_in_diff")]
    public async Task GoalAcceptanceVerifierTestTamperGuardAbsentWhenNoTestFilesInDiff()
    {
        var calls = new List<string[]>();
        var root = CreatePartitionedInfrastructureManifestWorkspace();

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            return Task.FromResult(args.Length >= 3 &&
                args[0] == "dotnet" &&
                args[1] == "test" &&
                args[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                ? new GoalAcceptanceVerifier.CommandResult(1, "unexpected vstest fallback")
                : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles: [
                "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs",
                "README.md"
            ]);

        Assert.True(result.Passed);
        Assert.False(result.Checks?.Any(c => c.Name == "test tamper guard") == true);
        var infrastructureCalls = calls
            .Where(IsInfrastructurePartitionTestCall)
            .ToArray();
        var laneCount = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count;
        Assert.Equal(laneCount, infrastructureCalls.Length);
        foreach (var call in infrastructureCalls)
            Assert.True(
                call.Contains("--filter-class") || call.Contains("--filter-not-class"),
                "Each infrastructure shard must carry a translated MTP class filter.");
        // Each MTP shard runs a `dotnet build` before its executable, and there is no git diff
        // call because no test files changed: shutdown + lane count * (build + executable).
        Assert.Equal(1 + (laneCount * 2), calls.Count);
        Assert.DoesNotContain(calls, call => call.Length >= 2 && call[0] == "git" && call[1] == "diff");
        DeleteDirectoryWithRetry(root);
    }

    private static string CreateManifestWorkspace(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-tests", Guid.NewGuid().ToString("N"));
        var manifestDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(manifestDirectory);
        File.WriteAllText(
            Path.Combine(manifestDirectory, "acceptance-manifest.json"),
            AcceptanceManifestTestDefaults.WithEngine(manifest));
        return root;
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_trusted_discovery_enumerates_test_projects_omitted_from_manifest")]
    public void GoalAcceptanceVerifierTrustedDiscoveryEnumeratesTestProjectsOmittedFromManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-project-discovery", Guid.NewGuid().ToString("N"));
        var mainRoot = Path.Combine(Path.GetTempPath(), "mcg-acceptance-project-discovery-main", Guid.NewGuid().ToString("N"));
        try
        {
            var coreProject = Path.Combine(root, "tests", "Core.Tests", "Core.Tests.csproj");
            var addedProject = Path.Combine(root, "tests", "Added", "Added.csproj");
            var supportProject = Path.Combine(root, "tests", "Support", "Support.csproj");
            var mainOnlyProject = Path.Combine(mainRoot, "tests", "Legacy.Tests", "Legacy.Tests.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(coreProject)!);
            Directory.CreateDirectory(Path.GetDirectoryName(addedProject)!);
            Directory.CreateDirectory(Path.GetDirectoryName(supportProject)!);
            Directory.CreateDirectory(Path.GetDirectoryName(mainOnlyProject)!);
            File.WriteAllText(coreProject, "<Project />");
            File.WriteAllText(
                addedProject,
                "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
            File.WriteAllText(supportProject, "<Project />");
            File.WriteAllText(mainOnlyProject, "<Project />");

            var projects = GoalAcceptanceVerifier.DiscoverTrustedTestProjects(root, mainRoot);

            Assert.Equal(
                [
                    "tests/Added/Added.csproj",
                    "tests/Core.Tests/Core.Tests.csproj",
                    "tests/Legacy.Tests/Legacy.Tests.csproj"
                ],
                projects);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_scopes_deleted_test_files_to_their_project")]
    public void GoalAcceptanceVerifierScopesDeletedTestFilesToTheirProject()
    {
        var deleted = new[]
        {
            "tests/Core.Tests/FooTests.cs",
            "tests/Infrastructure.Tests/FooTests.cs",
            "tests/Infrastructure.Tests/Nested/BarTests.cs"
        };

        var core = GoalAcceptanceVerifier.DeletedTestFilesForProject(
            deleted,
            "tests/Core.Tests/Core.Tests.csproj");

        Assert.Equal(["tests/Core.Tests/FooTests.cs"], core);
    }

    private static string? SetAcceptanceTimeoutEnvironment(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(AcceptanceCheckTimeouts.EnvironmentVariable);
        Environment.SetEnvironmentVariable(AcceptanceCheckTimeouts.EnvironmentVariable, value);
        return previous;
    }

    private static string CreateStandardManifestWorkspace() =>
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

    private static string CreateCheckedInManifestShapeWorkspace() =>
        CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] },
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
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

    private static string CreatePartitionedInfrastructureManifestWorkspace() =>
        CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);

    private static async Task<AcceptanceVerificationResult> RunTamperGuardAsync(
        GoalAcceptanceVerifier verifier,
        IReadOnlyList<string> changedFiles)
    {
        var root = CreatePartitionedInfrastructureManifestWorkspace();
        try
        {
            return await verifier.RunAsync(root, changedFiles: changedFiles);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    private static string CreateAdvisoryWorkspace(string criteriaJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-tests", Guid.NewGuid().ToString("N"));
        var orchestratorDir = Path.Combine(root, ".orchestrator");
        Directory.CreateDirectory(orchestratorDir);
        File.WriteAllText(
            Path.Combine(orchestratorDir, "goal-acceptance-criteria.json"),
            criteriaJson);
        return root;
    }
}

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class RealProcessShardAlphaSmokeTests
{
    [Xunit.Fact]
    public Task SynchronizesWithBetaShard() =>
        GoalAcceptanceVerifierDotnetBuildSlotTests.SynchronizeRealProcessShardSmokeAsync(
            "MCG_SHARD_SMOKE_ALPHA_SIGNAL",
            "MCG_SHARD_SMOKE_BETA_SIGNAL");
}

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class RealProcessShardBetaSmokeTests
{
    [Xunit.Fact]
    public Task SynchronizesWithAlphaShard() =>
        GoalAcceptanceVerifierDotnetBuildSlotTests.SynchronizeRealProcessShardSmokeAsync(
            "MCG_SHARD_SMOKE_BETA_SIGNAL",
            "MCG_SHARD_SMOKE_ALPHA_SIGNAL");
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
