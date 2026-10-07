using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all effects are recording delegates with per-test state.
public sealed class DispatchStartExecutorTests
{
    [Xunit.Theory(DisplayName = "Sandbox recovery retries recorded starts with the refreshed goal")]
    [Xunit.InlineData(GoalLifecycleState.WorkspaceReady, "dispatch")]
    [Xunit.InlineData(GoalLifecycleState.Dispatched, "recorded")]
    public void SandboxRecoveryRetriesRecordedStart(GoalLifecycleState state, string firstDelegate)
    {
        var action = RecoveryAction();
        var first = DispatchStartOutcome.RecoverableSandboxPrep(action);
        var second = DispatchStartOutcome.Started();
        var fixture = new RecordingEffects(first, second);
        fixture.Recover = observed => { Assert.Same(action, observed); return true; };

        var result = fixture.Execute(state);

        Assert.Same(second, result.Outcome);
        Assert.Null(result.RecoveryFailureReason);
        Assert.Equal(new[] { firstDelegate, "recorded" }, fixture.Starts.Select(call => call.Delegate));
        Assert.Same(fixture.InitialGoal, fixture.Starts[0].Goal);
        Assert.Same(fixture.RefreshedGoal, fixture.Starts[1].Goal);
        Assert.All(fixture.Starts, call => Assert.Same(fixture.Policy, call.Policy));
        Assert.Equal(1, fixture.RecoveryCalls);
        Assert.Equal(0, fixture.RemediationCalls);
        Assert.Equal(2, fixture.RefreshCalls);
        Assert.Equal(new[] { "result=RecoverableSandboxPrep", "result=Started retry=sandbox-prep" },
            fixture.Timings.Select(timing => timing.Detail));
        Assert.All(fixture.Timings, timing =>
        {
            Assert.Equal("dispatch-prep", timing.Phase);
            Assert.Same(fixture.InitialGoal, timing.Goal);
        });
        Assert.Same(first, fixture.Timings[0].Outcome);
        Assert.Same(second, fixture.Timings[1].Outcome);
        Assert.Equal(new[] { firstDelegate, "timing", "refresh", "recover", "recorded", "timing", "refresh" },
            fixture.Events);
    }

