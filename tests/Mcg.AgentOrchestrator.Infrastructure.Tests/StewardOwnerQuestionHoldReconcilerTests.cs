using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fixture owns its stores, clock and conduct log.
public sealed class StewardOwnerQuestionHoldReconcilerTests
{
    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Retried_trigger_clears_hold_once_and_records_exact_event()
    {
        using var fixture = new Fixture();
        await fixture.RaiseHold();
        var before = Xunit.Assert.IsType<GoalHoldState>(fixture.Goal.CurrentHold);
        var trigger = $"{fixture.Goal.Id.Value}:{fixture.Task.Id.Value}:{StewardHarness.Sha}:DeveloperNoChangeWithConfirmedRed";
        fixture.Retry();
        var changed = new HashSet<GoalId>();

        fixture.Reconcile(changed);

        Xunit.Assert.Null(fixture.Goal.CurrentHold);
        Xunit.Assert.Equal(fixture.Goal.Id, Xunit.Assert.Single(changed));
        var entry = Xunit.Assert.Single(fixture.ReconciledEvents());
        Xunit.Assert.Equal(fixture.Goal.Id.Value, entry.GetProperty("goalId").GetString());
        Xunit.Assert.Equal($"item=StewardTrigger:{trigger} target=retried " +
            $"retried_at={fixture.Task.LatestRetryAt!.Value.ToString("O", CultureInfo.InvariantCulture)} " +
            $"hold_started={before.StartedAt.ToString("O", CultureInfo.InvariantCulture)}",
            entry.GetProperty("detail").GetString());
        changed.Clear();
        fixture.Reconcile(changed);
        Xunit.Assert.Empty(changed);
        Xunit.Assert.Single(fixture.ReconciledEvents());
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData("no-retry")]
    [Xunit.InlineData("retry-before-hold")]
    [Xunit.InlineData("retry-at-hold")]
    [Xunit.InlineData("failed-again")]
    [Xunit.InlineData("author")]
    [Xunit.InlineData("parked")]
    [Xunit.InlineData("cancelled")]
    [Xunit.InlineData("other-goal")]
    [Xunit.InlineData("malformed")]
    [Xunit.InlineData("missing-task")]
    public async Task Ineligible_hold_keeps_identity_and_start_without_events(string scenario)
    {
        using var fixture = new Fixture();
        await fixture.RaiseHold();
        var original = Xunit.Assert.IsType<GoalHoldState>(fixture.Goal.CurrentHold);
        if (scenario != "no-retry") fixture.Retry();
        if (scenario is "retry-before-hold" or "retry-at-hold")
            fixture.ReplaceHold(original.State, original.Blocker,
                fixture.Task.LatestRetryAt!.Value.AddHours(scenario == "retry-before-hold" ? 1 : 0), original.Identity);
        if (scenario == "failed-again") fixture.Kernel.ReportTaskProgress(fixture.Goal.Id, fixture.Task.Id,
            WorkTaskStatus.Failed, "Retry failed again");
        if (scenario == "author") fixture.ReplaceHold("author-owner-question",
            "author-owner-question item=HumanInput:target reason=model-ask-owner question=Repair? recommendation=Repair",
            original.StartedAt, original.Identity);
        if (scenario == "parked") fixture.Kernel.ParkGoal(fixture.Goal.Id, "Keep parked");
        if (scenario == "cancelled") fixture.Kernel.CancelGoal(fixture.Goal.Id, "Stop work");
        if (scenario == "malformed") fixture.ReplaceHold(original.State,
            "steward-owner-question case=A trigger=broken question=Repair? evidence=[]", original.StartedAt, original.Identity);
        if (scenario == "missing-task") fixture.ReplaceHold(original.State,
            original.Blocker.Replace(fixture.Task.Id.Value, TaskId.New().Value, StringComparison.Ordinal),
            original.StartedAt, original.Identity);
        var onlyGoalId = scenario == "other-goal" ? fixture.Kernel.CreateGoal("Other goal",
            [new TaskSpec(TaskId.New(), "Other work", AgentRole.Developer)]).Id.Value : null;
        var before = Xunit.Assert.IsType<GoalHoldState>(fixture.Goal.CurrentHold);
        var logBefore = File.ReadAllText(fixture.Harness.ConductPath);
        var changed = new HashSet<GoalId>();

        fixture.Reconcile(changed, onlyGoalId);

        Xunit.Assert.Equal(before.Identity, fixture.Goal.CurrentHold?.Identity);
        Xunit.Assert.Equal(before.StartedAt, fixture.Goal.CurrentHold?.StartedAt);
        Xunit.Assert.Empty(changed);
        Xunit.Assert.Empty(fixture.ReconciledEvents());
        Xunit.Assert.Equal(logBefore, File.ReadAllText(fixture.Harness.ConductPath));
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Disabled_host_clears_resolved_hold_without_model_call()
    {
        using var fixture = new Fixture();
        await fixture.RaiseHold();
        fixture.Retry();
        var calls = fixture.Harness.Model.Calls;
        var host = new ConductorStewardHost(
            new ConductorStewardTriggerStore(Path.Combine(fixture.Harness.Root, "disabled-triggers.db")),
            new ConductorStewardTriggerDetector((_, _) => "GoalAcceptanceVerifier", _ => []),
            fixture.Harness.Model, fixture.Harness.Intents, new AdjudicationEvidenceResolver(fixture.Harness.Root),
            _ => 7, _ => fixture.Harness.Root,
            new GoalLifecycleEventWriter(Path.Combine(fixture.Harness.Root, "disabled-lifecycle")), fixture.Conduct,
            enabled: false);
        try
        {
            var changed = host.ServiceTick(fixture.Kernel);
            Xunit.Assert.Equal(fixture.Goal.Id, Xunit.Assert.Single(changed));
            Xunit.Assert.Null(fixture.Goal.CurrentHold);
            Xunit.Assert.Equal(calls, fixture.Harness.Model.Calls);
            Xunit.Assert.Null(host.CurrentRound);
            Xunit.Assert.Single(fixture.ReconciledEvents());
        }
        finally { host.Stop(); }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly StewardHarness Harness = new("A");
        internal readonly ConductEventLogWriter Conduct;
        private readonly RetryClock _clock = new();
        internal AgentOrchestratorKernel Kernel { get; private set; } = null!;
        internal Goal Goal => Kernel.Goals.Single(goal => goal.Id == Harness.Goal.Id);
        internal TaskSpec Task => Goal.Tasks.Single();

        internal Fixture() => Conduct = new ConductEventLogWriter(Harness.ConductPath);

        internal async Task RaiseHold()
        {
            Harness.Seed("A");
            Harness.Model.Reply("""{"kind":"ask-owner","question":"Which repair?","evidenceReferences":["worker-output=receipt-1"]}""");
            Harness.Host.ServiceTick(Harness.Kernel);
            await Harness.Model.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Harness.Host.CurrentRound!.WaitAsync(TimeSpan.FromSeconds(30));
            Harness.Host.ServiceTick(Harness.Kernel);
            var hold = Xunit.Assert.IsType<GoalHoldState>(Harness.Goal.CurrentHold);
            Xunit.Assert.Equal("steward-owner-question", hold.State);
            // The retry clock is explicit; the four-condition decision never depends on elapsed time.
            _clock.UtcNow = hold.StartedAt.AddHours(1);
            Kernel = AgentOrchestratorKernel.FromSnapshot(Harness.Kernel.ExportSnapshot(), _clock);
        }

        internal void Retry() => Kernel.RetryTask(Goal.Id, Task.Id, "Recover trigger task",
            retryCause: RetryCause.EnvironmentApparatusFailure, invalidateDownstream: true, retryRoundKind: null);

        internal void ReplaceHold(string state, string blocker, DateTimeOffset started, string identity)
        {
            Kernel.ClearGoalHold(Goal.Id);
            Kernel.ObserveGoalHold(Goal.Id, state, blocker, started, TimeSpan.MaxValue, identity);
        }

        internal void Reconcile(ISet<GoalId> changed, string? onlyGoalId = null) =>
            StewardOwnerQuestionHoldReconciler.Reconcile(Kernel, Conduct, onlyGoalId, changed);

        internal JsonElement[] ReconciledEvents() => File.ReadAllLines(Harness.ConductPath).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).Where(entry => entry.GetProperty("eventKind").GetString() == "owner-question-hold-reconciled").ToArray();

        public void Dispose() { Harness.Host.Stop(); Harness.Dispose(); }
    }

    private sealed class RetryClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
