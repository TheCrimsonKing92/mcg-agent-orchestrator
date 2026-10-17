using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scenario owns its repositories, stores and cleanup hooks; all process I/O is injected.
public sealed class MergeTrainLaunchTickScenarioTests : AcceptanceCohortWorkflowTests
{
    private const int FirstGeneration = 91001;
    private const int SecondGeneration = 91002;
    private const int HolderPid = 92001;
    private const string ImagePath = "C:\\dotnet.exe";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnLaunch_NextTick_KeepsOneUnreconciledAttempt(bool redDrop)
    {
        using var scenario = new Scenario();
        var launches = 0;
        var stops = 0;
        var events = new List<string>();
        var generation = new SpawnProcessIdentity(FirstGeneration, DateTimeOffset.UnixEpoch, ImagePath);
        var holder = new SpawnProcessIdentity(HolderPid, DateTimeOffset.UnixEpoch, ImagePath);
        var lease = new AcceptanceHolderLease(
            classify: identity => identity == generation || identity == holder
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
        var coordinator = new ConductorGroupedGateAttemptCoordinator(scenario.AttemptRoot,
            generationId: FirstGeneration,
            launch: attempt =>
            {
                launches++;
                using var claim = lease.Claim(AcceptanceHolderBinding.For(attempt), holder);
                return new(HolderPid, holder.StartedAt, holder.ImagePath);
            },
            isProcessAlive: pid => pid == FirstGeneration || pid == HolderPid,
            stop: _ => { stops++; return true; }, eventSink: events.Add,
            holderLease: lease, generationIdentity: generation);
        var driver = scenario.Driver(coordinator);
        var selection = ProjectTrainSelection(driver, scenario.Goals);
        if (redDrop) scenario.SaveFullCompositionRed(selection);

        var firstTick = Run(driver, selection, scenario.Goals);
        Assert.Contains("outcome=inflight", firstTick.Detail, StringComparison.Ordinal);
        var launched = Assert.Single(coordinator.ReadAll());
        Assert.Equal(1, launched.HolderContractVersion);
        // Prove the production lease path would permit self-inspection without the own-launch fence.
        Assert.True(lease.CanAdopt(AcceptanceHolderBinding.For(launched), generation));
        if (redDrop) Assert.Contains(firstTick.Ejections, ejection =>
            ejection.Reason == MergeTrainEjectionReason.RedNewestMember);

        var secondTick = Run(driver, selection, scenario.Goals);
        var active = driver.GetActiveCohortGateMemberGoalIds();

        Assert.Contains("outcome=inflight", secondTick.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(events, line => line.Contains("ACCEPTANCE_COHORT_ADOPTION_REFUSED", StringComparison.Ordinal));
        Assert.Equal(0, stops);
        Assert.Equal(1, launches);
        var record = Assert.Single(coordinator.ReadAll());
        Assert.Equal(redDrop ? 2 : 3, record.Members.Count);
        Assert.Null(record.ReconciledAt);
        Assert.Null(record.AdoptedByGenerationId);
        Assert.True(active.Count == record.Members.Count,
            $"Expected {record.Members.Count} materialized members to stay active (redDrop={redDrop}).");
        Assert.True(active.ToHashSet(StringComparer.Ordinal).SetEquals(record.Members.Select(member => member.GoalId)));
    }

    [Fact]
    public void SuccessorAfterRedDrop_AdoptsMaterializedCompositionWithoutRelaunch()
    {
        using var scenario = new Scenario();
        var first = scenario.Coordinator(FirstGeneration,
            _ => new(HolderPid, DateTimeOffset.UnixEpoch, ImagePath));
        var driver = scenario.Driver(first);
        var selection = ProjectTrainSelection(driver, scenario.Goals);
        scenario.SaveFullCompositionRed(selection);
        Assert.Contains("outcome=inflight", Run(driver, selection, scenario.Goals).Detail, StringComparison.Ordinal);
        var original = Assert.Single(first.ReadAll());
        Assert.Equal(2, original.Members.Count);

        var launches = 0;
        var stops = 0;
        var events = new List<string>();
        var successor = scenario.Coordinator(SecondGeneration,
            _ => { launches++; return new(HolderPid, DateTimeOffset.UnixEpoch, ImagePath); },
            _ => { stops++; return true; }, events.Add);
        Assert.False(successor.LaunchedByThisInstance(original));
        var restartedDriver = scenario.Driver(successor);

        Assert.Equal(2, restartedDriver.GetActiveCohortGateMemberGoalIds().Count);
        var result = Run(restartedDriver, ProjectTrainSelection(restartedDriver, scenario.Goals), scenario.Goals);

        Assert.Contains("outcome=inflight", result.Detail, StringComparison.Ordinal);
        Assert.Single(events, line => line.StartsWith("ACCEPTANCE_COHORT_ADOPTED ", StringComparison.Ordinal));
        Assert.DoesNotContain(events, line => line.Contains("ACCEPTANCE_COHORT_ADOPTION_REFUSED", StringComparison.Ordinal));
        Assert.Equal(0, stops);
        Assert.Equal(0, launches);
        var adopted = Assert.Single(successor.ReadAll());
        Assert.Equal(SecondGeneration, adopted.AdoptedByGenerationId);
        Assert.Null(adopted.ReconciledAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("main")]
    [InlineData("candidate")]
    [InlineData("tree")]
    [InlineData("identity")]
    public void RefusedIdentity_NextTick_LaunchesOnlyWhenAnInputChanges(string? changedField)
    {
        using var scenario = new Scenario();
        var first = scenario.Coordinator(FirstGeneration,
            _ => new(HolderPid, DateTimeOffset.UnixEpoch, ImagePath));
        var driver = scenario.Driver(first);
        Assert.Contains("outcome=inflight",
            Run(driver, ProjectTrainSelection(driver, scenario.Goals), scenario.Goals).Detail,
            StringComparison.Ordinal);
        var refused = Assert.Single(first.ReadAll()) with
        {
            ReconciledAt = DateTimeOffset.UtcNow, Outcome = "Reconciled",
            Detail = "refused:identity-changed field=tree"
        };
        refused = changedField switch
        {
            null => refused,
            "main" => refused with { MainRevision = new string('f', 40) },
            "candidate" => refused with { Members = refused.Members.Select((member, index) => index == 0
                ? member with { CandidateRevision = new string('f', 40) } : member).ToArray() },
            "tree" => refused with { CombinedTreeRevision = new string('f', 40) },
            "identity" => refused with { IdentityValue = "different-identity" },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField))
        };
        ConductorGroupedGateAttemptCoordinator.Save(refused);
        var launches = 0;
        var next = scenario.Coordinator(SecondGeneration,
            _ => { launches++; return new(HolderPid, DateTimeOffset.UnixEpoch, ImagePath); });
        var nextDriver = scenario.Driver(next);

        var result = Run(nextDriver, ProjectTrainSelection(nextDriver, scenario.Goals), scenario.Goals);

        if (changedField is null)
        {
            Assert.Equal(0, launches);
            Assert.Single(next.ReadAll());
            Assert.Contains($"outcome=identity-refused identity={refused.IdentityValue}", result.Detail, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(1, launches);
            Assert.Equal(2, next.ReadAll().Count());
            Assert.Contains("outcome=inflight", result.Detail, StringComparison.Ordinal);
        }
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
                    CreateCompletedGoal(_kernel, $"Launch tick member {index}", _repo)).ToArray();
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

        internal ConductorGroupedGateAttemptCoordinator Coordinator(int generation,
            Func<ConductorGroupedGateAttempt, ConductorGroupedGateLaunchResult> launch,
            Func<ConductorGroupedGateAttempt, bool>? stop = null, Action<string>? eventSink = null) => new(
                AttemptRoot, generationId: generation, launch: launch, isProcessAlive: pid => pid == HolderPid,
                stop: stop ?? (_ => throw new InvalidOperationException("Unexpected holder stop")), eventSink: eventSink,
                generationIdentity: new SpawnProcessIdentity(generation, DateTimeOffset.UnixEpoch, ImagePath));

        internal void SaveFullCompositionRed(ConductorMergeTrainSelection selection)
        {
            using var integration = GoalWorktrees.CreateMergeTrainWorkspace(_repo,
                selection.Members[0].MainRevision, selection.BindMembers(), TrunkBranchName.Default, _cleanup);
            Assert.Equal(3, integration.Members.Count);
            Assert.Empty(integration.Ejections);
            var manifest = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(integration.Path,
                integration.Members.SelectMany(member => member.LandingPaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            var identity = MergeTrainIdentity.Create(integration.Members,
                selection.Members[0].MainRevision, integration.TreeRevision, manifest);
            var store = new MergeTrainAcceptanceStore(Path.Combine(_workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            store.SaveGateReceipt(new MergeTrainReceipt("full-composition-red", identity,
                MergeTrainGateOutcome.Failed, DateTimeOffset.UtcNow, 1, ["cli tests"], 1, []));
            Assert.Equal(MergeTrainGateOutcome.Failed, store.TryReadReceipt(identity.Value)!.Outcome);
        }

        public void Dispose() => DeleteDirectory(_repo);
    }
}
