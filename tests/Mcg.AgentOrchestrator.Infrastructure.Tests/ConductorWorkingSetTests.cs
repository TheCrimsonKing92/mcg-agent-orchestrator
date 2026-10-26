using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class ConductorWorkingSetTests : ConductorBatchLoopTests
{
    public ConductorWorkingSetTests(ITestOutputHelper output) : base(output) { }

    public static IEnumerable<object[]> StatusTable()
    {
        var expected = new Dictionary<GoalStatus, (bool Terminal, bool PendingExcluded, bool AssignedExcluded)>
        {
            [GoalStatus.Draft] = (false, false, false),
            [GoalStatus.Active] = (false, false, false),
            [GoalStatus.WaitingForHuman] = (false, false, false),
            [GoalStatus.Parked] = (false, false, false),
            [GoalStatus.Verifying] = (false, false, false),
            [GoalStatus.Verified] = (false, false, false),
            [GoalStatus.AcceptanceFailed] = (false, false, false),
            [GoalStatus.Completed] = (true, false, true),
            [GoalStatus.Failed] = (true, true, true),
            [GoalStatus.Cancelled] = (true, true, true),
            [GoalStatus.Superseded] = (true, true, true)
        };

        foreach (var status in Enum.GetValues<GoalStatus>())
        {
            var row = expected[status];
            yield return [status, WorkTaskStatus.Pending, row.Terminal, row.PendingExcluded,
                row.PendingExcluded || status == GoalStatus.Parked];
            yield return [status, WorkTaskStatus.Assigned, row.Terminal, row.AssignedExcluded,
                row.AssignedExcluded || status == GoalStatus.Parked];
        }

        yield return [GoalStatus.Completed, WorkTaskStatus.Running, true, true, true];
        yield return [GoalStatus.Completed, WorkTaskStatus.WaitingForHuman, true, true, true];
    }

    [Theory]
    [MemberData(nameof(StatusTable))]
    public void TerminalAndPreWalkPredicates_MatchStatusTable(
        GoalStatus status,
        WorkTaskStatus taskStatus,
        bool terminal,
        bool excludedTerminal,
        bool excluded)
    {
        var goal = CreateGoal(status, taskStatus);

        Assert.Equal(status, goal.Status);
        Assert.Equal(taskStatus, Assert.Single(goal.Tasks).Status);
        Assert.Equal(terminal, ConductorWorkingSet.IsTerminalGoal(goal));
        Assert.Equal(excludedTerminal, ConductorWorkingSet.IsPreWalkExcludedTerminalGoal(goal));
        Assert.Equal(excluded, ConductorWorkingSet.IsPreWalkExcludedGoal(goal));
    }

    [Fact]
    public void LoopEligibility_ReadsLifecycleFactsOnlyForVerifyingVerifiedAndCompleted()
    {
        var factReads = 0;
        var driver = MakeDriver(getFacts: _ =>
        {
            factReads++;
            return GoalLifecycleFacts.None;
        });
        var cache = new GoalProjectionCache();

        foreach (var status in new[] { GoalStatus.Parked, GoalStatus.Superseded })
        {
            Assert.False(ConductorWorkingSet.IsLoopEligibleGoal(CreateGoal(status), driver, cache));
            Assert.Equal(0, factReads);
        }

        foreach (var status in new[] { GoalStatus.Active, GoalStatus.AcceptanceFailed })
        {
            Assert.True(ConductorWorkingSet.IsLoopEligibleGoal(CreateGoal(status), driver, cache));
            Assert.Equal(0, factReads);
        }

        foreach (var status in new[] { GoalStatus.Verifying, GoalStatus.Verified, GoalStatus.Completed })
        {
            var readsBefore = factReads;
            Assert.True(ConductorWorkingSet.IsLoopEligibleGoal(CreateGoal(status), driver, cache));
            Assert.Equal(readsBefore + 1, factReads);
        }

        var verified = CreateGoal(GoalStatus.Verified);
        var cleanedUp = new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true);
        Assert.Equal(GoalLifecycleState.CleanedUp, GoalLifecycle.ResolveState(verified, cleanedUp));
        var cleanedUpReads = 0;
        var cleanedUpDriver = MakeDriver(getFacts: _ =>
        {
            cleanedUpReads++;
            return cleanedUp;
        });
        Assert.False(ConductorWorkingSet.IsLoopEligibleGoal(verified, cleanedUpDriver, new GoalProjectionCache()));
        Assert.Equal(1, cleanedUpReads);

        Assert.NotEqual(GoalLifecycleState.CleanedUp, GoalLifecycle.ResolveState(verified, GoalLifecycleFacts.None));
        var cachedReads = 0;
        var cachedDriver = MakeDriver(getFacts: _ =>
        {
            cachedReads++;
            return GoalLifecycleFacts.None;
        });
        var verifiedCache = new GoalProjectionCache();
        Assert.True(ConductorWorkingSet.IsLoopEligibleGoal(verified, cachedDriver, verifiedCache));
        Assert.True(ConductorWorkingSet.IsLoopEligibleGoal(verified, cachedDriver, verifiedCache));
        Assert.Equal(1, cachedReads);
        Assert.Equal(1, verifiedCache.Count);

        var throwingReads = 0;
        var throwingDriver = MakeDriver(getFacts: _ =>
        {
            throwingReads++;
            throw new InvalidOperationException("Facts unavailable");
        });
        Assert.True(ConductorWorkingSet.IsLoopEligibleGoal(verified, throwingDriver, new GoalProjectionCache()));
        Assert.Equal(1, throwingReads);
    }

    [Fact]
    public void RecheckableCount_HonorsScopeConditionAndTransientSet()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var agents = DefaultAgents();
        var a = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, agents, "A");
        var b = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, agents, "B");
        var c = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, agents, "C");
        var d = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, agents, "D");
        var e = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, agents, "E");
        kernel = WithGoalAndTaskStatus(kernel, c.Id, GoalStatus.Completed, WorkTaskStatus.Pending);
        var setAside = new Dictionary<string, BatchSetAsideEntry>
        {
            [a.Id.Value] = new(a.Id.Value, BatchSetAsideCondition.AwaitingClarification, "fixed"),
            [b.Id.Value] = new(b.Id.Value, BatchSetAsideCondition.DependencyEscalated, "fixed"),
            [c.Id.Value] = new(c.Id.Value, BatchSetAsideCondition.AwaitingClarification, "fixed")
        };
        var transient = new HashSet<string> { d.Id.Value };

        Assert.Equal(3, ConductorWorkingSet.CountRecheckableNonTerminalGoals(
            kernel, null, setAside, transientRecheckableGoalIds: transient));
        Assert.Equal(1, ConductorWorkingSet.CountRecheckableNonTerminalGoals(
            kernel, null, setAside, BatchSetAsideCondition.AwaitingClarification, transient));
        Assert.Equal(1, ConductorWorkingSet.CountRecheckableNonTerminalGoals(
            kernel, b.Id.Value, setAside));
        Assert.Equal(0, ConductorWorkingSet.CountRecheckableNonTerminalGoals(
            kernel, e.Id.Value, setAside));
    }

    [Fact]
    public void WorkingSetPredicates_HaveOneOwner()
    {
        string[] movedNames =
        [
            "IsTerminalGoal",
            "CountRecheckableNonTerminalGoals",
            "IsPreWalkExcludedGoal",
            "IsPreWalkExcludedTerminalGoal",
            "IsLoopEligibleGoal",
            "IsStaleTerminalGoalWithAssignedWork"
        ];
        const string removedName = "CountParkedExcludedGoals";
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var loopMethods = typeof(ConductorBatchLoop).GetMethods(flags);
        var workingSetType = typeof(ConductorWorkingSet);
        var workingSetMethods = workingSetType.GetMethods(flags);

        Assert.True(workingSetType.IsAbstract);
        Assert.True(workingSetType.IsSealed);
        foreach (var name in movedNames)
        {
            Assert.DoesNotContain(loopMethods, method => method.Name == name);
            Assert.True(Assert.Single(workingSetMethods, method => method.Name == name).IsStatic);
        }

        Assert.DoesNotContain(loopMethods, method => method.Name == removedName);
        Assert.DoesNotContain(workingSetMethods, method => method.Name == removedName);
    }

    private static Goal CreateGoal(GoalStatus status, WorkTaskStatus taskStatus = WorkTaskStatus.Pending)
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Working set fixture");
        return Assert.Single(WithGoalAndTaskStatus(kernel, goal.Id, status, taskStatus).Goals);
    }

    private static AgentOrchestratorKernel WithGoalAndTaskStatus(
        AgentOrchestratorKernel kernel, GoalId goalId, GoalStatus status, WorkTaskStatus taskStatus)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == goalId.Value ? goal with
            {
                Status = status,
                Tasks = goal.Tasks.Select(task => task with { Status = taskStatus }).ToArray()
            } : goal).ToArray()
        }, new FixedClock());
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
