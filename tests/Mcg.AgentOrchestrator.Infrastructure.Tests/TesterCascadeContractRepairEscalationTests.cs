using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: isolated temporary repositories, injected launcher probe, no environment mutation or worker launch.
public sealed class TesterCascadeContractRepairEscalationTests : WorkerDispatchTestSupport
{
    private const string CatalogJson = """
        {"Agents":[{"Id":{"Value":"openai-tester"},"Name":"Codex GPT-6.1 Sol tester","Role":"Tester","Model":{"ProviderName":"OpenAI","ModelName":"gpt-6.1-sol","Capabilities":"Text, Code, ToolUse","SubscriptionMode":"ApiKey","ReasoningEffort":"medium","MaxOutputTokens":2048},"Status":"Available","ExecutionPolicy":"PreferSubscription","Subscription":{"WorkerProfileName":"codex-cli","ModelAlias":"gpt-6.1-sol","ReasoningEffort":"medium"},"ComplexModel":{"ProviderName":"OpenAI","ModelName":"gpt-6.1-sol","Capabilities":"Text, Code, ToolUse","SubscriptionMode":"ApiKey","ReasoningEffort":"high","MaxOutputTokens":4096},"ReasoningEffortPolicy":null,"IsProviderRoutingConstrained":true}]}
        """;
    private const string MalformedResult = """
        WORKER_RESULT:
        files: none
        commands: inspected gate receipt
        tests: pass - receipt finding-evidence-fixture green; Passed: 14
        commit: none
        blockers: none
        model_fit: OpenAI/gpt-6-luna - adequate - verification shape - inspected receipt
        skills: none
        confidence: high
        """;
    private const string ValidResult = MalformedResult + "\nEND_WORKER_RESULT";
    private const string MalformedRepair = "Tester terminal contract repair 1/2; same-role retry: terminal_contract=Malformed; Final WORKER_RESULT is missing END_WORKER_RESULT.";
    private static readonly DateTimeOffset At = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly WorkerSandboxOptions Sandbox = new(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    [Fact]
    public void MalformedCheapRoundEscalatesRefreshesAndPreservesRepairBudget()
    {
        using var f = new Fixture();
        f.Prepare(At.AddMinutes(10));
        AssertRoute(f.Task, "codex-luna", "gpt-6-luna", "codex-luna:cascade-cheap", "cheap", "tester-cheap-first");
        f.Record(MalformedResult);
        Assert.Equal(MalformedRepair, Assert.Single(RepairEvents(f.Goal)).Message);

        f.Prepare(At.AddMinutes(20));
        AssertRoute(f.Task, "codex-cli", "gpt-6.1-sol", "codex-cli:cascade-escalated", "escalated", "tester-terminal-contract-malformed");
        var escalated = RouteValues(f.Task);
        new GoalDispatchOperations().RefreshPreparedDispatchBeforeStart(f.Kernel, f.Workspace, f.Goal,
            f.Task, f.Agents, WorkerProfileCatalog.Default(), sandboxOptions: Sandbox);
        Assert.Equal(escalated, RouteValues(f.Task));

        f.Record(MalformedResult);
        Assert.Equal(2, RepairEvents(f.Goal).Length);
        Assert.StartsWith("Tester terminal contract repair 2/2; same-role retry: terminal_contract=Malformed;", RepairEvents(f.Goal)[1].Message);
        f.Prepare(f.Task.LastDispatch!.DispatchedAt.AddMinutes(10));
        AssertRoute(f.Task, "codex-cli", "gpt-6.1-sol", "codex-cli", "primary", "prior-escalation");
        f.Record(MalformedResult);
        AssertBudgetExhausted(f);
    }

    [Fact]
    public void WellFormedCheapRoundKeepsIdenticalRouteAfterDeveloperRework()
    {
        using var f = new Fixture();
        f.Prepare(At.AddMinutes(10));
        var first = RouteValues(f.Task);
        f.Record(ValidResult);
        Assert.DoesNotContain(f.Goal.Timeline, e => e.Message.StartsWith("Tester terminal contract repair ", StringComparison.Ordinal));
        Assert.Equal(WorkTaskStatus.Completed, f.Task.Status);
        f.Kernel.RetryTask(f.Goal.Id, f.Task.Id, "Recheck after the Developer rework.");
        f.Prepare(At.AddMinutes(20));
        Assert.Equal(first, RouteValues(f.Task));
        AssertRoute(f.Task, "codex-luna", "gpt-6-luna", "codex-luna:cascade-cheap", "cheap", "tester-cheap-first");
    }

    [Theory]
    [InlineData(false, true, "switched-off")]
    [InlineData(true, false, "cheap-profile-unavailable")]
    public void StandardLaneMalformedRepairsKeepIdenticalRouteAndBudget(
        bool cheapFirst, bool hasCheapProfile, string rule)
    {
        using var f = new Fixture(cheapFirst, hasCheapProfile);
        f.Prepare(At.AddMinutes(10));
        var first = RouteValues(f.Task);
        AssertRoute(f.Task, "codex-cli", "gpt-6.1-sol", "codex-cli", "primary", rule);
        f.Record(MalformedResult);
        Assert.Equal(MalformedRepair, Assert.Single(RepairEvents(f.Goal)).Message);

        for (var round = 2; round <= 3; round++)
        {
            f.Prepare(At.AddMinutes(round * 10));
            Assert.Equal(first, RouteValues(f.Task));
            Assert.DoesNotContain("tester-terminal-contract-malformed", f.Task.LastDispatch!.ModelSelectionReason!, StringComparison.Ordinal);
            f.Record(MalformedResult);
        }
        AssertBudgetExhausted(f);
    }

    [Theory]
    [InlineData(0, ProgressKind.TaskRetried, MalformedRepair, true)]
    [InlineData(1, ProgressKind.TaskRetried, MalformedRepair, false)]
    [InlineData(2, ProgressKind.TaskRetried, MalformedRepair, false)]
    [InlineData(-1, ProgressKind.TaskRetried, MalformedRepair, false)]
    [InlineData(0, ProgressKind.TaskNote, MalformedRepair, false)]
    [InlineData(0, ProgressKind.TaskFailed, MalformedRepair, false)]
    [InlineData(0, ProgressKind.TaskFailed, "Tester terminal contract repair budget exhausted; operator action required: terminal_contract=Malformed; Final WORKER_RESULT is missing END_WORKER_RESULT.", false)]
    [InlineData(0, ProgressKind.TaskRetried, "Tester terminal contract repair 1/2; same-role retry: terminal_contract=Missing; No WORKER_RESULT.", false)]
    [InlineData(0, ProgressKind.TaskRetried, "Tester terminal contract repair 1/2; same-role retry: terminal_contract=Stale; Old contract.", false)]
    [InlineData(0, ProgressKind.TaskRetried, "Tester terminal contract repair 1/2; same-role retry: terminal_contract=Unreadable; Unreadable contract.", false)]
    [InlineData(0, ProgressKind.TaskRetried, "Tester terminal contract repair 1/2; same-role retry: terminal_contract=Interrupted; Interrupted contract.", false)]
    [InlineData(0, ProgressKind.TaskRetried, "Reviewer terminal contract repair 1/2; same-role retry: terminal_contract=Malformed; Missing end marker.", false)]
    [InlineData(0, ProgressKind.TaskRetried, "tester terminal contract repair 1/2; same-role retry: terminal_contract=Malformed; Missing end marker.", false)]
    [InlineData(0, ProgressKind.TaskRetried, "Tester terminal contract repair 1/2; same-role retry: terminal_contract=malformed; Missing end marker.", false)]
    public void PredicateRequiresLatestCheapRoundAndExactRepairEvidence(
        int newerDispatches, ProgressKind kind, string message, bool expected)
    {
        var e = new ProgressEvent(GoalId.New(), TaskId.New(), kind, message, At);
        Assert.Equal(expected, TesterCascadeContractRepairEscalation.ShouldEscalate(newerDispatches, [e]));
    }

    [Fact]
    public void PredicateEmptyEventsDoNotEscalate()
    {
        Assert.False(TesterCascadeContractRepairEscalation.ShouldEscalate(0, []));
    }

    private static ProgressEvent[] RepairEvents(Goal goal) => goal.Timeline.Where(e =>
        e.Kind == ProgressKind.TaskRetried && e.Message.StartsWith("Tester terminal contract repair ", StringComparison.Ordinal)).ToArray();

    private static void AssertBudgetExhausted(Fixture f)
    {
        Assert.Equal(WorkTaskStatus.Failed, f.Task.Status);
        Assert.Equal(2, RepairEvents(f.Goal).Length);
        Assert.StartsWith("Tester terminal contract repair budget exhausted", Assert.Single(f.Goal.Timeline,
            e => e.Kind == ProgressKind.TaskFailed).Message);
    }

    private static (string Worker, string? Model, string? Lane, string? Reason) RouteValues(TaskSpec task)
    {
        var dispatch = task.LastDispatch!;
        return (dispatch.WorkerName, dispatch.ModelName, dispatch.DispatchLane, dispatch.ModelSelectionReason);
    }

    private static void AssertRoute(TaskSpec task, string worker, string model, string lane, string decision, string rule)
    {
        var dispatch = task.LastDispatch!;
        Assert.NotNull(dispatch);
        Assert.Equal(worker, dispatch.WorkerName);
        Assert.Equal(model, dispatch.ModelName);
        Assert.Equal(lane, dispatch.DispatchLane);
        Assert.StartsWith("provider-constrained: Tester remains on OpenAI", dispatch.ModelSelectionReason);
        Assert.True(CascadeRouteMarker.TryParse(dispatch.ModelSelectionReason, out var marker));
        Assert.Equal(decision, marker.Decision);
        Assert.Equal(rule, marker.RuleId);
        Assert.Contains($"cascade={decision} rule={rule}", dispatch.ModelSelectionReason!, StringComparison.Ordinal);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => At;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = CreateSeededDispatchRepository();
        internal string WorkingDirectory { get; }
        internal OrchestratorWorkspace Workspace { get; }
        internal AgentOrchestratorKernel Kernel { get; } = new(new FixedClock());
        internal TaskSpec Task { get; } = new(TaskId.New(), "Verify focused classes.", AgentRole.Tester);
        internal Goal Goal { get; }
        internal IReadOnlyList<AgentDefinition> Agents { get; }
        private readonly WorkerProfileCatalog _profiles;
        private readonly bool _cheapFirst;

        internal Fixture(bool cheapFirst = true, bool hasCheapProfile = true)
        {
            Workspace = InfrastructureTestSupport.CreateRefinedWorkspace(Root);
            var catalogPath = Path.Combine(Root, "catalog.json");
            File.WriteAllText(catalogPath, CatalogJson);
            Agents = AgentCatalogStore.Load(catalogPath).Agents;
            Goal = Kernel.CreateGoal("Focused verification.", [Task]);
            InfrastructureTestSupport.MarkGoalRefined(Kernel, Goal);
            Kernel.ActivateGoal(Goal.Id, Agents);
            WorkingDirectory = GoalWorktrees.Ensure(Root, Goal.Id);
            _cheapFirst = cheapFirst;
            _profiles = hasCheapProfile ? WorkerProfileCatalog.Default() : new WorkerProfileCatalog(
                WorkerProfileCatalog.Default().Profiles.Where(p => p.Name != "codex-luna").ToList());
        }

        internal void Prepare(DateTimeOffset dispatchedAt) => WorkerProfileDispatcher.PrepareSubscriptionTask(
            Kernel, Goal, Task, Agents, _profiles, Workspace.PromptDirectory, WorkingDirectory, dispatchedAt,
            sandboxOptions: Sandbox, commandExists: _ => true,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli, cascadeTesterCheapFirst: _cheapFirst);

        internal void Record(string output)
        {
            var dispatch = Task.LastDispatch!;
            Kernel.ReportTaskProgress(Goal.Id, Task.Id, WorkTaskStatus.Running, "Fixture attempt started.");
            Kernel.RecordDispatchExecutionResult(Goal.Id, Task.Id, new TaskVerificationRecord(
                dispatch.Command, dispatch.WorkingDirectory, 0, output, "", dispatch.DispatchedAt.AddSeconds(1),
                WorkerResultPresent: true, DispatchStartedAt: dispatch.DispatchedAt,
                EvidenceSource: VerificationEvidenceSource.Dispatch, DispatchAttemptAt: dispatch.DispatchedAt));
        }

        public void Dispose() => GoalWorktrees.DeleteDirectoryWithRetry(Root);
    }
}
