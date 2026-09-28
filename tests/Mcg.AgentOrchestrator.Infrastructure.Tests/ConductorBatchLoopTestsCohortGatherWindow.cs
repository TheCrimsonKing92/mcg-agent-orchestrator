using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsCohortGatherWindow : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsCohortGatherWindow(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void CompatibleRunningReviewerHoldsLoneReadyGoalThenCohorts()
    {
        var scenario = new Scenario();
        var held = scenario.Tick();
        Assert.Equal(0, scenario.SoloCalls);
        Assert.Contains(held.ProgressLines!, line => line.Contains("outcome=gathering") &&
            line.Contains(scenario.Ready.Id.Value[..8]) &&
            line.Contains(scenario.Reviewing!.Id.Value[..8]) &&
            line.Contains("remaining_seconds=480"));

        var reviewerTask = scenario.Kernel.GetGoal(scenario.Reviewing.Id).Tasks.Single();
        scenario.Kernel.RecordTaskVerification(scenario.Reviewing.Id, reviewerTask.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 0, "ok", "", scenario.Now));
        scenario.Now = scenario.Now.AddSeconds(10);
        scenario.Tick();
        Assert.Equal(0, scenario.SoloCalls);
        Assert.Equal(
            new[] { scenario.Ready.Id, scenario.Reviewing.Id }.OrderBy(id => id.Value),
            scenario.CohortMembers!.OrderBy(id => id.Value));
    }

    [Xunit.Fact]
    public void WindowExpiryAdmitsSoloOnFirstElapsedTick()
    {
        var scenario = new Scenario();
        scenario.Tick();
        scenario.Now = scenario.Now.AddSeconds(479);
        Assert.Contains(scenario.Tick().ProgressLines!, line => line.Contains("remaining_seconds=1"));
        Assert.Equal(0, scenario.SoloCalls);
        scenario.Now = scenario.Now.AddSeconds(1);
        scenario.Tick();
        Assert.Equal(1, scenario.SoloCalls);
    }

    [Xunit.Fact]
    public void ReviewerEndAdmitsSoloImmediately()
    {
        var scenario = new Scenario();
        scenario.Tick();
        scenario.ReplaceReviewerStatus(WorkTaskStatus.Failed);
        var tick = scenario.Tick();
        Assert.Equal(1, scenario.SoloCalls);
        Assert.DoesNotContain(tick.ProgressLines!, line => line.Contains("outcome=gathering"));
    }

    [Xunit.Fact]
    public void IncompatibleLandingScopeAdmitsSoloImmediately()
    {
        var scenario = new Scenario();
        scenario.Tick();
        scenario.Paths[scenario.Reviewing!.Id] = scenario.Paths[scenario.Ready.Id];
        var tick = scenario.Tick();
        Assert.Equal(1, scenario.SoloCalls);
        Assert.DoesNotContain(tick.ProgressLines!, line => line.Contains("outcome=gathering"));
    }

    [Xunit.Fact]
    public void DisabledWindowAdmitsSoloImmediately()
    {
        var scenario = new Scenario();
        var tick = scenario.Tick(ConductorAutonomyPolicy.Permissive with { AcceptanceCohortGatherWindowSeconds = 0 });
        Assert.Equal(1, scenario.SoloCalls);
        Assert.DoesNotContain(tick.ProgressLines!, line => line.Contains("outcome=gathering"));
    }

    [Xunit.Fact]
    public void NoReviewerAdmitsSoloImmediately()
    {
        var scenario = new Scenario(includeReviewingGoal: false);
        var tick = scenario.Tick();
        Assert.Equal(1, scenario.SoloCalls);
        Assert.DoesNotContain(tick.ProgressLines!, line => line.Contains("outcome=gathering"));
    }

    [Xunit.Fact]
    public void LiveAcceptanceOccupantDoesNotStartGathering()
    {
        var scenario = new Scenario();
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            [new GateLoadContextProbe.LiveGateOccupant(4102, "other-goal", 1, TimeSpan.Zero)]);
        var tick = scenario.Tick();
        Assert.DoesNotContain(tick.ProgressLines!, line => line.Contains("outcome=gathering"));
    }

    [Xunit.Fact]
    public void OtherEligibleGoalWithUnknownProjectionDoesNotPreventGathering()
    {
        var scenario = new Scenario(includeExcludedReadyGoal: true);
        var tick = scenario.Tick();
        Assert.DoesNotContain(scenario.Ready.Id, scenario.SoloGoals);
        Assert.Contains(tick.ProgressLines!, line => line.Contains("outcome=gathering") &&
            line.Contains(scenario.Ready.Id.Value[..8]));
    }

    [Xunit.Fact]
    public void ShorterConductorRunDeadlineAdmitsSolo()
    {
        var scenario = new Scenario();
        var tick = scenario.Tick(maxDuration: TimeSpan.FromSeconds(300));
        Assert.Equal(1, scenario.SoloCalls);
        Assert.DoesNotContain(tick.ProgressLines!, line => line.Contains("outcome=gathering"));
    }

    private sealed class Scenario
    {
        internal AgentOrchestratorKernel Kernel { get; private set; }
        internal Goal Ready { get; }
        internal Goal? Reviewing { get; }
        internal Dictionary<GoalId, IReadOnlyList<string>> Paths { get; } = new();
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-28T14:00:00Z");
        internal int SoloCalls { get; private set; }
        internal List<GoalId> SoloGoals { get; } = [];
        internal IReadOnlyList<GoalId>? CohortMembers { get; private set; }
        private readonly ConductorDriver _driver;
        private readonly ConductorBatchLoop _loop;

        internal Scenario(bool includeReviewingGoal = true, bool includeExcludedReadyGoal = false)
        {
            Kernel = new AgentOrchestratorKernel();
            Ready = CreateVerifiedSimpleGoal(Kernel,
                "Update src/Mcg.AgentOrchestrator.Core/Application/GatherReady.cs");
            Paths[Ready.Id] = ["src/Mcg.AgentOrchestrator.Core/Application/GatherReady.cs"];
            if (includeReviewingGoal)
            {
                Reviewing = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, DefaultAgents(),
                    "Update src/Mcg.AgentOrchestrator.App/Dashboard/Api/GatherReviewing.cs");
                Paths[Reviewing.Id] = ["src/Mcg.AgentOrchestrator.App/Dashboard/Api/GatherReviewing.cs"];
                ReplaceReviewerStatus(WorkTaskStatus.Running);
            }
            if (includeExcludedReadyGoal)
            {
                var excluded = CreateVerifiedSimpleGoal(Kernel,
                    "Update src/Mcg.AgentOrchestrator.App/Orchestration/GatherExcluded.cs");
                Paths[excluded.Id] = ["src/Mcg.AgentOrchestrator.App/Orchestration/GatherExcluded.cs"];
            }
            const string main = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var projector = new GateReadyCandidateProjector(
                id => new GateReadyCandidateRevisionPair(id.Value.PadRight(40, 'b')[..40], main),
                id => new GateReadyLandingScopeObservation(true, Paths[id]),
                (_, _, _) => new GateReadyMergeTreeObservation(true));
            _driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    SoloCalls++;
                    SoloGoals.Add(goal.Id);
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                classifyRisk: _ => ChangeRiskTier.Behavior,
                getLandingFileScopes: goal => Paths[goal.Id],
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector,
                runAcceptanceCohort: (selection, _, policy) =>
                {
                    CohortMembers = selection.Members.Select(member => member.GoalId).ToArray();
                    return new ConductorAcceptanceCohortRunResult(
                        null,
                        selection.Members.ToDictionary(
                            member => member.GoalId.Value,
                            member => new ConductorAdvanceResult(
                                member.GoalId.Value,
                                member.GoalId.Value[..8],
                                policy.Name,
                                new ConductorAdvanceOutcome.Executed(
                                    GoalLifecycleState.Verified, "admitted by cohort")),
                            StringComparer.Ordinal),
                        "outcome=passed");
                });
            _loop = new ConductorBatchLoop(utcNow: () => Now);
        }

        internal void ReplaceReviewerStatus(WorkTaskStatus status)
        {
            var snapshot = Kernel.ExportSnapshot();
            Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal.Id == Reviewing!.Id.Value
                    ? goal with
                    {
                        Tasks = goal.Tasks.Select(task => task with
                        {
                            RequiredRole = AgentRole.Reviewer,
                            Status = status,
                            LastDispatch = new TaskDispatchSnapshot("test-worker", "test.exe", "C:\\tmp", Now)
                        }).ToArray()
                    }
                    : goal).ToArray()
            });
        }

        internal BatchTickSummary Tick(ConductorAutonomyPolicy? policy = null, TimeSpan? maxDuration = null)
        {
            BatchTickSummary? tick = null;
            _loop.Run(Kernel, _driver, policy ?? ConductorAutonomyPolicy.Permissive,
                NoStopPath(), maxIterations: 1, onTick: current => tick = current,
                maxDuration: maxDuration);
            return tick!;
        }
    }
}
