using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsStewardNoCommitRejection
{
    [Xunit.Fact]
    public void Unmatched_reason_with_zero_commits_and_confirmed_red_triggers_case_A()
    {
        using var harness = new StewardHarness("A");
        var goal = CreateGoal(harness, Marker(false, 0));

        var trigger = Xunit.Assert.Single(new ConductorStewardTriggerDetector().Detect(goal));
        Xunit.Assert.Equal(ConductorStewardTriggerKind.DeveloperNoChangeWithConfirmedRed, trigger.Kind);
        Xunit.Assert.Equal(StewardHarness.Sha, trigger.CandidateSha);
    }

    [Xunit.Fact]
    public void Recognized_no_change_marker_with_zero_commits_triggers_case_A()
    {
        using var harness = new StewardHarness("A");
        var goal = CreateGoal(harness, Marker(true, 0));

        var trigger = Xunit.Assert.Single(new ConductorStewardTriggerDetector().Detect(goal));
        Xunit.Assert.Equal(ConductorStewardTriggerKind.DeveloperNoChangeWithConfirmedRed, trigger.Kind);
    }

    [Xunit.Fact]
    public void Post_dispatch_commit_disqualifies_even_with_legacy_failure_reason()
    {
        using var harness = new StewardHarness("A");
        var goal = CreateGoal(harness, Marker(true, 1), failureReason: "no-change-evidence");

        Xunit.Assert.Empty(new ConductorStewardTriggerDetector().Detect(goal));
    }

    [Xunit.Fact]
    public void No_confirmed_red_on_candidate_disqualifies()
    {
        using var harness = new StewardHarness("A");
        var goal = CreateGoal(harness, Marker(false, 0), confirmedRed: false);

        Xunit.Assert.Empty(new ConductorStewardTriggerDetector().Detect(goal));
    }

    [Xunit.Fact]
    public void Non_developer_role_disqualifies()
    {
        using var harness = new StewardHarness("A");
        var goal = CreateGoal(harness, Marker(false, 0), role: AgentRole.Tester);

        Xunit.Assert.Empty(new ConductorStewardTriggerDetector().Detect(goal));
    }

    [Xunit.Fact]
    public void No_marker_or_legacy_failure_reason_disqualifies()
    {
        using var harness = new StewardHarness("A");
        var goal = CreateGoal(harness, "worker stderr");

        Xunit.Assert.Empty(new ConductorStewardTriggerDetector().Detect(goal));
    }

    [Xunit.Theory]
    [Xunit.InlineData("\n")]
    [Xunit.InlineData("\r\n")]
    public void Parser_and_detector_accept_LF_and_CRLF(string lineEnding)
    {
        using var harness = new StewardHarness("A");
        var stderr = "worker stderr" + lineEnding + Marker(false, 0) + lineEnding + "trailing stderr";
        var parsed = DispatchRejectionDiagnosticMarker.TryParse(stderr, out _, out var reason,
            out var commits, out _);
        Xunit.Assert.True(parsed);
        Xunit.Assert.Equal(DispatchRejectionDiagnosticMarker.VerificationPatternUnmatched, reason);
        Xunit.Assert.Equal(0, commits);
        var goal = CreateGoal(harness, stderr);

        var trigger = Xunit.Assert.Single(new ConductorStewardTriggerDetector().Detect(goal));
        Xunit.Assert.Equal(ConductorStewardTriggerKind.DeveloperNoChangeWithConfirmedRed, trigger.Kind);
    }

    [Xunit.Fact]
    public void Deferred_no_change_completion_followed_by_red_does_not_dispatch_steward()
    {
        using var harness = new StewardHarness("A");
        var (kernel, goal) = ConductorDriverTests.SoftwareGoal("Deferred no-change candidate");
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        foreach (var predecessor in goal.Tasks.TakeWhile(task => task.Id != developer.Id))
            ConductorDriverTests.PassVerification(kernel, goal, predecessor);
        kernel.RetryTask(goal.Id, developer.Id, "Retry the unchanged candidate.");
        ConductorDriverTests.DispatchTask(kernel, goal, developer, baseCommit: StewardHarness.Sha);
        var output = "NO_CHANGE: the candidate already contains the repair.\n" +
            "WORKER_RESULT:\nfiles: none\ncommands: none\n" +
            "tests: deferred - ExampleTests\ncommit: none\nblockers: none\n" +
            "assigned_scope_complete: true\nmodel_fit: test/model - adequate - fixture\n" +
            "skills: none\nconfidence: high\nEND_WORKER_RESULT";
        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 0, output,
                new DeferredNoChangeOutcome(StewardHarness.Sha, ["ExampleTests"],
                    "The candidate already contains the repair.").FormatMarker(),
                DateTimeOffset.UtcNow, WorkerResultPresent: true, HasCommittedChanges: false));
        Xunit.Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        ConductorDriverTests.DispatchTask(kernel, goal, tester, baseCommit: StewardHarness.Sha);
        kernel.RecordTaskVerification(goal.Id, tester.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "candidate RED", "",
                DateTimeOffset.UtcNow, FindingEvidenceReceipts: [RedReceipt()]));

        Xunit.Assert.Empty(new ConductorStewardTriggerDetector().Detect(goal));
        harness.Host.ServiceTick(kernel, goal.Id.Value);
        Xunit.Assert.Null(harness.Host.CurrentRound);
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    private static string Marker(bool verificationRecognized, int commits) =>
        DispatchRejectionDiagnosticMarker.Format(verificationRecognized, commits, "");

    private static Goal CreateGoal(StewardHarness harness, string stderr,
        AgentRole role = AgentRole.Developer, bool confirmedRed = true,
        string? failureReason = null)
    {
        var task = new TaskSpec(TaskId.New(), "No-change round", role);
        var goal = harness.Kernel.CreateGoal("No-change detection", [task]);
        harness.Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        harness.Kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "test.exe", harness.Root, DateTimeOffset.UtcNow,
                BaseCommit: StewardHarness.Sha));
        harness.Kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", harness.Root, 1,
                "WORKER_RESULT:\nfiles: none\ntests: deferred - ExampleTests\n" +
                "blockers: none\nassigned_scope_complete: true\nEND_WORKER_RESULT", stderr,
                DateTimeOffset.UtcNow, WorkerResultPresent: true,
                FindingEvidenceReceipts: confirmedRed ? [RedReceipt()] : null,
                OrchestratorFailureReason: failureReason));
        harness.Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "rejected");
        return goal;
    }

    private static FindingEvidenceReceipt RedReceipt() => new("red-receipt", StewardHarness.Sha,
        new FindingEvidenceRequest([]), true, false, "candidate RED",
        [new FindingEvidenceArmReceipt(FindingEvidenceArm.Candidate, StewardHarness.Sha,
            FindingEvidenceArmDisposition.Red, true, false, "Assert.Equal expected 1 actual 2",
            FailingTestIdentities: ["ExampleTests.Fails"]),
         new FindingEvidenceArmReceipt(FindingEvidenceArm.Baseline, StewardHarness.Sha,
            FindingEvidenceArmDisposition.Green, true, true, "baseline GREEN")]);
}
