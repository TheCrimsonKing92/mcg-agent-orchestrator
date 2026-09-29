using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceCohortWorkflowTestsGroupedGateAttempts : AcceptanceCohortWorkflowTests
{
    private const int FirstGeneration = 91001;
    private const int SecondGeneration = 91002;
    private const int FirstChild = 92001;
    private const int SecondChild = 92002;

    [Fact]
    public void ReconciledLaunchCannotBeClaimedByLateChild()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grouped-claim-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(root, "attempt", "attempt.attempt.json");
            var record = new ConductorGroupedGateAttempt(
                "attempt", "cohort", [], new string('a', 40), new string('b', 40),
                "manifest", "identity", DateTimeOffset.UnixEpoch, 0, FirstGeneration,
                path, path + ".result", path + ".exit", path + ".out", path + ".err",
                root, ConductorAutonomyPolicy.Conservative.ToJson());
            ConductorGroupedGateAttemptCoordinator.Save(record);
            var coordinator = new ConductorGroupedGateAttemptCoordinator(root,
                isProcessAlive: _ => false);
            coordinator.Reconcile(record, "owner-dead");
            Assert.Null(ConductorGroupedGateAttemptCoordinator.TryClaimOwner(
                path, FirstChild, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe"));
            Assert.Equal(0, ConductorGroupedGateAttemptCoordinator.Read(path).OwnerProcessId);
        }
        finally { DeleteDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmissionWritesOwnedAttemptBeforeReturningWithoutRunningVerifier(bool train)
    {
        var (repo, kernel, goals) = CreateReadyMembers(train ? 3 : 2);
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new SequenceAcceptanceVerifier([]);
            var driver = CreateDriver(kernel, workspace, verifier, cleanup.Hooks);
            var background = new BackgroundGateStartHarness();
            background.Capture(driver);
            var launches = new List<ConductorGroupedGateAttempt>();
            driver.EnableOwnedGroupedGateAttempts(Coordinator(workspace, FirstGeneration,
                attempt =>
                {
                    Assert.True(File.Exists(attempt.MetadataPath));
                    Assert.Equal(0, ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath).OwnerProcessId);
                    launches.Add(attempt);
                    return new ConductorGroupedGateLaunchResult(FirstChild,
                        DateTimeOffset.UnixEpoch, "C:\\dotnet.exe");
                }, pid => pid == FirstChild));

            if (train)
            {
                var selection = ProjectTrainSelection(driver, goals);
                var result = driver.RunMergeTrain(selection, goals,
                    ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
                Assert.Contains("outcome=inflight", result.Detail, StringComparison.Ordinal);
            }
            else
            {
                var selection = ProjectSelection(driver, goals[0], goals[1]);
                var result = driver.RunAcceptanceCohort(selection, goals,
                    ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
                Assert.Contains("outcome=inflight", result.Detail, StringComparison.Ordinal);
            }

            var launched = Assert.Single(launches);
            var record = ConductorGroupedGateAttemptCoordinator.Read(launched.MetadataPath);
            Assert.Equal(train ? "train" : "cohort", record.Kind);
            Assert.Equal(goals.Select(goal => goal.Id.Value), record.Members.Select(member => member.GoalId));
            Assert.All(record.Members, member => Assert.Equal(40, member.CandidateRevision.Length));
            Assert.Equal(40, record.MainRevision.Length);
            Assert.Equal(40, record.CombinedTreeRevision.Length);
            Assert.False(string.IsNullOrWhiteSpace(record.ManifestIdentity));
            Assert.False(string.IsNullOrWhiteSpace(record.IdentityValue));
            Assert.Equal(FirstChild, record.OwnerProcessId);
            Assert.Equal(FirstGeneration, record.LaunchingGenerationId);
            Assert.Equal(0, background.StartCount);
            Assert.Equal(0, verifier.RunCount);
            Assert.True(File.Exists(record.MetadataPath));
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessorAdoptsMatchingLiveAttemptAndReplaysPublishedReceipt(bool train)
    {
        var (repo, kernel, goals) = CreateReadyMembers(train ? 3 : 2);
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var first = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            first.EnableOwnedGroupedGateAttempts(Coordinator(workspace, FirstGeneration,
                _ => new ConductorGroupedGateLaunchResult(FirstChild, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe"),
                pid => pid == FirstChild));
            var cohort = train ? null : ProjectSelection(first, goals[0], goals[1]);
            var mergeTrain = train ? ProjectTrainSelection(first, goals) : null;
            Run(first, goals, cohort, mergeTrain);
            var record = Assert.Single(Records(workspace));

            var launches = 0;
            var events = new List<string>();
            var alive = true;
            var second = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            second.EnableOwnedGroupedGateAttempts(Coordinator(workspace, SecondGeneration,
                _ => { launches++; return new ConductorGroupedGateLaunchResult(SecondChild, null, null); },
                pid => pid == FirstChild && alive,
                eventSink: events.Add));
            Assert.Equal(goals.Length, second.GetActiveCohortGateMemberGoalIds().Count);
            Assert.Equal(1, second.GetActiveAcceptanceCohortCapacity().ActiveRootCount);
            cohort = train ? null : ProjectSelection(second, goals[0], goals[1]);
            mergeTrain = train ? ProjectTrainSelection(second, goals) : null;
            Run(second, goals, cohort, mergeTrain);
            Run(second, goals, cohort, mergeTrain);
            Assert.Equal(0, launches);
            Assert.Single(events, line => line.StartsWith("ACCEPTANCE_COHORT_ADOPTED ", StringComparison.Ordinal));
            Assert.Equal(goals.Length, second.GetActiveCohortGateMemberGoalIds().Count);
            Assert.Equal(1, second.GetActiveAcceptanceCohortCapacity().ActiveRootCount);

            var landings = new List<ConductorLandingReceipt>();
            second.SuccessfulLandingSink = landings.Add;
            PublishPassingReceipt(record, workspace, repo);
            File.WriteAllText(record.ResultPath, "passed");
            File.WriteAllText(record.ExitCodePath, "0");
            alive = false;
            _ = second.GetActiveCohortGateMemberGoalIds();
            var completed = Run(second, goals, cohort, mergeTrain);
            Assert.Equal(goals.Length, completed);
            Assert.Equal(goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal),
                landings.Select(landing => landing.GoalId).Order(StringComparer.Ordinal));
            if (!train)
            {
                // The direct cohort call lands both members. The conduct loop then records and
                // completes them in its two ordinary post-landing lifecycle steps.
                foreach (var goal in goals)
                {
                    var recorded = Assert.IsType<ConductorAdvanceOutcome.Executed>(
                        second.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);
                    Assert.Equal(GoalLifecycleState.Merged, recorded.FromState);
                    var finished = Assert.IsType<ConductorAdvanceOutcome.Executed>(
                        second.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);
                    Assert.Equal(GoalLifecycleState.Recorded, finished.FromState);
                }
            }
            Assert.All(goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            Assert.Equal(0, launches);
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData("members")]
    [InlineData("candidate")]
    [InlineData("main")]
    [InlineData("tree")]
    public void ChangedOrphanIdentityIsStoppedBeforeOneFreshLaunch(string field)
    {
        var (repo, kernel, goals) = CreateReadyMembers(2);
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var first = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            first.EnableOwnedGroupedGateAttempts(Coordinator(workspace, FirstGeneration,
                _ => new ConductorGroupedGateLaunchResult(FirstChild, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe"),
                pid => pid == FirstChild));
            var selection = ProjectSelection(first, goals[0], goals[1]);
            Run(first, goals, selection, null);
            var original = Assert.Single(Records(workspace));
            var changed = field switch
            {
                "members" => original with { Members = original.Members.Select((member, index) =>
                    index == 0 ? member with { GoalId = new string('f', 32) } : member).ToArray() },
                "candidate" => original with { Members = original.Members.Select((member, index) =>
                    index == 0 ? member with { CandidateRevision = new string('f', 40) } : member).ToArray() },
                "main" => original with { MainRevision = new string('f', 40) },
                _ => original with { CombinedTreeRevision = new string('f', 40) }
            };
            ConductorGroupedGateAttemptCoordinator.Save(changed);
            var stops = 0;
            var launches = 0;
            var events = new List<string>();
            var second = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            second.EnableOwnedGroupedGateAttempts(Coordinator(workspace, SecondGeneration,
                _ => { launches++; return new ConductorGroupedGateLaunchResult(SecondChild, null, null); },
                pid => pid == FirstChild || pid == SecondChild,
                _ => { stops++; return true; }, events.Add));
            Run(second, goals, ProjectSelection(second, goals[0], goals[1]), null);
            Assert.Equal(1, stops);
            Assert.Equal(1, launches);
            Assert.Single(events, line => line.Contains($"ADOPTION_REFUSED") && line.EndsWith($"field={field}"));
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void DeadChildWithoutReceiptRecordsOneIndeterminateOutcomeAndDoesNotRelaunch()
    {
        var (repo, kernel, goals) = CreateReadyMembers(2);
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var first = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            first.EnableOwnedGroupedGateAttempts(Coordinator(workspace, FirstGeneration,
                _ => new ConductorGroupedGateLaunchResult(FirstChild, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe"),
                pid => pid == FirstChild));
            Run(first, goals, ProjectSelection(first, goals[0], goals[1]), null);
            var record = Assert.Single(Records(workspace));
            var launches = 0;
            var second = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            second.EnableOwnedGroupedGateAttempts(Coordinator(workspace, SecondGeneration,
                _ => { launches++; return new ConductorGroupedGateLaunchResult(SecondChild, null, null); },
                _ => false));
            var selection = ProjectSelection(second, goals[0], goals[1]);
            Run(second, goals, selection, null);
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory,
                "cohort-acceptance.db"));
            var firstReceipt = Assert.IsType<AcceptanceCohortReceipt>(store.TryReadReceipt(record.IdentityValue));
            Run(second, goals, selection, null);
            var receipt = Assert.IsType<AcceptanceCohortReceipt>(store.TryReadReceipt(record.IdentityValue));
            Assert.Equal(firstReceipt.ReceiptId, receipt.ReceiptId);
            Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, receipt.Outcome);
            Assert.Equal(AcceptanceCohortInvalidationReason.InfrastructureRetryExhausted,
                receipt.Invalidation?.Reason);
            Assert.Equal(0, launches);
        }
        finally { DeleteDirectory(repo); }
    }

    private static ConductorDriver CreateDriver(AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace, IGoalAcceptanceVerifier verifier,
        GoalWorktreeCleanupHooks cleanup) => new(kernel, workspace, verifier,
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup);

    private static ConductorGroupedGateAttemptCoordinator Coordinator(
        OrchestratorWorkspace workspace, int generation,
        Func<ConductorGroupedGateAttempt, ConductorGroupedGateLaunchResult> launch,
        Func<int, bool> alive,
        Func<ConductorGroupedGateAttempt, bool>? stop = null,
        Action<string>? eventSink = null) => new(
            Path.Combine(workspace.OrchestratorDirectory, "grouped-gate-attempts"),
            generationId: generation, launch: launch, isProcessAlive: alive,
            stop: stop, eventSink: eventSink);

    private static IReadOnlyList<ConductorGroupedGateAttempt> Records(OrchestratorWorkspace workspace) =>
        new ConductorGroupedGateAttemptCoordinator(Path.Combine(workspace.OrchestratorDirectory,
            "grouped-gate-attempts")).ReadAll().ToArray();

    private static int Run(ConductorDriver driver, Goal[] goals,
        ConductorAcceptanceCohortSelection? cohort, ConductorMergeTrainSelection? train)
    {
        if (cohort is not null)
            return driver.RunAcceptanceCohort(cohort, goals,
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true).MemberResults.Count;
        return driver.RunMergeTrain(train!, goals,
            ConductorAutonomyPolicy.Permissive, runGateInBackground: true).MemberResults.Count;
    }

    private static void PublishPassingReceipt(ConductorGroupedGateAttempt attempt,
        OrchestratorWorkspace workspace, string repo)
    {
        var trx = WritePassingTrx(repo, "grouped-gate-adopted.trx");
        if (attempt.Kind == "cohort")
        {
            var identity = AcceptanceCohortIdentity.Create(
                new ConductorAcceptanceCohortSelection(attempt.Members
                    .Select(member => member.ToProjection(attempt.MainRevision)).ToArray(), []).BindMembers(),
                attempt.MainRevision, attempt.CombinedTreeRevision, attempt.ManifestIdentity);
            Assert.Equal(attempt.IdentityValue, identity.Value);
            new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory,
                "cohort-acceptance.db")).SaveGateReceipt(new AcceptanceCohortReceipt(
                "grouped-adopted", identity, AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow, 1, [], 0, [trx], ValidForLanding: true));
        }
        else
        {
            // The train store persists the same identity that the admitting call materialized.
            using var materialized = GoalWorktrees.CreateMergeTrainWorkspace(repo,
                attempt.MainRevision, new ConductorMergeTrainSelection(attempt.Members
                    .Select(member => member.ToProjection(attempt.MainRevision)).ToArray()).BindMembers());
            var identity = MergeTrainIdentity.Create(materialized.Members,
                attempt.MainRevision, materialized.TreeRevision, attempt.ManifestIdentity);
            Assert.Equal(attempt.IdentityValue, identity.Value);
            new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory,
                "merge-train-acceptance.db")).SaveGateReceipt(new MergeTrainReceipt(
                "grouped-adopted", identity, MergeTrainGateOutcome.Passed,
                DateTimeOffset.UtcNow, 1, [], 0, [trx], ValidForLanding: true));
        }
    }

    private static (string Repo, AgentOrchestratorKernel Kernel, Goal[] Goals) CreateReadyMembers(int count)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        AddAcceptanceManifest(repo);
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, count)
            .Select(index => CreateCompletedGoal(kernel, $"Grouped gate member {index}", repo)).ToArray();
        var paths = new[]
        {
            "tests/Mcg.AgentOrchestrator.Core.Tests/First.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Second.cs",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Third.cs"
        };
        for (var index = 0; index < count; index++)
        {
            var revision = CreateWorktreeCandidate(repo, goals[index].Id, paths[index], $"member-{index}");
            kernel.RecordGoalRefinement(goals[index].Id, new RefinedSpec(goals[index].Objective,
                ["The acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
            { AcceptanceGateOwnedAcceptanceCriteria = ["The acceptance gate passes"] });
            kernel.MapCriterionEvidenceOwner(goals[index].Id, 0, 1,
                CriterionEvidenceOwner.Acceptance, "test",
                CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: revision);
        }
        return (repo, kernel, goals);
    }
}
