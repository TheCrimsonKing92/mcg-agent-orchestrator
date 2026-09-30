using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class AcceptanceCohortWorkflowTestsBackgroundAndCapacity : AcceptanceCohortWorkflowTests
{
    internal readonly record struct DrainedTeardown(bool Drained, string Condition)
    {
        public void AssertDrained() =>
            Assert.True(Drained, $"Background cohort gate did not drain: {Condition}.");
    }

    internal static DrainedTeardown RemoveAfterObservedDrain(
        Func<bool> drainCondition, string conditionName, TimeSpan bound, params Action[] removals)
    {
        var drained = SpinWait.SpinUntil(drainCondition, bound);
        if (drained)
        {
            foreach (var removal in removals)
                removal();
        }
        return new DrainedTeardown(drained, conditionName);
    }


    [Fact]
    public void ConductLog_FairnessYield_PersistsTypedDecision()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-fairness-decision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var blockedHead = new GoalId("11111111111111111111111111111111");
            var selected = new GoalId("22222222222222222222222222222222");
            const string holder = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var decision = new ConductorAcceptanceCohortFairnessDecision(
                ConductorAcceptanceCohortFairnessOutcome.YieldedToRunnableYounger,
                blockedHead,
                selected,
                new ConductorAcceptanceHeldConflict(
                    blockedHead,
                    holder,
                    ConductorAcceptanceHeldConflictKind.LandingPath,
                    "path:src/Blocked.cs:src/Blocked.cs"),
                OvertakeCount: 1,
                CandidateConflicts: []);
            var conductLogPath = Path.Combine(root, "fairness-decision.jsonl");
            var writer = new ConductEventLogWriter(conductLogPath);
            var loop = new ConductorBatchLoop();
            var emit = typeof(ConductorBatchLoop).GetMethod(
                "EmitAcceptanceCohortFairnessDecision",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(emit);
            var writerContextField = typeof(ConductorBatchLoop).GetField(
                "CurrentConductEventLogWriter",
                BindingFlags.Static | BindingFlags.NonPublic);
            var writerContext = Assert.IsType<AsyncLocal<ConductEventLogWriter?>>(writerContextField?.GetValue(null));
            var previousWriter = writerContext.Value;
            try
            {
                writerContext.Value = writer;
                emit.Invoke(loop, [decision]);
            }
            finally
            {
                writerContext.Value = previousWriter;
            }

            var records = (File.Exists(conductLogPath) ? File.ReadAllLines(conductLogPath) : [])
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record =>
                    record.EventKind == "acceptance-cohort" &&
                    record.Detail.StartsWith("ACCEPTANCE_COHORT_FAIRNESS ", StringComparison.Ordinal))
                .ToArray();
            var fairness = Assert.Single(records);
            Assert.Contains($"blocked={blockedHead.Value} selected={selected.Value}", fairness.Detail, StringComparison.Ordinal);
            Assert.Contains(
                $"holder={holder} conflict_kind=LandingPath conflict=path:src/Blocked.cs:src/Blocked.cs overtakes=1",
                fairness.Detail,
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void CohortProgressEvents_AreStructuredForBothMembersWithoutUnknownGoal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var first = new GoalId("11111111111111111111111111111111");
            var second = new GoalId("22222222222222222222222222222222");
            var bindings = new[]
            {
                Bind(first, new string('a', 40), "src/First.cs", "production:first"),
                Bind(second, new string('b', 40), "src/Second.cs", "production:second")
            };
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                new string('c', 40),
                new string('d', 40),
                "manifest-v1");
            var logPath = Path.Combine(root, "conduct-events.jsonl");
            var writer = new ConductEventLogWriter(logPath);
            var now = DateTimeOffset.UtcNow;

            ConductorDriver.AppendCohortGateProgressEvents(
                writer,
                identity,
                bindings,
                new AcceptanceGateProgress(
                    GoalId: null,
                    Phase: "lane",
                    CurrentTarget: "focused-cohort",
                    SlotIndex: 0,
                    ProcessId: Environment.ProcessId,
                    ChildProcessId: 1234,
                    StartedAt: now.AddSeconds(-1),
                    LastObservedAt: now,
                    LastProgressAt: now,
                    Elapsed: TimeSpan.FromSeconds(1),
                    OutputBytes: 42,
                    HeartbeatPath: Path.Combine(root, "heartbeat.json")));

            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();
            Assert.Equal(2, records.Length);
            Assert.Contains(records, record => record.GoalId == first.Value[..8]);
            Assert.Contains(records, record => record.GoalId == second.Value[..8]);
            Assert.All(records, record =>
            {
                Assert.Equal("gate-progress", record.EventKind);
                Assert.Contains($"cohort={identity.Value[..18]}", record.Detail, StringComparison.Ordinal);
                Assert.Contains($"members={first.Value[..8]},{second.Value[..8]}", record.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("goal=unknown", record.Detail, StringComparison.Ordinal);
            });
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void BackgroundCohortGate_MainAdvanceDoesNotLaunchDuplicateForSameMembers()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-member-pair-{Guid.NewGuid():N}.trx");
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        DrainedTeardown teardown;
        try
        {
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First stable member-pair member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second stable member-pair member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new BlockingAcceptanceVerifier(
                gateStarted,
                gateRelease,
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("shared-member-pair-gate", true, 0, null)],
                    TestResultPaths: [trx]));
            var driver = new ConductorDriver(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var originalPairFingerprint = ConductorAcceptanceCohortSelector.PairFingerprint(
                selection.Members[0],
                selection.Members[1]);

            var first = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive,
                runGateInBackground: true);

            Assert.Contains("outcome=inflight", first.Detail, StringComparison.Ordinal);
            Assert.True(gateStarted.Wait(TimeSpan.FromSeconds(10)), "The controlled cohort verifier did not start.");
            Assert.Equal(1, verifier.RunCount);
            Assert.Single(Directory.EnumerateDirectories(
                Path.Combine(repo, GoalWorktrees.DirectoryName),
                "c-*"));

            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main advanced while cohort gate remained held");
            RunGit(repo, "add", "main-advanced.txt");
            RunGit(repo, "commit", "-m", "Advance main during cohort gate");
            var revisedSelection = ProjectSelection(driver, firstGoal, secondGoal);
            Assert.NotEqual(selection.Members[0].MainRevision, revisedSelection.Members[0].MainRevision);
            Assert.NotEqual(
                originalPairFingerprint,
                ConductorAcceptanceCohortSelector.PairFingerprint(
                    revisedSelection.Members[0],
                    revisedSelection.Members[1]));

            var held = driver.RunAcceptanceCohort(
                revisedSelection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive,
                runGateInBackground: true);

            Assert.Contains("outcome=inflight", held.Detail, StringComparison.Ordinal);
            Assert.Contains($"fingerprint={originalPairFingerprint}", held.Detail, StringComparison.Ordinal);
            Assert.Equal(1, verifier.RunCount);
            Assert.Single(Directory.EnumerateDirectories(
                Path.Combine(repo, GoalWorktrees.DirectoryName),
                "c-*"));
        }
        finally
        {
            gateRelease.Set();
            teardown = RemoveAfterObservedDrain(
                () => !Directory.Exists(repo) ||
                      !Directory.EnumerateDirectories(
                          Path.Combine(repo, GoalWorktrees.DirectoryName),
                          "c-*").Any(),
                "no c-* cohort worktree remains under repo",
                TimeSpan.FromSeconds(10),
                () => { if (File.Exists(trx)) File.Delete(trx); },
                () => DeleteDirectory(repo));
        }
        teardown.AssertDrained();
    }

    [Fact]
    public void BackgroundCohortGate_OverlappingMembersAreHeldByActualOwner()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-overlap-{Guid.NewGuid():N}.trx");
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        ConductorDriver? driver = null;
        TaskCompletionSource? registrationProbe = null;
        DrainedTeardown teardown;
        try
        {
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3)
                .Select(index => CreateCompletedGoal(kernel, $"Overlapping cohort member {index}", repo))
                .ToArray();
            var paths = new[]
            {
                "src/Mcg.AgentOrchestrator.Infrastructure/OverlapFirst.cs",
                "tests/OverlapSecond.cs",
                "src/Mcg.AgentOrchestrator.Core/OverlapThird.cs"
            };
            for (var index = 0; index < goals.Length; index++)
            {
                _ = CreateWorktreeCandidate(repo, goals[index].Id, paths[index], $"overlap-{index}");
            }

            var verifier = new BlockingAcceptanceVerifier(
                gateStarted,
                gateRelease,
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("overlap-owner-gate", true, 0, null)],
                    TestResultPaths: [trx]));
            driver = new ConductorDriver(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            var policy = ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 2 };
            var ownerSelection = ProjectSelection(driver, goals[0], goals[1]);
            var overlappingSelection = ProjectSelection(driver, goals[1], goals[2]);

            _ = driver.RunAcceptanceCohort(
                ownerSelection,
                goals,
                policy,
                runGateInBackground: true);
            registrationProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.False(driver.TryRegisterCohortGateRunForTests(overlappingSelection, registrationProbe));
            var held = driver.RunAcceptanceCohort(
                overlappingSelection,
                goals,
                policy,
                runGateInBackground: true);

            var owner = string.Join(
                "+",
                ownerSelection.Members.Select(member => member.GoalId.Value).OrderBy(id => id, StringComparer.Ordinal));
            var capacity = driver.GetActiveAcceptanceCohortCapacity();
            Assert.Equal(1, capacity.ActiveRootCount);
            Assert.Single(capacity.ActiveRoots.Where(root => root.MemberGoalIds.Contains(goals[1].Id.Value)));
            Assert.Contains("outcome=inflight", held.Detail, StringComparison.Ordinal);
            Assert.Contains($"owner={owner}", held.Detail, StringComparison.Ordinal);
            Assert.True(driver.TryGetCohortGateHold(goals[1].Id, out var hold));
            Assert.Contains($"owner={owner}", hold, StringComparison.Ordinal);
            Assert.False(driver.TryGetCohortGateHold(goals[2].Id, out _));
        }
        finally
        {
            registrationProbe?.TrySetResult();
            gateRelease.Set();
            teardown = RemoveAfterObservedDrain(
                () => driver is null || driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount == 0,
                "ActiveRootCount == 0",
                TimeSpan.FromSeconds(10),
                () => { if (File.Exists(trx)) File.Delete(trx); },
                () => DeleteDirectory(repo));
        }
        teardown.AssertDrained();
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task BackgroundCohortGate_TerminalRunReleasesMembersWhileDisjointRunRemainsActive(
        string terminalState)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 5)
                .Select(index => CreateCompletedGoal(kernel, $"Terminal cohort member {index}", repo))
                .ToArray();
            using var unusedStarted = new ManualResetEventSlim();
            using var unusedRelease = new ManualResetEventSlim();
            var driver = new ConductorDriver(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new BlockingAcceptanceVerifier(
                    unusedStarted,
                    unusedRelease,
                    new AcceptanceVerificationResult(
                        Passed: true,
                        Skipped: false,
                        ExitCode: 0,
                        OutputTail: null,
                        Checks: [],
                        TestResultPaths: [])),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            var ownerSelection = TestSelection(goals[0], goals[1]);
            var disjointSelection = TestSelection(goals[2], goals[3]);
            var releasedSelection = TestSelection(goals[0], goals[4]);
            var ownerCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disjointCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Assert.True(driver.TryRegisterCohortGateRunForTests(ownerSelection, ownerCompletion));
            Assert.True(driver.TryRegisterCohortGateRunForTests(disjointSelection, disjointCompletion));
            Assert.Equal(2, driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount);

            switch (terminalState)
            {
                case "succeeded":
                    ownerCompletion.SetResult();
                    await ownerCompletion.Task;
                    break;
                case "failed":
                    ownerCompletion.SetException(new InvalidOperationException("controlled cohort failure"));
                    await Assert.ThrowsAsync<InvalidOperationException>(async () => await ownerCompletion.Task);
                    break;
                case "cancelled":
                    ownerCompletion.SetCanceled();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ownerCompletion.Task);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown terminal state '{terminalState}'.");
            }

            var activeMembers = driver.GetActiveCohortGateMemberGoalIds();
            Assert.DoesNotContain(goals[0].Id.Value, activeMembers);
            Assert.DoesNotContain(goals[1].Id.Value, activeMembers);
            Assert.Contains(goals[2].Id.Value, activeMembers);
            Assert.Contains(goals[3].Id.Value, activeMembers);
            Assert.True(driver.TryRegisterCohortGateRunForTests(releasedSelection, releasedCompletion));

            disjointCompletion.SetResult();
            releasedCompletion.SetResult();
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void BackgroundCohortGate_SamePairCanRegisterWhenIncumbentCompletesAfterSweep()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 2)
                .Select(index => CreateCompletedGoal(kernel, $"Same-pair cohort member {index}", repo))
                .ToArray();
            using var unusedStarted = new ManualResetEventSlim();
            using var unusedRelease = new ManualResetEventSlim();
            var driver = new ConductorDriver(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new BlockingAcceptanceVerifier(
                    unusedStarted,
                    unusedRelease,
                    new AcceptanceVerificationResult(
                        Passed: true,
                        Skipped: false,
                        ExitCode: 0,
                        OutputTail: null,
                        Checks: [],
                        TestResultPaths: [])),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            var selection = TestSelection(goals[0], goals[1]);
            var incumbentCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Assert.True(driver.TryRegisterCohortGateRunForTests(selection, incumbentCompletion));
            var registered = false;
            var exception = Record.Exception(() =>
                registered = driver.TryRegisterCohortGateRunForTests(
                    selection,
                    replacementCompletion,
                    incumbentCompletion.SetResult));

            Assert.Null(exception);
            Assert.True(registered);
            replacementCompletion.SetResult();
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static ConductorAcceptanceCohortSelection TestSelection(Goal first, Goal second)
    {
        var mainRevision = new string('c', 40);
        return new ConductorAcceptanceCohortSelection(
            [
                TestProjection(first.Id, new string('a', 40), mainRevision, $"src/{first.Id.Value[..8]}.cs"),
                TestProjection(second.Id, new string('b', 40), mainRevision, $"tests/{second.Id.Value[..8]}.cs")
            ],
            []);
    }

    private static GateReadyCandidateProjection TestProjection(
        GoalId goalId,
        string branchRevision,
        string mainRevision,
        string landingPath) =>
        new(
            goalId,
            GoalLifecycleState.Verified,
            GateReadyVerificationState.Satisfied,
            ChangeRiskTier.DocsOnly,
            ConductorTransitionDecision.Auto,
            [landingPath],
            [$"production:{goalId.Value[..8]}"],
            new GateReadyMergeEvidence(
                branchRevision,
                mainRevision,
                GateReadyMergeStatus.Clean,
                GateReadyMergeReason.NoConflictsDetected));

    [Fact]
    public void ProductionBatch_LongCohortGateDoesNotBlockTicksOrOperatorIntents_AndReconcilesLater()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-production-{Guid.NewGuid():N}.trx");
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First production cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second production cohort member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new BlockingAcceptanceVerifier(
                gateStarted,
                gateRelease,
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("shared-production-gate", true, 0, null)],
                    TestResultPaths: [trx]));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel,
                workspace,
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            var fairnessStore = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            fairnessStore.RecordOvertake(firstGoal.Id);
            fairnessStore.RecordOvertake(secondGoal.Id);
            var stopPath = Path.Combine(repo, "stop-does-not-exist");
            var conductLogPath = Path.Combine(workspace.OrchestratorDirectory, "logs", "cohort-nonblocking.jsonl");
            var intentStore = new SqliteOperatorIntentStore(
                Path.Combine(workspace.OrchestratorDirectory, "operator-intents.db"),
                Path.Combine(workspace.OrchestratorDirectory, "logs"));
            const string intentId = "cohort-running-progress";
            var ticks = new List<BatchTickSummary>();

            var heldSummary = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(intentStore),
                conductEventLogWriter: new ConductEventLogWriter(conductLogPath)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                stopPath,
                maxIterations: 3,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ => false,
                onTick: tick =>
                {
                    ticks.Add(tick);
                    if (ticks.Count != 1)
                    {
                        return;
                    }

                    Assert.True(gateStarted.Wait(TimeSpan.FromSeconds(10)), "The controlled cohort verifier did not start.");
                    intentStore.EnqueueAsync(new OperatorIntentRecord(
                        intentId,
                        "cohort-running-progress-key",
                        OperatorIntentVerbs.Progress,
                        firstGoal.Id.Value,
                        firstGoal.Tasks.Single().Id.Value,
                        JsonSerializer.Serialize(
                            new ProgressOperatorIntentPayload(WorkTaskStatus.Completed, "operator intent applied while cohort gate remained held"),
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                        [],
                        "operator",
                        "test",
                        "local-process",
                        DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
                },
                persistGoalTick: (_, _) => { });

            Assert.Equal(3, ticks.Count);
            Assert.Equal(1, verifier.RunCount);
            Assert.True(verifier.StableSlotLeaseObserved);
            Assert.True(Directory.Exists(verifier.WorktreePath));
            Assert.Equal(OperatorIntentStatus.Applied, intentStore.GetAsync(intentId).GetAwaiter().GetResult()!.Status);
            Assert.Equal(GoalStatus.Verified, firstGoal.Status);
            Assert.Equal(GoalStatus.Verified, secondGoal.Status);
            Assert.Empty(driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts(
                [firstGoal.Id.Value, secondGoal.Id.Value]));
            Assert.Equal(0, heldSummary.Advanced);
            var conductEvents = File.ReadAllLines(conductLogPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();
            var gateProgressEvents = File.ReadAllLines(workspace.ConductEventsLogPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "gate-progress")
                .ToArray();
            Assert.Equal(2, gateProgressEvents.Length);
            Assert.Contains(gateProgressEvents, record => record.GoalId == firstGoal.Id.Value[..8]);
            Assert.Contains(gateProgressEvents, record => record.GoalId == secondGoal.Id.Value[..8]);
            Assert.All(gateProgressEvents, record =>
            {
                Assert.Contains($"members={firstGoal.Id.Value[..8]},{secondGoal.Id.Value[..8]}", record.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("goal=unknown", record.Detail, StringComparison.Ordinal);
            });
            var cohortEvents = conductEvents
                .Where(record => record.EventKind == "acceptance-cohort")
                .ToArray();
            Assert.StartsWith("ACCEPTANCE_COHORT_ENTRY", cohortEvents[0].Detail, StringComparison.Ordinal);
            Assert.Contains(cohortEvents, record => record.Detail.StartsWith("ACCEPTANCE_COHORT_ENTRY", StringComparison.Ordinal));
            Assert.Contains(cohortEvents, record => record.Detail.StartsWith("ACCEPTANCE_COHORT_EXIT", StringComparison.Ordinal));
            var fairnessTransition = Assert.Single(cohortEvents.Where(record =>
                record.Detail.StartsWith("ACCEPTANCE_COHORT_FAIRNESS_TRANSITION", StringComparison.Ordinal)));
            Assert.Contains($"oldest={firstGoal.Id.Value}", fairnessTransition.Detail, StringComparison.Ordinal);
            Assert.Contains("previous=1 resulting=0 oldest_admitted=True", fairnessTransition.Detail, StringComparison.Ordinal);
            var inFlightTicks = cohortEvents
                .Where(record => record.Detail.StartsWith("ACCEPTANCE_COHORT_INFLIGHT", StringComparison.Ordinal))
                .Select(record => record.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Single(token => token.StartsWith("tick=", StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.True(inFlightTicks.Length >= 2, "Expected in-flight cohort records from at least two completed ticks.");

            gateRelease.Set();
            Assert.True(SpinWait.SpinUntil(ReceiptPersisted, TimeSpan.FromSeconds(10)), "The background cohort receipt was not persisted.");
            var landedSummary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                stopPath,
                maxIterations: 1);

            Assert.Equal(0, fairnessStore.ReadOvertakeCount(firstGoal.Id));
            Assert.Equal(0, fairnessStore.ReadOvertakeCount(secondGoal.Id));
            Assert.Equal(2, landedSummary.Advanced);
            Assert.True(File.Exists(Path.Combine(
                repo,
                "src",
                "Mcg.AgentOrchestrator.Infrastructure",
                "First.cs")));
            Assert.True(File.Exists(Path.Combine(repo, "tests", "Second.cs")));
            AssertNoCohortWorkspaces(repo);
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*), MIN(gate_exit_code), MAX(json_array_length(gate_test_result_paths_json))
                FROM cohort_receipts;
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));

            bool ReceiptPersisted()
            {
                using var receiptConnection = new SqliteConnection(
                    $"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")};Pooling=False");
                receiptConnection.Open();
                using var receiptCommand = receiptConnection.CreateCommand();
                receiptCommand.CommandText = "SELECT COUNT(*) FROM cohort_receipts;";
                return Convert.ToInt32(receiptCommand.ExecuteScalar()) == 1;
            }
        }
        finally
        {
            gateRelease.Set();
            if (File.Exists(trx)) File.Delete(trx);
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void IndependentWorkspaceDrivers_OverlapWithoutSharingStorageRoots()
    {
        var repositories = new List<string>();
        var drivers = new List<ConductorDriver>();
        var roots = new List<DotnetBuildStorageRoot>();
        var verifiers = new List<BlockingAcceptanceVerifier>();
        using var firstStarted = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var starts = new[] { firstStarted, secondStarted };
        var drained = true;
        try
        {
            for (var index = 0; index < starts.Length; index++)
            {
                var repo = CreateReducedAcceptanceCohortRepository();
                repositories.Add(repo);
                AddAcceptanceManifest(repo);
                var kernel = new AgentOrchestratorKernel();
                var first = CreateCompletedGoal(kernel, $"Workspace {index} first member", repo);
                var second = CreateCompletedGoal(kernel, $"Workspace {index} second member", repo);
                _ = CreateWorktreeCandidate(repo, first.Id,
                    "src/Mcg.AgentOrchestrator.Core/IndependentFirst.cs", "first");
                _ = CreateWorktreeCandidate(repo, second.Id,
                    "tests/IndependentSecond.cs", "second");
                var context = CreateIsolatedCleanupContext(repo);
                roots.Add(context.Hooks.BuildStorageRoot!);
                var verifier = new BlockingAcceptanceVerifier(starts[index], release,
                    new AcceptanceVerificationResult(
                        Passed: true,
                        Skipped: false,
                        ExitCode: 0,
                        OutputTail: null,
                        Checks: [new AcceptanceCheckResult("independent-root-gate", true, 0, null)],
                        TestResultPaths: [WritePassingTrx(repo, "independent-root.trx")]));
                verifiers.Add(verifier);
                var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo),
                    verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                    cleanupHooks: context.Hooks);
                drivers.Add(driver);
                _ = driver.RunAcceptanceCohort(ProjectSelection(driver, first, second),
                    [first, second], ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            }

            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(30)), "First workspace gate did not enter.");
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(30)), "Second workspace gate did not enter.");
            Assert.False(release.IsSet);
            Assert.NotEqual(roots[0].RootPath, roots[1].RootPath);
            for (var index = 0; index < drivers.Count; index++)
            {
                Assert.Equal(1, drivers[index].GetActiveAcceptanceCohortCapacity().ActiveRootCount);
                Assert.True(verifiers[index].StableSlotLeaseObserved);
                Assert.True(roots[index].ContainsPath(verifiers[index].StableSlotRootPath),
                    $"Workspace {index} acquired lease root '{verifiers[index].StableSlotRootPath}' " +
                    $"outside configured root '{roots[index].RootPath}'.");
                Assert.False(roots[1 - index].ContainsPath(verifiers[index].StableSlotRootPath));
            }
        }
        finally
        {
            release.Set();
            foreach (var driver in drivers)
            {
                drained &= SpinWait.SpinUntil(
                    () => driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount == 0,
                    TimeSpan.FromSeconds(30));
            }
            if (drained)
            {
                foreach (var repo in repositories)
                    DeleteDirectory(repo);
            }
        }
        Assert.True(drained, "Independent workspace gate did not drain after release.");
    }

    [Fact]
    public void ProductionBatch_TwoLiveCohortRootsFillSharedAcceptanceCapacity()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-shared-capacity-{Guid.NewGuid():N}.trx");
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        ConductorDriver? driver = null;
        DrainedTeardown teardown;
        try
        {
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 5)
                .Select(index => CreateCompletedGoal(kernel, $"Shared capacity member {index}", repo))
                .ToArray();
            var paths = new[]
            {
                "src/Mcg.AgentOrchestrator.Infrastructure/CapacityFirst.cs",
                "tests/CapacitySecond.cs",
                "src/Mcg.AgentOrchestrator.Core/CapacityThird.cs",
                "tests/CapacityFourth.cs",
                "src/Mcg.AgentOrchestrator.App/CapacityWaiter.cs"
            };
            for (var index = 0; index < goals.Length; index++)
            {
                _ = CreateWorktreeCandidate(repo, goals[index].Id, paths[index], $"capacity-{index}");
            }

            var verifier = new BlockingAcceptanceVerifier(
                gateStarted,
                gateRelease,
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("shared-capacity-gate", true, 0, null)],
                    TestResultPaths: [trx]));
            driver = new ConductorDriver(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            var first = ProjectSelection(driver, goals[0], goals[1]);
            var second = ProjectSelection(driver, goals[2], goals[3]);

            _ = driver.RunAcceptanceCohort(
                first,
                [goals[0], goals[1]],
                ConductorAutonomyPolicy.Permissive,
                runGateInBackground: true);
            _ = driver.RunAcceptanceCohort(
                second,
                [goals[2], goals[3]],
                ConductorAutonomyPolicy.Permissive,
                runGateInBackground: true);
            Assert.True(
                SpinWait.SpinUntil(() => verifier.RunCount == 2, TimeSpan.FromSeconds(10)),
                "Both controlled cohort roots did not enter the verifier.");

            var capacity = driver.GetActiveAcceptanceCohortCapacity();
            Assert.Equal(2, capacity.ActiveRootCount);
            BatchTickSummary? tick = null;
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                Path.Combine(repo, "stop-does-not-exist"),
                maxIterations: 1,
                onTick: current => tick = current);

            Assert.Empty(driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts([goals[4].Id.Value]));
            Assert.Equal(GoalStatus.Verified, goals[4].Status);
            Assert.Contains(tick!.ProgressLines!, line =>
                line.Contains("reason=parallel-acceptance-slot-cap", StringComparison.Ordinal));
        }
        finally
        {
            gateRelease.Set();
            teardown = RemoveAfterObservedDrain(
                () => driver is null || driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount == 0,
                "ActiveRootCount == 0",
                TimeSpan.FromSeconds(10),
                () => { if (File.Exists(trx)) File.Delete(trx); },
                () => DeleteDirectory(repo));
        }
        teardown.AssertDrained();
    }

    [Fact]
    public void ProductionBatch_OrdinaryParallelAcceptanceStillStartsPromptlyAndReconcilesLater()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var attemptRoot = Path.Combine(repo, ".orchestrator", "ordinary-negative-control");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Ordinary acceptance negative control", repo);
            ConductorParallelAcceptanceOwnedProcessLaunch? launch = null;
            const int ownerProcessId = 8123;
            var landed = false;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: pending =>
                {
                    launch = pending;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(ownerProcessId);
                },
                acquireStableSlotLease: (_, _) => null);
            var driver = new ConductorDriver(
                getFacts: _ => landed
                    ? new GoalLifecycleFacts(
                        WorkspaceExists: true,
                        IsMerged: true,
                        IsRecorded: true,
                        IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningPaidWorkerCount: () => 0,
                createWorkspace: _ => repo,
                dispatchAndStart: _ => DispatchStartOutcome.Started(),
                startRecordedDispatches: null,
                buildServerShutdown: null,
                runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                runAdvisorySemanticAcceptance: null,
                retryTask: null,
                recordTaskNote: null,
                recordCriterionRetryFeedback: null,
                clearCriterionRetryFeedback: null,
                rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                    GoalWorktrees.BranchName(goal.Id),
                    "current",
                    [],
                    null),
                land: (candidate, _) =>
                {
                    landed = true;
                    return new LandingResult(
                        candidate.Id.Value,
                        candidate.Id.Value[..8],
                        new LandingDecision.Promote(),
                        LandingExecutor.IntegrationBranchName,
                        MainAdvanced: true,
                        "landed");
                },
                afterSuccessfulLanding: null,
                record: _ => { },
                cleanup: _ => new GoalWorktreeRemoveResult("clean", null, [], null),
                writeEscalation: (_, _, _) => { },
                classifyChangeRisk: _ => ChangeRiskTier.Behavior,
                getLandingFileScopes: _ => ["src/Ordinary.cs"],
                runAcceptanceVerificationWithSlot: (_, _) =>
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                parallelAcceptanceAttemptCoordinator: coordinator);
            var stopPath = Path.Combine(repo, "stop-does-not-exist");
            BatchTickSummary? startTick = null;

            var started = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                stopPath,
                maxIterations: 1,
                onTick: tick => startTick = tick);

            Assert.Equal(0, started.Advanced);
            Assert.Equal(1, started.Held);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.NotNull(launch);
            Assert.False(driver.TryGetCohortGateHold(goal.Id, out _));
            Assert.Contains(startTick!.ProgressLines!, line =>
                line.Contains("ACCEPTANCE", StringComparison.Ordinal) &&
                line.Contains("result=started", StringComparison.Ordinal));
            launch.ExecuteInCurrentProcess(ownerProcessId);

            BatchTickSummary? reconcileTick = null;
            var completed = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                stopPath,
                maxIterations: 1,
                onTick: tick => reconcileTick = tick);

            Assert.Equal(1, completed.Advanced);
            Assert.True(landed);
            Assert.Contains(reconcileTick!.ProgressLines!, line =>
                line.Contains("ACCEPTANCE", StringComparison.Ordinal) &&
                line.Contains("result=passed", StringComparison.Ordinal));
            Assert.Empty(coordinator.GetUnreconciledAttempts([goal.Id.Value]));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void CohortStableSlotLease_RefusesToExceedTrustedHostGateCap()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-slot-cap-{Guid.NewGuid():N}");
        var storageRoot = new DotnetBuildStorageRoot(root);
        try
        {
            using var first = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(0, TimeSpan.Zero, storageRoot: storageRoot)).Lease;
            using var second = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(1, TimeSpan.Zero, storageRoot: storageRoot)).Lease;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                runInline: true, buildStorageRoot: storageRoot);

            Assert.Throws<DotnetBuildSlotsBusyException>(() =>
                coordinator.AcquireCohortStableSlotLease("cohort-v2-slot-cap", timeout: TimeSpan.Zero));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

}
