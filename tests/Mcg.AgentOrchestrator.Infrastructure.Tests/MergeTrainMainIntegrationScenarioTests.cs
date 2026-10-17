using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: repositories, stores and cleanup hooks are local; process operations are injected.
public sealed class MergeTrainMainIntegrationScenarioTests : AcceptanceCohortWorkflowTests
{
    private const int FirstGeneration = 91001;
    private const int SecondGeneration = 91002;
    private const int HolderPid = 92001;
    private const string ImagePath = "C:\\dotnet.exe";

    [Fact]
    public void IntegratedMergeCommitMember_ComposesAndSurvivesIdentityInspectionOnSuccessorAdoption()
    {
        using var scenario = new Scenario();
        var events = new List<string>();
        var launches = 0;
        var stops = 0;
        var generation = new SpawnProcessIdentity(FirstGeneration, DateTimeOffset.UnixEpoch, ImagePath);
        var successorGeneration = new SpawnProcessIdentity(SecondGeneration, DateTimeOffset.UnixEpoch, ImagePath);
        var holder = new SpawnProcessIdentity(HolderPid, DateTimeOffset.UnixEpoch, ImagePath);
        var liveGeneration = generation;
        var lease = new AcceptanceHolderLease(
            classify: identity => identity == liveGeneration || identity == holder
                ? SpawnTrackedProcessStatus.LiveMatch : SpawnTrackedProcessStatus.DeadOrRecycled,
            attach: _ => new EmptyContainment(),
            job: (_, identity, status, cancel) =>
            {
                Assert.Equal(holder, identity);
                Assert.Equal(SpawnTrackedProcessStatus.LiveMatch, status);
                if (cancel) stops++;
                return new(true, cancel, "scenario-holder");
            }, eventSink: events.Add,
            stopUnclaimed: _ => throw new InvalidOperationException("Holder must have claimed its lease"));
        var first = new ConductorGroupedGateAttemptCoordinator(scenario.AttemptRoot,
            generationId: FirstGeneration,
            launch: attempt =>
            {
                launches++;
                using var claim = lease.Claim(AcceptanceHolderBinding.For(attempt), holder);
                return new(HolderPid, holder.StartedAt, holder.ImagePath);
            }, isProcessAlive: pid => pid == FirstGeneration || pid == HolderPid,
            stop: _ => { stops++; return true; }, eventSink: events.Add,
            holderLease: lease, generationIdentity: generation);
        var driver = scenario.Driver(first);
        var selection = ProjectTrainSelection(driver, scenario.Goals);

        var result = Run(driver, selection, scenario.Goals);

        Assert.Contains("outcome=inflight", result.Detail, StringComparison.Ordinal);
        Assert.Empty(result.Ejections);
        Assert.Equal(1, launches);
        Assert.Equal(0, stops);
        var attempt = Assert.Single(first.ReadAll());
        Assert.Equal(1, attempt.HolderContractVersion);
        Assert.Equal(scenario.Goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal),
            attempt.Members.Select(member => member.GoalId).Order(StringComparer.Ordinal));
        scenario.AssertNoStoredEjections();
        Assert.Equal((true, (string?)null), scenario.Inspect(attempt, selection));

        liveGeneration = successorGeneration;
        launches = 0;
        stops = 0;
        events.Clear();
        var successor = new ConductorGroupedGateAttemptCoordinator(scenario.AttemptRoot,
            generationId: SecondGeneration,
            launch: _ => { launches++; return new(HolderPid, holder.StartedAt, holder.ImagePath); },
            isProcessAlive: pid => pid == SecondGeneration || pid == HolderPid,
            stop: _ => { stops++; return true; }, eventSink: events.Add,
            holderLease: lease, generationIdentity: successorGeneration);
        Assert.False(successor.LaunchedByThisInstance(attempt));
        var successorDriver = scenario.Driver(successor);
        var successorSelection = ProjectTrainSelection(successorDriver, scenario.Goals);

        var successorResult = Run(successorDriver, successorSelection, scenario.Goals);

