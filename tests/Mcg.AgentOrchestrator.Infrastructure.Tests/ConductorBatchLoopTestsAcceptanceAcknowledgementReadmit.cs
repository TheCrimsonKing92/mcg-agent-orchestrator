using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Private SQLite/attempt roots; the collection also isolates shared acceptance fairness state.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsAcceptanceAcknowledgementReadmit : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsAcceptanceAcknowledgementReadmit(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public void ParkUnpark_SecondWriter_ReadmitsOnceAfterOccupiedWidthReleases() =>
        RunScenario("acknowledged");

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData("unresolved")]
    [Xunit.InlineData("unrelated")]
    [Xunit.InlineData("older-ack")]
    [Xunit.InlineData("parked")]
    [Xunit.InlineData("unavailable-facts")]
    [Xunit.InlineData("null-facts")]
    public void SetAside_WithoutUsableAcknowledgement_RemainsHeld(string scenario) =>
        RunScenario(scenario);

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public void ReadmittedAttempt_InterruptedAgain_RequiresNewAcknowledgement() =>
        RunScenario("fail-again");

    private void RunScenario(string scenario)
    {
        const string candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string main = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string readmitPrefix = "Batch loop re-admitted acknowledged acceptance escalation:";
        // External gate heartbeats belong to the host running this test, not this
        // private scenario. Its real coordinator attempts still occupy width.
        using var liveGates = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var root = CreateTempDirectory("mcg-acceptance-ack-readmit");
        var now = new DateTimeOffset(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);
        var kernel = new AgentOrchestratorKernel();
        var target = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Update src/AckTarget.cs");
        var task = target.Tasks.Single();
        kernel.RecordTaskDispatch(target.Id, task.Id,
            new TaskDispatchRecord("seed", "test.exe", root, now, ResultCommit: candidate));
        kernel.RecordTaskVerification(target.Id, task.Id,
            new TaskVerificationRecord("test.exe", root, 0, "ok", "", now, ReviewedCommit: candidate));
        // Recording passing task evidence completes the task and refreshes the goal
        // to Verified before the loop's first acceptance admission pass.
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(GoalStatus.Verified, target.Status);
        var originalTask = JsonSerializer.Serialize(kernel.ExportSnapshot().Goals.Single().Tasks);
        var originalTaskEvents = target.Timeline.Where(e => e.TaskId is not null).ToArray();
        // Keep passes productive even when both acceptance goals become set aside,
        // including the readmission-only mutant. This neighbor never starts a worker.
        var neighbor = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Held neighbor");
        var targetLaunches = new List<ConductorParallelAcceptanceOwnedProcessLaunch>();
        var holderLaunches = new List<ConductorParallelAcceptanceOwnedProcessLaunch>();
        var dead = new HashSet<string>(StringComparer.Ordinal);
        Goal? holder = null;
        ProgressEvent? failure = null;
        ConductorParallelAcceptanceAttempt? failedAttempt = null;
        ConductorParallelAcceptanceAttempt? freshAttempt = null;
        VerifiedAcceptanceEscalationDecision.EventIdentity? escalationIdentity = null;
        VerifiedAcceptanceEscalationDecision.EventIdentity? clearingIdentity = null;
        var writes = 0;
        var reloads = 0;
        var reloadPending = false;
        var acknowledged = false;
        var released = false;
        var occupiedPasses = 0;
        var freshPasses = 0;
        var repeatFailureSeen = false;
        var reconciledAfterReload = false;
        var widthDeferred = false;
        var factsFailures = 0;
        var workerStarts = 0;
        var neighborHolds = 0;
        var gateRuns = 0;
        var landings = 0;
        ConductorBatchLoop.ResetParallelAcceptanceFairnessForTests();
        try
        {
            var repositoryA = OpenStateRepository(Path.Combine(root, "state.db"));
            var repositoryB = OpenStateRepository(Path.Combine(root, "state.db"));
            Assert.NotSame(repositoryA, repositoryB);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"), TrunkBranchName.Default,
                utcNow: () => now,
                recentHeartbeatGrace: TimeSpan.FromSeconds(1),
                isProcessAlive: _ => true,
                classifyHolder: a => dead.Contains(a.AttemptId)
                    ? SpawnTrackedProcessStatus.DeadOrRecycled : SpawnTrackedProcessStatus.LiveMatch,
                launchOwnedProcess: launch =>
                {
                    (launch.Attempt.GoalId == target.Id.Value ? targetLaunches : holderLaunches).Add(launch);
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7001);
                },
                // The synthetic gate needs no machine build slot; its coordinator
                // attempt still occupies ordinary acceptance width until reconciled.
                acquireStableSlotLease: (_, _) => null,
                resultPublishedForTests: a => dead.Add(a.AttemptId));
            var driver = MakeDriver(
                getFacts: g =>
                {
                    if (scenario is "unavailable-facts" or "null-facts" && acknowledged && g.Id == target.Id)
                    {
                        factsFailures++;
                        if (scenario == "null-facts") return null!;
                        throw new IOException("controlled lifecycle facts unavailable");
                    }
                    return new GoalLifecycleFacts(WorkspaceExists: true);
                },
                dispatchAndStart: g =>
                {
                    if (g.Id == neighbor.Id)
                    {
                        neighborHolds++;
                        return DispatchStartOutcome.EmptyBatch("Held for operator approval.");
                    }
                    workerStarts++;
                    return DispatchStartOutcome.Started();
                },
                // This callback enables the driver's parallel acceptance path. Only
                // explicitly released children execute the failing holder gate.
                runAcceptanceWithSlot: (g, _) =>
                {
                    Assert.Equal(holder!.Id, g.Id);
                    gateRuns++;
                    return AcceptanceVerificationSummary.Failed;
                },
                getLandingFileScopes: g => [g.Id == target.Id ? "src/AckTarget.cs" : "src/AckHolder.cs"],
                resolveAcceptanceHeads: _ => (candidate, main),
                parallelAcceptanceAttemptCoordinator: coordinator,
                land: g =>
                {
                    landings++;
                    return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "landed");
                });
            Assert.True(driver.ParallelAcceptanceEnabled,
                "Scenario must enable the driver's owned parallel acceptance path.");
            var loop = new ConductorBatchLoop(utcNow: () => now, sweep: loopKernel =>
            {
                if (!reloadPending) return;
                var before = loopKernel.GetGoal(target.Id);
                var loaded = repositoryA.LoadAsync().GetAwaiter().GetResult();
                Assert.NotSame(loopKernel, loaded);
                CliCommandHandlers.RefreshTrackedGoalsPreservingCheckpointHolds(
                    loopKernel, loaded.ExportSnapshot(), new HashSet<string>());
                var after = loopKernel.GetGoal(target.Id);
                Assert.NotSame(before, after);
                Assert.Contains(after.Timeline, e => JsonSerializer.Serialize(e) == JsonSerializer.Serialize(failure));
                Assert.Equal(originalTask, JsonSerializer.Serialize(loopKernel.ExportSnapshot().Goals.Single(g => g.Id == target.Id.Value).Tasks));
                if (escalationIdentity is not null)
                    clearingIdentity = VerifiedAcceptanceEscalationDecision.FindAcknowledgement(after, escalationIdentity);
                if (scenario is "acknowledged" or "fail-again" or "unavailable-facts" or "null-facts" or "older-ack")
                {
                    Assert.Equal(GoalStatus.Active, after.Status);
                    Assert.Contains(after.Timeline, e => e.Message.StartsWith("Goal parked:", StringComparison.Ordinal));
                    Assert.Contains(after.Timeline, e => e.Message.StartsWith("Goal unparked:", StringComparison.Ordinal));
                }
                reloadPending = false;
                reloads++;
            });
            var summary = loop.Run(kernel, driver,
                ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 },
                Path.Combine(root, "stop.txt"), maxIterations: 30,
                watchInterval: TimeSpan.FromSeconds(1), keepAliveWhenIdle: true,
                // Age interrupted holders past grace. Once acknowledged, live holders
                // stay within grace; a deliberately interrupted fresh attempt ages again.
                sleepFunc: _ =>
                {
                    now = now.Add(acknowledged && !(scenario == "fail-again" && freshAttempt is not null)
                        ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromSeconds(2));
                    return false;
                },
                unscopedStallTickThreshold: 100,
                onTick: tick =>
                {
                    var current = kernel.GetGoal(target.Id);
                    if (tick.Tick == 1)
                        Assert.True(targetLaunches.Count == 1,
                            "Scenario must start its initial ordinary acceptance attempt. " +
                            string.Join(Environment.NewLine, tick.ProgressLines ?? []));
                    if (failure is null)
                    {
                        foreach (var launch in targetLaunches) dead.Add(launch.Attempt.AttemptId);
                        failure = current.Timeline.FirstOrDefault(e =>
                            e.TickOutcome?.EscalationKind == nameof(ConductorEscalationKind.BackgroundAcceptanceFailed));
                        if (failure is null) return;
                        failedAttempt = ReadAttempt(targetLaunches.Last().Attempt.MetadataPath);
                        escalationIdentity = VerifiedAcceptanceEscalationDecision.FindUnresolvedEscalation(current);
                        Assert.NotNull(escalationIdentity);
                        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied, failedAttempt.Outcome);
                        Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, failedAttempt.TransientFailureCount);
                        Assert.False(File.Exists(failedAttempt.ResultPath));
                        holder = CreateVerifiedSimpleGoal(kernel, "Update src/AckHolder.cs");
                        return;
                    }
                    if (!acknowledged && holderLaunches.Count == 1)
                    {
                        Assert.True(coordinator.HasLiveAttempt(holder!.Id.Value));
                        repositoryA.SaveAsync(kernel).GetAwaiter().GetResult();
                        // Both transactions commit between passes; reader A never sees Parked.
                        repositoryB.TransactAsync((writer, _) =>
                        {
                            Assert.NotSame(kernel, writer);
                            Assert.Contains(writer.GetGoal(target.Id).Timeline,
                                e => JsonSerializer.Serialize(e) == JsonSerializer.Serialize(failure));
                            if (scenario == "unresolved") return Task.FromResult((false, true));
                            if (scenario == "unrelated")
                                writer.RecordGoalPolicyDecision(target.Id, "unrelated policy, note and version change");
                            else writer.ParkGoal(target.Id, "acknowledge interrupted gate");
                            return Task.FromResult((true, true));
                        }).GetAwaiter().GetResult();
                        writes++;
                        if (scenario is not ("unresolved" or "unrelated" or "parked"))
                        {
                            repositoryB.TransactAsync((writer, _) =>
                            {
                                writer.UnparkGoal(target.Id, "resume normal admission");
                                if (scenario == "older-ack")
                                    writer.RecordGoalPolicyDecision(target.Id, "newer acceptance failure",
                                        new ConductorTickOutcomePayload("Escalated", "Verified", "BackgroundAcceptanceFailed"));
                                return Task.FromResult((true, true));
                            }).GetAwaiter().GetResult();
                            writes++;
                        }
                        acknowledged = true;
                        reloadPending = true;
                        return;
                    }
                    if (!acknowledged || reloadPending) return;
                    if (scenario is not ("acknowledged" or "fail-again")) return;
                    if (!released)
                    {
                        occupiedPasses++;
                        reconciledAfterReload |= current.Status == GoalStatus.Verified;
                        widthDeferred |= current.Status == GoalStatus.Verified &&
                            (tick.ProgressLines ?? []).Any(line => line.Contains("parallel-acceptance-slot-cap", StringComparison.Ordinal));
                        Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, targetLaunches.Count);
                        Assert.True(coordinator.HasLiveAttempt(holder!.Id.Value));
                        if (occupiedPasses < 3) return;
                        // Release even in the RED mutation so its final assertion is admission,
                        // never a missing callback, setup exception or hang detector.
                        holderLaunches.Single().ExecuteInCurrentProcess(7001);
                        released = true;
                        return;
                    }
                    if (targetLaunches.Count <= ConductorBatchLoop.ParallelAcceptanceTransientFailureCap) return;
                    var latest = ReadAttempt(targetLaunches.Last().Attempt.MetadataPath);
                    if (freshAttempt is null)
                    {
                        freshAttempt = latest;
                        Assert.NotEqual(failedAttempt!.AttemptId, latest.AttemptId);
                        Assert.Equal(failedAttempt.BranchHeadSha, latest.BranchHeadSha);
                        Assert.Equal(candidate, latest.BranchHeadSha);
                        Assert.Equal(main, latest.MainHeadSha);
                        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, latest.Outcome);
                        if (scenario == "fail-again") dead.Add(latest.AttemptId);
                    }
                    Assert.Equal(freshAttempt.AttemptId, latest.AttemptId);
                    freshPasses++;
                    repeatFailureSeen |= current.Timeline.Count(e =>
                        e.TickOutcome?.EscalationKind == "BackgroundAcceptanceFailed") == 2;
                });

            Assert.Equal(30, summary.Ticks);
            Assert.NotNull(failure);
            Assert.NotNull(failedAttempt);
            Assert.True(acknowledged);
            Assert.Equal(1, reloads);
            var final = kernel.GetGoal(target.Id);
            Assert.Contains(final.Timeline, e => JsonSerializer.Serialize(e) == JsonSerializer.Serialize(failure));
            Assert.Equal(originalTask, JsonSerializer.Serialize(kernel.ExportSnapshot().Goals.Single(g => g.Id == target.Id.Value).Tasks));
            Assert.Equal(JsonSerializer.Serialize(originalTaskEvents),
                JsonSerializer.Serialize(final.Timeline.Where(e => e.TaskId is not null).ToArray()));
            Assert.Equal(candidate, final.Tasks.Single().LastDispatch!.ResultCommit);
            Assert.Equal(candidate, final.Tasks.Single().LastVerification!.ReviewedCommit);
            Assert.Equal(0, workerStarts);
            Assert.True(neighborHolds >= 3);
            Assert.Equal(0, landings);
            Assert.DoesNotContain(final.Timeline, e => e.Message.StartsWith(ConductorBatchLoop.SetAsideSelfClearDecisionPrefix, StringComparison.Ordinal));
            var readmissions = final.Timeline.Where(e => e.Message.StartsWith(readmitPrefix, StringComparison.Ordinal)).ToArray();
            if (scenario is "acknowledged" or "fail-again")
            {
                Assert.True(released);
                Assert.Equal(2, writes);
                Assert.True(occupiedPasses >= 3);
                Assert.Equal(1, gateRuns); // The holder's injected failing gate only.
                Assert.True(targetLaunches.Count == ConductorBatchLoop.ParallelAcceptanceTransientFailureCap + 1,
                    "Acknowledged target must obtain exactly one fresh ordinary admission after width release.");
                Assert.NotNull(freshAttempt);
                Assert.True(freshPasses >= 3);
                Assert.Single(readmissions);
                Assert.NotNull(clearingIdentity);
                Assert.Contains($"escalation={escalationIdentity!.TimelineIndex}:{escalationIdentity.ContentHash}", readmissions[0].Message);
                Assert.Contains($"acknowledgement={clearingIdentity.TimelineIndex}:{clearingIdentity.ContentHash}", readmissions[0].Message);
                Assert.True(reconciledAfterReload);
                Assert.True(widthDeferred, "Acknowledged target must be ready and deferred by ordinary occupied width 1 before release.");
                Assert.Equal(scenario == "fail-again" ? 2 : 1, final.Timeline.Count(e =>
                    e.TickOutcome?.EscalationKind == "BackgroundAcceptanceFailed"));
                if (scenario == "fail-again") Assert.True(repeatFailureSeen);
            }
            else
            {
                Assert.Empty(readmissions);
                Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, targetLaunches.Count);
                Assert.Equal(0, gateRuns);
                Assert.Equal(scenario == "older-ack" ? 2 : 1, final.Timeline.Count(e =>
                    e.TickOutcome?.EscalationKind == "BackgroundAcceptanceFailed"));
                if (scenario == "parked") Assert.Equal(GoalStatus.Parked, final.Status);
                if (scenario is "unavailable-facts" or "null-facts") Assert.True(factsFailures > 0);
            }
            Assert.Equal(JsonSerializer.Serialize(failedAttempt), JsonSerializer.Serialize(ReadAttempt(failedAttempt!.MetadataPath)));
            Assert.False(File.Exists(failedAttempt.ResultPath));
            _output.WriteLine($"candidate old={failedAttempt.BranchHeadSha} new={freshAttempt?.BranchHeadSha}; " +
                $"attempt old={failedAttempt.AttemptId} new={freshAttempt?.AttemptId}; reloads={reloads}");
        }
        finally
        {
            // Launches are injected callbacks, with no child processes or pending async work.
            ConductorBatchLoop.ResetParallelAcceptanceFairnessForTests();
            Directory.Delete(root, recursive: true);
        }
    }
}
