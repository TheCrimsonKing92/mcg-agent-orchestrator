using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: per-test kernel, clock and private temporary stores; no processes or global state.
public sealed class ConductorStewardCaseFTriggerTests
{
    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Reviewer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void Answered_blocker_emits_one_F_for_last_developer(AgentRole role)
    {
        using var harness = new StewardCaseFHarness(role);
        var trigger = Assert.Single(harness.Detector.Detect(harness.Goal));
        Assert.Equal(ConductorStewardTriggerKind.ReviewerOrTesterBlockerWithAnswer, trigger.Kind);
        Assert.Equal("F", trigger.CaseLetter);
        Assert.Equal(harness.Developer.Id.Value, trigger.TaskId);
        Assert.Equal(StewardCaseFHarness.Sha, trigger.CandidateSha);
        Assert.Equal(harness.Request.AuthoritativeAnswer!.AnsweredAt, trigger.OccurredAt);
        Assert.Contains($"clarification-request={harness.Request.Id.Value}", trigger.EvidenceReferences);
        Assert.Contains($"clarification-answer={StewardCaseFHarness.Answer}", trigger.EvidenceReferences);
        Assert.Contains($"blocker-task={harness.BlockerTask.Id.Value}", trigger.EvidenceReferences);
        Assert.Contains(StewardCaseFHarness.Blocker, trigger.Evidence);
        Assert.Contains(StewardCaseFHarness.Answer, trigger.Evidence);
        var repeated = Assert.Single(harness.Detector.Detect(harness.Goal));
        Assert.Equal(trigger.Identity, repeated.Identity);
        Assert.Equal(trigger.OccurredAt, repeated.OccurredAt);
    }

    [Xunit.Theory]
    [Xunit.InlineData("pending")]
    [Xunit.InlineData("dismissed")]
    [Xunit.InlineData("superseded")]
    [Xunit.InlineData("unanswered")]
    [Xunit.InlineData("older-answer")]
    [Xunit.InlineData("same-time-answer")]
    [Xunit.InlineData("different-task")]
    [Xunit.InlineData("different-goal")]
    [Xunit.InlineData("different-candidate")]
    [Xunit.InlineData("terminal")]
    [Xunit.InlineData("owner-question")]
    [Xunit.InlineData("parked")]
    [Xunit.InlineData("active-developer")]
    [Xunit.InlineData("active-blocker")]
    [Xunit.InlineData("no-blocker")]
    [Xunit.InlineData("satisfied-gate")]
    public void Ineligible_answer_or_blocker_emits_no_F(string invalidCase)
    {
        using var harness = new StewardCaseFHarness(AgentRole.Reviewer);
        Assert.Single(harness.Detector.Detect(harness.Goal));
        var snapshot = harness.Kernel.ExportSnapshot();
        var request = Assert.Single(snapshot.HumanInputRequests);
        var answer = Assert.Single(request.AnswerHistory!);
        var blockerTime = harness.BlockerTask.LastVerification!.CompletedAt;
        request = invalidCase switch
        {
            "pending" => request with { IsCompleted = false, Answer = null, AnsweredAt = null, AnswerHistory = null },
            "dismissed" => request with { WasDismissed = true, Answer = null, AnswerHistory = null },
            "superseded" => request with { SupersededByRequestId = HumanInputRequestId.New().Value },
            "unanswered" => request with { AnswerHistory = [answer with { SupersededByAnswerId = "retracted" }] },
            "older-answer" => request with { AnswerHistory = [answer with { AnsweredAt = blockerTime.AddSeconds(-1) }] },
            "same-time-answer" => request with { AnswerHistory = [answer with { AnsweredAt = blockerTime }] },
            "different-task" => request with { TaskId = harness.EarlierDeveloper.Id.Value },
            "different-goal" => request with { GoalId = GoalId.New().Value },
            "owner-question" => request with { Kind = HumanWaitKind.OperatorApproval },
            "parked" => request with { AnswerHistory = [answer with { Text = "Goal parked: later" }] },
            _ => request
        };
        var goal = Assert.Single(snapshot.Goals);
        if (invalidCase == "terminal") goal = goal with { Status = GoalStatus.Cancelled };
        goal = goal with
        {
            Tasks = goal.Tasks.Select(task =>
                invalidCase == "active-developer" && task.Id == harness.Developer.Id.Value
                    ? task with { Status = WorkTaskStatus.Running }
                : task.Id != harness.BlockerTask.Id.Value ? task
                : invalidCase switch
                {
                    "different-candidate" => task with { LastDispatch = task.LastDispatch! with { BaseCommit = new string('b', 40) } },
                    "active-blocker" => task with { Status = WorkTaskStatus.Running },
                    "no-blocker" => task with { VerificationHistory = null, LastVerification = task.LastVerification! with
                    { StandardOutput = "WORKER_RESULT:\nblockers: none\nEND_WORKER_RESULT", AuthoritativeStandardOutput = "WORKER_RESULT:\nblockers: none\nEND_WORKER_RESULT" } },
                    "satisfied-gate" => task with { VerificationHistory = null, LastVerification = task.LastVerification! with
                    { ExitCode = 0, StandardOutput = "WORKER_RESULT:\nblockers: none\nEND_WORKER_RESULT", AuthoritativeStandardOutput = "WORKER_RESULT:\nblockers: none\nEND_WORKER_RESULT" } },
                    _ => task
                }).ToArray()
        };
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [goal], HumanInputRequests = [request] });
        var detector = new ConductorStewardAnsweredBlockerDetector(_ => kernel.HumanInputRequests.ToArray());
        Assert.Empty(detector.Detect(Assert.Single(kernel.Goals)));
    }

    [Xunit.Fact]
    public void Inspection_filter_and_default_detector_do_not_emit_F()
    {
        using var harness = new StewardCaseFHarness(AgentRole.Reviewer);
        Assert.Single(harness.Detector.Detect(harness.Goal));
        Assert.Empty(harness.Detector.Detect(harness.Goal, (_, _, _) => false));
        Assert.Empty(new ConductorStewardTriggerDetector().Detect(harness.Goal));
    }
}