        Assert.Contains("outcome=inflight", successorResult.Detail, StringComparison.Ordinal);
        Assert.Empty(successorResult.Ejections);
        Assert.DoesNotContain(events, line => line.Contains("ACCEPTANCE_COHORT_ADOPTION_REFUSED", StringComparison.Ordinal));
        Assert.Equal(0, launches);
        Assert.Equal(0, stops);
        var adopted = Assert.Single(successor.ReadAll());
        Assert.Equal(attempt.AttemptId, adopted.AttemptId);
        Assert.Equal(SecondGeneration, adopted.AdoptedByGenerationId);
        Assert.Null(adopted.ReconciledAt);
        Assert.False(adopted.Detail?.StartsWith("refused:", StringComparison.Ordinal) == true);
        Assert.Equal((true, (string?)null), scenario.Inspect(adopted, successorSelection));
        scenario.AssertNoStoredEjections();
    }

    private static ConductorMergeTrainRunResult Run(ConductorDriver driver,
        ConductorMergeTrainSelection selection, Goal[] goals) => driver.RunMergeTrain(selection, goals,
            ConductorAutonomyPolicy.Permissive, runGateInBackground: true);

    private sealed class EmptyContainment : IDisposable
    {
        public void Dispose() { }
    }

    // Trimmed from MergeTrainLaunchTickScenarioTests.Scenario, with a real resolved integration.
    private sealed class Scenario : IDisposable
    {
        private const string SharedPath = "tests/Mcg.AgentOrchestrator.Core.Tests/Shared.cs";
        private readonly string _repo;
        private readonly AgentOrchestratorKernel _kernel;
        private readonly OrchestratorWorkspace _workspace;
        private readonly GoalWorktreeCleanupHooks _cleanup;
        internal Goal[] Goals { get; }
        internal string AttemptRoot => Path.Combine(_workspace.OrchestratorDirectory, "grouped-gate-attempts");
        private string DatabasePath => Path.Combine(_workspace.OrchestratorDirectory, "merge-train-acceptance.db");

        internal Scenario()
        {
            _repo = CreateReducedAcceptanceCohortRepository();
            try
            {
                AddAcceptanceManifest(_repo);
                RunGit(_repo, "config", "core.autocrlf", "false");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_repo, SharedPath))!);
                File.WriteAllText(Path.Combine(_repo, SharedPath), "// base\n");
                RunGit(_repo, "add", SharedPath);
                RunGit(_repo, "commit", "-m", "Seed shared file");
                _kernel = new AgentOrchestratorKernel();
                Goals = [CreateCompletedGoal(_kernel, "Integrated member B", _repo),
                    CreateCompletedGoal(_kernel, "Unrelated member C", _repo)];
                _ = CreateWorktreeCandidate(_repo, Goals[0].Id, SharedPath, "// member B\n");
                var landed = GoalId.New();
                _ = CreateWorktreeCandidate(_repo, landed, SharedPath, "// landed A\n");
                RunGit(_repo, "merge", "--ff-only", GoalWorktrees.BranchName(landed));
                var memberWorktree = GoalWorktrees.Ensure(_repo, Goals[0].Id);
                Assert.Equal(1, RunGitExitCode(memberWorktree, "merge", "--no-edit", "main"));
                Assert.Equal(SharedPath, RunGitOutput(memberWorktree, "diff", "--name-only", "--diff-filter=U"));
                File.WriteAllText(Path.Combine(memberWorktree, SharedPath), "// landed A\n// member B\n");
                RunGit(memberWorktree, "add", SharedPath);
                RunGit(memberWorktree, "commit", "-m", "Resolve main integration keeping both sides");
                Assert.Equal("1", RunGitOutput(_repo, "rev-list", "--merges", "--count",
                    $"main..{GoalWorktrees.BranchName(Goals[0].Id)}"));
                var revisions = new[]
                {
                    RunGitOutput(memberWorktree, "rev-parse", "HEAD"),
                    CreateWorktreeCandidate(_repo, Goals[1].Id,
                        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Other.cs", "// member C\n")
                };
                for (var index = 0; index < Goals.Length; index++)
                {
                    _kernel.RecordGoalRefinement(Goals[index].Id, new RefinedSpec(Goals[index].Objective,
                        ["The acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                        { AcceptanceGateOwnedAcceptanceCriteria = ["The acceptance gate passes"] });
                    _kernel.MapCriterionEvidenceOwner(Goals[index].Id, 0, 1,
                        CriterionEvidenceOwner.Acceptance, "test",
                        CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: revisions[index]);
                }
                _workspace = OrchestratorWorkspace.ForDirectory(_repo);
                _cleanup = CreateIsolatedCleanupContext(_repo).Hooks;
            }
            catch { DeleteDirectory(_repo); throw; }
        }

        internal ConductorDriver Driver(ConductorGroupedGateAttemptCoordinator coordinator)
        {
            var driver = new ConductorDriver(_kernel, _workspace, new SequenceAcceptanceVerifier([]),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: _cleanup);
            driver.EnableOwnedGroupedGateAttempts(coordinator);
            return driver;
        }

        internal (bool Known, string? Difference) Inspect(ConductorGroupedGateAttempt attempt,
            ConductorMergeTrainSelection selection) => GroupedGateIdentityInspector.InspectTree(attempt,
                selection.Members, _repo, selection.Members[0].MainRevision, TrunkBranchName.Default, _cleanup);

        internal void AssertNoStoredEjections()
        {
            _ = new MergeTrainAcceptanceStore(DatabasePath);
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM merge_train_ejections;";
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        }

        public void Dispose() => DeleteDirectory(_repo);
    }
}
