using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: injected clock, per-test kernel and private temporary stores.
public sealed class ConductorStewardCaseGTriggerTests
{
    [Xunit.Fact]
    public void Quiet_verifying_goal_emits_G_for_last_completed_task()
    {
        using var h = new StewardCaseGHarness();
        var trigger = Assert.Single(h.CaseG.Detect(h.Goal, h.Clock.UtcNow));
        Assert.Equal(ConductorStewardTriggerKind.VerifyingWithoutLiveGate, trigger.Kind);
        Assert.Equal("G", trigger.CaseLetter);
        Assert.Equal(h.Target.Id.Value, trigger.TaskId);
        Assert.Equal(StewardCaseGHarness.Sha, trigger.CandidateSha);
        Assert.Equal(h.QuietSince, trigger.OccurredAt);
        Assert.Contains("steward-case=G", trigger.EvidenceReferences);
        Assert.Contains($"quiet-since={h.QuietSince:O}", trigger.EvidenceReferences);
        Assert.Contains($"branch-head={StewardCaseGHarness.Sha}", trigger.EvidenceReferences);
        Assert.Equal(StewardCaseGHarness.WorkerResult, trigger.WorkerResult);
        var note = ConductorStewardCaseG.ComposeNote(trigger, trigger.WorkerResult);
        Assert.Contains(h.Goal.Id.Value, note);
        Assert.Contains("minutes quiet=31", note);
        Assert.Contains("threshold=30 minutes", note);
        Assert.Contains("Completed=3", note);
        Assert.Contains("no live acceptance attempt", note);
        Assert.Contains("no landing evidence", note);
        Assert.EndsWith("\n\n--- Developer WORKER_RESULT (verbatim) ---\n" +
            StewardCaseGHarness.WorkerResult, note);
    }

    [Xunit.Theory]
    [Xunit.InlineData("live")]
    [Xunit.InlineData("grouped")]
    [Xunit.InlineData("29-minutes")]
    [Xunit.InlineData("recent-event")]
    [Xunit.InlineData("assigned")]
    [Xunit.InlineData("running")]
    [Xunit.InlineData("verified")]
    [Xunit.InlineData("landed")]
    [Xunit.InlineData("null-head")]
    [Xunit.InlineData("null-live")]
    [Xunit.InlineData("throw-live")]
    [Xunit.InlineData("null-grouped")]
    [Xunit.InlineData("throw-grouped")]
    [Xunit.InlineData("null-landing")]
    [Xunit.InlineData("throw-landing")]
    [Xunit.InlineData("null-head-source")]
    [Xunit.InlineData("throw-head")]
    [Xunit.InlineData("empty-timeline")]
    [Xunit.InlineData("no-completed")]
    public void Ineligible_state_or_unavailable_source_emits_nothing(string scenario)
    {
        using var h = new StewardCaseGHarness();
        Assert.Single(h.CaseG.Detect(h.Goal, h.Clock.UtcNow));
        Func<GoalId, bool>? live = _ => scenario == "live";
        Func<GoalId, bool>? grouped = _ => scenario == "grouped";
        Func<Goal, bool>? landing = _ => scenario == "landed";
        Func<Goal, string?>? head = _ => scenario == "null-head" ? null : StewardCaseGHarness.Sha;
        if (scenario == "null-live") live = null;
        if (scenario == "throw-live") live = _ => throw new IOException("unreadable live attempt");
        if (scenario == "null-grouped") grouped = null;
        if (scenario == "throw-grouped") grouped = _ => throw new IOException("unreadable grouped attempt");
        if (scenario == "null-landing") landing = null;
        if (scenario == "throw-landing") landing = _ => throw new IOException("unreadable landing journal");
        if (scenario == "null-head-source") head = null;
        if (scenario == "throw-head") head = _ => throw new IOException("unreadable head");
        var snapshot = h.Kernel.ExportSnapshot();
        var goal = Assert.Single(snapshot.Goals);
        if (scenario == "verified") goal = goal with { Status = GoalStatus.Verified };
        if (scenario == "empty-timeline") goal = goal with { Timeline = [] };
        goal = goal with
        {
            Tasks = goal.Tasks.Select((task, index) => scenario == "no-completed"
                ? task with { Status = WorkTaskStatus.Cancelled }
                : index != 0 ? task : scenario switch
                {
                    "assigned" => task with { Status = WorkTaskStatus.Assigned },
                    "running" => task with { Status = WorkTaskStatus.Running },
                    _ => task
                }).ToArray()
        };
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [goal] }, h.Clock);
        var candidate = Assert.Single(kernel.Goals);
        if (scenario == "recent-event")
            kernel.RecordGoalPolicyDecision(candidate.Id, "Acceptance observation changed");
        var now = scenario == "29-minutes" ? h.QuietSince.AddMinutes(29) :
            scenario == "recent-event" ? h.Clock.UtcNow.AddMinutes(5) : h.Clock.UtcNow;
        Assert.Empty(new ConductorStewardCaseG(live, grouped, landing, head).Detect(candidate, now));
    }

    [Xunit.Fact]
    public void Exactly_thirty_minutes_is_stranded_and_earlier_tasks_are_not_targets()
    {
        using var h = new StewardCaseGHarness();
        var now = h.QuietSince.Add(ConductorStewardCaseG.QuietThreshold);
        Assert.Single(h.CaseG.Detect(h.Goal, now));
        Assert.True(ConductorStewardCaseG.IsStranded(h.Goal, h.Target, now));
        Assert.False(ConductorStewardCaseG.IsStranded(h.Goal, h.Goal.Tasks[0], now));
    }

    [Xunit.Theory]
    [Xunit.InlineData("latest-time")]
    [Xunit.InlineData("tie")]
    [Xunit.InlineData("no-time")]
    public void Detection_and_admission_share_completion_order(string scenario)
    {
        using var h = new StewardCaseGHarness();
        var snapshot = h.Kernel.ExportSnapshot();
        var goal = Assert.Single(snapshot.Goals);
        goal = goal with
        {
            Tasks = goal.Tasks.Select((task, index) => task with
            {
                VerificationHistory = null,
                LastVerification = scenario == "no-time" ? null : task.LastVerification! with
                {
                    CompletedAt = h.QuietSince.AddSeconds(scenario == "latest-time" && index == 0 ? 1 : 0)
                }
            }).ToArray()
        };
        var candidate = Assert.Single(AgentOrchestratorKernel.FromSnapshot(
            snapshot with { Goals = [goal] }, h.Clock).Goals);
        var target = scenario == "latest-time" ? candidate.Tasks[0] : candidate.Tasks[^1];
        var trigger = Assert.Single(h.CaseG.Detect(candidate, h.Clock.UtcNow));
        Assert.Equal(target.Id.Value, trigger.TaskId);
        var payload = new AdjudicateOperatorIntentPayload("reopen-regate", "Recovery", trigger.EvidenceReferences,
            7, h.Root, Precedent: $"steward-case=G trigger={trigger.Identity}");
        Assert.True(ConductorStewardCaseG.Admits(candidate, target, payload, h.Clock.UtcNow));
        Assert.All(candidate.Tasks.Where(task => task.Id != target.Id), task =>
            Assert.False(ConductorStewardCaseG.Admits(candidate, task, payload, h.Clock.UtcNow)));
    }
}