    [Xunit.Theory(DisplayName = "Failed or throwing recovery returns the existing failure reason without retrying")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RecoveryFailureDoesNotRetry(bool throws)
    {
        var action = RecoveryAction();
        var first = DispatchStartOutcome.RecoverableSandboxPrep(action);
        var fixture = new RecordingEffects(first);
        fixture.Recover = _ => throws ? throw new InvalidOperationException("recovery exception") : false;

        var result = fixture.Execute(GoalLifecycleState.WorkspaceReady);

        Assert.Same(first, result.Outcome);
        Assert.Equal($"Low-IL sandbox prep recovery failed for goal {fixture.Prefix}: " +
            (throws ? "recovery exception" : action.Reason), result.RecoveryFailureReason);
        Assert.Single(fixture.Starts);
        Assert.Equal(1, fixture.RecoveryCalls);
        Assert.Equal(0, fixture.RemediationCalls);
        Assert.Single(fixture.Timings);
        Assert.Equal(new[] { "dispatch", "timing", "refresh", "recover" }, fixture.Events);
    }

    [Xunit.Theory(DisplayName = "Missing recovery action preserves the reason including the empty and null cases")]
    [Xunit.InlineData("missing action")]
    [Xunit.InlineData("")]
    [Xunit.InlineData(null)]
    public void MissingRecoveryActionDoesNotInvokeRecovery(string? reason)
    {
        var first = new DispatchStartOutcome(DispatchStartOutcomeCategory.RecoverableSandboxPrep, reason);
        var fixture = new RecordingEffects(first);

        var result = fixture.Execute(GoalLifecycleState.WorkspaceReady);

        Assert.Same(first, result.Outcome);
        Assert.Equal(reason ?? "Low-IL sandbox prep recovery action was missing.", result.RecoveryFailureReason);
        Assert.Single(fixture.Starts);
        Assert.Equal(0, fixture.RecoveryCalls);
        Assert.Equal(0, fixture.RemediationCalls);
        Assert.Single(fixture.Timings);
        Assert.Equal(new[] { "dispatch", "timing", "refresh" }, fixture.Events);
    }

    [Xunit.Theory(DisplayName = "An empty remediation retry restores the original spawn failure")]
    [Xunit.InlineData(GoalLifecycleState.WorkspaceReady, "dispatch", "recorded")]
    [Xunit.InlineData(GoalLifecycleState.Dispatched, "recorded", "recorded")]
    [Xunit.InlineData(GoalLifecycleState.Created, "dispatch", "dispatch")]
    public void EmptyRemediationRetryRestoresSpawnFailure(
        GoalLifecycleState state, string firstDelegate, string retryDelegate)
    {
        var first = DispatchStartOutcome.SpawnFailed("original spawn failure");
        var second = DispatchStartOutcome.EmptyBatch("retry found no tasks");
        var fixture = new RecordingEffects(first, second);

        var result = fixture.Execute(state);

        Assert.Same(first, result.Outcome);
        Assert.Null(result.RecoveryFailureReason);
        Assert.Equal(new[] { firstDelegate, retryDelegate }, fixture.Starts.Select(call => call.Delegate));
        Assert.Same(fixture.RefreshedGoal, fixture.Starts[1].Goal);
        Assert.Equal(0, fixture.RecoveryCalls);
        Assert.Equal(1, fixture.RemediationCalls);
        Assert.Equal(2, fixture.RefreshCalls);
        Assert.Equal(new[] { "dispatch-prep", "dispatch-remediation", "dispatch-prep" },
            fixture.Timings.Select(timing => timing.Phase));
        Assert.Equal(new[] { "result=SpawnFailed", "result=remediated", "result=EmptyBatch retry=spawn-failed" },
            fixture.Timings.Select(timing => timing.Detail));
        Assert.Same(first, fixture.Timings[0].Outcome);
        Assert.Null(fixture.Timings[1].Outcome);
        Assert.Same(second, fixture.Timings[2].Outcome);
        Assert.Same(fixture.InitialGoal, fixture.Timings[0].Goal);
        Assert.Same(fixture.RefreshedGoal, fixture.Timings[1].Goal);
        Assert.Same(fixture.InitialGoal, fixture.Timings[2].Goal);
        Assert.Equal(new[] { firstDelegate, "timing", "refresh", "remediate", "timing", retryDelegate, "timing", "refresh" },
            fixture.Events);
    }

    [Xunit.Fact(DisplayName = "A started dispatch reports one attempt and invokes no recovery effects")]
    public void StartedReturnsWithoutRecoveryEffects()
    {
        var started = DispatchStartOutcome.Started();
        var fixture = new RecordingEffects(started);

        var result = fixture.Execute(GoalLifecycleState.WorkspaceReady);

        Assert.Same(started, result.Outcome);
        Assert.Null(result.RecoveryFailureReason);
        Assert.Single(fixture.Starts);
        Assert.Equal(0, fixture.RecoveryCalls);
        Assert.Equal(0, fixture.RemediationCalls);
        var timing = Assert.Single(fixture.Timings);
        Assert.Equal("dispatch-prep", timing.Phase);
        Assert.Equal("result=Started", timing.Detail);
        Assert.Same(started, timing.Outcome);
        Assert.Equal(new[] { "dispatch", "timing", "refresh" }, fixture.Events);
    }

    [Xunit.Fact(DisplayName = "A spawn failure after sandbox recovery also runs the remediation retry")]
    public void SandboxRetrySpawnFailureRunsRemediation()
    {
        var first = DispatchStartOutcome.RecoverableSandboxPrep(RecoveryAction());
        var spawnFailed = DispatchStartOutcome.SpawnFailed("retry failed to spawn");
        var fixture = new RecordingEffects(first, spawnFailed, DispatchStartOutcome.EmptyBatch("empty"));
        fixture.Recover = _ => true;

        var result = fixture.Execute(GoalLifecycleState.WorkspaceReady);

        Assert.Same(spawnFailed, result.Outcome);
        Assert.Null(result.RecoveryFailureReason);
        Assert.Equal(new[] { "dispatch", "recorded", "recorded" }, fixture.Starts.Select(call => call.Delegate));
        Assert.Equal(1, fixture.RecoveryCalls);
        Assert.Equal(1, fixture.RemediationCalls);
        Assert.Equal(3, fixture.RefreshCalls);
        Assert.Equal(new[] { "result=RecoverableSandboxPrep", "result=SpawnFailed retry=sandbox-prep",
            "result=remediated", "result=EmptyBatch retry=spawn-failed" }, fixture.Timings.Select(timing => timing.Detail));
    }

    private static WorkerSandboxPrepRecoverableAction RecoveryAction() =>
        new("fake-worktree", "fake-sandbox", "fake-failed-root", "prep needs recovery", false);

    private sealed class RecordingEffects
    {
        private readonly Queue<DispatchStartOutcome> _outcomes;
        internal Goal InitialGoal { get; } = new(GoalId.New(), "initial",
            [new TaskSpec(TaskId.New(), "dispatch task", AgentRole.Developer)]);
        internal Goal RefreshedGoal { get; }
        internal ConductorAutonomyPolicy Policy { get; } = ConductorAutonomyPolicy.Conservative;
        internal string Prefix => InitialGoal.Id.Value[..8];
        internal Func<WorkerSandboxPrepRecoverableAction, bool> Recover { get; set; } =
            _ => throw new InvalidOperationException("Unexpected recovery call.");
        internal List<(string Delegate, Goal Goal, ConductorAutonomyPolicy Policy)> Starts { get; } = [];
        internal List<DispatchStartTiming> Timings { get; } = [];
        internal List<string> Events { get; } = [];
        internal int RecoveryCalls { get; private set; }
        internal int RemediationCalls { get; private set; }
        internal int RefreshCalls { get; private set; }

        internal RecordingEffects(params DispatchStartOutcome[] outcomes)
        {
            _outcomes = new(outcomes);
            RefreshedGoal = new(InitialGoal.Id, "refreshed", InitialGoal.Tasks);
        }

        internal DispatchStartExecution Execute(GoalLifecycleState state) => DispatchStartExecutor.Execute(
            InitialGoal, Prefix, Policy, state,
            (goal, policy) => Start("dispatch", goal, policy),
            (goal, policy) => Start("recorded", goal, policy),
            action => { Events.Add("recover"); RecoveryCalls++; return Recover(action); },
            () => { Events.Add("remediate"); RemediationCalls++; return "remediated"; },
            () => { Events.Add("refresh"); RefreshCalls++; return RefreshedGoal; },
            timing => { Events.Add("timing"); Timings.Add(timing); });

        private DispatchStartOutcome Start(string name, Goal goal, ConductorAutonomyPolicy policy)
        {
            Events.Add(name);
            Starts.Add((name, goal, policy));
            return _outcomes.Dequeue();
        }
    }
}
