using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsPriorityScheduling : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsPriorityScheduling(ITestOutputHelper output) : base(output) { }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DependencyPriorityOrdersGateThenIntegratesBeforeWorker(bool high)
    {
        using var isolation = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        using var fixture = new SchedulingFixture(high);
        Assert.Equal(AgentRole.Developer, fixture.H.Tasks.Single().RequiredRole);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.H.Tasks.Single().Status);
        var checkpoints = new List<GoalSnapshot>();
        var loop = new ConductorBatchLoop(utcNow: () => fixture.Now,
            conductEventLogWriter: new ConductEventLogWriter(fixture.Log));
        var driver = fixture.Driver();
        loop.Run(fixture.Kernel, driver, SchedulingFixture.Policy, NoStopPath(), maxIterations: 1,
            checkpointGoalTick: (kernel, ids) => Checkpoint(kernel, ids, checkpoints));
        Assert.Equal(high ? new[] { fixture.D.Id, fixture.N1.Id, fixture.N2.Id }
            : new[] { fixture.N1.Id, fixture.N2.Id, fixture.D.Id }, fixture.Accepted);
        Assert.Contains(fixture.H.Timeline, evt => evt.Message.Contains("waiting on dependency", StringComparison.Ordinal));
        Assert.Empty(fixture.Dispatched);
        Assert.Contains(fixture.D.Id, fixture.Landed);
        fixture.Kernel.CompleteGoal(fixture.D.Id, "Fixture landing completed");
        fixture.Kernel.MarkKnownCompletedDependencyGoals([fixture.D.Id]);
        fixture.BlockWorkers = false;
        fixture.Now += TimeSpan.FromMinutes(1);
        loop.Run(fixture.Kernel, driver, SchedulingFixture.Policy, NoStopPath(), maxIterations: 1,
            checkpointGoalTick: (kernel, ids) => Checkpoint(kernel, ids, checkpoints));
        var expected = high ? fixture.H : fixture.W;
        Assert.Equal(new[] { expected.Id }, fixture.Dispatched);
        Assert.Equal(new[] { $"integrate:{expected.Id}", $"dispatch:{expected.Id}" }, fixture.WorkerEvents);
        var events = ReadPriorityEvents(fixture.Log);
        if (high)
        {
            var gate = Assert.Single(events.Where(line => line.StartsWith("PRIORITY_ADMISSION stage=gate")));
            Assert.Contains($"goal={fixture.D.Id.Value[..8]}", gate);
            Assert.Contains($"inheritedFrom={fixture.H.Id.Value[..8]}", gate);
            var dispatch = Assert.Single(events.Where(line => line.StartsWith("PRIORITY_ADMISSION stage=dispatch")));
            Assert.Contains($"goal={fixture.H.Id.Value[..8]}", dispatch);
            Assert.Contains($"overtook={fixture.W.Id.Value[..8]}:normal", dispatch);
        }
        else
        {
            Assert.Empty(events);
            Assert.NotEmpty(checkpoints);
            Assert.All(checkpoints, goal => Assert.Null(goal.PriorityScheduling));
        }
    }

    [Fact]
    public void RelaunchDriftRepairOrdersAdmissionAndCheckpointsDependency()
    {
        using var isolation = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        using var fixture = new SchedulingFixture(true);
        var snapshot = fixture.Kernel.ExportSnapshot();
        Assert.Equal("high", snapshot.Goals.Single(goal => goal.Id == fixture.D.Id.Value).PriorityScheduling!.EffectiveLevel);
        fixture.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == fixture.D.Id.Value
                ? goal with { PriorityScheduling = null } : goal).ToArray()
        });
        var checkpoints = new List<GoalSnapshot>();
        new ConductorBatchLoop(utcNow: () => fixture.Now,
            conductEventLogWriter: new ConductEventLogWriter(fixture.Log)).Run(
                fixture.Kernel, fixture.Driver(), SchedulingFixture.Policy, NoStopPath(), maxIterations: 1,
                checkpointGoalTick: (kernel, ids) => Checkpoint(kernel, ids, checkpoints));
        Assert.Equal(fixture.D.Id, fixture.Accepted[0]);
        var drift = Assert.Single(ReadPriorityEvents(fixture.Log).Where(line => line.StartsWith("PRIORITY_DRIFT")));
        Assert.Contains($"goal={fixture.D.Id.Value[..8]} stored=normal computed=high", drift);
        Assert.Contains(checkpoints, goal => goal.Id == fixture.D.Id.Value && goal.PriorityScheduling?.EffectiveLevel == "high");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void GroupingKeepsHighHeadAndPreservesNormalPair(bool high, bool compatible)
    {
        using var isolation = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        var root = CreateTempDirectory("priority-groups");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var a = VerifiedAt(kernel, "A", 0);
            var b = VerifiedAt(kernel, "B", 1);
            var c = VerifiedAt(kernel, "C", 2);
            if (high) kernel.SetGoalPriority(c.Id, GoalPriority.High, "test", "setup");
            var paths = new Dictionary<GoalId, IReadOnlyList<string>>
            {
                [a.Id] = ["tests/Mcg.AgentOrchestrator.Core.Tests/Alpha.cs"],
                [b.Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Beta.cs"],
                [c.Id] = compatible ? ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Beta.cs"]
                    : ["tests/Mcg.AgentOrchestrator.Core.Tests/Alpha.cs", "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Beta.cs"]
            };
            var pairs = new List<GoalId[]>();
            var solos = new List<GoalId>();
            var driver = GroupingDriver(kernel, root, paths, pairs, solos);
            Assert.True(driver.AcceptanceCohortsEnabled);
            Assert.All(new[] { a, b, c }, goal => Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive)));
            var snapshots = new List<GoalSnapshot>();
            var log = Path.Combine(root, "events.jsonl");
            new ConductorBatchLoop(conductEventLogWriter: new ConductEventLogWriter(log)).Run(
                kernel, driver, ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = 1 }, NoStopPath(), maxIterations: 1,
                checkpointGoalTick: (current, ids) => Checkpoint(current, ids, snapshots));
            if (high && !compatible)
            {
                Assert.Empty(pairs);
                Assert.Equal(c.Id, solos[0]);
            }
            else Assert.Equal(high ? new[] { c.Id, a.Id } : new[] { a.Id, b.Id }, Assert.Single(pairs));
            if (!high)
            {
                Assert.Empty(ReadPriorityEvents(log));
                Assert.NotEmpty(snapshots);
                Assert.All(snapshots, goal => Assert.Null(goal.PriorityScheduling));
            }
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public void DependencyPairNeverEntersCohortOrTrain()
    {
        using var isolation = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        var root = CreateTempDirectory("priority-dependent-group");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var s = VerifiedAt(kernel, "S", 0);
            var t = VerifiedAt(kernel, "T", 1);
            kernel.SetGoalDependency(s.Id, t.Id);
            var pairs = new List<GoalId[]>();
            var solos = new List<GoalId>();
            var paths = new Dictionary<GoalId, IReadOnlyList<string>>
            {
                [s.Id] = ["tests/Mcg.AgentOrchestrator.Core.Tests/S.cs"],
                [t.Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/T.cs"]
            };
            var driver = GroupingDriver(kernel, root, paths, pairs, solos);
            var candidates = new[] { s, t }.Select(goal => new ConductorSpeculativeAcceptanceCandidate(
                goal.Id, driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive))).ToArray();
            Assert.All(candidates, candidate => Assert.IsType<GateReadyCandidateProjectionResult.Ready>(candidate.ProjectionResult));
            Assert.NotNull(ConductorAcceptanceCohortSelector.Select(candidates).Selection);
            new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Permissive, NoStopPath(), maxIterations: 1);
            Assert.Empty(pairs);
            Assert.DoesNotContain(s.Id, solos);
            Assert.Contains(t.Id, solos);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public void CriticalGoalCannotPreemptLiveAttemptOrBypassDependency()
    {
        using var isolation = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        var root = CreateTempDirectory("priority-live");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var occupant = CreateVerifiedSimpleGoal(kernel, "Occupant");
            var critical = CreateVerifiedSimpleGoal(kernel, "Critical");
            var blocked = CreateVerifiedSimpleGoal(kernel, "Blocked critical");
            var blockedWorker = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Blocked Developer");
            var dependency = kernel.CreateGoal("Unlanded dependency");
            kernel.SetGoalDependency(blocked.Id, dependency.Id);
            kernel.SetGoalDependency(blockedWorker.Id, dependency.Id);
            kernel.SetGoalPriority(critical.Id, GoalPriority.Critical, "test", "setup");
            kernel.SetGoalPriority(blocked.Id, GoalPriority.Critical, "test", "setup");
            kernel.SetGoalPriority(blockedWorker.Id, GoalPriority.Critical, "test", "setup");
            var launches = 0;
            var attempts = new ConductorParallelAcceptanceAttemptCoordinator(root, TrunkBranchName.Default,
                isProcessAlive: _ => true, launchOwnedProcess: _ =>
                {
                    launches++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(1234);
                });
            kernel.BeginGoalAcceptanceVerification(occupant.Id, "seed live gate");
            var started = attempts.Evaluate(ConductorParallelAcceptanceCandidate.Create(occupant, 0, ["src/Occupant.cs"]),
                ConductorAutonomyPolicy.Conservative, PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            var dispatched = new List<GoalId>();
            var admitted = new List<GoalId>();
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningCount: () => 0,
                dispatchAndStart: goal => { dispatched.Add(goal.Id); return DispatchStartOutcome.Started(); },
                runAcceptanceWithSlot: (goal, _) => { admitted.Add(goal.Id); return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; },
                getLandingFileScopes: goal => [$"src/{goal.Id.Value}.cs"], parallelAcceptanceAttemptCoordinator: attempts);
            new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 },
                NoStopPath(), maxIterations: 1);
            Assert.Contains(critical.Timeline, evt => evt.Message.Contains("acceptance width", StringComparison.Ordinal));
            Assert.Contains(blocked.Timeline, evt => evt.Message.Contains("waiting on dependency", StringComparison.Ordinal));
            Assert.DoesNotContain(blocked.Id, admitted);
            Assert.DoesNotContain(blocked.Id, dispatched);
            Assert.DoesNotContain(blockedWorker.Id, dispatched);
            Assert.Equal(WorkTaskStatus.Assigned, blockedWorker.Tasks.Single().Status);
            Assert.Contains(blockedWorker.Timeline, evt => evt.Message.Contains("waiting on dependency", StringComparison.Ordinal));
            Assert.Empty(admitted);
            Assert.Equal(1, launches);
            var live = Assert.Single(attempts.GetUnreconciledAttempts([occupant.Id.Value]));
            Assert.Equal(started.Attempt.AttemptId, live.AttemptId);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, live.Outcome);
            Assert.Null(live.ReconciledAt);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public void ReconciledReceiptDoesNotCountAsNewPriorityAdmission()
    {
        using var isolation = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        var root = CreateTempDirectory("priority-receipt");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var normal = VerifiedAt(kernel, "Normal waiter", 0);
            var high = VerifiedAt(kernel, "Prior High admission", 1);
            kernel.SetGoalPriority(high.Id, GoalPriority.High, "test", "setup");
            var attempts = new ConductorParallelAcceptanceAttemptCoordinator(root, TrunkBranchName.Default, runInline: true);
            var seed = attempts.Evaluate(ConductorParallelAcceptanceCandidate.Create(high, 0, [$"src/{high.Id.Value}.cs"]),
                ConductorAutonomyPolicy.Conservative, PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, seed.Attempt.Outcome);
            Assert.Null(seed.Attempt.ReconciledAt);
            var admitted = new List<GoalId>();
            var log = Path.Combine(root, "events.jsonl");
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getLandingFileScopes: goal => [$"src/{goal.Id.Value}.cs"],
                parallelAcceptanceAttemptCoordinator: attempts,
                runAcceptanceWithSlot: (goal, _) => { admitted.Add(goal.Id); return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; });
            new ConductorBatchLoop(conductEventLogWriter: new ConductEventLogWriter(log)).Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 }, NoStopPath(), maxIterations: 1);
            Assert.Contains(normal.Id, admitted);
            Assert.DoesNotContain(high.Id, admitted);
            Assert.DoesNotContain(ReadPriorityEvents(log), line => line.StartsWith("PRIORITY_ADMISSION stage=gate") &&
                line.Contains($"goal={high.Id.Value[..8]}", StringComparison.Ordinal));
            Assert.Equal(0, normal.PriorityScheduling?.GateOvertakes ?? 0);
        }
        finally { TryDeleteDirectory(root); }
    }

    private static IReadOnlyList<GoalSnapshotCheckpointResult> Checkpoint(AgentOrchestratorKernel kernel,
        IReadOnlyCollection<GoalId> ids, List<GoalSnapshot> snapshots)
    {
        snapshots.AddRange(kernel.ExportSnapshot().Goals.Where(goal => ids.Any(id => id.Value == goal.Id)));
        return ids.Select(id => new GoalSnapshotCheckpointResult(id.Value, GoalSnapshotCheckpointDisposition.Durable,
            null, "test", "test.db", "checkpoint")).ToArray();
    }

    private static string[] ReadPriorityEvents(string path)
    {
        Assert.True(File.Exists(path), "The conductor must write the bound event stream.");
        return File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(evt => evt.GetProperty("eventKind").GetString() == "priority")
            .Select(evt => evt.GetProperty("detail").GetString()!).ToArray();
    }

    private static Goal VerifiedAt(AgentOrchestratorKernel kernel, string name, int minute)
    {
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), name);
        PassVerificationAt(kernel, goal, goal.Tasks.Single(), DateTimeOffset.Parse("2026-10-01T00:00:00Z").AddMinutes(minute));
        return goal;
    }

    private static ConductorDriver GroupingDriver(AgentOrchestratorKernel kernel, string root,
        Dictionary<GoalId, IReadOnlyList<string>> paths, List<GoalId[]> pairs, List<GoalId> solos)
    {
        const string main = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string Branch(GoalId id) => id.Value.PadRight(40, 'b');
        return MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            classifyRisk: _ => ChangeRiskTier.DocsOnly, isVerificationGateSatisfied: _ => true,
            getLandingFileScopes: goal => paths[goal.Id], resolveAcceptanceHeads: goal => (Branch(goal.Id), main),
            gateReadyCandidateProjector: new GateReadyCandidateProjector(id => new(Branch(id), main),
                id => new(true, paths[id]), (_, _, _) => new(true)),
            parallelAcceptanceAttemptCoordinator: new(root, TrunkBranchName.Default, runInline: true),
            runAcceptanceWithSlot: (goal, _) => { solos.Add(goal.Id); return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; },
            runAcceptanceCohort: (selection, _, policy) =>
            {
                pairs.Add(selection.Members.Select(member => member.GoalId).ToArray());
                return new(null, selection.Members.ToDictionary(member => member.GoalId.Value,
                    member => new ConductorAdvanceResult(member.GoalId.Value, member.GoalId.Value[..8], policy.Name,
                        new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "admitted by cohort"))), "outcome=passed");
            });
    }

    private sealed class SchedulingFixture : IDisposable
    {
        internal AgentOrchestratorKernel Kernel = new();
        internal readonly string Root = CreateTempDirectory("priority-scheduling");
        internal string Log => Path.Combine(Root, "events.jsonl");
        internal DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T01:00:00Z");
        internal readonly Goal N1, N2, D, W, H;
        internal readonly List<GoalId> Accepted = [], Dispatched = [];
        internal readonly List<string> WorkerEvents = [];
        internal readonly HashSet<GoalId> Landed = [];
        internal bool BlockWorkers = true;
        internal static ConductorAutonomyPolicy Policy => ConductorAutonomyPolicy.Conservative with
            { AcceptanceWidth = 3, MaxConcurrentPaidWorkers = 1 };

        internal SchedulingFixture(bool high)
        {
            N1 = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, DefaultAgents(), "N1");
            N2 = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, DefaultAgents(), "N2");
            W = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, DefaultAgents(), "W");
            H = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, DefaultAgents(), "H");
            D = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, DefaultAgents(), "D");
            var start = Now.AddHours(-1);
            PassVerificationAt(Kernel, N1, N1.Tasks.Single(), start);
            PassVerificationAt(Kernel, N2, N2.Tasks.Single(), start.AddMinutes(1));
            PassVerificationAt(Kernel, D, D.Tasks.Single(), start.AddMinutes(3));
            Kernel.SetGoalDependency(H.Id, D.Id);
            if (high) Kernel.SetGoalPriority(H.Id, GoalPriority.High, "test", "setup");
        }

        // MakeDriver's constructor seam with the integration callback that its helper does not expose.
        internal ConductorDriver Driver() => new(
            goal => Landed.Contains(goal.Id)
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            () => BlockWorkers ? 1 : Dispatched.Count, _ => Root,
            goal =>
            {
                WorkerEvents.Add($"dispatch:{goal.Id}");
                Dispatched.Add(goal.Id);
                var task = goal.Tasks.Single();
                Kernel.RecordTaskDispatch(goal.Id, task.Id, new("test", "test.exe", Root, Now));
                Kernel.RecordTaskProcessStarted(goal.Id, task.Id, new(100 + Dispatched.Count, "test.exe", Root,
                    Path.Combine(Root, "out"), Path.Combine(Root, "err"), Path.Combine(Root, "exit"), Now, null, null));
                return DispatchStartOutcome.Started();
            }, null, null, _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria, null, null, null, null, null,
            _ => DefaultRebaseSuccess(),
            (goal, _) =>
            {
                Landed.Add(goal.Id);
                return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            }, null, _ => { }, _ => new GoalWorktreeRemoveResult("clean", null, [], null), (_, _, _) => { }, _ => null,
            getLandingFileScopes: goal => [$"src/{goal.Id.Value}.cs"],
            runAcceptanceVerificationWithSlot: (goal, _) => { Accepted.Add(goal.Id); return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; },
            parallelAcceptanceAttemptCoordinator: new(Path.Combine(Root, "attempts"), TrunkBranchName.Default, runInline: true),
            integrateMainBeforeDeveloperDispatch: goal =>
            {
                WorkerEvents.Add($"integrate:{goal.Id}");
                return new(DeveloperBranchIntegrationStatus.Current, "current", []);
            }, utcNow: () => Now);

        public void Dispose() => TryDeleteDirectory(Root);
    }
}
