using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsSetAsideRecovery : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsSetAsideRecovery(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void DependencyRetry_ReadmitsDependentUnderOrdinaryHold()
    {
        var (kernel, dependency) = SimpleGoal("dependency awaiting retry");
        var dependent = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "dependent");
        kernel.SetGoalDependency(dependent.Id, dependency.Id);
        kernel.ReportTaskProgress(dependency.Id, dependency.Tasks.Single().Id, WorkTaskStatus.Failed, "Needs retry.");
        var dependentFingerprint = dependent.Tasks.Single().Status;
        var workspaceGoals = new List<GoalId>();
        var dispatchGoals = new List<GoalId>();
        var driver = MakeDriver(
            createWorkspace: goal => { workspaceGoals.Add(goal.Id); return "C:\\goal"; },
            dispatchAndStart: goal => { dispatchGoals.Add(goal.Id); return DispatchStartOutcome.Started(); });
        var retried = false;
        BatchLoopSummary? summary = null;
        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            summary = new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
                maxIterations: 6, watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ =>
                {
                    if (!retried && Decisions(kernel, dependent).Any(message =>
                        message.EndsWith($"held: dependency escalated: {dependency.Id.Value[..8]}", StringComparison.Ordinal)))
                    {
                        kernel.RetryTask(dependency.Id, dependency.Tasks.Single().Id,
                            "Operator repaired dependency.", RetryCause.ContractClarification);
                        Assert.Equal(GoalStatus.Active, kernel.GetGoal(dependency.Id).Status);
                        retried = true;
                    }
                    return false;
                });
        });

        Assert.True(retried);
        Assert.NotNull(summary);
        Assert.Null(summary.Handoff);
        Assert.Equal(dependentFingerprint, kernel.GetGoal(dependent.Id).Tasks.Single().Status);
        var decisions = Decisions(kernel, dependent);
        var held = decisions.Where(message => message.Contains(": held: ", StringComparison.Ordinal)).ToArray();
        Assert.EndsWith($"held: dependency escalated: {dependency.Id.Value[..8]}", held[0]);
        Assert.EndsWith($"held: waiting on dependency {dependency.Id.Value[..8]}", held[1]);
        Assert.Contains($"Set-aside re-admitted: condition=dependency-escalated; dependency={dependency.Id.Value[..8]}; hold=waiting on dependency {dependency.Id.Value[..8]}.", decisions);
        Assert.Contains($"SET_ASIDE_READMITTED condition=dependency-escalated goal={dependent.Id.Value[..8]} dependency={dependency.Id.Value[..8]} reason=waiting_on_dependency_{dependency.Id.Value[..8]}", output);
        Assert.DoesNotContain(dependent.Id, workspaceGoals);
        Assert.DoesNotContain(dependent.Id, dispatchGoals);
    }

    [Xunit.Fact]
    public void DependencyChain_DerivedEscalationDoesNotCascade()
    {
        var (kernel, root) = SimpleGoal("root dependency");
        var middle = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "middle dependency");
        var leaf = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "leaf dependent");
        kernel.SetGoalDependency(middle.Id, root.Id);
        kernel.SetGoalDependency(leaf.Id, middle.Id);
        kernel.ReportTaskProgress(root.Id, root.Tasks.Single().Id, WorkTaskStatus.Failed, "Root escalates.");

        var summary = new ConductorBatchLoop().Run(
            kernel, MakeDriver(), ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 5, watchInterval: TimeSpan.FromMilliseconds(1), sleepFunc: _ => false);

        Assert.Contains(Decisions(kernel, middle), message =>
            message.EndsWith($"held: dependency escalated: {root.Id.Value[..8]}", StringComparison.Ordinal));
        Assert.Contains(Decisions(kernel, leaf), message =>
            message.EndsWith($"held: waiting on dependency {middle.Id.Value[..8]}", StringComparison.Ordinal));
        Assert.DoesNotContain(Decisions(kernel, leaf), message =>
            message.Contains($"dependency escalated: {middle.Id.Value[..8]}", StringComparison.Ordinal));
        // The derived membership still counts as escalated for the other batch consumers.
        Assert.Equal(2, summary.Escalated);
    }

    [Xunit.Theory]
    [Xunit.InlineData(3, 1)]
    [Xunit.InlineData(8, 2)]
    public void AdvanceFault_ThreeFurtherPassesRetryThenAdvance(int iterations, int expectedAttempts)
    {
        var (kernel, goal) = SimpleGoal("transient acceptance fault");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var sleeps = 0;
        var attemptPasses = new List<int>();
        var landed = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: landed),
            runAcceptance: _ =>
            {
                attemptPasses.Add(sleeps);
                if (attemptPasses.Count == 1)
                    throw new InvalidOperationException("generic advance fault");
                return true;
            },
            land: g =>
            {
                landed = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            isVerificationGateSatisfied: _ => true);
        var output = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: iterations, watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => { sleeps++; return false; }));

        Assert.Equal(expectedAttempts, attemptPasses.Count);
        Assert.Equal(0, attemptPasses[0]);
        Assert.Single(Decisions(kernel, goal).Where(message => message.Contains("fault isolating goal", StringComparison.Ordinal)));
        var readmissions = RecoveryDecisions(kernel, goal);
        if (expectedAttempts == 1)
        {
            Assert.Empty(readmissions);
            Assert.False(landed);
        }
        else
        {
            Assert.Equal(3, attemptPasses[1]);
            Assert.Equal("Set-aside re-admitted: condition=advance-fault; fingerprint=generic_advance_fault; passes=3.", Assert.Single(readmissions));
            Assert.Contains($"SET_ASIDE_READMITTED condition=advance-fault goal={goal.Id.Value[..8]} fingerprint=generic_advance_fault ticks=3", output);
            Assert.True(landed);
        }
    }

    [Xunit.Fact]
    public void AdvanceFault_RepeatedSanitizedFingerprintStaysSetAside()
    {
        var (kernel, goal) = SimpleGoal("persistent acceptance fault");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var attempts = 0;
        var sleeps = 0;
        var secondFaultPass = -1;
        var driver = MakeDriver(
            runAcceptance: _ =>
            {
                attempts++;
                if (attempts == 2)
                    secondFaultPass = sleeps;
                throw new InvalidOperationException(attempts == 1 ? "same fault" : "same\nfault");
            },
            isVerificationGateSatisfied: _ => true);

        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 12, watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => { sleeps++; return false; });

        Assert.Equal(2, attempts);
        Assert.True(sleeps - secondFaultPass >= 3, "The run must cover the second fault's full retry window.");
        Assert.Equal(2, summary.Escalated);
        Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
        Assert.Equal(2, Decisions(kernel, goal).Count(message => message.Contains("fault isolating goal", StringComparison.Ordinal)));
        Assert.Equal("Set-aside re-admitted: condition=advance-fault; fingerprint=same_fault; passes=3.",
            Assert.Single(RecoveryDecisions(kernel, goal)));
    }

    [Xunit.Fact]
    public void AdvanceFault_DifferentFingerprintGetsSeparateRetry()
    {
        var (kernel, goal) = SimpleGoal("distinct acceptance faults");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var attempts = 0;
        var driver = MakeDriver(
            runAcceptance: _ => throw new InvalidOperationException(++attempts == 1 ? "first fault" : "second fault"),
            isVerificationGateSatisfied: _ => true);

        new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 16, watchInterval: TimeSpan.FromMilliseconds(1), sleepFunc: _ => false);

        Assert.Equal(3, attempts);
        Assert.Equal(new[]
        {
            "Set-aside re-admitted: condition=advance-fault; fingerprint=first_fault; passes=3.",
            "Set-aside re-admitted: condition=advance-fault; fingerprint=second_fault; passes=3."
        }, RecoveryDecisions(kernel, goal));
    }

    [Xunit.Fact]
    public void AdvanceFault_SameMessageOnDifferentGoalsGetsIndependentRetries()
    {
        var (kernel, first) = SimpleGoal("first faulting goal");
        var second = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "second faulting goal");
        PassVerification(kernel, first, first.Tasks.Single());
        PassVerification(kernel, second, second.Tasks.Single());
        var attempts = new Dictionary<GoalId, int>();
        var driver = MakeDriver(
            runAcceptance: goal =>
            {
                attempts.TryGetValue(goal.Id, out var count);
                attempts[goal.Id] = count + 1;
                throw new InvalidOperationException("shared fault");
            },
            isVerificationGateSatisfied: _ => true);

        new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 12, watchInterval: TimeSpan.FromMilliseconds(1), sleepFunc: _ => false);

        Assert.Equal(2, attempts[first.Id]);
        Assert.Equal(2, attempts[second.Id]);
        Assert.Single(RecoveryDecisions(kernel, first));
        Assert.Single(RecoveryDecisions(kernel, second));
    }

    private static string[] Decisions(AgentOrchestratorKernel kernel, Goal goal) =>
        kernel.GetGoal(goal.Id).Timeline.Where(item => item.Kind == ProgressKind.GoalPolicyDecision)
            .Select(item => item.Message).ToArray();

    private static string[] RecoveryDecisions(AgentOrchestratorKernel kernel, Goal goal) =>
        Decisions(kernel, goal).Where(message => message.StartsWith("Set-aside re-admitted:", StringComparison.Ordinal)).ToArray();
}