internal sealed class StewardCaseGHarness : IDisposable
{
    internal const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string WorkerResult = "WORKER_RESULT:\nfiles: none\ntests: deferred - ExampleTests\ncommit: none\nblockers: none\nEND_WORKER_RESULT";

    internal StewardCaseGHarness(int quietMinutes = 31, Func<GoalId, long?>? version = null)
    {
        Directory.CreateDirectory(Root);
        Kernel = new AgentOrchestratorKernel(Clock);
        Goal = Kernel.CreateGoal("Recover stranded acceptance", Enumerable.Range(1, 3)
            .Select(index => new TaskSpec(TaskId.New(), $"Implementation {index}", AgentRole.Developer)).ToArray());
        Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
        foreach (var task in Goal.Tasks)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            Kernel.RecordTaskVerification(Goal.Id, task.Id, new TaskVerificationRecord(
                "worker", Root, 0, $"preamble\n{WorkerResult}\ntrailer", "", Clock.UtcNow, WorkerResultPresent: true));
            Kernel.ReportTaskProgress(Goal.Id, task.Id, WorkTaskStatus.Completed, "done");
        }
        Assert.Equal(GoalStatus.Verified, Goal.Status);
        Assert.True(Kernel.BeginGoalAcceptanceVerification(Goal.Id, "gate started"));
        QuietSince = Goal.Timeline.Max(item => item.OccurredAt);
        Clock.Advance(TimeSpan.FromMinutes(quietMinutes));
        CaseG = new ConductorStewardCaseG(_ => false, _ => false, _ => false, _ => Sha);
        Intents = new SqliteOperatorIntentStore(Path.Combine(Root, "intents.db"), Path.Combine(Root, "logs"));
        Triggers = new ConductorStewardTriggerStore(Path.Combine(Root, "triggers.db"));
        Host = new ConductorStewardHost(Triggers, new ConductorStewardTriggerDetector(), Model, Intents,
            new AdjudicationEvidenceResolver(Root), version ?? (_ => 7), _ => Root,
            new GoalLifecycleEventWriter(Path.Combine(Root, "lifecycle")),
            new ConductEventLogWriter(ConductPath, utcNow: () => Clock.UtcNow),
            utcNow: () => Clock.UtcNow, caseG: CaseG);
    }

    internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-steward-g-{Guid.NewGuid():N}");
    internal string ConductPath => Path.Combine(Root, "conduct-events.log");
    internal StewardCaseGClock Clock { get; } = new();
    internal AgentOrchestratorKernel Kernel { get; }
    internal Goal Goal { get; }
    internal TaskSpec Target => Goal.Tasks[^1];
    internal DateTimeOffset QuietSince { get; }
    internal ConductorStewardCaseG CaseG { get; }
    internal SqliteOperatorIntentStore Intents { get; }
    internal ConductorStewardTriggerStore Triggers { get; }
    internal StewardFakeModel Model { get; } = new();
    internal ConductorStewardHost Host { get; }

    public void Dispose()
    {
        Host.Stop();
        Directory.Delete(Root, recursive: true);
    }

    internal sealed class StewardCaseGClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        internal void Advance(TimeSpan by) => UtcNow += by;
    }
}
