using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsSetAsideReadmit : ConductorBatchLoopTests
{
    [Fact]
    public void PlannerConflict_EscalatesAtOccupiedWidthAndUsesExistingSetAsideRecheck()
    {
        var harness = new PlannerConflictHarness();
        var first = CreateVerifiedSimpleGoal(harness.Kernel, "In-flight first member");
        var second = CreateVerifiedSimpleGoal(harness.Kernel, "In-flight second member");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(harness.Driver.TryRegisterCohortGateRunForTests(
            new ConductorAcceptanceCohortSelection([PlannerReady(first.Id), PlannerReady(second.Id)], []), completion));
        Assert.Equal(1, harness.Driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount);
        using var occupancy = PlannerOccupiedWidth();

        var summary = harness.Run(2);

        var write = Assert.Single(harness.Writes);
        Assert.Equal(harness.Goal.Id, write.GoalId);
        Assert.Equal(GoalLifecycleState.Verified, write.State);
        Assert.StartsWith("pre-landing rebase conflict", write.Reason);
        Assert.Contains("src/B.cs, src/a.cs", write.Reason, StringComparison.Ordinal);
        Assert.Equal(1, summary.Escalated);
        Assert.True(harness.Rechecks > 0); // Only the PreLandingRebaseConflict set-aside invokes this seam.
        Assert.Equal(1, harness.Probes);
        Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(harness.Goal, harness.Driver.GetFacts(harness.Goal)));
    }

    [Fact]
    public void PlannerConflict_ConsecutiveTicksWriteOnceAndCleanRecheckReadmits()
    {
        var harness = new PlannerConflictHarness();
        harness.Recheck = () =>
        {
            if (harness.Rechecks >= 2) harness.Conflicts = false;
            return !harness.Conflicts;
        };
        using var occupancy = PlannerOccupiedWidth();

        harness.Run(3);

        Assert.Single(harness.Writes);
        Assert.Equal(1, harness.Probes);
        Assert.Equal(2, harness.Rechecks); // The set-aside stops rechecking after the clean observation.
        Assert.True(harness.CleanMergeReads > 0); // Admission projects the goal again after self-clear.
        Assert.Contains(harness.Goal.Timeline, item => item.Kind == ProgressKind.GoalPolicyDecision &&
            item.Message.StartsWith("Landing escalation self-cleared", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 2)]
    public void PlannerConflict_SelfClearDeduplicatesSamePairButAllowsChangedRevision(bool changeMain, bool changeBranch, int expectedWrites)
    {
        var harness = new PlannerConflictHarness();
        harness.Recheck = () =>
        {
            if (changeMain) harness.MainRevision = new string('c', 40);
            if (changeBranch) harness.BranchRevision = new string('d', 40);
            return true;
        };
        using var occupancy = PlannerOccupiedWidth();

        harness.Run(2);

        Assert.Equal(expectedWrites, harness.Writes.Count);
        Assert.True(harness.Rechecks > 0);
        Assert.Contains(harness.Goal.Timeline, item => item.Message.StartsWith("Landing escalation self-cleared", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("resolvable")]
    [InlineData("indeterminate")]
    [InlineData("stale")]
    [InlineData("probe-indeterminate")]
    [InlineData("probe-throws")]
    public void PlannerConflict_NegativeControlsDoNotWriteOrSetAside(string control)
    {
        var harness = new PlannerConflictHarness(control);
        using var occupancy = PlannerOccupiedWidth();

        harness.Run(2);

        Assert.Empty(harness.Writes);
        Assert.Equal(0, harness.Rechecks);
        Assert.Equal(control is "resolvable" or "probe-indeterminate" or "probe-throws" ? 2 : 0, harness.Probes);
    }

    [Fact]
    public void PlannerConflict_InFlightMemberNeverProbesOrEscalates()
    {
        var harness = new PlannerConflictHarness();
        var other = CreateVerifiedSimpleGoal(harness.Kernel, "Other in-flight member");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(harness.Driver.TryRegisterCohortGateRunForTests(
            new ConductorAcceptanceCohortSelection([PlannerReady(harness.Goal.Id), PlannerReady(other.Id)], []), completion));
        using var occupancy = PlannerOccupiedWidth();

        harness.Run(1);

        Assert.True(harness.MergeReads > 0); // A real MergeConflict exclusion was projected for the member.
        Assert.Empty(harness.Writes);
        Assert.Equal(0, harness.Probes);
        Assert.Equal(0, harness.Rechecks);
    }

    [Fact]
    public void PlannerConflict_NonVerifiedGoalAndOtherExclusionsNeverProbe()
    {
        var harness = new PlannerConflictHarness();
        var unverified = GoalLifecycleCommands.CreateAndActivateSimpleGoal(harness.Kernel, DefaultAgents(), "Unverified");
        var conflict = new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.MergeConflict)
            { ConflictPaths = ["src/B.cs"] };

        Assert.Null(harness.Driver.TryEscalatePreLandingMergeConflict(unverified,
            ConductorAutonomyPolicy.Conservative, conflict, _ => false, out _));
        foreach (var reason in Enum.GetValues<GateReadyCandidateExclusionReason>().Where(reason => reason != GateReadyCandidateExclusionReason.MergeConflict))
            Assert.Null(harness.Driver.TryEscalatePreLandingMergeConflict(harness.Goal,
                ConductorAutonomyPolicy.Conservative, new(reason) { ConflictPaths = ["src/B.cs"] }, _ => false, out _));

        Assert.Empty(harness.Writes);
        Assert.Equal(0, harness.Probes);
    }

    [Fact]
    public void PlannerConflict_WriteFailureIsRetriedWithoutRecordingDedupOrSetAside()
    {
        var harness = new PlannerConflictHarness("write-throws");
        using var occupancy = PlannerOccupiedWidth();

        harness.Run(2);

        Assert.Equal(2, harness.WriteAttempts);
        Assert.Empty(harness.Writes);
        Assert.Equal(0, harness.Rechecks);
        Assert.Equal(2, harness.Probes);
    }

    private static IDisposable PlannerOccupiedWidth() => GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
        [new GateLoadContextProbe.LiveGateOccupant(4101, "cccccccccccccccccccccccccccccccc", 0, TimeSpan.Zero)]);

    private static GateReadyCandidateProjection PlannerReady(GoalId id) =>
        new(id, GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied, ChangeRiskTier.DocsOnly,
            ConductorTransitionDecision.Auto, [$"src/{id.Value}.cs"], [$"production:{id.Value}"],
            new(new string('b', 40), new string('a', 40), GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected));

    private sealed class PlannerConflictHarness
    {
        internal readonly AgentOrchestratorKernel Kernel = new();
        internal readonly Goal Goal;
        internal readonly ConductorDriver Driver;
        internal readonly List<(GoalId GoalId, GoalLifecycleState State, string Reason)> Writes = [];
        internal string MainRevision = new('a', 40);
        internal string BranchRevision = new('b', 40);
        internal bool Conflicts = true;
        internal int Probes, Rechecks, MergeReads, CleanMergeReads, WriteAttempts;
        internal Func<bool> Recheck = () => false;

        internal PlannerConflictHarness(string control = "conflict")
        {
            Goal = CreateVerifiedSimpleGoal(Kernel, "Planner conflict candidate");
            var revisions = 0;
            var projector = new GateReadyCandidateProjector(
                _ => new(BranchRevision, control == "stale" && ++revisions % 2 == 0 ? new string('e', 40) : MainRevision),
                id => new(true, [$"src/Mcg.AgentOrchestrator.Core/{id.Value}.cs"]),
                (_, _, _) =>
                {
                    MergeReads++;
                    if (control == "indeterminate") throw new InvalidOperationException("merge-tree unavailable");
                    var clean = control is "clean" or "stale" || !Conflicts;
                    if (clean) CleanMergeReads++;
                    return new(clean, ["src/B.cs", "src/a.cs"]);
                });
            Driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                isVerificationGateSatisfied: _ => true,
                getLandingFileScopes: goal => [$"src/Mcg.AgentOrchestrator.Core/{goal.Id.Value}.cs"],
                gateReadyCandidateProjector: projector,
                writeEscalation: (goal, state, reason) =>
                {
                    WriteAttempts++;
                    if (control == "write-throws") throw new IOException("write failed");
                    Writes.Add((goal.Id, state, reason));
                },
                recheckPreLandingRebaseConflict: _ =>
                {
                    Rechecks++;
                    var clean = Recheck();
                    return new(clean, clean ? "MergeTreeClean" : "MergeTreeConflict", "test observation",
                        $"branch={BranchRevision};main={MainRevision}");
                });
            Assert.True(Driver.ParallelAcceptanceEnabled);
            Driver.PreLandingMergeConflictProbe = _ =>
            {
                Probes++;
                if (control == "probe-throws") throw new IOException("probe failed");
                return new(BranchRevision, MainRevision, control switch
                {
                    "resolvable" => AdditiveConflictProbeOutcome.Resolvable,
                    "probe-indeterminate" => AdditiveConflictProbeOutcome.Indeterminate,
                    _ => AdditiveConflictProbeOutcome.Unresolvable
                }, "test probe");
            };
        }

        internal BatchLoopSummary Run(int ticks) => new ConductorBatchLoop().Run(
            Kernel, Driver, ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 }, NoStopPath(),
            maxIterations: ticks, watchInterval: TimeSpan.FromMilliseconds(1), sleepFunc: _ => false,
            keepAliveWhenIdle: true);
    }

    public ConductorBatchLoopTestsSetAsideReadmit(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact(DisplayName = "BatchLoop_readmits_awaiting_clarification_goal_when_blocker_clears")]
    public void BatchLoopReadmitsAwaitingClarificationGoalWhenBlockerClears()
    {
        var (kernel, goal) = SimpleGoal();
        var hasOpenClarification = true;
        var workspaceCreates = 0;
        var escalations = 0;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(HasOpenClarification: hasOpenClarification),
            createWorkspace: _ =>
            {
                workspaceCreates++;
                return "C:\\goal";
            },
            writeEscalation: (_, _, _) => escalations++);

        BatchLoopSummary? summary = null;
        var output = AsyncLocalConsoleRouter.Capture(() =>
            summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 2,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ =>
                {
                    hasOpenClarification = false;
                    return false;
                }));

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, escalations);
        Assert.Equal(1, workspaceCreates);
        Assert.Equal(1, CountOccurrences(output, "BLOCKED_RECHECK_INTERVAL_CLAMPED"));
        Assert.Contains(
            $"BLOCKED_RECHECK_INTERVAL_CLAMPED goal={goal.Id.Value[..8]} condition=awaitingclarification computedSeconds=0.001 floorSeconds={ConductorBatchLoop.WatchStopPollIntervalSeconds}",
            output,
            StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_readmits_store_answered_clarification_without_restart_and_dispatches")]
    public async Task BatchLoopReadmitsStoreAnsweredClarificationWithoutRestartAndDispatches()
    {
        var root = CreateTempDirectory("mcg-clarification-readmit");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var (kernel, goal) = SimpleGoal("Clarified goal");
        var questionKey = $"{GoalRefinementService.CorrelationKeyPrefix}{goal.Id.Value}:scope:test";
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Do the clarified thing.",
            ["Dispatch starts after clarification."],
            VerificationClass.TestVerifiable,
            [],
            [new RefinedSpecOpenQuestion(questionKey, "Which scope?", "scope", "Open")]));
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Which scope?",
            "body",
            questionKey);
        var workspaceCreated = false;
        var dispatches = 0;
        var resolved = false;
        var task = goal.Tasks.Single();

        var driver = MakeDriver(
            getFacts: g => new GoalLifecycleFacts(
                WorkspaceExists: workspaceCreated,
                HasOpenClarification: GoalRefinementGate.HasOpenClarification(workspace, g)),
            createWorkspace: _ =>
            {
                workspaceCreated = true;
                return "C:\\goal";
            },
            dispatchAndStart: g =>
            {
                dispatches++;
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\goal", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                if (!resolved)
                {
                    store.TryResolveAsync(questionKey, "Use the narrow scope.").GetAwaiter().GetResult();
                    resolved = true;
                }

                return false;
            });

        Assert.Equal(3, summary.Ticks);
        Assert.True(workspaceCreated);
        Assert.Equal(1, dispatches);
        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("re-admitted escalated goal after state changed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_readmits_escalated_goal_when_task_state_changes_mid_run")]
    public void BatchLoopReadmitsEscalatedGoalWhenTaskStateChangesMidRun()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Needs operator repair.");
        var repaired = false;
        var escalations = 0;
        var workspaceCreates = 0;

        var driver = MakeDriver(
            createWorkspace: _ =>
            {
                workspaceCreates++;
                return "C:\\goal";
            },
            writeEscalation: (_, _, _) => escalations++);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                if (!repaired)
                {
                    kernel.RetryTask(goal.Id, task.Id, "Operator repaired failed task.");
                    repaired = true;
                }

                return false;
            });

        Assert.Equal(2, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, escalations);
        Assert.Equal(1, workspaceCreates);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("re-admitted escalated goal after state changed", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void BatchLoopReadmitsLifecycleEscalationWhenSweepBlockerClears()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Readmit cleared sweep blocker");
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");
        var blocker = new TerminalGoalSweepBlocker(
            "terminal-dirty-worktree",
            "untracked acceptance debris remains",
            $"remove debris for {goal.Id.Value[..8]}");
        var sweepCalls = 0;
        var landAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptance: _ => true,
            land: g =>
            {
                landAttempts++;
                return new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            });

        var summary = new ConductorBatchLoop(
            measuredSweep: _ => ++sweepCalls == 1
                ? new TerminalGoalSweepResult(
                    [new TerminalGoalSweepGoalResult(goal.Id, goal.Id.Value[..8], [], [blocker])],
                    SweptGoalIds: [goal.Id])
                : new TerminalGoalSweepResult([], SweptGoalIds: [goal.Id])).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(2, sweepCalls);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, landAttempts);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith("Set-aside self-cleared:", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void BatchLoopKeepsLifecycleEscalationWhileSweepBlockerPersists()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Keep persistent sweep blocker");
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");
        var blocker = new TerminalGoalSweepBlocker(
            "terminal-dirty-worktree",
            "untracked acceptance debris remains",
            $"remove debris for {goal.Id.Value[..8]}");
        var actionableBlocker = new TerminalGoalSweepBlocker(
            "completed-branch-unmerged",
            "verified branch remains unmerged",
            TerminalGoalRemedy.Acceptance(goal.Id, goal.Id.Value[..8], gateArtifact: null));
        var sweepCalls = 0;
        var landAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            land: _ =>
            {
                landAttempts++;
                throw new InvalidOperationException("A persistently blocked goal must not reach landing.");
            });

        var summary = new ConductorBatchLoop(
            measuredSweep: _ =>
            {
                sweepCalls++;
                return new TerminalGoalSweepResult(
                    [new TerminalGoalSweepGoalResult(goal.Id, goal.Id.Value[..8], [], [actionableBlocker, blocker])],
                    SweptGoalIds: [goal.Id]);
            }).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(2, sweepCalls);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(0, landAttempts);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith("Set-aside self-cleared:", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void BatchLoopReadmitsLifecycleEscalationWhenSweepBlockerHasSafeProgressRemedy()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Readmit actionable sweep blocker");
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");
        var blocker = new TerminalGoalSweepBlocker(
            "completed-branch-unmerged",
            "verified branch remains unmerged",
            TerminalGoalRemedy.Acceptance(goal.Id, goal.Id.Value[..8], gateArtifact: null));
        var sweepCalls = 0;
        var acceptanceAttempts = 0;
        var landAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptance: _ =>
            {
                acceptanceAttempts++;
                return true;
            },
            land: g =>
            {
                landAttempts++;
                return new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            });

        var summary = new ConductorBatchLoop(
            measuredSweep: _ =>
            {
                sweepCalls++;
                return new TerminalGoalSweepResult(
                    [new TerminalGoalSweepGoalResult(goal.Id, goal.Id.Value[..8], [], [blocker])],
                    SweptGoalIds: [goal.Id]);
            }).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(2, sweepCalls);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, acceptanceAttempts);
        Assert.Equal(1, landAttempts);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith("Set-aside self-cleared:", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void BatchLoopDoesNotReadmitWhenSweepDidNotEvaluateGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Hold when sweep skips goal");
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");
        var blocker = new TerminalGoalSweepBlocker(
            "completed-branch-unmerged",
            "verified branch remains unmerged",
            $"acceptance {goal.Id.Value[..8]}");
        var eventLogPath = Path.Combine(CreateTempDirectory("mcg-skipped-sweep-heartbeat"), "conduct-events.log");
        var now = DateTimeOffset.Parse("2026-08-23T00:00:00Z");
        var sweepCalls = 0;
        var landAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            land: _ =>
            {
                landAttempts++;
                throw new InvalidOperationException("An unevaluated goal must not reach landing.");
            });

        _ = new ConductorBatchLoop(
            measuredSweep: _ => ++sweepCalls == 1
                ? new TerminalGoalSweepResult(
                    [new TerminalGoalSweepGoalResult(goal.Id, goal.Id.Value[..8], [], [blocker])],
                    SweptGoalIds: [goal.Id])
                : new TerminalGoalSweepResult([], SweptGoalIds: []),
            conductEventLogWriter: new ConductEventLogWriter(eventLogPath),
            utcNow: () => now,
            blockedRecheckHeartbeatInterval: TimeSpan.FromSeconds(1)).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: interval =>
            {
                now = now.Add(interval);
                return false;
            });

        Assert.Equal(2, sweepCalls);
        Assert.Equal(0, landAttempts);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith("Set-aside self-cleared:", StringComparison.Ordinal));
        var records = File.ReadAllLines(eventLogPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Assert.Contains(records, record =>
            record.EventKind == "blocked-recheck-heartbeat" &&
            record.Detail.Contains("rechecks=2", StringComparison.Ordinal) &&
            record.Detail.Contains("completed-branch-unmerged(recurrences=1)", StringComparison.Ordinal) &&
            record.Detail.Contains("evidence=verified_branch_remains_unmerged", StringComparison.Ordinal) &&
            record.Detail.Contains($"clears=acceptance_{goal.Id.Value[..8]}", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void BatchLoopSelfClearsSameSweepBlockerOnlyOnce()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Bound repeated sweep blocker clear");
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");
        var blocker = new TerminalGoalSweepBlocker(
            "completed-branch-unmerged",
            "verified branch remains unmerged",
            TerminalGoalRemedy.Acceptance(goal.Id, goal.Id.Value[..8], gateArtifact: null));
        var sweepCalls = 0;
        var acceptanceAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptance: _ =>
            {
                acceptanceAttempts++;
                return false;
            });

        _ = new ConductorBatchLoop(
            measuredSweep: _ =>
            {
                sweepCalls++;
                return new TerminalGoalSweepResult(
                    [new TerminalGoalSweepGoalResult(goal.Id, goal.Id.Value[..8], [], [blocker])],
                    SweptGoalIds: [goal.Id]);
            }).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 4,
            maxVerifyRetries: 0,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(4, sweepCalls);
        Assert.Equal(1, acceptanceAttempts);
        Assert.Single(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith("Set-aside self-cleared:", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void BatchLoopResolvedRebaseConflictReadmitsGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Resolve landing conflict");
        var rebaseChecks = 0;
        var conflictChecks = 0;
        var landAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            rebaseOntoMain: _ => ++rebaseChecks == 1
                ? new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.Conflict,
                    "goal/test",
                    "Conflict remains.",
                    ["docs/test-design-discipline.md"],
                    "workspace rebase")
                : new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                    "goal/test",
                    "Branch can fast-forward into main.",
                    [],
                    null),
            recheckPreLandingRebaseConflict: _ =>
            {
                conflictChecks++;
                return new LandingEscalationRecheckResult(
                    ConditionResolved: true,
                    Status: "MergeTreeClean",
                    Observation: "Read-only merge-tree check found no conflict with main.",
                    EvidenceFingerprint: "branch=resolved;main=current");
            },
            runAcceptance: _ => true,
            land: g =>
            {
                landAttempts++;
                return new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(2, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(0, summary.Done);
        Assert.Equal(2, rebaseChecks);
        Assert.Equal(1, conflictChecks);
        Assert.Equal(1, landAttempts);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Landing escalation self-cleared", StringComparison.Ordinal) &&
            evt.Message.Contains("status=MergeTreeClean", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void BatchLoopPersistingRebaseConflictStaysSetAside()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Keep landing conflict escalated");
        var rebaseChecks = 0;
        var conflictChecks = 0;
        var landAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            rebaseOntoMain: _ =>
            {
                rebaseChecks++;
                return new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.Conflict,
                    "goal/test",
                    "Conflict remains.",
                    ["docs/test-design-discipline.md"],
                    "workspace rebase");
            },
            recheckPreLandingRebaseConflict: _ =>
            {
                conflictChecks++;
                return new LandingEscalationRecheckResult(
                    ConditionResolved: false,
                    Status: "MergeTreeConflict",
                    Observation: "Read-only merge-tree check still conflicts with main.",
                    EvidenceFingerprint: "branch=conflicted;main=current");
            },
            runAcceptance: _ => true,
            land: g =>
            {
                landAttempts++;
                return new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(1, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, rebaseChecks);
        Assert.Equal(1, conflictChecks);
        Assert.Equal(0, landAttempts);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Landing escalation self-cleared", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void BatchLoopWatchRechecksBlockedGoalUntilMaxDuration()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Keep watching landing conflict");
        var now = DateTimeOffset.Parse("2026-08-04T00:00:00Z");
        var rebaseChecks = 0;
        var conflictChecks = 0;
        var sleeps = 0;
        var observedSleep = TimeSpan.Zero;
        var eventLogPath = Path.Combine(CreateTempDirectory("mcg-blocked-recheck-heartbeat"), "conduct-events.log");
        var blocker = new TerminalGoalSweepBlocker(
            "completed-branch-unmerged",
            "verified branch remains unmerged",
            $"acceptance {goal.Id.Value[..8]}");
        var sweepResult = new TerminalGoalSweepResult([
            new TerminalGoalSweepGoalResult(goal.Id, goal.Id.Value[..8], [], [blocker])
        ]);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            rebaseOntoMain: _ =>
            {
                rebaseChecks++;
                return new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.Conflict,
                    "goal/test",
                    "Conflict remains.",
                    ["docs/test-design-discipline.md"],
                    "workspace rebase");
            },
            recheckPreLandingRebaseConflict: _ =>
            {
                conflictChecks++;
                return new LandingEscalationRecheckResult(
                    ConditionResolved: false,
                    Status: "MergeTreeConflict",
                    Observation: "Read-only merge-tree check still conflicts with main.",
                    EvidenceFingerprint: "branch=conflicted;main=current");
            },
            runAcceptance: _ => true);

        BatchLoopSummary? summary = null;
        var output = AsyncLocalConsoleRouter.Capture(() =>
            summary = new ConductorBatchLoop(
                measuredSweep: _ => sweepResult,
                conductEventLogWriter: new ConductEventLogWriter(eventLogPath),
                utcNow: () => now,
                blockedRecheckHeartbeatInterval: TimeSpan.FromSeconds(1)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: interval =>
                {
                    sleeps++;
                    observedSleep = interval;
                    now = now.Add(interval);
                    return false;
                },
                maxDuration: TimeSpan.FromSeconds(11)));

        Assert.NotNull(summary);
        Assert.Equal("max-duration", summary!.StopReason);
        Assert.Equal(1, summary.Ticks);
        Assert.Equal(3, summary.Rechecks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, rebaseChecks);
        Assert.Equal(2, conflictChecks);
        Assert.Equal(3, sleeps);
        Assert.Equal(TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds), observedSleep);
        Assert.Contains("BLOCKED_RECHECK_INTERVAL_CLAMPED", output, StringComparison.Ordinal);
        Assert.Contains("computedSeconds=1", output, StringComparison.Ordinal);
        Assert.Contains("BLOCKED_RECHECK_SLEEP goals=1 seconds=5", output, StringComparison.Ordinal);
        var records = File.ReadAllLines(eventLogPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Assert.Contains(records, record =>
            record.EventKind == "blocked-recheck-heartbeat" &&
            record.Detail.Contains("rechecks=1", StringComparison.Ordinal) &&
            record.Detail.Contains($"{goal.Id.Value[..8]}:completed-branch-unmerged(recurrences=1)", StringComparison.Ordinal) &&
            record.Detail.Contains("evidence=verified_branch_remains_unmerged", StringComparison.Ordinal) &&
            record.Detail.Contains($"clears=acceptance_{goal.Id.Value[..8]}", StringComparison.Ordinal));
        Assert.Contains(records, record =>
            record.EventKind == "blocked-recheck-heartbeat" &&
            record.Detail.Contains("rechecks=3", StringComparison.Ordinal) &&
            record.Detail.Contains($"{goal.Id.Value[..8]}:completed-branch-unmerged(recurrences=3)", StringComparison.Ordinal));
        Assert.Contains(records, record =>
            record.EventKind == "loop-stop" &&
            record.Detail.Contains("rechecks=3", StringComparison.Ordinal));
        Assert.DoesNotContain(records, record => record.Detail.StartsWith("BLOCKED_RECHECK_SLEEP", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void BatchLoopOneShotEscalationRechecksBeforeStopping()
    {
        var kernel = new AgentOrchestratorKernel();
        _ = CreateVerifiedSimpleGoal(kernel, "Recheck one-shot landing conflict");
        var rebaseChecks = 0;
        var conflictChecks = 0;
        var sleeps = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            rebaseOntoMain: _ =>
            {
                rebaseChecks++;
                return new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.Conflict,
                    "goal/test",
                    "Conflict remains.",
                    ["docs/test-design-discipline.md"],
                    "workspace rebase");
            },
            recheckPreLandingRebaseConflict: _ =>
            {
                conflictChecks++;
                return new LandingEscalationRecheckResult(
                    ConditionResolved: false,
                    Status: "MergeTreeConflict",
                    Observation: "Read-only merge-tree check still conflicts with main.",
                    EvidenceFingerprint: "branch=conflicted;main=current");
            },
            runAcceptance: _ => true);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            sleepFunc: _ =>
            {
                sleeps++;
                Assert.True(sleeps <= 1, "One-shot blocked rechecks exceeded their deterministic sleep bound.");
                return false;
            });

        Assert.Equal("blocked-recheck-exhausted", summary.StopReason);
        Assert.Equal(1, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, rebaseChecks);
        Assert.Equal(2, conflictChecks);
        Assert.Equal(1, sleeps);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, 2, 0, 1)]
    [Xunit.InlineData(true, 3, 1, 2)]
    public void BatchLoopResolvedRebaseEvidenceRetriesOnlyWhenGitCandidateChanges(
        bool gitCandidateChanges,
        int expectedRebaseChecks,
        int expectedLandAttempts,
        int expectedSelfClears)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Bound mismatched landing conflict probe");
        var rebaseChecks = 0;
        var conflictChecks = 0;
        var landAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            rebaseOntoMain: _ =>
            {
                rebaseChecks++;
                if (gitCandidateChanges && rebaseChecks == 3)
                {
                    return new GoalWorktreeRebaseResult(
                        GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                        "goal/test",
                        "Changed candidate no longer conflicts.",
                        [],
                        null);
                }

                return new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.Conflict,
                    "goal/test",
                    "Commit replay still conflicts.",
                    ["docs/test-design-discipline.md"],
                    "workspace rebase");
            },
            recheckPreLandingRebaseConflict: _ =>
            {
                conflictChecks++;
                var candidate = gitCandidateChanges && conflictChecks == 2
                    ? "branch=changed;main=changed"
                    : "branch=unchanged;main=unchanged";
                return new LandingEscalationRecheckResult(
                    ConditionResolved: true,
                    Status: "MergeTreeClean",
                    Observation: "Tip trees merge cleanly despite the replay conflict.",
                    EvidenceFingerprint: candidate);
            },
            runAcceptance: _ => true,
            land: g =>
            {
                landAttempts++;
                return new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(gitCandidateChanges ? 3 : 2, summary.Ticks);
        Assert.Equal(2, summary.Escalated);
        Assert.Equal(expectedRebaseChecks, rebaseChecks);
        Assert.Equal(2, conflictChecks);
        Assert.Equal(expectedLandAttempts, landAttempts);
        Assert.Equal(expectedSelfClears, goal.Timeline.Count(evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Landing escalation self-cleared", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public void BatchLoopGitLaunchFailureTerminatesRecheckWithoutNewGoalState()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Keep failed recheck escalated");
        var rebaseChecks = 0;
        var conflictChecks = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            rebaseOntoMain: _ =>
            {
                rebaseChecks++;
                return new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.Conflict,
                    "goal/test",
                    "Conflict remains.",
                    ["docs/test-design-discipline.md"],
                    "workspace rebase");
            },
            recheckPreLandingRebaseConflict: _ =>
            {
                conflictChecks++;
                return new LandingEscalationRecheckResult(
                    ConditionResolved: false,
                    Status: "GitMergeTreeCouldNotStart",
                    Observation: "git merge-tree could not start",
                    EvidenceFingerprint: "launch-failed",
                    TerminalUnsatisfiable: true);
            },
            runAcceptance: _ => true);

        BatchLoopSummary? summary = null;
        var output = AsyncLocalConsoleRouter.Capture(() =>
            summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 2,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ => false));

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, rebaseChecks);
        Assert.Equal(1, conflictChecks);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("terminal-unsatisfiable", StringComparison.Ordinal));
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Landing escalation self-cleared", StringComparison.Ordinal));
        Assert.Contains("ESCALATION_RECHECK_UNSATISFIABLE", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ESCALATION_RECHECK_FAILED", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_keeps_unchanged_escalated_goal_set_aside_without_reescalating")]
    public void BatchLoopKeepsUnchangedEscalatedGoalSetAsideWithoutReescalating()
    {
        var kernel = new AgentOrchestratorKernel();
        var failedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "failed goal");
        var heldGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "held goal");
        var failedTask = failedGoal.Tasks.Single();
        kernel.ReportTaskProgress(failedGoal.Id, failedTask.Id, WorkTaskStatus.Failed, "Needs operator repair.");
        var escalations = 0;
        var heldAttempts = 0;
        var snapshotReloaded = false;
        GoalLifecycleState? staleLifecycleAfterRetry = null;
        var durableSnapshot = kernel.ExportGoalSnapshot(failedGoal.Id) with { Status = GoalStatus.Verifying };

        var driver = MakeDriver(
            getFacts: goal => goal.Id == heldGoal.Id
                ? new GoalLifecycleFacts(WorkspaceExists: true)
                : GoalLifecycleFacts.None,
            dispatchAndStart: goal =>
            {
                if (goal.Id == heldGoal.Id)
                {
                    heldAttempts++;
                    return DispatchStartOutcome.EmptyBatch("Held for operator approval.");
                }

                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, _) =>
            {
                escalations++;
                kernel.RetryTask(
                    failedGoal.Id,
                    failedTask.Id,
                    "Transient in-memory retry before durable reload.",
                    RetryCause.EnvironmentApparatusFailure);
                staleLifecycleAfterRetry = GoalLifecycle.ResolveState(failedGoal, GoalLifecycleFacts.None);
                kernel.ReplaceGoalWithSnapshot(durableSnapshot);
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                if (!snapshotReloaded)
                {
                    kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(failedGoal.Id));
                    snapshotReloaded = true;
                }
                return false;
            });

        Assert.Equal(3, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, escalations);
        Assert.Equal(3, heldAttempts);
        Assert.NotSame(failedGoal, kernel.GetGoal(failedGoal.Id));
        Assert.Equal(GoalStatus.Verifying, kernel.GetGoal(failedGoal.Id).Status);
        Assert.Equal(GoalLifecycleState.Created, staleLifecycleAfterRetry);
        Assert.Equal(
            GoalLifecycleState.Verifying,
            GoalLifecycle.ResolveState(kernel.GetGoal(failedGoal.Id), GoalLifecycleFacts.None));
        Assert.Equal(WorkTaskStatus.Failed, kernel.GetTask(failedGoal.Id, failedTask.Id).Status);
        Assert.DoesNotContain(kernel.GetGoal(failedGoal.Id).Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("re-admitted escalated goal", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void SetAsideFingerprint_EvidenceContextThrows_TickContinues()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "failed goal");
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Needs operator repair.");
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => throw new InvalidOperationException(
                "Set-aside fingerprints must not launch evidence discovery."));

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(1, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_readmits_snapshot_reloaded_goal_when_durable_task_fields_change")]
    public void BatchLoopReadmitsSnapshotReloadedGoalWhenDurableTaskFieldsChange()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "failed goal");
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Needs operator repair.");
        var snapshotChanged = false;
        var workspaceCreates = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            createWorkspace: _ =>
            {
                workspaceCreates++;
                return "C:\\goal";
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                if (!snapshotChanged)
                {
                    kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id));
                    kernel.RetryTask(goal.Id, task.Id, "Durable operator repair.", RetryCause.ContractClarification);
                    snapshotChanged = true;
                }
                return false;
            });

        Assert.Equal(2, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, workspaceCreates);
        Assert.Contains(kernel.GetGoal(goal.Id).Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("re-admitted escalated goal", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_sets_aside_ownership_hold_escalation_without_relanding")]
    public void BatchLoopSetsAsideOwnershipHoldEscalationWithoutRelanding()
    {
        var root = CreateTempDirectory("mcg-ownership-hold-set-aside");
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var (kernel, goal) = SimpleGoal("ownership held goal");
            var heldGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "ordinary held goal");
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            var landAttempts = 0;
            var heldAttempts = 0;

            var driver = MakeDriver(
                getFacts: g => g.Id == heldGoal.Id
                    ? new GoalLifecycleFacts(WorkspaceExists: true)
                    : GoalLifecycleFacts.None,
                getRunningCount: () =>
                {
                    heldAttempts++;
                    return ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers;
                },
                runAcceptance: _ => true,
                land: g =>
                {
                    landAttempts++;
                    OperatorInbox.RecordOwnershipHolds(
                        workspace,
                        g,
                        [
                            new OwnershipHoldRequest(
                                task.Id,
                                1,
                                task.RequiredRole,
                                ["src/Mcg.AgentOrchestrator.Infrastructure/Protected.cs"],
                                "test ownership hold")
                        ]);
                    return new LandingResult(
                        g.Id.Value,
                        g.Id.Value[..8],
                        new LandingDecision.Escalate("ownership-denylist hold: task touched protected path"),
                        "integration",
                        false,
                        "Held");
                });

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 3,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ => false);

            Assert.Equal(3, summary.Ticks);
            Assert.Equal(1, summary.Escalated);
            Assert.Equal(1, landAttempts);
            Assert.Equal(3, heldAttempts);
            var inbox = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
            Assert.Single(inbox.Items.Where(item => item.Kind == OperatorInboxKind.OwnershipHold));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_keeps_unresolved_clarification_set_aside_without_reescalating")]
    public void BatchLoopKeepsUnresolvedClarificationSetAsideWithoutReescalating()
    {
        var kernel = new AgentOrchestratorKernel();
        var blockedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "blocked goal");
        var activeGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active goal");
        var workspaces = new HashSet<string>(StringComparer.Ordinal);
        var blockedClarificationPolls = 0;
        var escalations = 0;
        var dispatches = 0;

        var driver = MakeDriver(
            getFacts: goal =>
            {
                if (goal.Id == blockedGoal.Id)
                {
                    blockedClarificationPolls++;
                    return new GoalLifecycleFacts(HasOpenClarification: true);
                }

                return new GoalLifecycleFacts(WorkspaceExists: workspaces.Contains(goal.Id.Value));
            },
            createWorkspace: goal =>
            {
                workspaces.Add(goal.Id.Value);
                return "C:\\active";
            },
            dispatchAndStart: goal =>
            {
                if (goal.Id == activeGoal.Id)
                {
                    var task = goal.Tasks.Single();
                    kernel.RecordTaskDispatch(goal.Id, task.Id,
                        new TaskDispatchRecord("test-worker", "test.exe", "C:\\active", DateTimeOffset.UtcNow));
                    kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                        new TaskProcessRecord(123, "test.exe", "C:\\active", "out.log", "err.log", "exit.txt",
                            DateTimeOffset.UtcNow, null, null));
                    dispatches++;
                }

                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, _) => escalations++);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(3, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, escalations);
        Assert.Equal(1, dispatches);
        Assert.True(blockedClarificationPolls >= 3);
    }

    [Xunit.Fact]
    public void BatchLoopReadmitsUnresolvedClarificationWhenDurableTaskFieldsChange()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "blocked goal");
        var task = goal.Tasks.Single();
        var escalations = 0;
        var retried = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(HasOpenClarification: true),
            writeEscalation: (_, _, _) => escalations++);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                if (!retried)
                {
                    kernel.RetryTask(goal.Id, task.Id, "Durable task state changed while clarification remained open.");
                    retried = true;
                }
                return false;
            });

        Assert.Equal(2, summary.Ticks);
        Assert.Equal(2, summary.Escalated);
        Assert.Equal(2, escalations);
        Assert.Contains(kernel.GetGoal(goal.Id).Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("re-admitted escalated goal after state changed", StringComparison.Ordinal));
    }
}
