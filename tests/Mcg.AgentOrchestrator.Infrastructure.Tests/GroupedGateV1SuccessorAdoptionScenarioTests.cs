using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the scenario owns its repositories and stores; process I/O and lease time are injected.
public sealed class GroupedGateV1SuccessorAdoptionScenarioTests : AcceptanceCohortWorkflowTests
{
    private const int FirstGeneration = 91001;
    private const int SecondGeneration = 91002;
    private const int HolderPid = 92001;
    private const string ImagePath = "C:\\dotnet.exe";

    [Fact]
    public void Successor_TwoTrainTicks_AdoptsDurableHolderExactlyOnce()
    {
        using var scenario = new Scenario();
        var now = DateTimeOffset.UtcNow;
        var firstAlive = true;
        var firstLaunches = 0;
        var launches = 0;
        var stops = 0;
        var events = new List<string>();
        var firstIdentity = new SpawnProcessIdentity(FirstGeneration, DateTimeOffset.UnixEpoch, ImagePath);
        var successorIdentity = new SpawnProcessIdentity(SecondGeneration, DateTimeOffset.UnixEpoch, ImagePath);
        var holder = new SpawnProcessIdentity(HolderPid, DateTimeOffset.UnixEpoch, ImagePath);
        var lease = new AcceptanceHolderLease(utcNow: () => now,
            classify: identity => identity == holder || identity == successorIdentity ||
                (identity == firstIdentity && firstAlive)
                ? SpawnTrackedProcessStatus.LiveMatch : SpawnTrackedProcessStatus.DeadOrRecycled,
            attach: _ => new EmptyContainment(),
            job: (_, identity, status, cancel) =>
            {
                Assert.Equal(holder, identity);
                Assert.Equal(SpawnTrackedProcessStatus.LiveMatch, status);
                if (cancel) stops++;
                return new(true, cancel, "scenario-holder");
            },
            stopUnclaimed: _ => throw new InvalidOperationException("Holder must have claimed its lease"));
        var first = new ConductorGroupedGateAttemptCoordinator(scenario.AttemptRoot,
            generationId: FirstGeneration, utcNow: () => now,
            launch: attempt =>
            {
                firstLaunches++;
                using var claim = lease.Claim(AcceptanceHolderBinding.For(attempt), holder);
                return new(HolderPid, holder.StartedAt, holder.ImagePath);
            },
            isProcessAlive: pid => pid == HolderPid || (pid == FirstGeneration && firstAlive),
            stop: _ => { stops++; return true; }, holderLease: lease, generationIdentity: firstIdentity);
        var firstDriver = scenario.Driver(first);
        var firstTick = Run(firstDriver, ProjectTrainSelection(firstDriver, scenario.Goals), scenario.Goals);
        Assert.Contains("outcome=inflight", firstTick.Detail, StringComparison.Ordinal);
        var original = Assert.Single(first.ReadAll());
        Assert.Equal(1, firstLaunches);
        Assert.Equal("train", original.Kind);
        Assert.Equal(1, original.HolderContractVersion);
        Assert.Equal(3, original.Members.Count);
        Assert.Null(original.AdoptedByGenerationId);
        Assert.Equal(holder, lease.Read(AcceptanceHolderBinding.For(original)).Holder);

        firstAlive = false;
        now = now.AddSeconds(1);
        var successor = new ConductorGroupedGateAttemptCoordinator(scenario.AttemptRoot,
            generationId: SecondGeneration, utcNow: () => now,
            launch: _ => { launches++; return new(HolderPid, holder.StartedAt, holder.ImagePath); },
            isProcessAlive: pid => pid == HolderPid,
            stop: _ => { stops++; return true; }, eventSink: events.Add,
            holderLease: lease, generationIdentity: successorIdentity);
        Assert.True(successor.ShouldAdopt(original));
        var successorDriver = scenario.Driver(successor);

        for (var tick = 0; tick < 2; tick++)
        {
            now = now.AddSeconds(1);
            var result = Run(successorDriver,
                ProjectTrainSelection(successorDriver, scenario.Goals), scenario.Goals);

            Assert.Contains("outcome=inflight", result.Detail, StringComparison.Ordinal);
            var saved = Assert.Single(successor.ReadAll());
            Assert.Equal(original.AttemptId, saved.AttemptId);
            Assert.Equal(SecondGeneration, saved.AdoptedByGenerationId);
            Assert.Null(saved.ReconciledAt);
            var active = successorDriver.GetActiveCohortGateMemberGoalIds();
            Assert.Equal(original.Members.Count, active.Count);
            Assert.True(active.ToHashSet(StringComparer.Ordinal).SetEquals(
                original.Members.Select(member => member.GoalId)));
        }

        Assert.Single(events, line => line.StartsWith("ACCEPTANCE_COHORT_ADOPTED ", StringComparison.Ordinal));
        Assert.DoesNotContain(events, line => line.StartsWith("ACCEPTANCE_COHORT_ADOPTION_REFUSED ", StringComparison.Ordinal));
        Assert.Equal(0, launches);
        Assert.Equal(0, stops);
    }

    private static ConductorMergeTrainRunResult Run(ConductorDriver driver,
        ConductorMergeTrainSelection selection, Goal[] goals) => driver.RunMergeTrain(selection, goals,
            ConductorAutonomyPolicy.Permissive, runGateInBackground: true);

    private sealed class EmptyContainment : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class Scenario : IDisposable
    {
        private readonly string _repo;
        private readonly AgentOrchestratorKernel _kernel;
        private readonly OrchestratorWorkspace _workspace;
        private readonly GoalWorktreeCleanupHooks _cleanup;
        internal Goal[] Goals { get; }
        internal string AttemptRoot => Path.Combine(_workspace.OrchestratorDirectory, "grouped-gate-attempts");

        internal Scenario()
        {
            _repo = CreateReducedAcceptanceCohortRepository();
            try
            {
                AddAcceptanceManifest(_repo);
                _kernel = new AgentOrchestratorKernel();
                Goals = Enumerable.Range(0, 3).Select(index =>
                    CreateCompletedGoal(_kernel, $"Successor adoption member {index}", _repo)).ToArray();
                var paths = new[]
                {
                    "tests/Mcg.AgentOrchestrator.Core.Tests/First.cs",
                    "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Second.cs",
                    "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Third.cs"
                };
                for (var index = 0; index < Goals.Length; index++)
                {
                    var revision = CreateWorktreeCandidate(_repo, Goals[index].Id, paths[index], $"member-{index}");
                    _kernel.RecordGoalRefinement(Goals[index].Id, new RefinedSpec(Goals[index].Objective,
                        ["The acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                        { AcceptanceGateOwnedAcceptanceCriteria = ["The acceptance gate passes"] });
                    _kernel.MapCriterionEvidenceOwner(Goals[index].Id, 0, 1,
                        CriterionEvidenceOwner.Acceptance, "test",
                        CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: revision);
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

        public void Dispose() => DeleteDirectory(_repo);
    }
}
