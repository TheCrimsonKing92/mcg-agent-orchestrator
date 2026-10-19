using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: repositories, stores, clocks and process/job seams belong to each scenario.
public sealed class RelaunchDrainV1GroupedGateScenarioTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DriverCount_DefaultRetainsAllLiveAttempts(bool legacy)
    {
        using var scenario = new Scenario();
        if (legacy)
        {
            var coordinator = new ConductorGroupedGateAttemptCoordinator(scenario.AttemptRoot,
                generationId: 111, isProcessAlive: pid => pid == 222);
            var attempt = ConductorLiveGroupedGateCountTests.SaveAttempt(coordinator, scenario.Repo, "cohort");
            Assert.Equal(0, attempt.HolderContractVersion);
            var driver = scenario.Driver(coordinator);
            Assert.Equal(1, driver.CountLiveGroupedGateAttempts());
            Assert.Equal(1, driver.CountLiveGroupedGateAttempts(relaunchBlockingOnly: true));
        }
        else
        {
            using var fixture = new ConductorGroupedGateAttemptRelaunchDrainCountTests.DurableHolderFixture(
                scenario.AttemptRoot, "cohort");
            var driver = scenario.Driver(fixture.Coordinator);
            Assert.Equal(1, driver.CountLiveGroupedGateAttempts());
            Assert.Equal(0, driver.CountLiveGroupedGateAttempts(relaunchBlockingOnly: true));
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void SameIdentity_HandoffThenAdoptOnceAndLandReceipt()
    {
        using var scenario = new Scenario();
        var original = scenario.LaunchFirstGeneration();
        AssertImmediateHandoff(scenario);

        scenario.FirstAlive = false;
        var successor = scenario.Successor();
        var driver = scenario.Driver(successor);
        for (var tick = 0; tick < 2; tick++)
        {
            scenario.Now = scenario.Now.AddSeconds(1);
            var result = scenario.Tick(driver);
            Assert.Contains("outcome=inflight", result.Detail, StringComparison.Ordinal);
        }
        var adopted = Assert.Single(scenario.SuccessorEvents,
            line => line.StartsWith("ACCEPTANCE_COHORT_ADOPTED kind=cohort ", StringComparison.Ordinal));
        Assert.Contains($"identity={original.IdentityValue}", adopted, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.SuccessorEvents,
            line => line.StartsWith("ACCEPTANCE_COHORT_ADOPTION_REFUSED", StringComparison.Ordinal));
        Assert.Equal(0, scenario.SuccessorLaunches);
        Assert.Equal(0, scenario.Cancels);

        var landings = new List<ConductorLandingReceipt>();
        driver.SuccessfulLandingSink = landings.Add;
        scenario.PublishPassingReceipt(original);
        scenario.HolderAlive = false;
        scenario.Now = scenario.Now.AddSeconds(1);
        var completed = scenario.Tick(driver);

        Assert.Equal(2, completed.MemberResults.Count);
        Assert.Equal(scenario.Goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal),
            landings.Select(landing => landing.GoalId).Order(StringComparer.Ordinal));
        var receipt = Assert.IsType<AcceptanceCohortReceipt>(new CohortAcceptanceStore(
            Path.Combine(scenario.Workspace.OrchestratorDirectory, "cohort-acceptance.db"))
            .TryReadReceipt(original.IdentityValue));
        Assert.Equal(AcceptanceCohortGateOutcome.Passed, receipt.Outcome);
        Assert.NotEqual(AcceptanceCohortGateOutcome.InfrastructureFailure, receipt.Outcome);
        Assert.Equal(0, scenario.SuccessorLaunches);
        Assert.Equal(0, scenario.Cancels);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void AdvancedIntegrationHead_RefusesAndCancelsOriginalLease()
    {
        using var scenario = new Scenario();
        var original = scenario.LaunchFirstGeneration();
        AssertImmediateHandoff(scenario);
        var head = scenario.AdvanceIntegrationHead();
        Assert.NotEqual(original.MainRevision, head);

        scenario.FirstAlive = false;
        var successor = scenario.Successor();
        var driver = scenario.Driver(successor);
        scenario.Now = scenario.Now.AddSeconds(1);
        scenario.Tick(driver);

        var refusal = Assert.Single(scenario.SuccessorEvents,
            line => line.StartsWith("ACCEPTANCE_COHORT_ADOPTION_REFUSED ", StringComparison.Ordinal));
        foreach (var member in original.Members)
            Assert.Contains(member.GoalId, refusal, StringComparison.Ordinal);
        Assert.Contains($"reason=identity-changed field={scenario.Workspace.IntegrationBranch}",
            refusal, StringComparison.Ordinal);
        Assert.Equal(1, scenario.Cancels);
        Assert.Equal("Cancelled", scenario.Lease.Read(AcceptanceHolderBinding.For(original)).Phase);
        var saved = ConductorGroupedGateAttemptCoordinator.Read(original.MetadataPath);
        Assert.NotNull(saved.ReconciledAt);
        Assert.StartsWith("refused:identity-changed", saved.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.SuccessorEvents,
            line => line.StartsWith("ACCEPTANCE_COHORT_ADOPTED ", StringComparison.Ordinal) &&
                line.Contains($"identity={original.IdentityValue}", StringComparison.Ordinal));
        Assert.All(scenario.LaunchedBySuccessor, attempt => Assert.Equal(head, attempt.MainRevision));
    }

    private static void AssertImmediateHandoff(Scenario scenario)
    {
        var kernel = RelaunchDrainLoopSupport.LandingKernel();
        var handoffs = 0;
        var sleeps = 0;
        scenario.FirstEvents.Clear();
        var output = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
            utcNow: () => scenario.Now,
            countLiveGroupedGates: () => scenario.FirstDriver!.CountLiveGroupedGateAttempts(relaunchBlockingOnly: true),
            selfRelaunchEnabled: true,
            selfRelaunch: _ => { handoffs++; return ConductorLiveGroupedGateCountTests.Handoff(); }).Run(
                kernel, ConductorLiveGroupedGateCountTests.LandingDriver(), ConductorAutonomyPolicy.Conservative,
                Path.Combine(scenario.Repo, "stop"), maxIterations: 5,
                watchInterval: TimeSpan.FromSeconds(1), keepAliveWhenIdle: true,
                sleepFunc: _ => { sleeps++; scenario.Now = scenario.Now.AddSeconds(1); return true; }));

        Assert.Equal(1, handoffs);
        Assert.Equal(0, sleeps);
        Assert.DoesNotContain("LOOP_RELAUNCH_DRAIN", output, StringComparison.Ordinal);
        Assert.Single(output.Split(Environment.NewLine),
            line => line.Contains("LOOP_RELAUNCH_REBUILD", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.FirstEvents,
            line => line.StartsWith("ACCEPTANCE_HOLDER_", StringComparison.Ordinal));
    }

    internal sealed class Scenario : IDisposable
    {
        private const int FirstGeneration = 91001;
        private const int SecondGeneration = 91002;
        private const int HolderPid = 92001;
        private const string ImagePath = "C:\\dotnet.exe";
        private readonly AgentOrchestratorKernel _kernel;
        private readonly GoalWorktreeCleanupHooks _cleanup;
        private readonly SpawnProcessIdentity _first = new(FirstGeneration, DateTimeOffset.UnixEpoch, ImagePath);
        private readonly SpawnProcessIdentity _second = new(SecondGeneration, DateTimeOffset.UnixEpoch, ImagePath);
        private readonly SpawnProcessIdentity _holder = new(HolderPid, DateTimeOffset.UnixEpoch, ImagePath);
        internal string Repo { get; }
        internal OrchestratorWorkspace Workspace { get; }
        internal Goal[] Goals { get; }
        internal string AttemptRoot => Path.Combine(Workspace.OrchestratorDirectory, "grouped-gate-attempts");
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch.AddDays(1);
        internal bool FirstAlive { get; set; } = true;
        internal bool HolderAlive { get; set; } = true;
        internal int Cancels { get; private set; }
        internal int SuccessorLaunches => LaunchedBySuccessor.Count;
        internal List<ConductorGroupedGateAttempt> LaunchedBySuccessor { get; } = [];
        internal List<string> FirstEvents { get; } = [];
        internal List<string> SuccessorEvents { get; } = [];
        internal AcceptanceHolderLease Lease { get; }
        internal ConductorDriver? FirstDriver { get; private set; }

        internal Scenario()
        {
            Repo = CreateReducedAcceptanceCohortRepository();
            try
            {
                AddAcceptanceManifest(Repo);
                _kernel = new AgentOrchestratorKernel();
                Goals = Enumerable.Range(0, 2).Select(index =>
                    CreateCompletedGoal(_kernel, $"Relaunch member {index}", Repo)).ToArray();
                var paths = new[] { "tests/Mcg.AgentOrchestrator.Core.Tests/First.cs",
                    "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Second.cs" };
                for (var index = 0; index < Goals.Length; index++)
                {
                    var revision = CreateWorktreeCandidate(Repo, Goals[index].Id, paths[index], $"member-{index}");
                    _kernel.RecordGoalRefinement(Goals[index].Id, new RefinedSpec(Goals[index].Objective,
                        ["The acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                        { AcceptanceGateOwnedAcceptanceCriteria = ["The acceptance gate passes"] });
                    _kernel.MapCriterionEvidenceOwner(Goals[index].Id, 0, 1,
                        CriterionEvidenceOwner.Acceptance, "test", CriterionEvidenceScopes.FullAcceptanceGate,
                        expectedCandidateSha: revision);
                }
                Workspace = OrchestratorWorkspace.ForDirectory(Repo);
                _cleanup = CreateIsolatedCleanupContext(Repo).Hooks;
                Lease = new AcceptanceHolderLease(utcNow: () => Now,
                    classify: identity => identity == _second || (identity == _first && FirstAlive) ||
                        (identity == _holder && HolderAlive)
                        ? SpawnTrackedProcessStatus.LiveMatch : SpawnTrackedProcessStatus.DeadOrRecycled,
                    attach: _ => new EmptyContainment(),
                    job: (_, identity, status, cancel) =>
                    {
                        Assert.Equal(_holder, identity);
                        if (cancel) Cancels++;
                        return new(status == SpawnTrackedProcessStatus.LiveMatch,
                            cancel || status == SpawnTrackedProcessStatus.DeadOrRecycled, "scenario-holder");
                    },
                    eventSink: line => (FirstAlive ? FirstEvents : SuccessorEvents).Add(line),
                    stopUnclaimed: _ => throw new InvalidOperationException("Holder must claim its lease"));
            }
            catch { DeleteDirectory(Repo); throw; }
        }

        internal ConductorDriver Driver(ConductorGroupedGateAttemptCoordinator coordinator)
        {
            var driver = new ConductorDriver(_kernel, Workspace, new SequenceAcceptanceVerifier([]),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: _cleanup);
            driver.EnableOwnedGroupedGateAttempts(coordinator);
            return driver;
        }

        internal ConductorGroupedGateAttempt LaunchFirstGeneration()
        {
            var launches = 0;
            var first = new ConductorGroupedGateAttemptCoordinator(AttemptRoot,
                generationId: FirstGeneration, utcNow: () => Now,
                launch: attempt =>
                {
                    launches++;
                    using var claim = Lease.Claim(AcceptanceHolderBinding.For(attempt), _holder);
                    return new(HolderPid, _holder.StartedAt, _holder.ImagePath);
                },
                isProcessAlive: pid => (pid == HolderPid && HolderAlive) || (pid == FirstGeneration && FirstAlive),
                classifyHolder: _ => HolderAlive ? SpawnTrackedProcessStatus.LiveMatch : SpawnTrackedProcessStatus.DeadOrRecycled,
                holderLease: Lease, generationIdentity: _first, eventSink: FirstEvents.Add);
            FirstDriver = Driver(first);
            Assert.Contains("outcome=inflight", Tick(FirstDriver).Detail, StringComparison.Ordinal);
            var original = Assert.Single(first.ReadAll());
            Assert.Equal(1, launches);
            Assert.Equal("cohort", original.Kind);
            Assert.Equal(1, original.HolderContractVersion);
            Assert.Equal(2, original.Members.Count);
            Assert.Equal(_holder, Lease.Read(AcceptanceHolderBinding.For(original)).Holder);
            return original;
        }

        internal ConductorGroupedGateAttemptCoordinator Successor() => new(AttemptRoot,
            generationId: SecondGeneration, utcNow: () => Now,
            launch: attempt =>
            {
                LaunchedBySuccessor.Add(attempt);
                using var claim = Lease.Claim(AcceptanceHolderBinding.For(attempt), _holder);
                return new(HolderPid, _holder.StartedAt, _holder.ImagePath);
            },
            isProcessAlive: pid => pid == HolderPid && HolderAlive,
            classifyHolder: _ => HolderAlive ? SpawnTrackedProcessStatus.LiveMatch : SpawnTrackedProcessStatus.DeadOrRecycled,
            holderLease: Lease, generationIdentity: _second, eventSink: SuccessorEvents.Add);

        internal ConductorAcceptanceCohortRunResult Tick(ConductorDriver driver) =>
            driver.RunAcceptanceCohort(ProjectSelection(driver, Goals[0], Goals[1]), Goals,
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true);

        internal string AdvanceIntegrationHead()
        {
            File.WriteAllText(Path.Combine(Repo, "unrelated.txt"), "Landing advanced the integration branch");
            RunGit(Repo, "add", "unrelated.txt");
            RunGit(Repo, "commit", "-m", "Advance integration head");
            return RunGitOutput(Repo, "rev-parse", Workspace.IntegrationBranch).Trim();
        }

        internal void PublishPassingReceipt(ConductorGroupedGateAttempt attempt)
        {
            var identity = AcceptanceCohortIdentity.Create(new ConductorAcceptanceCohortSelection(
                attempt.Members.Select(member => member.ToProjection(attempt.MainRevision)).ToArray(), []).BindMembers(),
                attempt.MainRevision, attempt.CombinedTreeRevision, attempt.ManifestIdentity);
            Assert.Equal(attempt.IdentityValue, identity.Value);
            new CohortAcceptanceStore(Path.Combine(Workspace.OrchestratorDirectory, "cohort-acceptance.db"))
                .SaveGateReceipt(new AcceptanceCohortReceipt("relaunch-adopted", identity,
                    AcceptanceCohortGateOutcome.Passed, Now, 1, [], 0,
                    [WritePassingTrx(Repo, "relaunch-adopted.trx")], ValidForLanding: true));
            File.WriteAllText(attempt.ResultPath, "passed");
            File.WriteAllText(attempt.ExitCodePath, "0");
        }

        public void Dispose() => DeleteDirectory(Repo);
    }

    private sealed class EmptyContainment : IDisposable
    {
        public void Dispose() { }
    }
}

internal sealed class RelaunchDrainLoopSupport : ConductorBatchLoopTests
{
    private RelaunchDrainLoopSupport(ITestOutputHelper output) : base(output) { }

    internal static AgentOrchestratorKernel LandingKernel()
    {
        var kernel = new AgentOrchestratorKernel();
        CreateVerifiedSimpleGoal(kernel, "Update conductor runtime");
        return kernel;
    }
}
