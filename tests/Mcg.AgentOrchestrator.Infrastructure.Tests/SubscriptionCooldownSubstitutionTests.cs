using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fixture owns its repository, worktrees and injected provider probes.
public sealed class SubscriptionCooldownSubstitutionTests
{
    [Xunit.Fact]
    public void Resolve_ActiveClassifiedLimit_SelectsAlternateWithoutMutation()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        var before = scenario.Kernel.ExportGoalSnapshot(goal.Id);
        var result = Assert.IsType<ProviderHoldAgentSubstitution.Result.Substitute>(scenario.Resolve(goal));
        Assert.Equal(scenario.Alternate.Id, result.Agent.Id);
        Assert.Equal("anthropic::<provider-default>", result.HeldBindingKey);
        Assert.Equal(scenario.Receipt(goal), result.EvidenceReceipt);
        Assert.NotNull(result.SubscriptionCooldown);
        Assert.Equal(task.SubscriptionRetryAfter, result.SubscriptionCooldown.RetryAfter);
        Assert.Equal(before.Timeline, scenario.Kernel.ExportGoalSnapshot(goal.Id).Timeline);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.True(scenario.AuthProbes > 0);
        Assert.Contains(scenario.Preflights, check => check.AgentId == scenario.Alternate.Id && check.Allowed);
        Assert.Null(scenario.Preflight(goal, task, scenario.Kernel.Goals).ErrorCode);
    }

    [Xunit.Fact]
    public void Resolve_OperatorPin_PreservesBindingAndDeferral()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        scenario.Kernel.ReassignTaskAgent(goal.Id, task.Id, scenario.Primary);
        var reset = task.SubscriptionRetryAfter;
        Assert.IsType<ProviderHoldAgentSubstitution.Result.WaitOnPin>(scenario.Resolve(goal));
        scenario.Resolver().ApplyBatch(goal, [task.Id]);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.Equal(reset, task.SubscriptionRetryAfter);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    [Xunit.Fact]
    public void Resolve_UnsuitableAlternates_PreservesHold()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        scenario.Agents = [scenario.Primary,
            scenario.Alternate with { Id = new("offline"), Status = AgentStatus.Offline },
            scenario.Alternate with { Id = new("wrong-role"), Role = AgentRole.Developer },
            scenario.Alternate with { Id = new("paid"), ExecutionPolicy = AgentExecutionPolicy.ApiOnly },
            scenario.Primary with { Id = new("same-provider") }];
        Assert.IsType<ProviderHoldAgentSubstitution.Result.None>(scenario.Resolve(goal));
        Assert.Equal(scenario.Primary.Id, goal.Tasks.Single().AssignedAgentId);
        Assert.NotNull(goal.Tasks.Single().SubscriptionRetryAfter);
    }

    [Xunit.Fact]
    public void Resolve_RejectedTrial_CannotReleaseNextTrialsDeferral()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var rejected = scenario.Alternate with
        {
            Id = new("missing-launcher"),
            Subscription = scenario.Alternate.Subscription! with { WorkerProfileName = "missing-profile" }
        };
        scenario.Agents = [scenario.Primary, rejected, scenario.Primary with { Id = new("same-provider") }];
        Assert.IsType<ProviderHoldAgentSubstitution.Result.None>(scenario.Resolve(goal));
        scenario.Agents = [..scenario.Agents, scenario.Alternate];
        var result = Assert.IsType<ProviderHoldAgentSubstitution.Result.Substitute>(scenario.Resolve(goal));
        Assert.Equal(scenario.Alternate.Id, result.Agent.Id);
        Assert.Equal(scenario.Primary.Id, goal.Tasks.Single().AssignedAgentId);
    }

    [Xunit.Theory]
    [Xunit.InlineData("expired")]
    [Xunit.InlineData("at-reset")]
    [Xunit.InlineData("missing-reset")]
    [Xunit.InlineData("connectivity")]
    [Xunit.InlineData("unrelated-provider")]
    public void Resolve_NonApplicableEvidence_DoesNotSubstitute(string control)
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal, control == "missing-reset" ? "ERROR: You've hit your usage limit."
            : control == "connectivity" ? "ERROR: Unable to connect to API: network connection failed."
            : Scenario.LimitText, control == "unrelated-provider" ? "OpenAI" : null);
        if (control is "expired" or "at-reset")
            scenario.Clock.UtcNow = goal.Tasks.Single().SubscriptionRetryAfter!.Value.AddSeconds(control == "expired" ? 1 : 0);
        Assert.IsType<ProviderHoldAgentSubstitution.Result.None>(scenario.Resolve(goal));
        scenario.Resolver().ApplyBatch(goal, [goal.Tasks.Single().Id]);
        Assert.Equal(scenario.Primary.Id, goal.Tasks.Single().AssignedAgentId);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    [Xunit.Fact]
    public void Apply_ChangedPinDuringPreflight_DoesNotMutateAssignment()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        var alternateChecked = false;
        var resolver = new ProviderHoldAgentSubstitution(scenario.Kernel, scenario.Agents, (g, t, scope) =>
        {
            var check = scenario.Preflight(g, t, scope);
            if (t.AssignedAgentId == scenario.Alternate.Id)
            {
                alternateChecked = true;
                scenario.Kernel.ReassignTaskAgent(goal.Id, task.Id, scenario.Primary);
            }
            return check;
        }, () => scenario.Clock.UtcNow);
        resolver.ApplyBatch(goal, [task.Id]);
        Assert.True(alternateChecked);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.NotNull(task.SubscriptionRetryAfter);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    [Xunit.Fact]
    public void Apply_CooldownExpiresDuringPreflight_PreservesAssignment()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        var checkedAlternate = false;
        var resolver = new ProviderHoldAgentSubstitution(scenario.Kernel, scenario.Agents, (g, t, scope) =>
        {
            var check = scenario.Preflight(g, t, scope);
            if (t.AssignedAgentId == scenario.Alternate.Id)
            {
                checkedAlternate = true;
                scenario.Clock.UtcNow = task.SubscriptionRetryAfter!.Value;
            }
            return check;
        }, () => scenario.Clock.UtcNow);
        resolver.ApplyBatch(goal, [task.Id]);
        Assert.True(checkedAlternate);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    [Xunit.Fact]
    public void Apply_AlternateHitsLimit_SharedBoundRejectsThirdAgent()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        scenario.Resolver().ApplyBatch(goal, [task.Id]);
        Assert.Equal(scenario.Alternate.Id, task.AssignedAgentId);
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddMinutes(1);
        scenario.Limit(goal, provider: "OpenAI");
        Assert.True(ProviderLimitSignals.RequiresSubscriptionLimitReview(task));
        scenario.Kernel.AcknowledgeSubscriptionLimitReview(goal.Id, task.Id,
            "Acknowledge repeated limits to isolate the automatic substitution bound.");
        var third = Scenario.Agent("third", "Ollama", "codex-oss-cli", "qwen3-coder");
        scenario.Agents = [..scenario.Agents, third];
        var scratch = AgentOrchestratorKernel.FromSnapshot(scenario.Kernel.ExportSnapshot(), scenario.Clock);
        var thirdTask = scratch.ReassignTaskAgent(goal.Id, task.Id, third);
        var check = scenario.Preflight(scratch.GetGoal(goal.Id), thirdTask, scratch.Goals);
        Assert.True(check.Allowed, string.Join("\n", check.Findings));
        Assert.IsType<ProviderHoldAgentSubstitution.Result.None>(scenario.Resolve(goal));
        scenario.Resolver().ApplyBatch(goal, [task.Id]);
        Assert.Equal(scenario.Alternate.Id, task.AssignedAgentId);
        Assert.Single(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    internal sealed class Scenario : WorkerDispatchTestSupport, IDisposable
    {
        internal const string LimitText = "ERROR: You've hit your usage limit. Try again in 2 hours.";
        internal readonly string Root = CreateSeededDispatchRepository();
        internal readonly CooldownClock Clock = new();
        internal AgentOrchestratorKernel Kernel;
        internal readonly AgentDefinition Primary = Agent("claude-planner", "Anthropic", "claude-cli", "claude-opus-4.1");
        internal readonly AgentDefinition Alternate = Agent("codex-planner", "OpenAI", "codex-cli", "gpt-6.1-sol");
        internal IReadOnlyList<AgentDefinition> Agents;
        internal readonly WorkerProfileCatalog Profiles = WorkerProfileCatalog.Default();
        internal readonly WorkerSandboxOptions Sandbox = new(true,
            WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
        internal readonly OrchestratorWorkspace Workspace;
        internal int AuthProbes;
        internal readonly List<(AgentId? AgentId, bool Allowed)> Preflights = [];

        internal Scenario()
        {
            Kernel = new(Clock);
            Agents = [Primary, Alternate];
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
        }

        internal Goal NewGoal()
        {
            var goal = InfrastructureTestSupport.MarkGoalRefined(Kernel, Kernel.CreateGoal("Plan a scoped change.",
                [new TaskSpec(TaskId.New(), "Plan the scoped change.", AgentRole.Planner)]));
            Kernel.ActivateGoal(goal.Id, Agents);
            GoalWorktrees.Ensure(Root, goal.Id);
            return goal;
        }

        internal string Receipt(Goal goal) => Path.Combine(Root, goal.Id.Value + ".stderr");

        internal void Limit(Goal goal, string text = LimitText, string? provider = null, TaskSpec? task = null)
        {
            task ??= goal.Tasks.Single();
            var agent = Agents.First(agent => agent.Id == task.AssignedAgentId);
            var dispatch = new TaskDispatchRecord(agent.Subscription!.WorkerProfileName, "fixture worker",
                Workspace.ResolveExecutionDirectory(goal.Id), Clock.UtcNow,
                ProviderName: provider ?? agent.Model.ProviderName, GoalId: goal.Id,
                AssignedAgentId: agent.Id.Value,
                WorkerProviderKind: WorkerProviderCatalog.Default().ResolveProfile(agent.Subscription.WorkerProfileName).Identity.Kind);
            Kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
            Kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(dispatch.Command,
                dispatch.WorkingDirectory, 1, "", text, Clock.UtcNow,
                DispatchStartedAt: dispatch.DispatchedAt, StandardErrorPath: Receipt(goal)));
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Clock.UtcNow = Clock.UtcNow.AddMinutes(1);
        }

        internal ClaudeCliAuthState ProbeAuth()
        {
            AuthProbes++;
            return DispatcherProviderProbeFakes.SignedInClaudeCli();
        }

        internal bool CommandExists(string _) => true;

        internal WorkerSubscriptionPreflightResult Preflight(Goal goal, TaskSpec task, IEnumerable<Goal> holds)
        {
            var result = WorkerProfileDispatcher.PreflightSubscriptionTask(goal, task, Agents, Profiles,
                Workspace.ResolveExecutionDirectory(goal.Id), Clock.UtcNow,
                allowGitReference: true, sandboxOptions: Sandbox, commandExists: CommandExists,
                providerHoldScope: holds, cascadeTesterCheapFirst: false, claudeAuthProbe: ProbeAuth);
            Preflights.Add((task.AssignedAgentId, result.Allowed));
            return result;
        }

        internal ProviderHoldAgentSubstitution Resolver() => new(Kernel, Agents, Preflight, () => Clock.UtcNow);
        internal ProviderHoldAgentSubstitution.Result Resolve(Goal goal) => Resolver().Resolve(goal, goal.Tasks.Single());

        internal static AgentDefinition Agent(string id, string provider, string profile, string model) => new(
            new AgentId(id), id, AgentRole.Planner,
            new ModelProfile(provider, model, ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
                SubscriptionMode.ApiKey, "high"), ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile(profile, model, "high"), IsProviderRoutingConstrained: true);

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }

    internal sealed class CooldownClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2030-10-27T12:00:00Z");
    }
}
