using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Scenario = ProviderHoldAgentSubstitutionTests.Scenario;

// Parallel-safe: each scenario owns its repositories and worktrees; no worker is launched.
public sealed class ProviderCapabilityAgentSubstitutionTests
{
    [Xunit.Fact]
    public void Resolve_SkillWrite_TrialsScratchThenAppliesOnePersistedSubstitution()
    {
        using var scenario = new Scenario(skillTarget: true, exhaustPrimary: false);
        var goal = scenario.NewGoal();
        var task = goal.Tasks.Single();
        var before = goal.Timeline.Count;
        var revision = task.ConductorRoutingRevision;
        var allowedTrials = 0;
        var resolver = new ProviderCapabilityAgentSubstitution(scenario.Kernel, scenario.Agents,
            (viewGoal, viewTask, scope) =>
            {
                var check = scenario.Preflight(viewGoal, viewTask, scope);
                if (viewTask.AssignedAgentId == scenario.Alternate.Id)
                {
                    Assert.NotSame(goal, viewGoal);
                    Assert.NotSame(task, viewTask);
                    Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
                    Assert.True(check.Allowed, string.Join("\n", check.Findings));
                    allowedTrials++;
                }
                return check;
            });

        var result = Assert.IsType<ProviderCapabilityAgentSubstitution.Result.Substitute>(resolver.Resolve(goal, task));

        Assert.Equal(scenario.Alternate.Id, result.Agent.Id);
        Assert.Equal(WorkerProfileDispatcher.ProviderCapabilityUnsupportedErrorCode, result.CapabilityCode);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.Equal(before, goal.Timeline.Count);
        Assert.Equal(1, allowedTrials);

        resolver.ApplyBatch(goal, [task.Id]);

        Assert.Equal(2, allowedTrials);
        Assert.Equal(scenario.Alternate.Id, task.AssignedAgentId);
        Assert.Equal(revision + 1, task.ConductorRoutingRevision);
        var evt = Assert.Single(goal.Timeline, item => item.Kind == ProgressKind.ProviderCapabilitySubstituted);
        Assert.Equal(task.Id, evt.TaskId);
        Assert.Contains("held agent=xai-tester", evt.Message, StringComparison.Ordinal);
        Assert.Contains("substitute=openai-tester", evt.Message, StringComparison.Ordinal);
        Assert.Contains(WorkerProfileDispatcher.ProviderCapabilityUnsupportedErrorCode, evt.Message, StringComparison.Ordinal);
        Assert.False(OfflineAssignedAgentRedelegation.IsOperatorPin(goal, task.Id));
        Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.ProviderHoldSubstituted);

        // Use the ordinary preflight after the real assignment has changed.
        var after = goal.Timeline.Count;
        var second = new ProviderCapabilityAgentSubstitution(scenario.Kernel, scenario.Agents, scenario.Preflight);
        Assert.IsType<ProviderCapabilityAgentSubstitution.Result.None>(second.Resolve(goal, task));
        second.ApplyBatch(goal, [task.Id]);
        Assert.Equal(after, goal.Timeline.Count);

        var restored = AgentOrchestratorKernel.FromSnapshot(scenario.Kernel.ExportSnapshot());
        Assert.Equal(evt, Assert.Single(restored.GetGoal(goal.Id).Timeline,
            item => item.Kind == ProgressKind.ProviderCapabilitySubstituted));
        Assert.Equal(scenario.Alternate.Id, restored.GetTask(goal.Id, task.Id).AssignedAgentId);
        Assert.Throws<InvalidOperationException>(() => restored.SubstituteCapabilityBlockedTaskAgent(
            goal.Id, task.Id, scenario.Primary, result.CapabilityCode));
    }

    [Xunit.Fact]
    public void Resolve_RejectedCandidates_UsesIndependentScratchViewsInRosterOrder()
    {
        using var scenario = new Scenario(skillTarget: true, exhaustPrimary: false);
        var untyped = Scenario.Agent("qwen-tester", "LlamaCpp", WorkerProfile.QwenCodeCliName, "qwen3-coder");
        var first = scenario.Alternate with { Id = new AgentId("first-capable") };
        scenario.Agents = [scenario.Primary,
            first with { Id = new AgentId("offline"), Status = AgentStatus.Offline },
            first with { Id = new AgentId("wrong-role"), Role = AgentRole.Planner },
            first with { Id = new AgentId("api"), ExecutionPolicy = AgentExecutionPolicy.ApiOnly },
            untyped, first, scenario.Alternate];
        var goal = scenario.NewGoal();
        var task = goal.Tasks.Single();
        var before = goal.Timeline.Count;
        var trials = new List<AgentId>();
        var resolver = new ProviderCapabilityAgentSubstitution(scenario.Kernel, scenario.Agents,
            (viewGoal, viewTask, scope) =>
            {
                if (viewTask.AssignedAgentId != scenario.Primary.Id)
                {
                    trials.Add(viewTask.AssignedAgentId!);
                    Assert.NotSame(goal, viewGoal);
                    Assert.Single(viewGoal.Timeline, item => item.Kind == ProgressKind.TaskRedelegated);
                }
                return scenario.Preflight(viewGoal, viewTask, scope);
            });

        var result = Assert.IsType<ProviderCapabilityAgentSubstitution.Result.Substitute>(resolver.Resolve(goal, task));

        Assert.Equal(first.Id, result.Agent.Id);
        Assert.Equal(new[] { untyped.Id, first.Id }, trials);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.Equal(before, goal.Timeline.Count);
    }

    [Xunit.Fact]
    public void Preflight_UntypedSkillWrite_ReportsCapabilityCodeAndReadyBlockedReason()
    {
        using var scenario = new Scenario(skillTarget: true, exhaustPrimary: false);
        var goal = scenario.NewGoal();
        var task = goal.Tasks.Single();

        var check = scenario.Preflight(goal, task, scenario.Kernel.ProviderHoldScope);
        var diagnostic = WorkerProfileDispatcher.BuildReadyBlockedDiagnostic(goal, task, check);

        Assert.False(check.Allowed);
        Assert.Equal(WorkerProfileDispatcher.ProviderCapabilityUnsupportedErrorCode, check.ErrorCode);
        Assert.Equal(WorkerProfileDispatcher.ProviderCapabilityUnsupportedErrorCode, diagnostic.Reason);
        Assert.Contains(check.Findings, finding => finding.StartsWith(
            $"blocked: {WorkerProfileDispatcher.ProviderCapabilityUnsupportedErrorCode}: ", StringComparison.Ordinal) &&
            finding.Contains("Repository-scoped skill writes require a typed Codex or Claude CLI provider.", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void Substitute_SameAgent_RejectsWithoutChangingTimeline()
    {
        using var scenario = new Scenario(skillTarget: true, exhaustPrimary: false);
        var goal = scenario.NewGoal();
        var task = goal.Tasks.Single();
        var before = goal.Timeline.Count;

        Assert.Throws<InvalidOperationException>(() => scenario.Kernel.SubstituteCapabilityBlockedTaskAgent(
            goal.Id, task.Id, scenario.Primary, WorkerProfileDispatcher.ProviderCapabilityUnsupportedErrorCode));

        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.Equal(before, goal.Timeline.Count);
    }
}
