using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Scenario = ProviderHoldAgentSubstitutionTests.Scenario;

// Parallel-safe: each scenario owns its repositories, worktrees and databases. Start-path tests
// inject an isolated stdin-gated shell; the prepared provider command is never executed.
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

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Resolve_NonCapabilityBlock_PreservesFindingsAndState(bool cooldown)
    {
        using var scenario = new Scenario(exhaustPrimary: false);
        var goal = scenario.NewGoal();
        var task = goal.Tasks.Single();
        var worktree = scenario.Workspace.ResolveExecutionDirectory(goal.Id);
        if (cooldown)
        {
            var source = scenario.Kernel.AddTask(goal.Id, AgentRole.Tester, "Earlier provider request.", scenario.Agents);
            scenario.Kernel.RecordTaskDispatch(goal.Id, source.Id, new TaskDispatchRecord(
                "grok-cli", "fixture worker", worktree, Scenario.ObservedAt,
                ProviderName: "xAI", WorkerProviderKind: ProviderKind.XaiGrokCli, GoalId: goal.Id));
            scenario.Kernel.RecordTaskVerification(goal.Id, source.Id, new TaskVerificationRecord(
                "fixture worker", worktree, 1, string.Empty,
                "ERROR: You've hit your usage limit. Try again in 2 hours.", Scenario.ObservedAt,
                ProviderFailureKind: ProviderFailureKind.RateLimit, DispatchStartedAt: Scenario.ObservedAt));
            Assert.True(ProviderLimitSignals.TryGetProviderSubscriptionCooldown(
                goal, task.Id, "xAI", Scenario.ObservedAt.AddHours(1), out _));
        }
        else
            File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "uncommitted");

        var original = scenario.Preflight(goal, task, scenario.Kernel.ProviderHoldScope);
        Assert.False(original.Allowed);
        Assert.Null(original.ErrorCode);
        Assert.Contains(original.Findings, finding => finding.StartsWith("blocked:", StringComparison.Ordinal) &&
            finding.Contains(cooldown ? "provider xAI is cooling down" : "worktree has 1 uncommitted change",
                StringComparison.Ordinal));
        Assert.Contains(scenario.Agents, agent => agent.Id == scenario.Alternate.Id &&
            agent.Status == AgentStatus.Available && agent.Role == task.RequiredRole &&
            AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy));

        Assert.Equal(0, AssertUnchanged(scenario, goal, task, original));
    }

    [Xunit.Fact]
    public void Resolve_OperatorPin_PreservesFindingsAndState()
    {
        using var scenario = new Scenario(skillTarget: true, exhaustPrimary: false);
        var goal = scenario.NewGoal();
        var task = goal.Tasks.Single();
        scenario.Kernel.ReassignTaskAgent(goal.Id, task.Id, scenario.Primary);
        Assert.True(OfflineAssignedAgentRedelegation.IsOperatorPin(goal, task.Id));
        var original = CapabilityBlock(scenario, goal, task);
        AssertCapableAlternate(scenario, goal, task);

        Assert.Equal(0, AssertUnchanged(scenario, goal, task, original));
    }

    [Xunit.Theory]
    [Xunit.InlineData("untyped")]
    [Xunit.InlineData("offline")]
    [Xunit.InlineData("both")]
    public void Resolve_NoCapableAlternate_PreservesFindingsAndState(string roster)
    {
        using var scenario = new Scenario(skillTarget: true, exhaustPrimary: false);
        var untyped = Scenario.Agent("qwen-tester", "LlamaCpp", WorkerProfile.QwenCodeCliName, "qwen3-coder");
        var offline = scenario.Alternate with { Status = AgentStatus.Offline };
        scenario.Agents = roster switch
        {
            "untyped" => [scenario.Primary, untyped],
            "offline" => [scenario.Primary, offline],
            _ => [scenario.Primary, untyped, offline]
        };
        var goal = scenario.NewGoal();
        var task = goal.Tasks.Single();
        var original = CapabilityBlock(scenario, goal, task);

        Assert.Equal(roster == "offline" ? 0 : 2, AssertUnchanged(scenario, goal, task, original));
    }

    [Xunit.Fact]
    public void Resolve_PreviouslyCapabilitySubstituted_PreservesFindingsAndState()
    {
        using var scenario = new Scenario(skillTarget: true, exhaustPrimary: false);
        var untyped = Scenario.Agent("qwen-tester", "LlamaCpp", WorkerProfile.QwenCodeCliName, "qwen3-coder");
        scenario.Agents = [scenario.Primary, untyped, scenario.Alternate];
        var goal = scenario.NewGoal();
        var task = goal.Tasks.Single();
        scenario.Kernel.SubstituteCapabilityBlockedTaskAgent(goal.Id, task.Id, untyped,
            WorkerProfileDispatcher.ProviderCapabilityUnsupportedErrorCode);
        Assert.Single(goal.Timeline, item => item.Kind == ProgressKind.ProviderCapabilitySubstituted);
        Assert.False(OfflineAssignedAgentRedelegation.IsOperatorPin(goal, task.Id));
        var original = CapabilityBlock(scenario, goal, task);
        AssertCapableAlternate(scenario, goal, task);

        Assert.Equal(0, AssertUnchanged(scenario, goal, task, original));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void Start_SkillWrite_DispatchesCapableAlternateOrReportsCapabilityHold(bool capable)
    {
        using var scenario = new Scenario(skillTarget: true, exhaustPrimary: false);
        if (!capable)
            scenario.Agents = [scenario.Primary,
                Scenario.Agent("qwen-tester", "LlamaCpp", WorkerProfile.QwenCodeCliName, "qwen3-coder")];
        var goal = scenario.NewGoal();
        var task = goal.Tasks.Single();
        CapabilityBlock(scenario, goal, task);
        // Skill writes need ownership approval before the batch reaches capability preflight.
        Assert.Empty(DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(goal, scenario.Agents).TaskIds);
        var approvedBatch = DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(
            goal, scenario.Agents, approveHighRiskOwnership: true);
        Assert.Equal(task.Id, Assert.Single(approvedBatch.TaskIds));
        var before = goal.Timeline.ToArray();
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(scenario.Workspace.SqliteStatePath);
        using var worker = new ProviderHoldFallbackScenarioTests.PreparationWorker();

        var start = new GoalDispatchOperations().StartSubscriptionReadyTasks(scenario.Kernel,
            scenario.Workspace, goal, scenario.Agents, scenario.Profiles,
            approveHighRiskOwnership: true,
            checkpointBeforeWorkerStart: (kernel, _, _, _) => repository.SaveAsync(kernel).GetAwaiter().GetResult(),
            runner: new BackgroundDispatchRunner(disableProcessStart: false, startProcess: worker.Start),
            sandboxOptions: scenario.Sandbox, commandExists: _ => true, cascadeTesterCheapFirst: false,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli,
            utcNow: () => Scenario.ObservedAt.AddHours(1));

        goal = scenario.Kernel.GetGoal(goal.Id);
        task = scenario.Kernel.GetTask(goal.Id, task.Id);
        Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.ProviderHoldSubstituted);
        Assert.Empty(start.Processes.StartFailures ?? []);
        Assert.Empty(start.Processes.StartRefusals ?? []);
        if (capable)
        {
            Assert.Equal(task.Id, Assert.Single(start.Dispatches).Task.Id);
            Assert.Equal(task.Id, Assert.Single(start.Processes.Tasks).Id);
            Assert.Empty(start.BlockedDiagnostics);
            Assert.Equal(scenario.Alternate.Id, task.AssignedAgentId);
            Assert.Equal(scenario.Alternate.Id.Value, task.LastDispatch!.AssignedAgentId);
            Assert.Equal("codex-cli", task.LastDispatch.WorkerName);
            Assert.NotNull(task.LastProcess);
            var evt = Assert.Single(goal.Timeline, item => item.Kind == ProgressKind.ProviderCapabilitySubstituted);
            Assert.Equal(task.Id, evt.TaskId);
            Assert.Contains("held agent=xai-tester", evt.Message, StringComparison.Ordinal);
            Assert.Contains("substitute=openai-tester", evt.Message, StringComparison.Ordinal);
            Assert.Contains(WorkerProfileDispatcher.ProviderCapabilityUnsupportedErrorCode, evt.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(start.Dispatches);
            Assert.Empty(start.Processes.Tasks);
            Assert.Equal(WorkerProfileDispatcher.ProviderCapabilityUnsupportedErrorCode,
                Assert.Single(start.BlockedDiagnostics).Reason);
            Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
            Assert.Null(task.LastDispatch);
            Assert.Null(task.LastProcess);
            Assert.Equal(before, goal.Timeline);
        }
    }

    private static WorkerSubscriptionPreflightResult CapabilityBlock(Scenario scenario, Goal goal, TaskSpec task)
    {
        var original = scenario.Preflight(goal, task, scenario.Kernel.ProviderHoldScope);
        Assert.False(original.Allowed);
        Assert.Equal(WorkerProfileDispatcher.ProviderCapabilityUnsupportedErrorCode, original.ErrorCode);
        return original;
    }

    private static void AssertCapableAlternate(Scenario scenario, Goal goal, TaskSpec task)
    {
        var scratch = AgentOrchestratorKernel.FromSnapshot(scenario.Kernel.ExportSnapshot());
        var trial = scratch.ReassignTaskAgent(goal.Id, task.Id, scenario.Alternate);
        var check = scenario.Preflight(scratch.GetGoal(goal.Id), trial, scratch.ProviderHoldScope);
        Assert.True(check.Allowed, string.Join("\n", check.Findings));
    }

    private static int AssertUnchanged(Scenario scenario, Goal goal, TaskSpec task,
        WorkerSubscriptionPreflightResult original)
    {
        var before = goal.Timeline.ToArray();
        var agent = task.AssignedAgentId;
        var revision = task.ConductorRoutingRevision;
        var trials = 0;
        var resolver = new ProviderCapabilityAgentSubstitution(scenario.Kernel, scenario.Agents,
            (viewGoal, viewTask, scope) =>
            {
                if (ReferenceEquals(viewTask, task)) return original;
                trials++;
                return scenario.Preflight(viewGoal, viewTask, scope);
            });

        var none = Assert.IsType<ProviderCapabilityAgentSubstitution.Result.None>(resolver.Resolve(goal, task));
        Assert.Same(original.Findings, none.Findings);
        resolver.ApplyBatch(goal, [task.Id]);
        Assert.Equal(agent, task.AssignedAgentId);
        Assert.Equal(revision, task.ConductorRoutingRevision);
        Assert.Equal(before, goal.Timeline);
        return trials;
    }
}
