using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scenario owns its repositories, worktrees and SQLite files; no worker is launched.
public sealed class ProviderHoldAgentSubstitutionTests
{
    [Xunit.Fact]
    public void HeldTesterSelectsAlternateWithBindingAndReceiptWithoutMutation()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        var before = goal.Timeline.Count;
        var result = Assert.IsType<ProviderHoldAgentSubstitution.Result.Substitute>(scenario.Resolve(goal));
        Assert.Equal(scenario.Alternate.Id, result.Agent.Id);
        Assert.Equal("xai::<provider-default>", result.HeldBindingKey);
        Assert.Equal(scenario.Hold.EvidenceReceipt, result.EvidenceReceipt);
        Assert.Equal(scenario.Primary.Id, goal.Tasks.Single().AssignedAgentId);
        Assert.Equal(before, goal.Timeline.Count);
    }

    [Xunit.Fact]
    public void SkipsHeldOfflineWrongRoleAndApiCandidatesInListOrder()
    {
        using var scenario = new Scenario();
        var held = scenario.Alternate;
        var source = scenario.NewGoal();
        scenario.Kernel.ReassignTaskAgent(source.Id, source.Tasks.Single().Id, held);
        scenario.Exhaust(source, held);
        Assert.True(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(scenario.Kernel.Goals,
            "OpenAI", null, out _));
        var first = Scenario.Agent("first", "Ollama", "codex-oss-cli", "qwen3-coder");
        var second = first with { Id = new AgentId("second") };
        scenario.Agents = [scenario.Primary,
            first with { Id = new AgentId("offline"), Status = AgentStatus.Offline },
            first with { Id = new AgentId("planner"), Role = AgentRole.Planner },
            first with { Id = new AgentId("api"), ExecutionPolicy = AgentExecutionPolicy.ApiOnly },
            held, first, second];
        var goal = scenario.NewGoal();
        var result = Assert.IsType<ProviderHoldAgentSubstitution.Result.Substitute>(scenario.Resolve(goal));
        Assert.Equal(first.Id, result.Agent.Id);
    }

    [Xunit.Fact]
    public void SkillWriteRejectsUntypedAlternateAndUsesLaterEligibleCandidate()
    {
        using var scenario = new Scenario(skillTarget: true);
        var untyped = Scenario.Agent("qwen-tester", "LlamaCpp", WorkerProfile.QwenCodeCliName, "qwen3-coder");
        scenario.Agents = [scenario.Primary, untyped];
        var goal = scenario.NewGoal();
        var scratch = AgentOrchestratorKernel.FromSnapshot(scenario.Kernel.ExportSnapshot());
        var candidate = scratch.ReassignTaskAgent(goal.Id, goal.Tasks.Single().Id, untyped);
        var candidateCheck = scenario.Preflight(scratch.GetGoal(goal.Id), candidate, scratch.Goals);
        Assert.Contains(candidateCheck.Findings, finding => finding.Contains(
            "Repository-scoped skill writes require a typed Codex or Claude CLI provider.", StringComparison.Ordinal));
        var original = scenario.Preflight(goal, goal.Tasks.Single(), scenario.Kernel.Goals);
        var none = Assert.IsType<ProviderHoldAgentSubstitution.Result.None>(scenario.Resolve(goal));
        Assert.Equal(original.Findings, none.Findings);
        Assert.Equal(WorkerProfileDispatcher.ProviderBudgetExhaustionErrorCode, original.ErrorCode);
        scenario.Agents = [scenario.Primary, untyped, scenario.Alternate];
        var substitute = Assert.IsType<ProviderHoldAgentSubstitution.Result.Substitute>(scenario.Resolve(goal));
        Assert.Equal(scenario.Alternate.Id, substitute.Agent.Id);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void ExplicitPinIsRespectedWhetherHeldOrAvailable(bool held)
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        var task = goal.Tasks.Single();
        var pinned = held ? scenario.Primary : scenario.Alternate;
        scenario.Kernel.ReassignTaskAgent(goal.Id, task.Id, pinned);
        var before = goal.Timeline.Count;
        var result = scenario.Resolve(goal);
        if (held) Assert.IsType<ProviderHoldAgentSubstitution.Result.WaitOnPin>(result);
        else Assert.IsType<ProviderHoldAgentSubstitution.Result.None>(result);
        Assert.Equal(pinned.Id, task.AssignedAgentId);
        Assert.Equal(before, goal.Timeline.Count);
    }

    [Xunit.Fact]
    public void StaleAgentRepairDoesNotPinTheRepairedAssignment()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Kernel.ReassignTaskAgent(goal.Id, goal.Tasks.Single().Id,
            scenario.Primary with { Id = new AgentId("removed-agent") });
        var batch = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(scenario.Kernel, goal,
            scenario.Agents, scenario.Profiles, scenario.Workspace.PromptDirectory,
            scenario.Workspace.ResolveExecutionDirectory(goal.Id), Scenario.ObservedAt,
            sandboxOptions: scenario.Sandbox, commandExists: _ => true, cascadeTesterCheapFirst: false);
        Assert.Empty(batch.Dispatches);
        Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRedelegated &&
            evt.Message.Contains("repaired stale", StringComparison.OrdinalIgnoreCase));
        Assert.IsType<ProviderHoldAgentSubstitution.Result.Substitute>(scenario.Resolve(goal));
    }

    [Xunit.Fact]
    public void NoAlternateKeepsTheOriginalHoldFinding()
    {
        using var scenario = new Scenario();
        scenario.Agents = [scenario.Primary];
        var goal = scenario.NewGoal();
        var original = scenario.Preflight(goal, goal.Tasks.Single(), scenario.Kernel.Goals);
        var none = Assert.IsType<ProviderHoldAgentSubstitution.Result.None>(scenario.Resolve(goal));
        Assert.Equal(original.Findings, none.Findings);
        Assert.Equal(WorkerProfileDispatcher.ProviderBudgetExhaustionErrorCode, original.ErrorCode);
    }

    internal sealed class Scenario : WorkerDispatchTestSupport, IDisposable
    {
        internal static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-10-17T11:07:00Z");
        internal readonly string Root = CreateSeededDispatchRepository();
        internal readonly AgentOrchestratorKernel Kernel = new();
        internal readonly AgentDefinition Primary = Agent("xai-tester", "xAI", "grok-cli", "grok-4.6");
        internal readonly AgentDefinition Alternate = Agent("openai-tester", "OpenAI", "codex-cli", "gpt-6.1-sol");
        internal IReadOnlyList<AgentDefinition> Agents;
        internal WorkerProfileCatalog Profiles = WorkerProfileCatalog.Default();
        internal readonly WorkerSandboxOptions Sandbox = new(true,
            WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
        internal readonly OrchestratorWorkspace Workspace;
        internal readonly Goal Source;
        private readonly bool _skillTarget;

        internal Scenario(bool skillTarget = false)
        {
            _skillTarget = skillTarget;
            Agents = [Primary, Alternate];
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Source = NewGoal();
            Exhaust(Source, Primary);
            Assert.Equal(GoalStatus.Failed, Source.Status);
        }

        internal ProviderBudgetExhaustionHold Hold
        {
            get
            {
                Assert.True(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
                    Kernel.Goals, "xAI", null, out var hold));
                return hold;
            }
        }

        internal Goal NewGoal()
        {
            var goal = InfrastructureTestSupport.MarkGoalRefined(Kernel, Kernel.CreateGoal("Check the change.",
                [new TaskSpec(TaskId.New(), _skillTarget
                    ? "Verify changes to .agents/skills/example/SKILL.md."
                    : "Verify the change.", AgentRole.Tester)]));
            Kernel.ActivateGoal(goal.Id, Agents);
            GoalWorktrees.Ensure(Root, goal.Id);
            return goal;
        }

        internal void Exhaust(Goal goal, AgentDefinition agent)
        {
            var task = goal.Tasks.Single();
            if (task.LastDispatch is null)
                Kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                    agent.Subscription!.WorkerProfileName, "fixture worker", Workspace.ResolveExecutionDirectory(goal.Id),
                    ObservedAt, ProviderName: agent.Model.ProviderName, GoalId: goal.Id,
                    WorkerProviderKind: WorkerProviderCatalog.Default().ResolveProfile(agent.Subscription.WorkerProfileName).Identity.Kind));
            var dispatch = task.LastDispatch!;
            Kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                dispatch.Command, dispatch.WorkingDirectory, 1, string.Empty,
                "API error (status 402 Payment Required): Grok Build usage balance exhausted",
                dispatch.DispatchedAt.AddMinutes(1), ProviderFailureKind: ProviderFailureKind.BudgetExhausted,
                DispatchStartedAt: dispatch.DispatchedAt, StandardErrorPath: Path.Combine(Root, agent.Id.Value + ".stderr")));
            Kernel.EscalateTaskFailure(goal.Id, task.Id, "Provider budget exhausted.");
        }

        internal WorkerSubscriptionPreflightResult Preflight(Goal goal, TaskSpec task, IEnumerable<Goal> holds) =>
            WorkerProfileDispatcher.PreflightSubscriptionTask(goal, task, Agents, Profiles,
                Workspace.ResolveExecutionDirectory(goal.Id), ObservedAt.AddHours(1),
                allowGitReference: true, sandboxOptions: Sandbox, commandExists: _ => true,
                providerHoldScope: holds, cascadeTesterCheapFirst: false);

        internal ProviderHoldAgentSubstitution Resolver(AgentOrchestratorKernel? kernel = null) =>
            new(kernel ?? Kernel, Agents, Preflight);

        internal ProviderHoldAgentSubstitution.Result Resolve(Goal goal) => Resolver().Resolve(goal, goal.Tasks.Single());

        internal static AgentDefinition Agent(string id, string provider, string profile, string model) => new(
            new AgentId(id), id, AgentRole.Tester,
            new ModelProfile(provider, model, ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
                SubscriptionMode.ApiKey, "high"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile(profile, model, "high"), IsProviderRoutingConstrained: true);

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
