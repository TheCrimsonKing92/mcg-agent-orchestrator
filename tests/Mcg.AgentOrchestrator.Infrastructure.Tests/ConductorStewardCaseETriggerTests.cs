using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique filesystem/SQLite stores, injected clock and candidate head.
public sealed class ConductorStewardCaseETriggerTests
{
    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void Reviewer_retry_without_commit_emits_E_and_excludes_D(bool rejection)
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed(rejection: rejection);

        Assert.True(ConductorStewardTriggerDetector.IsCaseETask(harness.Goal, harness.Developer, harness.Head));
        Assert.False(ConductorStewardTriggerDetector.IsCaseDTask(harness.Goal, harness.Developer, harness.Head));
        var trigger = Assert.Single(harness.Detector.Detect(harness.Goal));
        Assert.Equal(ConductorStewardTriggerKind.DeveloperReviewerFindingNoCommit, trigger.Kind);
        Assert.Equal("E", trigger.CaseLetter);
        Assert.Equal(harness.Developer.Id.Value, trigger.TaskId);
        Assert.Equal(StewardCaseEHarness.Sha, trigger.CandidateSha);
        Assert.Contains(harness.RetryMessage, trigger.Evidence, StringComparison.Ordinal);
        Assert.Equal(StewardCaseEHarness.WorkerResult, trigger.WorkerResult);
        Assert.Contains($"reviewer-task={harness.Reviewer.Id.Value[..8]}", trigger.EvidenceReferences);
        Assert.Equal(harness.Reviewer, ConductorStewardTriggerDetector.ResolveCaseEReviewer(harness.Goal, harness.Developer));
        Assert.Empty(harness.Detector.Detect(harness.Goal, (_, _, _) => false));
        Assert.Null(new ConductorStewardDeterministicRoute().TryBuild(trigger, harness.Root));
    }

    [Xunit.Theory]
    [Xunit.InlineData("committed-result")]
    [Xunit.InlineData("result-has-commit")]
    [Xunit.InlineData("rejection-reports-commit")]
    [Xunit.InlineData("moved-head")]
    [Xunit.InlineData("missing-head")]
    [Xunit.InlineData("acceptance-retry")]
    [Xunit.InlineData("tester-retry")]
    [Xunit.InlineData("unrelated-retry")]
    [Xunit.InlineData("no-blocker")]
    [Xunit.InlineData("retry-after-dispatch")]
    [Xunit.InlineData("earlier-developer")]
    [Xunit.InlineData("confirmed-red")]
    public void Ineligible_round_is_rejected_by_detection_and_close_admission(string invalidCase)
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed(rejection: invalidCase is "rejection-reports-commit" or "confirmed-red",
            resultCommit: invalidCase == "committed-result" ? StewardCaseEHarness.OtherSha : null,
            confirmedRed: invalidCase == "confirmed-red",
            retryAfterDispatch: invalidCase == "retry-after-dispatch",
            retryMessage: invalidCase switch
            {
                "acceptance-retry" => ConductorStewardTriggerDetector.AcceptanceRetryPrefix,
                "tester-retry" => harness.RetryMessage.Replace("Reviewer task", "Tester task"),
                "unrelated-retry" => "Unrelated repair",
                _ => null
            }, workerResult: invalidCase switch
            {
                "no-blocker" => StewardCaseEHarness.WorkerResult.Replace("exact-blocker - finding concerns result format only", "none"),
                "result-has-commit" or "rejection-reports-commit" => StewardCaseEHarness.WorkerResult.Replace("commit: none", $"commit: {StewardCaseEHarness.OtherSha}"),
                _ => null
            });
        if (invalidCase == "moved-head") harness.Head = StewardCaseEHarness.OtherSha;
        if (invalidCase == "missing-head") harness.Head = "";
        if (invalidCase == "earlier-developer")
            harness.Kernel.AddTask(harness.Goal.Id, AgentRole.Developer, "Later developer");

        Assert.False(ConductorStewardTriggerDetector.IsCaseETask(harness.Goal, harness.Developer, harness.Head));
        Assert.DoesNotContain(harness.Detector.Detect(harness.Goal), trigger =>
            trigger.Kind == ConductorStewardTriggerKind.DeveloperReviewerFindingNoCommit);
        Assert.False(ConductorStewardCaseEAdmission.Admits(harness.Goal, harness.Developer,
            harness.ClosePayload(), harness.Head));
        if (invalidCase == "confirmed-red")
            Assert.Equal(ConductorStewardTriggerKind.DeveloperNoChangeWithConfirmedRed,
                Assert.Single(harness.Detector.Detect(harness.Goal)).Kind);
    }

    [Xunit.Fact]
    public void Missing_head_source_does_not_emit_E()
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed();
        Assert.Empty(new ConductorStewardTriggerDetector().Detect(harness.Goal));
    }

    [Xunit.Fact]
    public void Inherited_retry_is_rejected_with_other_admission_facts_unchanged()
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed();
        Assert.True(ConductorStewardTriggerDetector.IsCaseETask(harness.Goal, harness.Developer, harness.Head));
        var snapshot = harness.Kernel.ExportGoalSnapshot(harness.Goal.Id);
        harness.Kernel.ReplaceGoalWithSnapshot(snapshot with
        {
            Tasks = snapshot.Tasks.Select(task => task.Id == harness.Developer.Id.Value
                ? task with { LatestRetryInherited = true } : task).ToArray()
        });
        var goal = Assert.Single(harness.Kernel.Goals);
        var developer = goal.FindTask(harness.Developer.Id);

        Assert.True(developer.LatestRetryInherited);
        Assert.False(ConductorStewardTriggerDetector.IsCaseETask(goal, developer, harness.Head));
        Assert.Empty(harness.Detector.Detect(goal));
        Assert.False(ConductorStewardCaseEAdmission.Admits(goal, developer, harness.ClosePayload(), harness.Head));
    }
}

