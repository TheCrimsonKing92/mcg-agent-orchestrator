using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Scenario = ProviderHoldAgentSubstitutionTests.Scenario;

// Parallel-safe: fixed-clock, in-memory kernels only; no shared state or worker processes.
public sealed class OfflineAssignedAgentRedelegationTests
{
    [Xunit.Theory]
    [Xunit.InlineData(AgentStatus.Offline)]
    [Xunit.InlineData(AgentStatus.Busy)]
    public void SelectsFirstAvailableSubscriptionAgentAndSecondApplyIsANoop(AgentStatus status)
    {
        var (kernel, goal, primary, alternate) = Create();
        var task = goal.Tasks.Single();
        var revision = task.ConductorRoutingRevision;
        IReadOnlyList<AgentDefinition> agents = [primary with { Status = status },
            alternate with { Id = new AgentId("busy"), Status = AgentStatus.Busy },
            alternate with { Id = new AgentId("offline"), Status = AgentStatus.Offline },
            alternate with { Id = new AgentId("developer"), Role = AgentRole.Developer },
            alternate with { Id = new AgentId("api"), ExecutionPolicy = AgentExecutionPolicy.ApiOnly },
            alternate, alternate with { Id = new AgentId("later") }];

        var result = OfflineAssignedAgentRedelegation.Apply(kernel, goal, agents, [task.Id]);

        Assert.Empty(result.ExcludedTaskIds);
        Assert.Empty(result.Blocked);
        Assert.Equal(alternate.Id, task.AssignedAgentId);
        Assert.Equal(revision + 1, task.ConductorRoutingRevision);
        var evt = Assert.Single(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRedelegated);
        Assert.Contains($"OFFLINE_AGENT_REDELEGATED from=xai-tester from_status={status} to=openai-tester role=Tester",
            evt.Message, StringComparison.Ordinal);
        Assert.False(OfflineAssignedAgentRedelegation.IsOperatorPin(goal, task.Id));
        var before = goal.Timeline.ToArray();

        var second = OfflineAssignedAgentRedelegation.Apply(kernel, goal, agents, [task.Id]);

        Assert.Empty(second.ExcludedTaskIds);
        Assert.Empty(second.Blocked);
        Assert.Equal(before, goal.Timeline);
        Assert.Equal(revision + 1, task.ConductorRoutingRevision);
    }

    [Xunit.Theory]
    [Xunit.InlineData("operator", true)]
    [Xunit.InlineData("stale", false)]
    [Xunit.InlineData("automatic", false)]
    [Xunit.InlineData("redelegate", false)]
    [Xunit.InlineData("provider-hold", false)]
    [Xunit.InlineData("provider-capability", false)]
    public void PinUsesLatestAssignmentEvenWithIdenticalTimestamps(string assignment, bool expected)
    {
        var (kernel, goal, primary, alternate) = Create();
        var task = goal.Tasks.Single();
        Assert.False(OfflineAssignedAgentRedelegation.IsOperatorPin(goal, task.Id));
        kernel.ReassignTaskAgent(goal.Id, task.Id, primary);
        Assert.True(OfflineAssignedAgentRedelegation.IsOperatorPin(goal, task.Id));

        switch (assignment)
        {
            case "operator": kernel.ReassignTaskAgent(goal.Id, task.Id, alternate); break;
            case "stale": kernel.ReassignTaskAgent(goal.Id, task.Id, alternate, "Warning: repaired stale Tester assignment."); break;
            case "automatic": kernel.ReassignTaskAgent(goal.Id, task.Id, alternate, OfflineAssignedAgentRedelegation.Marker); break;
            case "redelegate": kernel.RedelegateTask(goal.Id, task.Id, [alternate]); break;
            case "provider-hold": kernel.SubstituteProviderHeldTaskAgent(goal.Id, task.Id, alternate, "xai::<provider-default>", "fixture"); break;
            case "provider-capability": kernel.SubstituteCapabilityBlockedTaskAgent(goal.Id, task.Id, alternate,
                WorkerProfileDispatcher.ProviderCapabilityUnsupportedErrorCode); break;
            default: throw new ArgumentOutOfRangeException(nameof(assignment));
        }

        Assert.Equal(expected, OfflineAssignedAgentRedelegation.IsOperatorPin(goal, task.Id));
        Assert.Single(goal.Timeline.Select(evt => evt.OccurredAt).Distinct());
    }

    [Xunit.Fact]
    public void MissingPinnedAgentIsReassignedInsteadOfHeld()
    {
        var (kernel, goal, primary, alternate) = Create();
        var task = goal.Tasks.Single();
        kernel.ReassignTaskAgent(goal.Id, task.Id, primary);

        var result = OfflineAssignedAgentRedelegation.Apply(kernel, goal, [alternate], [task.Id]);

        Assert.Equal(alternate.Id, task.AssignedAgentId);
        Assert.Empty(result.ExcludedTaskIds);
        Assert.Empty(result.Blocked);
        Assert.Contains("from_status=absent", goal.Timeline.Last().Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PinnedUnavailableAgentWithoutAlternateKeepsExistingPath()
    {
        var (kernel, goal, primary, _) = Create();
        var task = goal.Tasks.Single();
        kernel.ReassignTaskAgent(goal.Id, task.Id, primary);
        var before = goal.Timeline.ToArray();
        var revision = task.ConductorRoutingRevision;

        var result = OfflineAssignedAgentRedelegation.Apply(kernel, goal,
            [primary with { Status = AgentStatus.Offline }], [task.Id]);

        Assert.Empty(result.ExcludedTaskIds);
        Assert.Empty(result.Blocked);
        Assert.Equal(before, goal.Timeline);
        Assert.Equal(primary.Id, task.AssignedAgentId);
        Assert.Equal(revision, task.ConductorRoutingRevision);
    }

    [Xunit.Fact]
    public void NonAssignedTaskIsUntouched()
    {
        var (kernel, goal, primary, alternate) = Create();
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Fixture running.");
        var before = goal.Timeline.ToArray();
        var revision = task.ConductorRoutingRevision;

        var result = OfflineAssignedAgentRedelegation.Apply(kernel, goal,
            [primary with { Status = AgentStatus.Offline }, alternate], [task.Id]);

        Assert.Empty(result.ExcludedTaskIds);
        Assert.Empty(result.Blocked);
        Assert.Equal(before, goal.Timeline);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Equal(primary.Id, task.AssignedAgentId);
        Assert.Equal(revision, task.ConductorRoutingRevision);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, AgentDefinition Primary, AgentDefinition Alternate) Create()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var primary = Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.6");
        var alternate = Scenario.Agent("openai-tester", "OpenAI", "codex-cli", "gpt-6.1-sol");
        var goal = kernel.CreateGoal("Verify routing.", [new TaskSpec(TaskId.New(), "Verify the change.", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, [primary, alternate]);
        return (kernel, goal, primary, alternate);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Scenario.ObservedAt;
    }
}
