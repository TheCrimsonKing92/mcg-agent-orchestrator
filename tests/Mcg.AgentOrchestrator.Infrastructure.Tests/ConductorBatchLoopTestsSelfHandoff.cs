using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsSelfHandoff : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsSelfHandoff(ITestOutputHelper output)
        : base(output)
    {
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Fact(DisplayName = "BatchLoop_default_self_relaunch_activation_does_not_schedule_or_execute")]
    public void BatchLoopDefaultSelfRelaunchActivationDoesNotScheduleOrExecute()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update conductor loop");
        var landed = false;
        var relaunchCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: candidate =>
            {
                landed = true;
                return new LandingResult(candidate.Id.Value, candidate.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ =>
                ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"]);

        var output = AsyncLocalConsoleRouter.Capture(() =>
            new ConductorBatchLoop(
                selfRelaunch: _ =>
                {
                    relaunchCalls++;
                    return new ConductorSelfRelaunchResult(false, "build", "must not run");
                }).Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 3));

        Assert.Equal(0, relaunchCalls);
        Assert.DoesNotContain("LOOP_RELAUNCH_SCHEDULED", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_infrastructure_landing_drains_before_self_handoff")]
    public void BatchLoopInfrastructureLandingDrainsBeforeSelfHandoff()
    {
        var kernel = new AgentOrchestratorKernel();
        var infraGoal = CreateVerifiedSimpleGoal(kernel, "Update conductor loop");
        var runningGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Existing worker");
        var runningTask = runningGoal.Tasks.Single();
        StartProcess(kernel, runningGoal, runningTask, DateTimeOffset.UtcNow, "abc123");
        var landed = false;
        var order = new List<string>();
        Process? successor = null;
        var handoffRoot = CreateTempDirectory("mcg-conduct-loop-drain-real-handoff");
        var loopStartedPath = Path.Combine(handoffRoot, "loop-started");
        var driver = MakeDriver(
            getFacts: goal => goal.Id == infraGoal.Id && landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: goal =>
            {
                landed = true;
                return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ =>
                ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"]);

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var summary = new ConductorBatchLoop(
                    selfRelaunch: request =>
                    {
                        order.Add("handoff");
                        var handoff = ConductorLoopHandoff.TryStartSuccessor(
                            HandoffOptions(
                                handoffRoot,
                                release: () => order.Add("authority-release"),
                                verificationTimeout: TimeSpan.FromMilliseconds(50),
                                loopStartProbe: (_, _) => File.Exists(loopStartedPath),
                                useProtocolReady: true),
                            new ConductorLoopHandoffRequest(request.Tick, TimeSpan.Zero, 0, 1),
                            launchRequest =>
                            {
                                successor = Process.Start(new ProcessStartInfo("powershell")
                                {
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    ArgumentList =
                                    {
                                        "-NoProfile",
                                        "-Command",
                                        $"Set-Content -LiteralPath '{launchRequest.ReadyFilePath!.Replace("'", "''", StringComparison.Ordinal)}' " +
                                        $"-Value ('LOOP_HANDOFF_READY token={launchRequest.HandoffToken} pid=' + $PID); " +
                                        $"while (-not (Test-Path -LiteralPath '{launchRequest.ActivationFilePath!.Replace("'", "''", StringComparison.Ordinal)}')) {{ Start-Sleep -Milliseconds 25 }}; " +
                                        $"Set-Content -LiteralPath '{loopStartedPath.Replace("'", "''", StringComparison.Ordinal)}' -Value started; Start-Sleep -Seconds 10"
                                    }
                                })!;
                                return new ConductLoopLaunchResult(
                                    successor.Id,
                                    launchRequest.StdoutPath,
                                    launchRequest.StderrPath,
                                    "spawnPath=test-real-process");
                            });
                        return new ConductorSelfRelaunchResult(
                            handoff.Started,
                            handoff.Started ? null : "handoff",
                            handoff.Reason,
                            handoff);
                    },
                    selfRelaunchEnabled: true).Run(
                        kernel,
                        driver,
                        ConductorAutonomyPolicy.Conservative,
                        NoStopPath(),
                        maxIterations: 5,
                        watchInterval: TimeSpan.FromMilliseconds(1),
                        sleepFunc: _ =>
                        {
                            order.Add("terminal-receipt");
                            CompleteDispatchedTask(kernel, runningGoal, runningTask, DateTimeOffset.UtcNow, "def456");
                            return false;
                        });

                Assert.True(summary.Handoff?.Started);
            });

            Assert.True(order.IndexOf("terminal-receipt") < order.IndexOf("handoff"));
            Assert.True(order.IndexOf("handoff") < order.IndexOf("authority-release"));
            Assert.Contains("LOOP_RELAUNCH_DRAIN", output, StringComparison.Ordinal);
            Assert.Contains("active=1 admitting=false", output, StringComparison.Ordinal);
            Assert.Contains("LOOP_HANDOFF", output, StringComparison.Ordinal);
            Assert.False(successor!.HasExited);
        }
        finally
        {
            if (successor is { HasExited: false })
            {
                successor.Kill(entireProcessTree: true);
                successor.WaitForExit(5000);
            }
            successor?.Dispose();
            TryDeleteDirectory(handoffRoot);
        }
    }

    [Xunit.Theory(DisplayName = "BatchLoop_self_relaunch_failure_rolls_back_and_continues")]
    [Xunit.InlineData("self-check", "LOOP_RELAUNCH_ROLLBACK", true)]
    [Xunit.InlineData("handoff", "LOOP_HANDOFF_FAILED", true)]
    [Xunit.InlineData("handoff", "LOOP_HANDOFF_FAILED", false)]
    public void BatchLoopSelfRelaunchFailureRollsBackAndContinues(
        string failedPhase,
        string expectedEvent,
        bool incumbentCanContinue)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update verifier");
        var landed = false;
        var relaunchCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: candidate =>
            {
                landed = true;
                return new LandingResult(candidate.Id.Value, candidate.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ =>
                ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"]);

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var loop = new ConductorBatchLoop(
                selfRelaunch: _ =>
                {
                    relaunchCalls++;
                    return new ConductorSelfRelaunchResult(
                        false,
                        failedPhase,
                        "forced failure",
                        IncumbentCanContinue: incumbentCanContinue);
                },
                selfRelaunchEnabled: true);
            if (incumbentCanContinue)
            {
                var summary = loop.Run(
                        kernel,
                        driver,
                        ConductorAutonomyPolicy.Conservative,
                        NoStopPath(),
                        maxIterations: 4);
                Assert.Null(summary.Handoff);
            }
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() => loop.Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 4));
                Assert.Contains("refusing to continue", error.Message, StringComparison.OrdinalIgnoreCase);
            }
        });

        Assert.Equal(1, relaunchCalls);
        Assert.Contains(expectedEvent, output, StringComparison.Ordinal);
        Assert.Contains($"phase={failedPhase}", output, StringComparison.Ordinal);
        Assert.Contains(
            incumbentCanContinue
                ? "rolledBack=true continuing=true"
                : "rolledBack=false continuing=false",
            output,
            StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_non_infrastructure_landing_does_not_self_relaunch")]
    public void BatchLoopNonInfrastructureLandingDoesNotSelfRelaunch()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update dashboard rendering");
        var landed = false;
        var relaunchCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: candidate =>
            {
                landed = true;
                return new LandingResult(candidate.Id.Value, candidate.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ =>
                ["src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.cs"]);

        new ConductorBatchLoop(
            selfRelaunch: _ =>
            {
                relaunchCalls++;
                return new ConductorSelfRelaunchResult(false, "build", "must not run");
            },
            selfRelaunchEnabled: true).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 3);

        Assert.Equal(0, relaunchCalls);
    }

    private static ConductLoopHandoffOptions HandoffOptions(
        string root,
        IReadOnlyList<string>? args = null,
        string? stopFilePath = null,
        string? runEventStorePath = null,
        int renewalCount = 0,
        int maxRenewals = ConductorLoopHandoff.DefaultMaxRenewalsWithoutLanding,
        Action? release = null,
        TimeSpan verificationTimeout = default,
        Func<ConductLoopHandoffOptions, long, bool>? loopStartProbe = null,
        bool useProtocolReady = false) =>
        new(
            Args: args ?? ["conduct", "--loop", "--watch", "--max-duration", "14400"],
            ExecutionDirectory: root,
            OrchestratorDirectory: Path.Combine(root, ".orchestrator"),
            LogDirectory: Path.Combine(root, ".orchestrator", "logs"),
            RunEventStorePath: runEventStorePath ?? Path.Combine(root, ".orchestrator", "run-events.db"),
            StopFilePath: stopFilePath ?? Path.Combine(root, ConductorBatchLoop.StopFileName),
            RenewalCount: renewalCount,
            MaxRenewals: maxRenewals,
            ReleaseCurrentLease: release ?? (() => { }),
            VerificationTimeout: verificationTimeout,
            LoopStartProbe: loopStartProbe,
            SuccessorReadyProbe: useProtocolReady ? null : (_, _) => true);

    [Xunit.Fact(DisplayName = "BatchLoop_max_duration_starts_handoff")]
    public void BatchLoopMaxDurationStartsHandoff()
    {
        var (kernel, _) = SimpleGoal();
        ConductorLoopHandoffRequest? request = null;
        var summary = new ConductorBatchLoop(
            handoffOnMaxDuration: handoffRequest =>
            {
                request = handoffRequest;
                return ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log");
            }).Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxDuration: TimeSpan.Zero);

        Assert.NotNull(request);
        Assert.True(summary.Handoff?.Started);
        Assert.Equal(1234, summary.Handoff.ProcessId);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_stop_file_does_not_handoff")]
    public void BatchLoopStopFileDoesNotHandoff()
    {
        var (kernel, _) = SimpleGoal();
        var called = false;

        var summary = new ConductorBatchLoop(
            handoffOnMaxDuration: _ =>
            {
                called = true;
                return ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log");
            }).Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            ExistingStopPath(),
            maxDuration: TimeSpan.FromSeconds(1));

        Assert.True(summary.StopRequested);
        Assert.False(called);
        Assert.Null(summary.Handoff);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_no_progress_exit_does_not_handoff")]
    public void BatchLoopNoProgressExitDoesNotHandoff()
    {
        var (kernel, _) = SimpleGoal();
        var called = false;

        new ConductorBatchLoop(
            handoffOnMaxDuration: _ =>
            {
                called = true;
                return ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log");
            }).Run(
            kernel,
            MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath());

        Assert.False(called);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_max_duration_detaches_running_dispatches_before_handoff")]
    public void BatchLoopMaxDurationDetachesRunningDispatchesBeforeHandoff()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        StartProcess(kernel, goal, task, DateTimeOffset.UtcNow, "base");
        var detached = 0;
        var cancelled = 0;

        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (_, _) => cancelled++,
            detachGoalRunningDispatches: (_, _) => detached++,
            handoffOnMaxDuration: _ => ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log")).Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxDuration: TimeSpan.Zero);

        Assert.True(summary.Handoff?.Started);
        Assert.Equal(1, detached);
        Assert.Equal(0, cancelled);
    }

    [Xunit.Fact(DisplayName = "ConductorLoopLease_refuses_second_loop")]
    public void ConductorLoopLeaseRefusesSecondLoop()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-lease");
        try
        {
            var orchestrator = Path.Combine(root, ".orchestrator");
            using var lease = ConductorLoopLease.Acquire(orchestrator);

            Assert.Throws<InvalidOperationException>(() => ConductorLoopLease.Acquire(orchestrator));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_stop_file_suppresses_successor")]
    public void ConductorLoopHandoffStopFileSuppressesSuccessor()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-stop-handoff");
        try
        {
            var stopFile = Path.Combine(root, ConductorBatchLoop.StopFileName);
            File.WriteAllText(stopFile, "stop");
            var result = ConductorLoopHandoff.TryStartSuccessor(
                HandoffOptions(root, stopFilePath: stopFile),
                new ConductorLoopHandoffRequest(0, TimeSpan.Zero, 0),
                _ => throw new InvalidOperationException("launch should not run"));

            Assert.False(result.Started);
            Assert.Equal("stop-file", result.Reason);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_stops_at_renewal_cap_without_landing")]
    public void ConductorLoopHandoffStopsAtRenewalCapWithoutLanding()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-renewal-cap");
        try
        {
            var result = ConductorLoopHandoff.TryStartSuccessor(
                HandoffOptions(root, renewalCount: 6, maxRenewals: 6),
                new ConductorLoopHandoffRequest(0, TimeSpan.Zero, 0),
                _ => throw new InvalidOperationException("launch should not run"));

            Assert.False(result.Started);
            Assert.Contains("renewal-cap", result.Reason, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_launches_successor_with_incremented_batch_fresh_logs_and_renewal_arg")]
    public void ConductorLoopHandoffLaunchesSuccessorWithIncrementedBatchFreshLogsAndRenewalArg()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-happy-handoff");
        var previousName = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME", "batch25");
            var released = false;
            ConductLoopLaunchRequest? launchRequest = null;
            var result = ConductorLoopHandoff.TryStartSuccessor(
                HandoffOptions(
                    root,
                    args: ["conduct", "--loop", "--watch", "--max-duration", "14400", ConductorLoopHandoff.RenewalCountFlag, "4"],
                    renewalCount: 4,
                    release: () => released = true),
                new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                request =>
                {
                    launchRequest = request;
                    return new ConductLoopLaunchResult(4567, request.StdoutPath, request.StderrPath);
                },
                (_, _) =>
                {
                    File.WriteAllText(launchRequest!.StdoutPath, "LOOP_START");
                    return new ConductLoopHandoffVerification(true, true, true, "processAlive=true stdoutLogExists=true loopStartJournaled=true");
                });

            Assert.True(result.Started);
            Assert.True(released);
            Assert.Equal("batch26", launchRequest!.Name);
            Assert.Contains("operator-batch26-", Path.GetFileName(launchRequest.StdoutPath), StringComparison.Ordinal);
            Assert.DoesNotContain(ConductorLoopHandoff.RenewalCountFlag, launchRequest.Args);
            Assert.Equal(5, launchRequest.RenewalCount);
            Assert.Equal(4567, result.ProcessId);
            Assert.Contains("guard=incumbent-held-until-successor-ready", result.VerificationOutcome, StringComparison.Ordinal);
            Assert.Contains("spawnPath=injected", result.VerificationOutcome, StringComparison.Ordinal);
            Assert.Contains("loopStartJournaled=true", result.VerificationOutcome, StringComparison.Ordinal);

            var records = new SqliteRunEventStore(Path.Combine(root, ".orchestrator", "run-events.db"))
                .ReadSinceAsync()
                .GetAwaiter()
                .GetResult();
            var handoffEvent = Assert.Single(records.Where(record => record.Operation == "LOOP_HANDOFF"));
            Assert.Equal("Started", handoffEvent.Status);
            Assert.Contains(Path.GetFullPath(launchRequest.StdoutPath), handoffEvent.Detail, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(launchRequest.StderrPath), handoffEvent.Detail, StringComparison.Ordinal);
            Assert.Contains("guard=incumbent-held-until-successor-ready", handoffEvent.Detail, StringComparison.Ordinal);
            Assert.Contains("spawnPath=injected", handoffEvent.Detail, StringComparison.Ordinal);
            Assert.Contains("loopStartJournaled=true", handoffEvent.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME", previousName);
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_handoff_renewal_cap_resets_for_landing_adopted_by_reconcile")]
    public void BatchLoopHandoffRenewalCapResetsForLandingAdoptedByReconcile()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-adopted-landing");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Adopted landing");
            var start = DateTimeOffset.Parse("2026-07-11T00:00:00Z");
            var nowCalls = 0;
            DateTimeOffset UtcNow() => nowCalls++ switch
            {
                0 => start,
                1 => start,
                _ => start.AddSeconds(2)
            };

            var reconciled = false;
            ConductLoopLaunchRequest? launchRequest = null;
            ConductorLoopHandoffRequest? handoffRequest = null;
            var summary = new ConductorBatchLoop(
                measuredSweep: loopKernel =>
                {
                    if (!reconciled)
                    {
                        reconciled = true;
                        loopKernel.CompleteGoal(goal.Id, "Test fixture: landing adopted during first reconcile.");
                    }

                    return null;
                },
                handoffOnMaxDuration: request =>
                {
                    handoffRequest = request;
                    return ConductorLoopHandoff.TryStartSuccessor(
                        HandoffOptions(root, renewalCount: 6, maxRenewals: 6),
                        request,
                        launch =>
                        {
                            launchRequest = launch;
                            return new ConductLoopLaunchResult(4567, launch.StdoutPath, launch.StderrPath);
                        },
                        (_, _) =>
                        {
                            File.WriteAllText(launchRequest!.StdoutPath, "LOOP_START");
                            return new ConductLoopHandoffVerification(true, true, true, "processAlive=true stdoutLogExists=true loopStartJournaled=true");
                        });
                },
                utcNow: UtcNow).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                Path.Combine(root, ConductorBatchLoop.StopFileName),
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => false,
                maxDuration: TimeSpan.FromSeconds(1),
                keepAliveWhenIdle: true);

            Assert.True(summary.Handoff?.Started);
            Assert.NotNull(handoffRequest);
            Assert.Equal(0, handoffRequest!.Done);
            Assert.Equal(1, handoffRequest.LandedGoalDelta);
            Assert.DoesNotContain(ConductorLoopHandoff.RenewalCountFlag, launchRequest!.Args);
            Assert.Equal(0, launchRequest.RenewalCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_journal_failure_is_loud_and_still_launches_successor")]
    public void ConductorLoopHandoffJournalFailureIsLoudAndStillLaunchesSuccessor()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-journal-failure");
        try
        {
            string outText = "";
            var errorText = CaptureConsoleError(() =>
            {
                outText = CaptureConsole(() =>
                {
                    var launched = false;

                    var result = ConductorLoopHandoff.TryStartSuccessor(
                        HandoffOptions(root, runEventStorePath: root),
                        new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                        request =>
                        {
                            launched = true;
                            return new ConductLoopLaunchResult(4567, request.StdoutPath, request.StderrPath);
                        },
                        (_, _) =>
                        {
                            return new ConductLoopHandoffVerification(true, true, true, "processAlive=true stdoutLogExists=true loopStartJournaled=true");
                        });

                    Assert.True(result.Started);
                    Assert.True(launched);
                });
            });

            Assert.Contains("LOOP_HANDOFF_JOURNAL_FAILED", outText, StringComparison.Ordinal);
            Assert.Contains("LOOP_HANDOFF_JOURNAL_FAILED", errorText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_does_not_retry_while_successor_is_alive")]
    public void ConductorLoopHandoffDoesNotRetryWhileSuccessorIsAlive()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-slow-handoff");
        try
        {
            string outText = "";
            var errorText = CaptureConsoleError(() =>
            {
                outText = CaptureConsole(() =>
                {
                    var scaledOldTimeout = TimeSpan.FromMilliseconds(500);
                    var scaledLoopStartDelay = TimeSpan.FromMilliseconds(750);
                    var stopwatch = Stopwatch.StartNew();
                    var attempts = 0;
                    var result = ConductorLoopHandoff.TryStartSuccessor(
                        HandoffOptions(
                            root,
                            verificationTimeout: scaledOldTimeout,
                            loopStartProbe: (_, _) => stopwatch.Elapsed >= scaledLoopStartDelay),
                        new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                        request =>
                        {
                            attempts++;
                            File.WriteAllText(request.StdoutPath, "successor booting");
                            return new ConductLoopLaunchResult(Environment.ProcessId, request.StdoutPath, request.StderrPath);
                        });

                    Assert.True(result.Started);
                    Assert.Equal(1, attempts);
                    Assert.Contains("processAlive=true", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("loopStartJournaled=true", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("terminalReason=loop-start", result.VerificationOutcome, StringComparison.Ordinal);
                });
            });

            Assert.Contains("LOOP_HANDOFF_PENDING", outText, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_HANDOFF_FAILED", outText, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_HANDOFF_FAILED", errorText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_slow_boot_successor_succeeds_without_retry")]
    public void ConductorLoopHandoffSlowBootSuccessorSucceedsWithoutRetry()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-delayed-handoff");
        try
        {
            var oldFixedStartupWindow = TimeSpan.FromSeconds(10);
            var loopStartDelay = TimeSpan.FromMilliseconds(10250);
            var stopwatch = Stopwatch.StartNew();
            var attempts = 0;
            var result = ConductorLoopHandoff.TryStartSuccessor(
                HandoffOptions(
                    root,
                    verificationTimeout: TimeSpan.FromSeconds(12),
                    loopStartProbe: (_, _) => stopwatch.Elapsed >= loopStartDelay),
                new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                request =>
                {
                    attempts++;
                    File.WriteAllText(request.StdoutPath, "successor booting");
                    return new ConductLoopLaunchResult(Environment.ProcessId, request.StdoutPath, request.StderrPath);
                });

            Assert.True(result.Started);
            Assert.Equal(1, attempts);
            Assert.Equal(Environment.ProcessId, result.ProcessId);
            Assert.True(stopwatch.Elapsed > oldFixedStartupWindow);
            Assert.Contains("loopStartJournaled=true", result.VerificationOutcome, StringComparison.Ordinal);
            Assert.Contains("terminalReason=loop-start", result.VerificationOutcome, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_dead_successor_fails_with_own_evidence")]
    public void ConductorLoopHandoffDeadSuccessorFailsWithOwnEvidence()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-failed-handoff");
        try
        {
            string outText = "";
            var errorText = CaptureConsoleError(() =>
            {
                outText = CaptureConsole(() =>
                {
                    var attempts = 0;
                    var result = ConductorLoopHandoff.TryStartSuccessor(
                        HandoffOptions(root),
                        new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                        request =>
                        {
                            attempts++;
                            return new ConductLoopLaunchResult(int.MaxValue, request.StdoutPath, request.StderrPath);
                        });

                    Assert.False(result.Started);
                    Assert.True(result.Failed);
                    Assert.Equal(1, attempts);
                    Assert.Equal("successor-child-dead", result.Reason);
                    Assert.Contains("attempt=1", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains($"pid={int.MaxValue}", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("guard=incumbent-held-until-successor-ready", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("spawnPath=injected", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("loopStartJournaled=false", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("terminalReason=child-dead", result.VerificationOutcome, StringComparison.Ordinal);
                });
            });

            Assert.Contains("LOOP_HANDOFF_FAILED", outText, StringComparison.Ordinal);
            Assert.Contains("LOOP_HANDOFF_FAILED", errorText, StringComparison.Ordinal);

            var records = new SqliteRunEventStore(Path.Combine(root, ".orchestrator", "run-events.db"))
                .ReadSinceAsync()
                .GetAwaiter()
                .GetResult();
            Assert.Equal(1, records.Count(record => record.Operation == "LOOP_HANDOFF" && record.Status == "Failed"));
            Assert.Contains(records, record =>
                record.Operation == "LOOP_HANDOFF" &&
                record.Status == "Failed" &&
                record.Detail is not null &&
                record.Detail.Contains($"pid={int.MaxValue}", StringComparison.Ordinal) &&
                record.Detail.Contains("terminalReason=child-dead", StringComparison.Ordinal));
            Assert.Contains(records, record =>
                record.Operation == "LOOP_HANDOFF" &&
                record.Status == "Escalated" &&
                record.Detail is not null &&
                record.Detail.Contains("successor-child-dead", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_handoff_failure_emits_failed_event_not_skipped")]
    public void BatchLoopHandoffFailureEmitsFailedEventNotSkipped()
    {
        var (kernel, _) = SimpleGoal();
        var root = CreateTempDirectory("mcg-conduct-loop-failed-event");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        try
        {
            var summary = new ConductorBatchLoop(
                handoffOnMaxDuration: _ => ConductorLoopHandoffResult.FailedStart(
                    "successor-verification-failed",
                    Path.Combine(root, "successor.out.log"),
                    Path.Combine(root, "successor.err.log"),
                    "processAlive=false stdoutLogExists=false loopStartJournaled=false"),
                conductEventLogWriter: writer)
                .Run(
                    kernel,
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxDuration: TimeSpan.Zero);

            Assert.True(summary.Handoff?.Failed);
            var log = File.ReadAllText(logPath);
            Assert.Contains("LOOP_HANDOFF_FAILED", log, StringComparison.Ordinal);
            Assert.Contains("successor.out.log", log, StringComparison.Ordinal);
            Assert.Contains("loopStartJournaled=false", log, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_HANDOFF_SKIPPED", log, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_windows_launcher_uses_direct_process_with_explicit_log_handles")]
    public void ConductorLoopHandoffWindowsLauncherUsesDirectProcessWithExplicitLogHandles()
    {
        var commandLine = ConductorLoopHandoff.BuildWindowsProcessCommandLine(
            ["dotnet", @"C:\repo\src\Mcg.AgentOrchestrator.App.dll", "conduct", "--loop", "--watch"]);

        Assert.Contains(@"""dotnet"" ""C:\repo\src\Mcg.AgentOrchestrator.App.dll"" ""conduct"" ""--loop"" ""--watch""", commandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", commandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", commandLine, StringComparison.OrdinalIgnoreCase);

        var source = File.ReadAllText(Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "src", "Mcg.AgentOrchestrator.App", "Orchestration", "ConductorLoopHandoff.cs"));
        Assert.Contains("CreateBreakawayFromJob", source, StringComparison.Ordinal);
        Assert.Contains("CreateInheritedOutputFile", source, StringComparison.Ordinal);
        Assert.Contains("UseStdHandles", source, StringComparison.Ordinal);
        Assert.Contains("bInheritHandles: true", source, StringComparison.Ordinal);
        Assert.Contains("ProcThreadAttributeHandleList", source, StringComparison.Ordinal);
        Assert.Contains("ExtendedStartupInfoPresent", source, StringComparison.Ordinal);
        Assert.Equal(0x01080600u, ConductorLoopHandoff.WindowsSuccessorCreationFlags);
        Assert.DoesNotContain("WindowsCreationFlags.CreateNoWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsCreationFlags.DetachedProcess", source, StringComparison.Ordinal);

        var jobSource = File.ReadAllText(Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "src", "Mcg.AgentOrchestrator.Infrastructure", "Processes", "OwnedProcessGroup.cs"));
        Assert.Contains("JobObjectLimitBreakawayOk", jobSource, StringComparison.Ordinal);
    }

    [Xunit.Fact(
        DisplayName = "ConductorLoopHandoff_windows_launcher_inherits_redirected_stdout_handle",
        Skip = "Requires Windows process-job semantics.",
        SkipUnless = nameof(IsWindows))]
    public void ConductorLoopHandoffWindowsLauncherInheritsRedirectedStdoutHandle()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-stdout-handoff");
        int? processId = null;
        var suppressionScopeActive = false;
        try
        {
            var stdoutPath = Path.Combine(root, "successor.out.log");
            var stderrPath = Path.Combine(root, "successor.err.log");
            var stdoutMarker = "handoff-stdout-marker-" + Guid.NewGuid().ToString("N");
            var stderrMarker = "handoff-stderr-marker-" + Guid.NewGuid().ToString("N");
            var scriptPath = Path.Combine(root, "write-marker.cmd");
            File.WriteAllText(scriptPath, $"@echo {stdoutMarker}{Environment.NewLine}@echo {stderrMarker} 1>&2{Environment.NewLine}");
            var cmdPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
            var result = ConductorLoopHandoff.LaunchDetachedWindows(
                new ConductLoopLaunchRequest("batch1", [], stdoutPath, stderrPath, root, 0),
                [
                    cmdPath,
                    "/d",
                    "/c",
                    scriptPath
                ],
                acquireConsoleSuppression: () =>
                {
                    suppressionScopeActive = true;
                    return new ProcessTreeGuiSuppression.ConsoleSpawnScope(
                        hiddenConsoleAcquired: true,
                        onDispose: () => suppressionScopeActive = false);
                },
                beforeCreateProcess: () => Assert.True(
                    suppressionScopeActive,
                    "Hidden-console suppression was disposed before CreateProcessW."));

            Assert.True(result.ProcessId > 0);
            Assert.Contains("breakawaySucceeded=true", result.LaunchDetail, StringComparison.Ordinal);
            Assert.Matches("residualJobMembership=(true|false)", result.LaunchDetail);
            Assert.False(suppressionScopeActive);
            processId = result.ProcessId;
            var conductEventsPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
            Assert.True(File.Exists(conductEventsPath), "Windows handoff did not journal its pre-spawn diagnostic.");
            var conductEvents = File.ReadAllText(conductEventsPath);
            Assert.Contains("\"eventKind\":\"loop-handoff-spawn\"", conductEvents, StringComparison.Ordinal);
            Assert.Contains("spawnPath=windows-createprocess", conductEvents, StringComparison.Ordinal);
            Assert.Matches("incumbentConsole=(present|absent)", conductEvents);
            Assert.Matches("incumbentConsoleAttached=(true|false)", conductEvents);
            Assert.Contains("suppression=hidden-console-acquired", conductEvents, StringComparison.Ordinal);
            Assert.True(WaitUntil(
                () => File.Exists(stdoutPath) && ReadAllTextShared(stdoutPath).Contains(stdoutMarker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)),
                $"stdout did not contain marker. child={DescribeProcess(processId.Value)} stdout={TryReadAllTextShared(stdoutPath)} stderr={TryReadAllTextShared(stderrPath)}");
            Assert.True(WaitUntil(
                () => File.Exists(stderrPath) && ReadAllTextShared(stderrPath).Contains(stderrMarker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)),
                $"stderr did not contain marker. child={DescribeProcess(processId.Value)} stdout={TryReadAllTextShared(stdoutPath)} stderr={TryReadAllTextShared(stderrPath)}");
        }
        finally
        {
            if (processId is { } pid)
            {
                TryKillProcess(pid);
            }

            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(
        Skip = "Requires Windows process-job semantics.",
        SkipUnless = nameof(IsWindows))]
    public void ConductorLoopHandoffSuppressionFailureStillStartsSuccessor()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-suppression-failure");
        int? processId = null;
        try
        {
            var stdoutPath = Path.Combine(root, "successor.out.log");
            var stderrPath = Path.Combine(root, "successor.err.log");
            var marker = "handoff-fail-open-marker-" + Guid.NewGuid().ToString("N");
            var scriptPath = Path.Combine(root, "write-marker.cmd");
            File.WriteAllText(scriptPath, $"@echo {marker}{Environment.NewLine}");
            var cmdPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");

            var result = ConductorLoopHandoff.LaunchDetachedWindows(
                new ConductLoopLaunchRequest("batch1", [], stdoutPath, stderrPath, root, 0),
                [cmdPath, "/d", "/c", scriptPath],
                acquireConsoleSuppression: () => throw new System.ComponentModel.Win32Exception(5, "synthetic suppression failure"));

            Assert.True(result.ProcessId > 0);
            Assert.Contains("breakawaySucceeded=true", result.LaunchDetail, StringComparison.Ordinal);
            Assert.Matches("residualJobMembership=(true|false)", result.LaunchDetail);
            processId = result.ProcessId;
            Assert.True(WaitUntil(
                () => File.Exists(stdoutPath) && ReadAllTextShared(stdoutPath).Contains(marker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)),
                $"successor did not start after suppression failure. stdout={TryReadAllTextShared(stdoutPath)} stderr={TryReadAllTextShared(stderrPath)}");

            var conductEvents = File.ReadAllText(Path.Combine(root, ConductEventLogWriter.CurrentFileName));
            Assert.Contains("\"eventKind\":\"loop-handoff-console-suppression-failed\"", conductEvents, StringComparison.Ordinal);
            Assert.Contains("error=Win32Exception", conductEvents, StringComparison.Ordinal);
            Assert.Contains("nativeError=5", conductEvents, StringComparison.Ordinal);
            Assert.Contains("suppression=acquisition-failed", conductEvents, StringComparison.Ordinal);
        }
        finally
        {
            if (processId is { } pid)
            {
                TryKillProcess(pid);
            }

            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(
        DisplayName = "ConductorLoopHandoff_successor_survives_parent_job_exit_and_emits_loop_start",
        Skip = "Requires Windows process-job semantics.",
        SkipUnless = nameof(IsWindows))]
    public void ConductorLoopHandoffSuccessorSurvivesParentJobExitAndEmitsLoopStart()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-runtime-handoff");
        var appAssembly = typeof(ConductorBatchLoop).Assembly.Location;
        Process? parent = null;
        int? successorPid = null;
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = root
            };
            startInfo.ArgumentList.Add(appAssembly);
            startInfo.ArgumentList.Add("conduct");
            startInfo.ArgumentList.Add("--loop");
            startInfo.ArgumentList.Add("--daemon");
            startInfo.ArgumentList.Add("--watch");
            startInfo.ArgumentList.Add("--poll-seconds");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("--max-duration");
            startInfo.ArgumentList.Add("3");
            startInfo.ArgumentList.Add("--quiet");
            startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = root;
            startInfo.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = Path.Combine(root, "project-registry");
            startInfo.Environment["MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME"] = "batch98";
            foreach (var key in startInfo.Environment.Keys
                         .Where(key => key.StartsWith("MCG_ORCHESTRATOR_HANDOFF_", StringComparison.Ordinal))
                         .ToArray())
            {
                // The acceptance worker may itself be a conductor successor. This child is the
                // incumbent under test and must not inherit that outer authority-transfer request.
                startInfo.Environment.Remove(key);
            }

            var bootstrapToken = Guid.NewGuid().ToString("N");
            var bootstrapActivationPath = Path.Combine(root, "bootstrap.activate");
            File.WriteAllText(bootstrapActivationPath, bootstrapToken);
            startInfo.Environment["MCG_ORCHESTRATOR_HANDOFF_READY_PATH"] = Path.Combine(root, "bootstrap.ready");
            startInfo.Environment["MCG_ORCHESTRATOR_HANDOFF_ACTIVATE_PATH"] = bootstrapActivationPath;
            startInfo.Environment["MCG_ORCHESTRATOR_HANDOFF_TOKEN"] = bootstrapToken;
            startInfo.Environment["MCG_ORCHESTRATOR_HANDOFF_INCUMBENT_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["MCG_ORCHESTRATOR_HANDOFF_WAIT_SECONDS"] = "5";

            parent = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start parent conductor process.");
            var stdoutTask = parent.StandardOutput.ReadToEndAsync();
            var stderrTask = parent.StandardError.ReadToEndAsync();
            using (var parentJob = OwnedProcessGroup.Attach(parent))
            {
                Assert.True(parent.WaitForExit(30000), "Parent conductor did not reach max-duration handoff.");
                parentJob.Dispose();
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            Assert.True(parent.ExitCode == 0, $"Parent conductor exited {parent.ExitCode}. stdout={stdout} stderr={stderr}");
            successorPid = ParseHandoffProcessId(stdout);

            Assert.True(IsProcessRunning(successorPid.Value), $"Successor pid {successorPid.Value} did not survive parent job close. stdout={stdout} stderr={stderr}");
            Assert.Contains("guard=incumbent-held-until-successor-ready", stdout, StringComparison.Ordinal);
            Assert.Contains("spawnPath=windows-createprocess", stdout, StringComparison.Ordinal);
            Assert.Contains("breakawayRequested=true", stdout, StringComparison.Ordinal);
            Assert.Contains("breakawaySucceeded=true", stdout, StringComparison.Ordinal);
            Assert.Matches("residualJobMembership=(true|false)", stdout);

            var logDirectory = Path.Combine(root, ".orchestrator", "logs");
            var conductEventsPath = Path.Combine(logDirectory, ConductEventLogWriter.CurrentFileName);
            Assert.True(WaitUntil(() =>
                File.Exists(conductEventsPath) &&
                ReadAllTextShared(conductEventsPath).Contains("LOOP_START", StringComparison.Ordinal),
                TimeSpan.FromSeconds(15)), $"Successor did not journal LOOP_START. stdout={stdout} stderr={stderr}");

            Assert.True(WaitUntil(
                () => Directory.GetFiles(logDirectory, "operator-batch99-*.out.log").Length > 0,
                TimeSpan.FromSeconds(10)),
                $"Successor did not create batch99 stdout log. stdout={stdout} stderr={stderr}");
        }
        finally
        {
            File.WriteAllText(Path.Combine(root, ConductorBatchLoop.StopFileName), "stop");
            if (successorPid is { } pid && !WaitUntil(() => !IsProcessRunning(pid), TimeSpan.FromSeconds(10)))
            {
                TryKillProcess(pid);
            }

            if (parent is not null)
            {
                TryKillProcess(parent.Id);
                parent.Dispose();
            }

            TryDeleteDirectory(root);
        }
    }

    private static int ParseHandoffProcessId(string output)
    {
        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("LOOP_HANDOFF tick=", StringComparison.Ordinal) ||
                !TryReadTokenValue(line, "pid=", out var pidText) ||
                !int.TryParse(pidText, out var pid))
            {
                continue;
            }

            return pid;
        }

        throw new InvalidOperationException("Parent conductor output did not include a LOOP_HANDOFF pid.");
    }

    private static bool TryReadTokenValue(string line, string token, out string value)
    {
        var start = line.IndexOf(token, StringComparison.Ordinal);
        if (start < 0)
        {
            value = string.Empty;
            return false;
        }

        start += token.Length;
        var end = line.IndexOf(' ', start);
        value = end < 0 ? line[start..] : line[start..end];
        return value.Length > 0;
    }

    private static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return predicate();
    }

    private static void TryKillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
