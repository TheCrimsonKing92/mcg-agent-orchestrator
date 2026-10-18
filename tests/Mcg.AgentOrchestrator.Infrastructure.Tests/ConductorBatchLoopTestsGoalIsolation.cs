using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsGoalIsolation(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public async Task PersistentCliIntentDefersItsTickThenTrainExcludesSoloWithoutGathering()
    {
        using var isolated = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        using var fixture = new GroupingFixture(4, trains: true);
        await fixture.Submit("solo");
        Assert.Equal(GoalIsolation.None, fixture.Goals[0].Isolation);
        // Scope the intent-only tick to A; B/C/D remain Verified for the first admitting tick.
        var first = fixture.Tick(fixture.Goals[0].Id.Value);
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Goals[0].Id.Value));
        Assert.Equal(OperatorIntentStatus.Applied, intent.Status);
        Assert.Equal(GoalIsolation.Solo, fixture.Goals[0].Isolation);
        Assert.Contains(first.ProgressLines ?? [], line => line.Contains("eligible=0 deferred_intent=1", StringComparison.Ordinal));
        Assert.Empty(fixture.Solos);
        Assert.Empty(fixture.Trains);
        fixture.Tick();
        Assert.Equal(fixture.Goals.Skip(1).Select(goal => goal.Id), Assert.Single(fixture.Trains));
        Assert.Contains(fixture.Goals[0].Id, fixture.Solos);
        Assert.Empty(fixture.Pairs);
        Assert.DoesNotContain(fixture.Lines, line => line.Contains("outcome=gathering", StringComparison.Ordinal) &&
            line.Contains("goal=" + fixture.Goals[0].Id.Value[..8], StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SameNormalFixturesGroupOnlyWhenIsolationIsNone(bool train, bool solo)
    {
        using var isolated = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        using var fixture = new GroupingFixture(train ? 4 : 2, train);
        if (solo) { await fixture.Submit("solo"); fixture.Tick(fixture.Goals[0].Id.Value); }
        fixture.Tick();
        if (train)
        {
            var members = Assert.Single(fixture.Trains);
            Assert.Equal(fixture.Goals.Skip(solo ? 1 : 0).Take(3).Select(goal => goal.Id), members);
        }
        else if (solo)
        {
            Assert.Empty(fixture.Pairs);
            Assert.Contains(fixture.Goals[0].Id, fixture.Solos);
        }
        else Assert.Equal(fixture.Goals.Select(goal => goal.Id), Assert.Single(fixture.Pairs));
        if (solo)
            Assert.All(fixture.Trains.Concat(fixture.Pairs), members => Assert.DoesNotContain(fixture.Goals[0].Id, members));
    }

    [Fact]
    public async Task CriticalSoloKeepsSoloOrderAndNeverBecomesAGroupMember()
    {
        using var isolated = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        using var fixture = new GroupingFixture(4, trains: true);
        fixture.Kernel.SetGoalPriority(fixture.Goals[0].Id, GoalPriority.Critical, "test", "setup");
        await fixture.Submit("solo");
        fixture.Tick(fixture.Goals[0].Id.Value);
        fixture.Tick();
        Assert.Equal(fixture.Goals[0].Id, fixture.Solos[0]);
        Assert.All(fixture.Trains.Concat(fixture.Pairs), members => Assert.DoesNotContain(fixture.Goals[0].Id, members));
    }

    [Fact]
    public async Task NoneIntentRestoresGroupingOnTheNextAdmittingTick()
    {
        using var isolated = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        using var fixture = new GroupingFixture(2, trains: false);
        await fixture.Submit("solo");
        fixture.Tick(fixture.Goals[0].Id.Value);
        Assert.Equal(GoalIsolation.Solo, fixture.Goals[0].Isolation);
        await fixture.Submit("none");
        fixture.Tick(fixture.Goals[0].Id.Value);
        Assert.Equal(GoalIsolation.None, fixture.Goals[0].Isolation);
        fixture.Tick();
        Assert.Equal(fixture.Goals.Select(goal => goal.Id), Assert.Single(fixture.Pairs));
        Assert.All(await fixture.Store.ListForGoalAsync(fixture.Goals[0].Id.Value), intent => Assert.Equal(OperatorIntentStatus.Applied, intent.Status));
    }

    [Theory]
    [InlineData(GoalPriority.High)]
    [InlineData(GoalPriority.Critical)]
    [InlineData(GoalPriority.Low)]
    [InlineData(GoalPriority.Normal)]
    public void LandingSeedsNextTickPriorityClearAndCheckpointsTheDependency(GoalPriority priority)
    {
        using var isolated = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        var root = CreateTempDirectory("isolation-terminal");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var dependency = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Dependency");
            kernel.SetGoalPriority(dependency.Id, GoalPriority.Low, "test", "own level");
            var goal = VerifiedAt(kernel, "Landing", 0);
            kernel.SetGoalDependency(goal.Id, dependency.Id);
            kernel.MarkKnownCompletedDependencyGoals([dependency.Id]);
            kernel.SetGoalPriority(goal.Id, priority, "test", "setup");
            if (priority >= GoalPriority.High) Assert.Equal(priority, GoalEffectivePriorityIndex.StoredLevel(dependency));
            var landed = false;
            var recorded = false;
            var cleaned = false;
            var completed = false;
            // Use the constructor seam: MakeDriver omits the production completion callback.
            var driver = new ConductorDriver(
                getFacts: item => item.Id == goal.Id
                    ? new(WorkspaceExists: true, IsMerged: landed, IsRecorded: recorded, IsCleanedUp: cleaned)
                    : new(WorkspaceExists: true),
                getRunningPaidWorkerCount: () => 1, createWorkspace: _ => root,
                dispatchAndStart: _ => throw new InvalidOperationException("Verified goals must not dispatch."),
                startRecordedDispatches: null, buildServerShutdown: null,
                runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                runAdvisorySemanticAcceptance: null, retryTask: null, recordTaskNote: null,
                recordCriterionRetryFeedback: null, clearCriterionRetryFeedback: null,
                rebaseOntoMain: _ => DefaultRebaseSuccess(),
                land: (item, _) => { landed = true; return new(item.Id.Value, item.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); },
                afterSuccessfulLanding: null, record: _ => recorded = true,
                cleanup: _ => { cleaned = true; return new("Workspace cleaned up.", null, [], null); },
                writeEscalation: (_, _, reason) => throw new InvalidOperationException(reason),
                classifyChangeRisk: _ => ChangeRiskTier.DocsOnly,
                completeGoal: item => { completed = true; kernel.CompleteGoal(item.Id, "Conductor completed fixture landing."); },
                getLandingFileScopes: item => [$"src/{item.Id.Value}.cs"], isVerificationGateSatisfied: _ => true,
                parallelAcceptanceAttemptCoordinator: new(root, TrunkBranchName.Default, runInline: true),
                runAcceptanceVerificationWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria);
            var checkpointed = new HashSet<GoalId>();
            var loop = new ConductorBatchLoop();
            void Tick() => loop.Run(kernel, driver, ConductorAutonomyPolicy.Permissive with { MaxConcurrentPaidWorkers = 1 }, NoStopPath(), maxIterations: 1,
                onlyGoalId: goal.IsTerminal ? null : goal.Id.Value, checkpointGoalTick: (_, ids) =>
                {
                    checkpointed.UnionWith(ids);
                    return Durable(ids);
                });
            for (var tick = 0; tick < 6 && !goal.IsTerminal; tick++) Tick();
            Assert.True(landed, "The landing delegate must have executed.");
            Assert.True(recorded && cleaned && completed, "The conductor must record, clean up and invoke completion.");
            Assert.True(goal.IsTerminal, "The real conductor tick must complete the goal before checking its clear.");
            checkpointed.Clear();
            Tick();
            Assert.Equal(GoalPriority.Normal, goal.Priority);
            Assert.Equal(dependency.Priority, GoalEffectivePriorityIndex.StoredLevel(dependency));
            var decisions = goal.Timeline.Where(item => item.Message.StartsWith("Goal priority cleared at terminal:")).ToArray();
            if (priority == GoalPriority.Normal) Assert.Empty(decisions);
            else
            {
                Assert.StartsWith($"Goal priority cleared at terminal: previous={GoalPriorities.Format(priority)} status=", Assert.Single(decisions).Message);
                Assert.Contains(goal.Id, checkpointed);
                if (priority >= GoalPriority.High) Assert.Contains(dependency.Id, checkpointed);
            }
            Tick();
            Assert.Equal(decisions.Length, goal.Timeline.Count(item => item.Message.StartsWith("Goal priority cleared at terminal:")));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    public void SoloLeaderOrFollowerPreventsFollowerGateStart(bool leaderSolo, bool followerSolo, int expectedStarts)
    {
        using var isolated = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        using var liveProbe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var root = CreateTempDirectory("isolation-follower");
        Action wait = () => { };
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var leader = VerifiedAt(kernel, "Leader", 0);
            var follower = VerifiedAt(kernel, "Follower", 1);
            if (leaderSolo) GoalIsolationCommands.Apply(kernel, leader.Id, GoalIsolation.Solo, "test", "setup");
            if (followerSolo) GoalIsolationCommands.Apply(kernel, follower.Id, GoalIsolation.Solo, "test", "setup");
            const string path = "src/Mcg.AgentOrchestrator.Core/Domain/FollowerGates.cs";
            const string main = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string Branch(GoalId id) => id.Value.PadRight(40, 'b');
            var coordinator = ConductorBatchLoopTestsParallelAcceptance.ThreadedAcceptanceAttemptCoordinator(root, out wait);
            var driver = MakeDriver(getFacts: _ => new(WorkspaceExists: true), classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: _ => [path], isVerificationGateSatisfied: _ => true,
                resolveAcceptanceHeads: item => (Branch(item.Id), main),
                gateReadyCandidateProjector: new(id => new(Branch(id), main), _ => new(true, [path]), (_, _, _) => new(true)),
                parallelAcceptanceAttemptCoordinator: coordinator,
                runAcceptanceWithSlot: (_, _) =>
                {
                    entered.Set();
                    Assert.True(release.Wait(TestHangGuard.Bound), "Leader acceptance release did not happen.");
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                });
            var policy = ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = 2, FollowerGatesEnabled = true };
            var candidate = Assert.IsType<ConductorParallelAcceptanceCandidate>(driver.TryBuildParallelAcceptanceCandidate(leader, policy, 0, out _));
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, coordinator.Evaluate(candidate, policy, driver.RunParallelLandingAcceptance).Kind);
            Assert.True(entered.Wait(TestHangGuard.Bound), "Leader acceptance entered event did not happen.");
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running,
                Assert.Single(coordinator.GetCapacityReservingAttempts([leader.Id.Value, follower.Id.Value])).Outcome);
            kernel.BeginGoalAcceptanceVerification(leader.Id, "leader gate running");
            var starts = 0;
            driver.FollowerGateStartOverride = (_, _, _) => { starts++; return new FollowerGateStartOutcome.Started(null!); };
            var lines = new List<string>();
            new ConductorBatchLoop().Run(kernel, driver, policy, NoStopPath(), maxIterations: 1,
                onTick: tick => lines.AddRange(tick.ProgressLines ?? []));
            Assert.Equal(expectedStarts, starts);
            if (leaderSolo)
                Assert.Contains(lines, line => line.StartsWith("FOLLOWER_GATE ") && line.Contains("reason=leader-solo"));
            if (leaderSolo || followerSolo)
                Assert.DoesNotContain(lines, line => line.StartsWith("FOLLOWER_GATE ") && line.Contains("result=started"));
        }
        finally { release.Set(); wait(); TryDeleteDirectory(root); }
    }

    private static Goal VerifiedAt(AgentOrchestratorKernel kernel, string name, int minute)
    {
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), name);
        PassVerificationAt(kernel, goal, goal.Tasks.Single(), DateTimeOffset.Parse("2026-10-01T00:00:00Z").AddMinutes(minute));
        return goal;
    }

    private static IReadOnlyList<GoalSnapshotCheckpointResult> Durable(IReadOnlyCollection<GoalId> ids) =>
        ids.Select(id => new GoalSnapshotCheckpointResult(id.Value, GoalSnapshotCheckpointDisposition.Durable,
            null, "test", "test.db", "checkpoint")).ToArray();

    private sealed class GroupingFixture : IDisposable
    {
        internal readonly AgentOrchestratorKernel Kernel = new();
        internal readonly Goal[] Goals;
        internal readonly List<GoalId[]> Trains = [], Pairs = [];
        internal readonly List<GoalId> Solos = [];
        internal readonly List<string> Lines = [];
        internal readonly SqliteOperatorIntentStore Store;
        private readonly string _root = CreateTempDirectory("isolation-grouping");
        private readonly string? _trainDisabled = Environment.GetEnvironmentVariable("MCG_MERGE_TRAIN_DISABLED");
        private readonly OrchestratorWorkspace _workspace;
        private readonly SqliteOrchestratorStateRepository _repository;
        private readonly ConductorDriver _driver;
        private readonly ConductorBatchLoop _loop;
        internal GroupingFixture(int count, bool trains)
        {
            Environment.SetEnvironmentVariable("MCG_MERGE_TRAIN_DISABLED", trains ? "0" : "1");
            Goals = Enumerable.Range(0, count).Select(index => VerifiedAt(Kernel, ((char)('A' + index)).ToString(), index)).ToArray();
            foreach (var goal in Goals)
            {
                Kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective, ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                    { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                Kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance, "test",
                    CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: goal.Id.Value.PadRight(40, 'b'));
            }
            var paths = Goals.Select((goal, index) => (goal.Id, Path: $"tests/{new[] { "Mcg.AgentOrchestrator.Core.Tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Mcg.AgentOrchestrator.TestSupport", "canary-fixture" }[index]}/Probe.cs"))
                .ToDictionary(item => item.Id, item => (IReadOnlyList<string>)new[] { item.Path });
            const string main = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string Branch(GoalId id) => id.Value.PadRight(40, 'b');
            _driver = MakeDriver(getFacts: _ => new(WorkspaceExists: true), classifyRisk: _ => ChangeRiskTier.DocsOnly,
                isVerificationGateSatisfied: _ => true, getLandingFileScopes: goal => paths[goal.Id],
                resolveAcceptanceHeads: goal => (Branch(goal.Id), main),
                gateReadyCandidateProjector: new(id => new(Branch(id), main), id => new(true, paths[id]), (_, _, _) => new(true)),
                parallelAcceptanceAttemptCoordinator: new(Path.Combine(_root, "attempts"), TrunkBranchName.Default, runInline: true),
                runAcceptanceWithSlot: (goal, _) => { Solos.Add(goal.Id); return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; },
                runMergeTrain: trains ? (selection, _, policy) =>
                {
                    Trains.Add(selection.Members.Select(member => member.GoalId).ToArray());
                    return new(null, Results(selection.Members.Select(member => member.GoalId), policy), [], "outcome=passed");
                } : null,
                runAcceptanceCohort: (selection, _, policy) =>
                {
                    Pairs.Add(selection.Members.Select(member => member.GoalId).ToArray());
                    return new(null, Results(selection.Members.Select(member => member.GoalId), policy), "outcome=passed");
                });
            _workspace = OrchestratorWorkspace.ForDirectory(_root);
            _repository = OpenStateRepository(_workspace.SqliteStatePath);
            Store = SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory);
            _loop = new(operatorIntents: new OperatorIntentCoordinator(Store));
            Assert.True(_driver.AcceptanceCohortsEnabled);
            Assert.All(Goals, goal => Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                _driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive)));
        }
        internal async Task Submit(string mode)
        {
            await _repository.SaveAsync(Kernel);
            IReadOnlyList<AgentDefinition> agents = DefaultAgents();
            var profiles = WorkerProfileCatalog.Default();
            Goal? current = null;
            InfrastructureTestSupport.CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
                ["goal-isolation", Goals[0].Id.Value[..8], mode], _repository, _workspace, ref agents,
                new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
        }
        internal BatchTickSummary Tick(string? onlyGoal = null)
        {
            BatchTickSummary? result = null;
            _loop.Run(Kernel, _driver, ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = 2 }, NoStopPath(),
                maxIterations: 1, onlyGoalId: onlyGoal, checkpointGoalTick: (kernel, ids) =>
                {
                    _repository.SaveAsync(kernel).GetAwaiter().GetResult();
                    return Durable(ids);
                },
                onTick: tick => { result = tick; Lines.AddRange(tick.ProgressLines ?? []); });
            return Assert.IsType<BatchTickSummary>(result);
        }
        private static Dictionary<string, ConductorAdvanceResult> Results(IEnumerable<GoalId> ids, ConductorAutonomyPolicy policy) =>
            ids.ToDictionary(id => id.Value, id => new ConductorAdvanceResult(id.Value, id.Value[..8], policy.Name,
                new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "admitted by group")));
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("MCG_MERGE_TRAIN_DISABLED", _trainDisabled);
            TryDeleteDirectory(_root);
        }
    }
}