internal sealed class StewardCaseEHarness : IDisposable
{
    internal const string Sha = StewardCaseDHarness.Sha;
    internal const string OtherSha = StewardCaseDHarness.OtherSha;
    internal const long Version = 7;
    internal static CandidateIdentity Candidate { get; } = new("steward-e-patch", "base", "manifest");
    internal const string WorkerResult = "WORKER_RESULT:\r\nfiles: none\r\ncommands: inspect\r\ntests: deferred - ResultFormatTests\r\ncommit: none\r\nblockers: exact-blocker - finding concerns result format only\r\nassigned_scope_complete: false\r\nEND_WORKER_RESULT";

    internal StewardCaseEHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), $"mcg-steward-e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Kernel = new AgentOrchestratorKernel(Clock);
        Developer = new TaskSpec(TaskId.New(), "Answer Reviewer finding", AgentRole.Developer);
        Reviewer = new TaskSpec(TaskId.New(), "Review candidate", AgentRole.Reviewer);
        Goal = Kernel.CreateGoal("Steward case E", [Developer, Reviewer]);
        Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
        Kernel.SetGoalRefinedSpec(Goal.Id, new RefinedSpec("Reviewer re-reviews explanation",
            ["Reviewer decides whether finding is resolved."], VerificationClass.TestVerifiable, [], []));
        Index = new AcceptanceFailingTestIndex(Path.Combine(Root, AcceptanceFailingTestIndex.FileName));
        Sources = new ConductorStewardCaseDSources(_ => Head, (_, _) => null, (_, _) => [], _ => [], Index);
        Detector = new ConductorStewardTriggerDetector(caseDSources: Sources);
        Intents = new SqliteOperatorIntentStore(Path.Combine(Root, "operator-intents.db"), Path.Combine(Root, "logs"));
        Decisions = CollaborationItemStore.ForDirectory(Root);
        Coordinator = CreateCoordinator();
        Triggers = new ConductorStewardTriggerStore(Path.Combine(Root, "steward-triggers.db"));
        Host = new ConductorStewardHost(Triggers, Detector, Model, Intents, new AdjudicationEvidenceResolver(Root),
            _ => Version, _ => Root, new GoalLifecycleEventWriter(Path.Combine(Root, "lifecycle")),
            new ConductEventLogWriter(Path.Combine(Root, "conduct-events.log")), utcNow: () => Clock.UtcNow, caseDSources: Sources);
    }

    internal string Root { get; }
    internal string Head { get; set; } = Sha;
    internal string RetryMessage => $"auto-review-retry round 1 convergence brief: Reviewer task {Reviewer.Id.Value[..8]} verdict=needs-work; retry upstream Developer task.\nFinding: WORKER_RESULT format is incomplete.";
    internal StewardCaseDHarness.CaseDClock Clock { get; } = new();
    internal AgentOrchestratorKernel Kernel { get; }
    internal TaskSpec Developer { get; }
    internal TaskSpec Reviewer { get; }
    internal Goal Goal { get; }
    internal AcceptanceFailingTestIndex Index { get; }
    internal ConductorStewardCaseDSources Sources { get; }
    internal ConductorStewardTriggerDetector Detector { get; }
    internal SqliteOperatorIntentStore Intents { get; }
    internal CollaborationItemStore Decisions { get; }
    internal OperatorIntentCoordinator Coordinator { get; }
    internal ConductorStewardTriggerStore Triggers { get; }
    internal StewardFakeModel Model { get; } = new();
    internal ConductorStewardHost Host { get; }

    internal OperatorIntentCoordinator CreateCoordinator(Func<GoalId, string?>? headResolver = null) =>
        new(Intents, utcNow: () => Clock.UtcNow, goalHeadResolver: headResolver ?? (_ => Head),
            decisions: Decisions, goalStateVersionResolver: _ => Version,
            evidenceResolver: new AdjudicationEvidenceResolver(Root), apparatusRegateIndex: Index);

    internal void Seed(bool rejection = true, string? retryMessage = null, string? resultCommit = null,
        string? workerResult = null, bool confirmedRed = false, bool retryAfterDispatch = false)
    {
        Kernel.ReportTaskProgress(Goal.Id, Developer.Id, WorkTaskStatus.Completed, "candidate ready");
        Kernel.RecordTaskVerification(Goal.Id, Developer.Id,
            new TaskVerificationRecord("focused", Root, 0, "passed", "", Clock.UtcNow));
        Kernel.ReportTaskProgress(Goal.Id, Reviewer.Id, WorkTaskStatus.Completed, "verdict=needs-work");
        Kernel.RecordTaskVerification(Goal.Id, Reviewer.Id,
            new TaskVerificationRecord("review", Root, 1,
                "WORKER_RESULT:\nverdict: needs-work\nblockers: WORKER_RESULT format is incomplete\nEND_WORKER_RESULT",
                "", Clock.UtcNow, WorkerResultPresent: true, CandidateIdentity: Candidate));
        Clock.Advance();
        Kernel.RetryTaskAutomatically(Goal.Id, Developer.Id, retryMessage ?? RetryMessage, RetryCause.NewSourceFinding);
        Clock.Advance();
        Kernel.RecordTaskDispatch(Goal.Id, Developer.Id, new TaskDispatchRecord("worker", "test.exe", Root,
            retryAfterDispatch ? Clock.UtcNow.AddSeconds(-2) : Clock.UtcNow, BaseCommit: Sha, ResultCommit: resultCommit));
        Clock.Advance();
        Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new TaskVerificationRecord("test.exe", Root, 1,
            "preamble\r\n" + (workerResult ?? WorkerResult) + "\r\ntrailing output",
            rejection ? "DISPATCH_REJECTED reason=no-change-evidence" : "", Clock.UtcNow,
            WorkerResultPresent: true, HasCommittedChanges: resultCommit is not null,
            FindingEvidenceReceipts: confirmedRed ? [StewardCaseDHarness.RedReceipt()] : null,
            OrchestratorFailureReason: rejection ? "no-change-evidence" : null));
        Kernel.ReportTaskProgress(Goal.Id, Developer.Id, WorkTaskStatus.Failed, "no commit");
        Assert.Equal(WorkTaskStatus.Failed, Developer.Status);
        Assert.False(Goal.IsTerminal);
    }

    internal AdjudicateOperatorIntentPayload ClosePayload() => new("close",
        ConductorStewardCaseDAdmission.ComposeText("Explanation answers the finding.", WorkerResult),
        ["steward-case=E", $"dispatch-base={Sha}"], Version, Root,
        Precedent: $"steward-case=E trigger={Goal.Id.Value}:{Developer.Id.Value}:{Sha}:DeveloperReviewerFindingNoCommit",
        Precondition: AdjudicationPrecondition.Capture(Goal, Developer));

    public void Dispose()
    {
        Host.Stop();
        Directory.Delete(Root, recursive: true);
    }
}
