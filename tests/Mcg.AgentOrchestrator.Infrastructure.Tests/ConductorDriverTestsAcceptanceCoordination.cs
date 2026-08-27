using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Xml.Linq;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed partial class ConductorDriverTestsAcceptanceCoordination
{
    private static string CreateTempDirectory() => ConductorDriverTests.CreateTempDirectory();

    // ── Empty-batch escalation diagnostics ───────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_named_failure_normalization_preserves_baseline_attribution")]
    public void ConductorDriverNamedFailureNormalizationPreservesBaselineAttribution()
    {
        var testResultPaths = new[] { "C:\\tmp\\focused.trx" };
        var attributions = new[]
        {
            new AcceptanceCheckAttribution(
                "core tests",
                AcceptanceFailureOrigin.Inherited,
                "also failed on main")
        };
        var acceptance = new AcceptanceVerificationSummary(
            false,
            [],
            "core tests failed",
            ["core tests"],
            "branch-a",
            "main-a",
            testResultPaths,
            attributions,
            "attested-red");

        var normalized = ConductorDriver.NormalizeNamedFailedChecksForRetry(acceptance);

        Assert.Equal(testResultPaths, normalized.TestResultPaths);
        Assert.Equal(attributions, normalized.CheckAttributions);
        Assert.Equal("attested-red", normalized.BaselineAttestation);
        Assert.Null(Assert.Single(normalized.RequiredUnmetCriteria).FailureClassification);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_all_inherited_red_gets_typed_apparatus_cause")]
    public void ConductorDriverAllInheritedRedGetsTypedApparatusCause()
    {
        var acceptance = new AcceptanceVerificationSummary(
            false,
            [new AcceptanceCheckResult("infrastructure tests: Remainder", false, 1, "red")],
            FailedChecks: ["infrastructure tests: Remainder"],
            BranchHeadSha: "candidate-a",
            MainHeadSha: "main-a",
            CheckAttributions:
            [
                new AcceptanceCheckAttribution(
                    "infrastructure tests: Remainder",
                    AcceptanceFailureOrigin.Inherited,
                    "typed process-output apparatus receipt",
                    AcceptanceFailureCause.EnvironmentalApparatus)
            ],
            BaselineAttestation: "attested-red");

        var classified = ConductorDriver.ClassifyInheritedBaselineApparatus(acceptance);

        Assert.Equal(
            AcceptanceFailureClassifications.InheritedBaselineApparatus,
            Assert.Single(classified.RequiredUnmetCriteria).FailureClassification);
        Assert.Equal(
            AcceptanceFailureCause.EnvironmentalApparatus,
            Assert.Single(classified.CheckAttributions!).Cause);
        Assert.Equal("candidate-a", classified.BranchHeadSha);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_real_baseline_attribution_consumes_typed_acceptance_receipt")]
    public void ConductorDriverRealBaselineAttributionConsumesTypedAcceptanceReceipt()
    {
        var current = GoalId.New();
        var first = GoalId.New();
        var second = GoalId.New();
        GoalOperationJournalEntry Entry(GoalId goalId) => new(
            Guid.NewGuid().ToString("N"),
            goalId,
            "conductor:acceptance",
            GoalOperationStatus.Failed,
            DateTimeOffset.UtcNow,
            "receipt",
            MainHeadSha: "main-a",
            AcceptanceOutcome: "failed",
            FailedCheckNames: ["infrastructure tests: Remainder"]);
        var firstEntry = Entry(first);
        var secondEntry = Entry(second);
        var journals = new Dictionary<GoalId, GoalOperationJournalSummary>
        {
            [first] = new("first", [firstEntry], [firstEntry], []),
            [second] = new("second", [secondEntry], [secondEntry], [])
        };
        var failedCheck = new AcceptanceCheckResult(
            "infrastructure tests: Remainder",
            false,
            1,
            "diagnostic text is not consulted",
            FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference);
        failedCheck = GoalAcceptanceVerifier.AttachFailureCauseEvidence(failedCheck);

        var baseline = CleanTestBaseline.Resolve(journals, current, "main-a", null);
        var attributions = CleanTestBaseline.Attribute(
            baseline,
            [failedCheck.Name],
            journals,
            current,
            "main-a",
            [failedCheck]);
        var classified = ConductorDriver.ClassifyInheritedBaselineApparatus(
            new AcceptanceVerificationSummary(
                false,
                [failedCheck],
                FailedChecks: [failedCheck.Name],
                BranchHeadSha: "candidate-a",
                MainHeadSha: "main-a",
                CheckAttributions: attributions,
                BaselineAttestation: CleanTestBaseline.FormatFailureAttestation(baseline)));

        var attribution = Assert.Single(classified.CheckAttributions!);
        Assert.Equal(AcceptanceFailureOrigin.Inherited, attribution.Origin);
        Assert.Equal(AcceptanceFailureCause.EnvironmentalApparatus, attribution.Cause);
        Assert.Equal(failedCheck.Name, failedCheck.FailureCauseEvidence?.CheckName);
        Assert.Equal(
            AcceptanceFailureClassifications.GateEnvironmentInterference,
            failedCheck.FailureCauseEvidence?.SourceClassification);
        Assert.Equal(
            AcceptanceFailureClassifications.InheritedBaselineApparatus,
            Assert.Single(classified.RequiredUnmetCriteria).FailureClassification);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_all_inherited_red_without_typed_cause_stays_unclassified")]
    public void ConductorDriverAllInheritedRedWithoutTypedCauseStaysUnclassified()
    {
        var acceptance = new AcceptanceVerificationSummary(
            false,
            [new AcceptanceCheckResult("infrastructure tests: Remainder", false, 1, "red")],
            FailedChecks: ["infrastructure tests: Remainder"],
            CheckAttributions:
            [
                new AcceptanceCheckAttribution(
                    "infrastructure tests: Remainder",
                    AcceptanceFailureOrigin.Inherited,
                    "same check failed on main")
            ]);

        var classified = ConductorDriver.ClassifyInheritedBaselineApparatus(acceptance);

        Assert.Null(Assert.Single(classified.RequiredUnmetCriteria).FailureClassification);
        Assert.Equal(
            AcceptanceFailureCause.NotClassified,
            Assert.Single(classified.CheckAttributions!).Cause);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_mixed_red_does_not_get_apparatus_classification")]
    public void ConductorDriverMixedRedDoesNotGetApparatusClassification()
    {
        var acceptance = new AcceptanceVerificationSummary(
            false,
            [
                new AcceptanceCheckResult("inherited", false, 1, "red"),
                new AcceptanceCheckResult("introduced", false, 1, "red")
            ],
            FailedChecks: ["inherited", "introduced"],
            CheckAttributions:
            [
                new AcceptanceCheckAttribution("inherited", AcceptanceFailureOrigin.Inherited, "baseline"),
                new AcceptanceCheckAttribution("introduced", AcceptanceFailureOrigin.Introduced, "green baseline")
            ]);

        var classified = ConductorDriver.ClassifyInheritedBaselineApparatus(acceptance);

        Assert.All(classified.RequiredUnmetCriteria, check => Assert.Null(check.FailureClassification));
        Assert.All(classified.CheckAttributions!, attribution =>
            Assert.Equal(AcceptanceFailureCause.NotClassified, attribution.Cause));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_real_facts_match_per_goal_read_path")]
    public void ConductorDriverRealFactsMatchPerGoalReadPath()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var merged = kernel.CreateGoal("Merged goal");
        var cleaned = kernel.CreateGoal("Cleaned goal");
        var missing = kernel.CreateGoal("Missing journal goal");
        var clarified = kernel.CreateGoal("Clarified goal");

        var worktreePath = GoalWorktrees.WorktreePath(root, merged.Id);
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: test");
        GoalOperationJournal.Completed(root, merged, "conductor:land", "landed");
        GoalOperationJournal.Completed(root, merged, "conductor:record", "recorded");
        GoalOperationJournal.Completed(root, cleaned, "conductor:cleanup", "cleaned");
        CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .RaiseAsync(
                CollaborationItemType.Clarification,
                clarified.Id.Value,
                "clarify",
                "body",
                $"spec-clarification:{clarified.Id.Value}:test")
            .GetAwaiter()
            .GetResult();

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        foreach (var goal in new[] { merged, cleaned, missing, clarified })
        {
            Assert.Equal(ReadFactsPerGoal(workspace, goal), driver.GetFacts(goal));
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_construction_does_not_eagerly_read_goal_journals")]
    public void ConductorDriverConstructionDoesNotEagerlyReadGoalJournals()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var queried = kernel.CreateGoal("Queried journal goal");
        var unqueried = kernel.CreateGoal("Unqueried locked journal goal");
        GoalOperationJournal.Completed(root, unqueried, "conductor:land", "landed");

        var unqueriedJournalPath = GoalOperationJournal.PathFor(root, unqueried.Id);
        using var exclusiveJournalLock = new FileStream(
            unqueriedJournalPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        Assert.Equal(ReadFactsPerGoal(workspace, queried), driver.GetFacts(queried));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_real_dispatch_checkpoint_rolls_back_then_notifies_on_success")]
    public void ConductorDriverRealDispatchCheckpointRollsBackThenNotifiesOnSuccess()
    {
        var root = CreateTempDirectory();
        SeedLocalSkillCatalog(root);
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");

        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var (kernel, goal) = SimpleGoal("Real dispatch checkpoint rollback");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Dispatch checkpoint rollback fixture is already refined.",
            ["The ready task reaches the real dispatch checkpoint."],
            VerificationClass.TestVerifiable,
            [],
            []));
        GoalWorktrees.Ensure(root, goal.Id);
        var before = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id));
        var persistAttempts = 0;
        var successfulCheckpointNotifications = 0;
        var profiles = WorkerProfileCatalog.Default();
        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            profiles,
            persistCriticalDispatchStart: (_, _) =>
            {
                persistAttempts++;
                if (persistAttempts == 1)
                    throw new SqliteException("injected busy checkpoint", 5);
            });
        driver.DispatchRecordWriteSucceededSink = _ => successfulCheckpointNotifications++;

        var ex = Assert.Throws<DispatchRecordWriteException>(() =>
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative));

        Assert.False(ex.ProcessMayHaveStarted);
        Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id)));
        Assert.Null(kernel.GetGoal(goal.Id).Tasks.Single().LastProcess);
        Assert.Equal(0, successfulCheckpointNotifications);

        var result = driver.AdvanceOnce(kernel.GetGoal(goal.Id), ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(4, persistAttempts);
        Assert.Equal(3, successfulCheckpointNotifications);
        Assert.NotNull(kernel.GetGoal(goal.Id).Tasks.Single().LastProcess);
    }

    [Xunit.Fact]
    public void ConductorDriverReplacementLeaseBlocksWorkspaceEvidenceMutation()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var (kernel, goal) = SimpleGoal("Lease-protected workspace creation");
        using var replacementLease = new ReconcileSweepRemediationStore(workspace.SqliteStatePath)
            .TryAcquireAcceptanceLease(
                goal.Id.Value,
                $"goal-replace:test:{Guid.NewGuid():N}",
                TimeSpan.FromMinutes(30));
        Assert.NotNull(replacementLease);
        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Created, held.State);
        Assert.Contains("reason=concurrent-acceptance-or-replacement", held.Reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(GoalWorktrees.WorktreePath(root, goal.Id)));
        Assert.Empty(GoalOperationJournal.Read(root, goal.Id).Entries);
    }

    [Xunit.Fact]
    public void ConductorDriverReplacementLeaseBlocksDispatchEvidenceMutation()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var (kernel, goal) = SimpleGoal("Lease-protected dispatch");
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: test");
        using var replacementLease = new ReconcileSweepRemediationStore(workspace.SqliteStatePath)
            .TryAcquireAcceptanceLease(
                goal.Id.Value,
                $"goal-replace:test:{Guid.NewGuid():N}",
                TimeSpan.FromMinutes(30));
        Assert.NotNull(replacementLease);
        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());
        var before = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id));
        var phaseTimings = new List<string>();
        driver.PhaseTimingSink = phaseTimings.Add;

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("blocked by concurrent acceptance or replacement", held.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(phaseTimings, timing => timing.Contains("phase=dispatch-remediation", StringComparison.Ordinal));
        Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id)));
        Assert.All(goal.Tasks, task => Assert.Null(task.LastDispatch));
        Assert.DoesNotContain(
            GoalOperationJournal.Read(root, goal.Id).Entries,
            entry => entry.Operation.StartsWith("conductor:dispatch", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ConductorDriverReplacementLeaseBlocksAcceptanceAndLandingEvidenceMutation()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");

        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Lease-protected conductor acceptance and landing");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "goal work");
        RunGit(worktree, "add", "feature.txt");
        RunGit(worktree, "commit", "-m", "goal work");
        var leaseOwner = $"goal-replace:test:{Guid.NewGuid():N}";
        using var replacementLease = new ReconcileSweepRemediationStore(workspace.SqliteStatePath)
            .TryAcquireAcceptanceLease(
                goal.Id.Value,
                leaseOwner,
                TimeSpan.FromMinutes(30));
        Assert.NotNull(replacementLease);
        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("acceptance lease", held.Reason, StringComparison.Ordinal);
        Assert.Contains($"owner={leaseOwner}", held.Reason, StringComparison.Ordinal);
        Assert.Contains("expiresAtUtc=", held.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(
            GoalOperationJournal.Read(root, goal.Id).Entries,
            entry => entry.Operation.StartsWith("conductor:acceptance", StringComparison.Ordinal) ||
                entry.Operation.StartsWith("conductor:land", StringComparison.Ordinal));
        Assert.NotEqual(
            0,
            GitCli.Run(root, "merge-base", "--is-ancestor", GoalWorktrees.BranchName(goal.Id), "main").ExitCode);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_real_facts_refresh_after_conductor_record")]
    public async Task ConductorDriverRealFactsRefreshAfterConductorRecord()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Record refresh goal");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: test");
        GoalOperationJournal.Completed(root, goal, "conductor:land", "landed");

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Merged, executed.FromState);
        Assert.True(driver.GetFacts(goal).IsRecorded);
        Assert.Equal(ReadFactsPerGoal(workspace, goal), driver.GetFacts(goal));
        var record = await new DogfoodLogStore(workspace.DogfoodLogStorePath)
            .GetByGoalIdAsync(goal.Id.Value);
        Assert.NotNull(record);
        Assert.Contains("Record refresh goal", record!.RenderedMarkdown);
        Assert.False(File.Exists(Path.Combine(root, "DOGFOOD_LOG.md")));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_AwaitingClarification_surfaces_stale_recovery_action")]
    public async Task ConductorDriverAwaitingClarificationSurfacesStaleRecoveryAction()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var (kernel, goal) = SimpleGoal("Conductor stale clarification goal.");
        var key = $"{GoalRefinementService.CorrelationKeyPrefix}{goal.Id.Value}:api-version";
        await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Spec clarification needed: Which API version?",
            "Question: Which API version?\nFork kind: external-contract",
            key);
        Assert.True(await store.TryResolveAsync(key, "REST v2"));
        await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Spec clarification needed: Which API version?",
            "Question: Which API version?\nFork kind: external-contract",
            key);
        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(GoalLifecycleState.AwaitingClarification, escalated.State);
        Assert.Contains("Stale spec clarification detected", escalated.Reason, StringComparison.Ordinal);
        Assert.Contains("api-version", escalated.Reason, StringComparison.Ordinal);
        Assert.Contains($"attention dismiss {goal.Id.Value[..8]}", escalated.Reason, StringComparison.Ordinal);
        var eventsPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
        var events = File.ReadAllText(eventsPath);
        Assert.Contains("\"eventType\":\"StaleClarificationDetected\"", events, StringComparison.Ordinal);
        Assert.Contains("\"recoveryCommand\":\"attention dismiss ", events, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_acceptance_slots_busy_journals_blocked_outcome")]
    public void ConductorDriverAcceptanceSlotsBusyJournalsBlockedOutcome()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");

        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Slots busy conductor goal");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "goal work");
        RunGit(worktree, "add", "feature.txt");
        RunGit(worktree, "commit", "-m", "goal work");
        var busy = new DotnetBuildLeaseAcquisition.SlotsBusy(
            "goal-slots-busy",
            [new DotnetBuildStableSlotWait(0, 12345)]);
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(root, ".orchestrator", "test-acceptance-attempts"),
            runInline: true,
            acquireStableSlotLease: (_, _) => null);

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new ThrowingAcceptanceVerifier(new DotnetBuildSlotsBusyException(busy)),
            DefaultAgents(),
            WorkerProfileCatalog.Default(),
            coordinator);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("slots busy", held.Reason, StringComparison.OrdinalIgnoreCase);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        var blockedOutcome = journal.Entries.LastOrDefault(entry => entry.AcceptanceOutcome == "blocked:slot-unavailable");
        Assert.NotNull(blockedOutcome);
        Assert.Equal(GoalOperationStatus.Failed, blockedOutcome.Status);
        Assert.False(string.IsNullOrWhiteSpace(blockedOutcome.BranchHeadSha));
        Assert.False(string.IsNullOrWhiteSpace(blockedOutcome.MainHeadSha));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_acceptance_cancellation_probe_allows_current_attempt_over_lagging_Active_row")]
    public async Task ConductorDriverAcceptanceCancellationProbeAllowsCurrentAttemptOverLaggingActiveRow()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                DefaultAgents(),
                "Allow current acceptance while persistence catches up");
            var task = goal.Tasks.Single();

            await repository.SaveAsync(kernel);
            PassVerification(kernel, goal, task);
            var coordinator = SeedLiveAcceptanceAttempt(root, goal);

            Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
            Assert.Equal(GoalStatus.Active, (await repository.LoadGoalAsync(goal.Id))?.Status);
            Assert.False(ConductorDriver.IsAcceptanceAttemptCancelled(
                workspace,
                goal.Id,
                attemptInvalidationRecorded: () =>
                    coordinator.TryGetLiveInvalidatedAttempt(goal.Id.Value, out _)));

            await repository.SaveAsync(kernel);
            Assert.False(ConductorDriver.IsAcceptanceAttemptCancelled(workspace, goal.Id));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Theory(DisplayName = "ConductorDriver_acceptance_cancellation_decision_stops_every_stopped_disposition")]
    [Xunit.InlineData(GoalStatus.Parked)]
    [Xunit.InlineData(GoalStatus.Cancelled)]
    [Xunit.InlineData(GoalStatus.Superseded)]
    [Xunit.InlineData(GoalStatus.Failed)]
    public void ConductorDriverAcceptanceCancellationDecisionStopsEveryStoppedDisposition(GoalStatus status)
    {
        var decision = AcceptanceAttemptCancellation.Decide(
            status,
            goalRecordReadable: true,
            attemptInvalidationRecorded: false);

        Assert.True(decision.ShouldCancel);
        Assert.Equal(AcceptanceAttemptCancellationCause.StoppedDisposition, decision.Cause);
        Assert.Equal(status, decision.ObservedStatus);
    }

    [Xunit.Theory(DisplayName = "ConductorDriver_acceptance_cancellation_decision_allows_transitional_dispositions")]
    [Xunit.InlineData(GoalStatus.Active)]
    [Xunit.InlineData(GoalStatus.AcceptanceFailed)]
    public void ConductorDriverAcceptanceCancellationDecisionAllowsTransitionalDispositions(GoalStatus status)
    {
        var decision = AcceptanceAttemptCancellation.Decide(
            status,
            goalRecordReadable: true,
            attemptInvalidationRecorded: false);

        Assert.False(decision.ShouldCancel);
        Assert.Equal(AcceptanceAttemptCancellationCause.None, decision.Cause);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_acceptance_cancellation_decision_census_has_only_stopped_dispositions")]
    public void ConductorDriverAcceptanceCancellationDecisionCensusHasOnlyStoppedDispositions()
    {
        var stopped = new HashSet<GoalStatus>
        {
            GoalStatus.Parked,
            GoalStatus.Cancelled,
            GoalStatus.Superseded,
            GoalStatus.Failed
        };

        foreach (var status in Enum.GetValues<GoalStatus>())
        {
            var decision = AcceptanceAttemptCancellation.Decide(
                status,
                goalRecordReadable: true,
                attemptInvalidationRecorded: false);

            Assert.Equal(stopped.Contains(status), decision.ShouldCancel);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_acceptance_cancellation_probe_stops_recorded_invalidation_and_Parked_row")]
    public async Task ConductorDriverAcceptanceCancellationProbeStopsRecordedInvalidationAndParkedRow()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                DefaultAgents(),
                "Stop invalidated acceptance before retry dispatch");
            await repository.SaveAsync(kernel);
            var coordinator = SeedLiveAcceptanceAttempt(root, goal);
            Assert.True(coordinator.InvalidateCurrent(
                goal.Id.Value,
                "Retry invalidated the running acceptance attempt."));

            var cancelled = ConductorDriver.IsAcceptanceAttemptCancelled(
                workspace,
                goal.Id,
                attemptInvalidationRecorded: () =>
                    coordinator.TryGetLiveInvalidatedAttempt(goal.Id.Value, out _));

            Assert.True(cancelled);

            kernel.ParkGoal(goal.Id, "Operator deliberately stopped this goal.");
            await repository.SaveAsync(kernel);
            Assert.True(ConductorDriver.IsAcceptanceAttemptCancelled(
                workspace,
                goal.Id,
                attemptInvalidationRecorded: () => false));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_acceptance_cancellation_probe_fails_closed_for_missing_or_unreadable_goal")]
    public void ConductorDriverAcceptanceCancellationProbeFailsClosedForMissingOrUnreadableGoal()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var goalId = GoalId.New();
            var loadAttempts = 0;

            var missing = ConductorDriver.GetAcceptanceAttemptCancellationDecision(
                workspace,
                goalId,
                loadGoalStatus: _ => null);
            var unreadable = ConductorDriver.GetAcceptanceAttemptCancellationDecision(
                workspace,
                goalId,
                loadGoalStatus: _ =>
                {
                    loadAttempts++;
                    throw new InvalidOperationException("state unavailable");
                });
            var activeGoal = new AgentOrchestratorKernel();
            var current = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                activeGoal,
                DefaultAgents(),
                "Ignore supplementary attempt metadata read failure");
            var metadataUnreadable = ConductorDriver.GetAcceptanceAttemptCancellationDecision(
                workspace,
                current.Id,
                loadGoalStatus: _ => current.Status,
                attemptInvalidationRecorded: () => throw new IOException("attempt metadata unavailable"));

            Assert.True(missing.ShouldCancel);
            Assert.Equal(AcceptanceAttemptCancellationCause.GoalRecordMissing, missing.Cause);
            Assert.True(unreadable.ShouldCancel);
            Assert.Equal(AcceptanceAttemptCancellationCause.GoalRecordUnreadable, unreadable.Cause);
            Assert.Equal(3, loadAttempts);
            Assert.False(metadataUnreadable.ShouldCancel);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver composes gate-ready input before invoking the projector")]
    public void ProjectGateReadyCandidateComposesSnapshotInOrder()
    {
        const string branchRevision = "1111111111111111111111111111111111111111";
        const string mainRevision = "2222222222222222222222222222222222222222";
        var (kernel, goal) = SimpleGoal("Gate-ready composition");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var calls = new List<string>();
        var projector = new GateReadyCandidateProjector(
            goalId =>
            {
                calls.Add("revisions");
                Assert.Equal(goal.Id, goalId);
                return new GateReadyCandidateRevisionPair(branchRevision, mainRevision);
            },
            goalId =>
            {
                calls.Add("scope");
                Assert.Equal(goal.Id, goalId);
                return new GateReadyLandingScopeObservation(
                    Succeeded: true,
                    Files: ["src/Mcg.AgentOrchestrator.Core/Feature.cs"]);
            },
            (goalId, observedBranch, observedMain) =>
            {
                calls.Add("merge");
                Assert.Equal(goal.Id, goalId);
                Assert.Equal(branchRevision, observedBranch);
                Assert.Equal(mainRevision, observedMain);
                return new GateReadyMergeTreeObservation(IsClean: true);
            });
        var driver = MakeDriver(
            getFacts: _ =>
            {
                calls.Add("lifecycle");
                return GoalLifecycleFacts.None;
            },
            classifyRisk: _ =>
            {
                calls.Add("risk");
                return ChangeRiskTier.DocsOnly;
            },
            isVerificationGateSatisfied: _ =>
            {
                calls.Add("gate");
                return true;
            },
            gateReadyCandidateProjector: projector);

        var result = driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Conservative);

        var projection = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(result).Projection;
        Assert.Equal(goal.Id, projection.GoalId);
        Assert.Equal(GoalLifecycleState.Verified, projection.LifecycleState);
        Assert.Equal(GateReadyVerificationState.Satisfied, projection.VerificationState);
        Assert.Equal(ChangeRiskTier.DocsOnly, projection.ChangeRiskTier);
        Assert.Equal(ConductorTransitionDecision.Auto, projection.AutoPromotionDisposition);
        Assert.Equal(branchRevision, projection.BranchRevision);
        Assert.Equal(mainRevision, projection.MainRevision);
        Assert.Equal(
            ["lifecycle", "gate", "risk", "revisions", "scope", "merge", "revisions"],
            calls);
    }

    [Xunit.Theory(DisplayName = "ConductorDriver maps gate-ready snapshot delegate failures to typed exclusions")]
    [Xunit.InlineData("lifecycle", (int)GateReadyCandidateExclusionReason.LifecycleNotReady, 0)]
    [Xunit.InlineData("gate", (int)GateReadyCandidateExclusionReason.GateNotReady, 0)]
    [Xunit.InlineData("risk", (int)GateReadyCandidateExclusionReason.RiskUnknown, 1)]
    public void ProjectGateReadyCandidateMapsSnapshotDelegateFailures(
        string failingDelegate,
        int expectedReasonValue,
        int expectedRevisionReads)
    {
        var (kernel, goal) = SimpleGoal("Gate-ready delegate failure");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var revisionReads = 0;
        var projector = new GateReadyCandidateProjector(
            _ =>
            {
                revisionReads++;
                return new GateReadyCandidateRevisionPair(
                    "1111111111111111111111111111111111111111",
                    "2222222222222222222222222222222222222222");
            },
            _ => throw new InvalidOperationException("Scope should not be read."),
            (_, _, _) => throw new InvalidOperationException("Merge status should not be read."));
        var driver = MakeDriver(
            getFacts: _ => failingDelegate == "lifecycle"
                ? throw new InvalidOperationException("Lifecycle unavailable.")
                : GoalLifecycleFacts.None,
            classifyRisk: _ => failingDelegate == "risk"
                ? throw new InvalidOperationException("Risk unavailable.")
                : ChangeRiskTier.DocsOnly,
            isVerificationGateSatisfied: _ => failingDelegate == "gate"
                ? throw new InvalidOperationException("Gate unavailable.")
                : true,
            gateReadyCandidateProjector: projector);

        var result = driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(
            (GateReadyCandidateExclusionReason)expectedReasonValue,
            Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(result).Reason);
        Assert.Equal(expectedRevisionReads, revisionReads);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver fails closed when the gate-ready projector is absent")]
    public void ProjectGateReadyCandidateWithoutProjectorReturnsRevisionUnknown()
    {
        var (kernel, goal) = SimpleGoal("Gate-ready projector missing");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: null);

        var result = driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(
            GateReadyCandidateExclusionReason.RevisionUnknown,
            Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(result).Reason);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_acceptance_slot_path_skips_already_merged_branch_before_lease")]
    public void ConductorDriverAcceptanceSlotPathSkipsAlreadyMergedBranchBeforeLease()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");

        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Already merged gate skip");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        File.WriteAllText(Path.Combine(worktree, "merged-before-gate.txt"), "goal work");
        RunGit(worktree, "add", "merged-before-gate.txt");
        RunGit(worktree, "commit", "-m", "goal work");
        var branchTip = GitCli.Run(worktree, "rev-parse", "HEAD").Output.Trim();
        RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());
        var candidate = driver.TryBuildParallelAcceptanceCandidate(goal, ConductorAutonomyPolicy.Conservative, 0);
        Assert.NotNull(candidate);
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(root, ".orchestrator", "test-acceptance-attempts"),
            root,
            runInline: true,
            tryRunPreSlot: driver.RunParallelLandingAcceptancePreSlot);
        var acceptanceRan = false;

        var decision = coordinator.Evaluate(
            candidate!,
            ConductorAutonomyPolicy.Conservative,
            (_, _, lease, _) =>
            {
                acceptanceRan = true;
                Assert.NotNull(lease);
                return ConductorParallelAcceptanceRunResult.Accepted(
                    candidate!,
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria);
            });

        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, decision.Kind);
        Assert.False(acceptanceRan);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, decision.Attempt.Outcome);
        Assert.Empty(decision.Attempt.LeaseReceipts ?? []);
        Assert.NotNull(decision.Run?.EarlyResult);
        Assert.Equal("skip-already-merged", decision.Run!.EarlyOutcome?.Kind);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        var skip = Assert.Single(journal.Entries.Where(entry => entry.AcceptanceOutcome == "skip-already-merged"));
        Assert.Equal(GoalOperationStatus.Skipped, skip.Status);
        Assert.Contains($"mergeCommitSha={branchTip}", skip.Detail, StringComparison.Ordinal);

        GoalOperationJournal.RecordLandingIntent(
            root,
            goal,
            GoalWorktrees.BranchName(goal.Id),
            LandingExecutor.IntegrationBranchName,
            branchTip,
            "test");
        var secondSkip = driver.RunParallelLandingAcceptancePreSlot(candidate!, ConductorAutonomyPolicy.Conservative);
        Assert.NotNull(secondSkip);
        Assert.Equal("skip-already-merged", secondSkip!.EarlyOutcome?.Kind);
    }

    [Xunit.Fact]
    public void ParallelAcceptanceCleanupFailureCannotReplaceAcceptedResult()
    {
        var (_, goal) = SimpleGoal("Acceptance cleanup is non-dispositive");
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs"],
            "branch-sha",
            "main-sha");
        var accepted = new AcceptanceVerificationSummary(
            true,
            [],
            BranchHeadSha: "branch-sha",
            MainHeadSha: "main-sha");
        var driver = MakeDriver(
            runAcceptanceSummary: _ => accepted,
            tryAcquireEvidenceMutationLease: (_, _) => new ThrowingDisposable());

        var result = driver.RunParallelLandingAcceptance(
            candidate,
            ConductorAutonomyPolicy.Conservative,
            stableSlotLease: null,
            CancellationToken.None);

        Assert.Null(result.Exception);
        Assert.Same(accepted, result.Acceptance);
        Assert.True(result.Acceptance!.Passed);
    }

    [Xunit.Fact]
    public void PreReviewAttempt_FocusedKind_SkipsLandingPreSlot()
    {
        var root = CreateTempDirectory();
        try
        {
            var (_, goal) = SoftwareGoal();
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                slotIndex: 0,
                fileScopes: [],
                branchHeadSha: "focused-sha",
                mainHeadSha: null);
            var preSlotRuns = 0;
            var focusedRuns = 0;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                runInline: true,
                tryRunPreSlot: (_, _) =>
                {
                    preSlotRuns++;
                    return ConductorParallelAcceptanceRunResult.Early(
                        candidate,
                        new ConductorAdvanceResult(
                            goal.Id.Value,
                            goal.Id.Value[..8],
                            ConductorAutonomyPolicy.Permissive.Name,
                            new ConductorAdvanceOutcome.Done(GoalLifecycleState.Verified)));
                },
                acquireStableSlotLease: (_, _) => null);

            var decision = coordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "Infrastructure.Tests: FocusedTests",
                (_, request, _, _) =>
                {
                    focusedRuns++;
                    return PassingPreReviewEvidence(request);
                });

            Assert.Equal(0, preSlotRuns);
            Assert.Equal(1, focusedRuns);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, decision.Kind);
            Assert.NotNull(decision.Run?.FocusedEvidence);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_batch_surfaces_operator_approval_reasons")]
    public void ConductorDriverEmptyBatchSurfacesOperatorApprovalReasons()
    {
        var plan = new ParallelExecutionPlan(
            [],
            [
                new ParallelExecutionDecision(
                    "task-1",
                    ParallelExecutionDisposition.RequiresOperatorApproval,
                    null,
                    ["high-risk ownership area requires operator approval: Script scripts/Invoke-TestSummary.ps1"])
            ]);

        var reason = ConductorDriver.DescribeEmptyBatch(plan);

        Assert.True(reason.Contains("require operator approval", StringComparison.OrdinalIgnoreCase));
        Assert.True(reason.Contains("scripts/Invoke-TestSummary.ps1", StringComparison.Ordinal));
        Assert.False(reason.Contains("no assigned or ready tasks", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_batch_surfaces_ready_blocked_diagnostics_before_generic_reason")]
    public void ConductorDriverEmptyBatchSurfacesReadyBlockedDiagnosticsBeforeGenericReason()
    {
        var taskId = TaskId.New().Value;
        var plan = new ParallelExecutionPlan([], []);
        var diagnostic = new ReadyBlockedDiagnostic(
            "abc12345",
            1,
            taskId,
            "codex-cli",
            "dirty-worktree",
            ["blocked: worktree has 1 uncommitted change(s) before dispatch; paths=[src/dirty.cs]"]);

        var reason = ConductorDriver.DescribeEmptyBatch(plan, [diagnostic]);

        Assert.Contains("assigned tasks were excluded", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(taskId, reason, StringComparison.Ordinal);
        Assert.Contains("codex-cli", reason, StringComparison.Ordinal);
        Assert.Contains("dirty-worktree", reason, StringComparison.Ordinal);
        Assert.Contains("worktree has 1 uncommitted change", reason, StringComparison.Ordinal);
        Assert.Contains("src/dirty.cs", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("no assigned or ready tasks", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_batch_without_approval_blocks_uses_generic_reason")]
    public void ConductorDriverEmptyBatchWithoutApprovalBlocksUsesGenericReason()
    {
        var plan = new ParallelExecutionPlan([], []);

        var reason = ConductorDriver.DescribeEmptyBatch(plan);

        Assert.True(reason.Contains("no assigned or ready tasks", StringComparison.Ordinal));
    }

}
