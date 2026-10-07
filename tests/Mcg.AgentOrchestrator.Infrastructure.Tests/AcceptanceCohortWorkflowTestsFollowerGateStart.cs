using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fact owns its repository, attempt registry and build storage.
public sealed class AcceptanceCohortWorkflowTestsFollowerGateStart : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void StartBindsFollowerPlanAndHoldsOnlyFollower()
    {
        using var scenario = CreateScenario();
        var verifier = new SequenceAcceptanceVerifier([]);
        var driver = scenario.Driver(verifier);
        var launches = 0;
        scenario.Enable(driver, launch: attempt =>
        {
            Assert.True(File.Exists(attempt.MetadataPath));
            launches++;
            return new(92001, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe");
        });
        var members = ProjectSelection(driver, scenario.Leader, scenario.Follower).Members;
        using var workspace = Assert.IsType<FollowerGateWorkspace>(GoalWorktrees.CreateFollowerWorkspace(
            scenario.Repo, members[0].MainRevision, members[0].GoalId, members[0].CandidateRevision,
            members[1].GoalId, members[1].BranchRevision, scenario.Cleanup).Workspace);
        var expectedPlan = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(workspace.Path, members[1].LandingPaths);
        var started = Assert.IsType<FollowerGateStartOutcome.Started>(driver.StartFollowerGate(
            members[0], members[1], ConductorAutonomyPolicy.Permissive));
        var record = Assert.Single(scenario.Records());
        Assert.Equal("follower", record.Kind);
        Assert.Equal(new[] { scenario.Leader.Id.Value, scenario.Follower.Id.Value }, record.Members.Select(member => member.GoalId));
        Assert.Equal(members[0].MainRevision, record.MainRevision);
        Assert.Equal(workspace.TestedTreeRevision, record.CombinedTreeRevision);
        Assert.Equal(expectedPlan, record.ManifestIdentity);
        Assert.Equal(FollowerGateIdentity.Create(workspace.ToReceipt(expectedPlan)), record.IdentityValue);
        Assert.Equal(record.AttemptId, started.Attempt.AttemptId);
        var fingerprints = new List<string>();
        Assert.Equal(new[] { scenario.Follower.Id.Value }, driver.GetActiveCohortGateMemberGoalIds(
            (_, detail) => fingerprints.Add(detail)));
        Assert.Single(fingerprints);
        Assert.Contains($"fingerprint=follower:{record.IdentityValue}", fingerprints[0], StringComparison.Ordinal);
        Assert.IsType<FollowerGateStartOutcome.LeaderHasFollower>(driver.StartFollowerGate(
            members[0], members[1], ConductorAutonomyPolicy.Permissive));
        Assert.Single(scenario.Records());
        Assert.Equal(1, launches);
        Assert.Equal(0, verifier.RunCount);
    }

    [Fact]
    public void RebaseConflictReturnsPathsWithoutRecordOrWorkspace()
    {
        using var scenario = CreateScenario(conflicting: true);
        var driver = scenario.Driver(new SequenceAcceptanceVerifier([]));
        scenario.Enable(driver, launch: _ => throw new InvalidOperationException("Conflict must not launch."));
        var members = ProjectSelection(driver, scenario.Leader, scenario.Follower).Members;
        var conflict = Assert.IsType<FollowerGateStartOutcome.Conflict>(driver.StartFollowerGate(
            members[0], members[1], ConductorAutonomyPolicy.Permissive));
        Assert.Equal(new[] { "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Shared.cs" }, conflict.Paths);
        Assert.Empty(scenario.Records());
        Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(scenario.Repo, GoalWorktrees.DirectoryName), "f-*"));
        Assert.DoesNotContain("/f-", RunGitOutput(scenario.Repo, "worktree", "list", "--porcelain").Replace('\\', '/'));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChildSavesVerdictBoundToRecordedIdentity(bool passed)
    {
        using var scenario = CreateScenario();
        var result = passed ? new AcceptanceVerificationResult(true, false, 0, null,
            TestResultPaths: [WritePassingTrx(scenario.Repo, "pass.trx")]) : FailedVerification(scenario.Repo, "fail.trx", "follower-check");
        var verifier = new SequenceAcceptanceVerifier([result]);
        var driver = scenario.Driver(verifier);
        scenario.Enable(driver);
        var members = ProjectSelection(driver, scenario.Leader, scenario.Follower).Members;
        var record = Assert.IsType<FollowerGateStartOutcome.Started>(driver.StartFollowerGate(
            members[0], members[1], ConductorAutonomyPolicy.Permissive)).Attempt;
        driver.RunGroupedGateAttemptBody(record);
        var receipt = Assert.IsType<FollowerGateRunReceipt>(scenario.Store.TryReadReceipt(record.IdentityValue));
        Assert.Equal(passed ? FollowerGateRunOutcome.Passed : FollowerGateRunOutcome.Failed, receipt.Outcome);
        Assert.Equal(record.IdentityValue, FollowerGateIdentity.Create(receipt.Binding));
        Assert.Equal(record.MainRevision, receipt.Binding.BaseMainRevision);
        Assert.Equal(record.CombinedTreeRevision, receipt.Binding.FollowerTestedTree);
        Assert.Equal(record.ManifestIdentity, receipt.Binding.FollowerPlanIdentity);
        Assert.Equal(members[0].CandidateRevision, receipt.Binding.LeaderCandidateRevision);
        Assert.Equal(members[1].BranchRevision, receipt.Binding.FollowerBranchHead);
        Assert.Equal(new[] { scenario.Follower.Id }, verifier.GoalIds);
        Assert.Equal(members[1].LandingPaths, Assert.Single(verifier.ChangedFiles));
        scenario.LandLeader();
        var decision = FollowerGateBindingRule.Decide(receipt, new(FollowerLeaderOutcome.Landed,
            RunGitOutput(scenario.Repo, "rev-parse", "main^1"), RunGitOutput(scenario.Repo, "rev-parse", "main^{tree}"),
            members[1].BranchRevision, receipt.Binding.FollowerTestedTree, record.ManifestIdentity));
        Assert.Equal(passed ? FollowerGateDisposition.LandFollower : FollowerGateDisposition.ChargeFollower, decision.Disposition);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void RestoreAfterExactLandingAdoptsAndHoldsOnlyFollower()
    {
        using var scenario = CreateScenario();
        var first = scenario.Driver(new SequenceAcceptanceVerifier([]));
        scenario.Enable(first);
        var record = scenario.Start(first);
        scenario.LandLeader();
        var second = scenario.Driver(new SequenceAcceptanceVerifier([]));
        var stops = 0;
        var events = new List<string>();
        scenario.Enable(second, generation: 91002, stop: _ => { stops++; return true; }, events: events.Add);
        Assert.Equal((true, (string?)null), second.InspectRestoredFollowerGateIdentity(record));
        Assert.Equal(new[] { scenario.Follower.Id.Value }, second.GetActiveCohortGateMemberGoalIds());
        Assert.Equal(91002, Assert.Single(scenario.Records()).AdoptedByGenerationId);
        Assert.Equal(0, stops);
        Assert.Single(events, line => line.StartsWith("ACCEPTANCE_COHORT_ADOPTED ", StringComparison.Ordinal));
        Assert.Null(scenario.Store.TryReadReceipt(record.IdentityValue));
    }

    [Fact]
    public void RestoreReportsMainForUnrelatedLandingAndMembersForMovedFollower()
    {
        using var scenario = CreateScenario();
        var driver = scenario.Driver(new SequenceAcceptanceVerifier([]));
        scenario.Enable(driver);
        var record = scenario.Start(driver);
        scenario.CommitOnMain("unrelated.txt", "unrelated");
        Assert.Equal((true, "main"), driver.InspectRestoredFollowerGateIdentity(record));
        CreateWorktreeCandidate(scenario.Repo, scenario.Follower.Id, "extra.txt", "new follower revision");
        Assert.Equal((true, "members"), driver.InspectRestoredFollowerGateIdentity(record));
    }

    [Fact]
    public void StartReturnsMainDiffersAndExistingReceiptWithoutLaunching()
    {
        using var scenario = CreateScenario();
        var driver = scenario.Driver(new SequenceAcceptanceVerifier([]));
        scenario.Enable(driver, launch: _ => throw new InvalidOperationException("Declined start must not launch."));
        var members = ProjectSelection(driver, scenario.Leader, scenario.Follower).Members;
        var follower = members[1];
        var differentMain = new GateReadyCandidateProjection(follower.GoalId, follower.LifecycleState,
            follower.VerificationState, follower.ChangeRiskTier, follower.AutoPromotionDisposition,
            follower.LandingPaths, follower.ResourceKeys, follower.MergeEvidence with { MainRevision = new string('f', 40) });
        Assert.IsType<FollowerGateStartOutcome.MainDiffers>(driver.StartFollowerGate(members[0], differentMain, ConductorAutonomyPolicy.Permissive));
        using var materialized = Assert.IsType<FollowerGateWorkspace>(GoalWorktrees.CreateFollowerWorkspace(
            scenario.Repo, members[0].MainRevision, members[0].GoalId, members[0].CandidateRevision,
            follower.GoalId, follower.BranchRevision, scenario.Cleanup).Workspace);
        var binding = materialized.ToReceipt(GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(materialized.Path, follower.LandingPaths));
        scenario.Store.SaveGateReceipt(new("exists", FollowerGateIdentity.Create(binding), binding,
            FollowerGateRunOutcome.Failed, DateTimeOffset.UnixEpoch, [], 1, [], null));
        Assert.IsType<FollowerGateStartOutcome.ReceiptExists>(driver.StartFollowerGate(members[0], follower, ConductorAutonomyPolicy.Permissive));
        Assert.Empty(scenario.Records());
    }

    [Fact]
    public void FollowerInAnotherGroupedRunIsBusyWithoutLaunching()
    {
        using var scenario = CreateScenario();
        var driver = scenario.Driver(new SequenceAcceptanceVerifier([]));
        scenario.Enable(driver, launch: _ => throw new InvalidOperationException("Busy follower must not launch."));
        var selection = ProjectSelection(driver, scenario.Leader, scenario.Follower);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(driver.TryRegisterCohortGateRunForTests(selection, completion));
        Assert.IsType<FollowerGateStartOutcome.FollowerBusy>(driver.StartFollowerGate(
            selection.Members[0], selection.Members[1], ConductorAutonomyPolicy.Permissive));
        Assert.Empty(scenario.Records());
        completion.SetResult();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChildUsesPinnedBaseAcrossLandingAndRecordsChangedIdentity(bool exactLanding)
    {
        using var scenario = CreateScenario();
        var leaderRevision = RunGitOutput(GoalWorktrees.TryResolve(scenario.Repo, scenario.Leader.Id)!, "rev-parse", "HEAD");
        var verifier = new IdentityCheckingVerifier(new SequenceAcceptanceVerifier([
            new AcceptanceVerificationResult(true, false, 0, null, TestResultPaths: [WritePassingTrx(scenario.Repo, "pinned.trx")])]),
            (path, executionOwner) =>
            {
                var owner = Assert.IsType<AcceptanceAttemptExecutionOwner>(executionOwner);
                Assert.Equal(leaderRevision, owner.Identity.MainSha);
                if (exactLanding) scenario.LandLeader();
                else scenario.CommitOnMain("unrelated.txt", "different tree");
                owner.EnsureResolvedIdentityCurrent(path);
            });
        var driver = scenario.Driver(verifier);
        scenario.Enable(driver);
        var record = scenario.Start(driver);
        driver.RunGroupedGateAttemptBody(record);
        var receipt = Assert.IsType<FollowerGateRunReceipt>(scenario.Store.TryReadReceipt(record.IdentityValue));
        Assert.Equal(exactLanding ? FollowerGateRunOutcome.Passed : FollowerGateRunOutcome.Invalidated, receipt.Outcome);
        Assert.Equal(exactLanding ? null : (FollowerGateInvalidReason?)FollowerGateInvalidReason.LeaderTreeDiffers, receipt.InvalidReason);
    }

    [Fact]
    public void MissingGateEvidenceIsInfrastructureFailure()
    {
        using var scenario = CreateScenario();
        var driver = scenario.Driver(new SequenceAcceptanceVerifier([new AcceptanceVerificationResult(true, false, 0, null)]));
        scenario.Enable(driver);
        var record = scenario.Start(driver);
        driver.RunGroupedGateAttemptBody(record);
        Assert.Equal(FollowerGateRunOutcome.InfrastructureFailure, scenario.Store.TryReadReceipt(record.IdentityValue)?.Outcome);
    }

    private sealed class IdentityCheckingVerifier(IGoalAcceptanceVerifier inner,
        Action<string, IAcceptanceAttemptExecutionOwner> check) : IGoalAcceptanceVerifier
    {
        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            check(worktreePath, executionOwner);
            return inner.RunOwnedAsync(worktreePath, goalId, changedFiles, stableSlotIndex, stableSlotLease, executionOwner);
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false) => throw new NotSupportedException();
    }

    internal static Scenario CreateScenario(bool conflicting = false)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            if (conflicting)
            {
                const string shared = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Shared.cs";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repo, shared))!);
                File.WriteAllText(Path.Combine(repo, shared), "base\n");
                RunGit(repo, "add", shared);
                RunGit(repo, "commit", "-m", "Seed shared line");
            }
            var kernel = new AgentOrchestratorKernel();
            var goals = new[] { CreateCompletedGoal(kernel, "Follower leader", repo), CreateCompletedGoal(kernel, "Follower member", repo) };
            var paths = conflicting ? new[] { "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Shared.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Shared.cs" } : new[]
            { "tests/Mcg.AgentOrchestrator.Core.Tests/Leader.cs", "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Follower.cs" };
            for (var index = 0; index < goals.Length; index++)
            {
                var revision = CreateWorktreeCandidate(repo, goals[index].Id, paths[index], $"member-{index}\n");
                kernel.RecordGoalRefinement(goals[index].Id, new RefinedSpec(goals[index].Objective,
                    ["The acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = ["The acceptance gate passes"] });
                kernel.MapCriterionEvidenceOwner(goals[index].Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                    "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: revision);
            }
            return new(repo, kernel, goals, CreateIsolatedCleanupContext(repo).Hooks);
        }
        catch { DeleteDirectory(repo); throw; }
    }

    internal sealed class Scenario(string repo, AgentOrchestratorKernel kernel, Goal[] goals, GoalWorktreeCleanupHooks cleanup) : IDisposable
    {
        internal string Repo => repo;
        internal AgentOrchestratorKernel Kernel => kernel;
        internal Goal Leader => goals[0];
        internal Goal Follower => goals[1];
        internal GoalWorktreeCleanupHooks Cleanup => cleanup;
        internal OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(repo);
        internal FollowerGateAcceptanceStore Store => new(Path.Combine(Workspace.OrchestratorDirectory, "follower-gate-acceptance.db"));

        internal ConductorDriver Driver(IGoalAcceptanceVerifier verifier) => new(kernel, Workspace, verifier,
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup);

        internal void Enable(ConductorDriver driver, int generation = 91001,
            Func<ConductorGroupedGateAttempt, ConductorGroupedGateLaunchResult>? launch = null,
            Func<ConductorGroupedGateAttempt, bool>? stop = null, Action<string>? events = null) =>
            driver.EnableOwnedGroupedGateAttempts(new(Path.Combine(Workspace.OrchestratorDirectory, "grouped-gate-attempts"),
                generationId: generation, launch: launch ?? (_ => new(92001, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe")),
                isProcessAlive: pid => pid == 92001, stop: stop ?? (_ => true), eventSink: events,
                buildStorageRoot: cleanup.BuildStorageRoot));

        internal ConductorGroupedGateAttempt Start(ConductorDriver driver)
        {
            var members = ProjectSelection(driver, Leader, Follower).Members;
            return Assert.IsType<FollowerGateStartOutcome.Started>(driver.StartFollowerGate(
                members[0], members[1], ConductorAutonomyPolicy.Permissive)).Attempt;
        }

        internal ConductorGroupedGateAttempt[] Records() => new ConductorGroupedGateAttemptCoordinator(
            Path.Combine(Workspace.OrchestratorDirectory, "grouped-gate-attempts")).ReadAll().ToArray();

        internal void LandLeader()
        {
            RunGit(repo, "checkout", "main");
            RunGit(repo, "merge", "--no-ff", GoalWorktrees.BranchName(Leader.Id), "-m", "Land exact leader");
            foreach (var obligation in Leader.OutstandingCriterionEvidenceObligations)
                kernel.RecordCriterionEvidence(Leader.Id, obligation.Id, CriterionEvidenceOwner.Acceptance,
                    obligation.ExpectedCandidateSha!, "leader-solo-fixture", CriterionEvidenceScopes.FullAcceptanceGate,
                    true, "The fixture's leader solo gate passed.");
            kernel.CompleteGoal(Leader.Id, "Exact leader landed");
        }

        internal void CommitOnMain(string path, string contents)
        {
            RunGit(repo, "checkout", "main");
            File.WriteAllText(Path.Combine(repo, path), contents);
            RunGit(repo, "add", path);
            RunGit(repo, "commit", "-m", "Unrelated main change");
        }

        public void Dispose() => DeleteDirectory(repo);
    }
}