internal sealed class StewardCaseFHarness : IDisposable
{
    internal const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Blocker = "exact-blocker - specification does not define empty output";
    internal const string Answer = "Empty output means: return status=empty.\nPreserve the existing success response.";

    internal StewardCaseFHarness(AgentRole role)
    {
        Root = Path.Combine(Path.GetTempPath(), $"mcg-steward-f-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Kernel = new AgentOrchestratorKernel(Clock);
        EarlierDeveloper = new TaskSpec(TaskId.New(), "Earlier implementation", AgentRole.Developer);
        Developer = new TaskSpec(TaskId.New(), "Current implementation", AgentRole.Developer);
        BlockerTask = new TaskSpec(TaskId.New(), "Check specification", role);
        Goal = Kernel.CreateGoal("Answered blocker", [EarlierDeveloper, Developer, BlockerTask]);
        Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
        Kernel.SetGoalRefinedSpec(Goal.Id, new RefinedSpec("Empty output has defined behavior",
            ["Empty output returns status=empty."], VerificationClass.TestVerifiable, [], []));
        CompleteDeveloper(EarlierDeveloper);
        CompleteDeveloper(Developer);
        RecordBlocker();
        Request = Kernel.RequestHumanInput(Goal.Id, BlockerTask.Id, "What should empty output mean?", isDismissible: true);
        Clock.Advance();
        Kernel.SubmitHumanInput(Request.Id, Answer);
        Kernel.ReportTaskProgress(Goal.Id, BlockerTask.Id, WorkTaskStatus.Completed, "Blocker requires Developer correction");
        Detector = new ConductorStewardTriggerDetector(answeredBlockers:
            new ConductorStewardAnsweredBlockerDetector(_ => Kernel.HumanInputRequests.ToArray()));
        Intents = new SqliteOperatorIntentStore(Path.Combine(Root, "operator-intents.db"), Path.Combine(Root, "logs"));
        Triggers = new ConductorStewardTriggerStore(Path.Combine(Root, "steward-triggers.db"));
        Host = new ConductorStewardHost(Triggers, Detector, Model, Intents,
            new AdjudicationEvidenceResolver(Root), _ => 7, _ => Root,
            new GoalLifecycleEventWriter(Path.Combine(Root, "lifecycle")),
            new ConductEventLogWriter(Path.Combine(Root, "conduct-events.log")), utcNow: () => Clock.UtcNow);
    }

    internal string Root { get; }
    internal StewardCaseDHarness.CaseDClock Clock { get; } = new();
    internal AgentOrchestratorKernel Kernel { get; }
    internal Goal Goal { get; }
    internal TaskSpec EarlierDeveloper { get; }
    internal TaskSpec Developer { get; }
    internal TaskSpec BlockerTask { get; }
    internal HumanInputRequest Request { get; }
    internal ConductorStewardTriggerDetector Detector { get; }
    internal SqliteOperatorIntentStore Intents { get; }
    internal ConductorStewardTriggerStore Triggers { get; }
    internal StewardFakeModel Model { get; } = new();
    internal ConductorStewardHost Host { get; }

    internal void CompleteDeveloper(TaskSpec task)
    {
        Clock.Advance();
        Kernel.RecordTaskDispatch(Goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "test.exe", Root, Clock.UtcNow, BaseCommit: Sha, ResultCommit: Sha));
        Kernel.RecordTaskVerification(Goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", Root, 0, "passed", "", Clock.UtcNow));
        Kernel.ReportTaskProgress(Goal.Id, task.Id, WorkTaskStatus.Completed, "Candidate ready");
    }

    internal void RecordBlocker()
    {
        Clock.Advance();
        Kernel.RecordTaskDispatch(Goal.Id, BlockerTask.Id,
            new TaskDispatchRecord("test-worker", "test.exe", Root, Clock.UtcNow, BaseCommit: Sha));
        Kernel.RecordTaskVerification(Goal.Id, BlockerTask.Id,
            new TaskVerificationRecord("test.exe", Root, 1,
                $"WORKER_RESULT:\nfiles: none\ntests: deferred - EmptyOutputTests\ncommit: none\nblockers: {Blocker}\nEND_WORKER_RESULT",
                "", Clock.UtcNow, WorkerResultPresent: true));
        Kernel.ReportTaskProgress(Goal.Id, BlockerTask.Id, WorkTaskStatus.Completed, "Blocking specification finding");
    }

    public void Dispose()
    {
        Host.Stop();
        Directory.Delete(Root, recursive: true);
    }
}
