using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scenario owns its kernel and any git fixture owns a unique temporary root.
public sealed class RoleReportEvidenceClosureTests
{
    internal const string ReportCriterion = "The Developer reports `tests: deferred - ` followed, directly after the hyphen and comma-separated, by every test class it touched or added. The Tester's evidence_request runs them. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
    private static readonly DateTimeOffset DispatchTime = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeferredReport_RevisedBrief_ClosesWithValidatedClasses()
    {
        var scenario = CreateScenario(reviseBrief: true);
        var request = CreateWait(scenario, "developer");
        var queried = new List<string>();

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "landing-C", name =>
        {
            queried.Add(name);
            return "attempt G1";
        });

        Assert.Empty(skipped);
        Assert.True(request.IsCompleted);
        Assert.Equal(2, request.PlannerIdentity!.BriefVersion);
        Assert.Equal(2, request.AuthoritativeAnswer!.BriefVersion);
        Assert.Equal(HumanInputAnswerOrigin.Worker, request.AuthoritativeAnswer.Origin);
        Assert.Equal(new[] { "AlphaTests", "BetaTests" }, queried);
        Assert.Contains("criterion=1 candidate=B landing_candidate=landing-C", request.Answer);
        Assert.Contains("tests=deferred - AlphaTests, BetaTests", request.Answer);
        Assert.Contains("gate=AlphaTests@attempt G1,BetaTests@attempt G1", request.Answer);
        Assert.Equal(request.Answer,
            Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputReceived)).Message);
        Assert.Empty(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id));
    }

    [Theory]
    [InlineData("newer-wait", "brief-version-differs")]
    [InlineData("no-classes", "developer-report-missing")]
    [InlineData("not-report", "not-a-report-criterion")]
    public void DeferredReport_RevisedBriefMissingPrerequisite_KeepsWait(string failure, string reason)
    {
        var scenario = CreateScenario(
            criterion: failure == "not-report" ? "The Developer implements the change." : ReportCriterion,
            tests: failure == "no-classes" ? "deferred - " : "deferred - AlphaTests, BetaTests",
            reviseBrief: true);
        var request = CreateWait(scenario, "developer", version: failure == "newer-wait" ? 3 : 2);

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "landing-C",
            _ => throw new Exception("Rejected report waits must not query gate evidence"));

        var entry = Assert.Single(skipped);
        Assert.Equal(request.Id, entry.Key);
        Assert.Equal(reason, entry.Value);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.Empty(request.AnswerHistory);
        // Future-version waits remain stored but are excluded from GetPendingHumanInput.
        Assert.Same(request, Assert.Single(scenario.Kernel.HumanInputRequests));
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.HumanInputReceived);
    }

    [Theory]
    [InlineData("worker")]
    [InlineData(" WORKER ")]
    public void WorkerOwnedReportWait_PassedClasses_ClosesAsDeveloperReport(string owner)
    {
        var scenario = CreateScenario();
        var request = CreateWait(scenario, owner);

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "landing-C", _ => "attempt G1");

        Assert.Empty(skipped);
        Assert.True(request.IsCompleted);
        Assert.Equal(HumanInputAnswerOrigin.Worker, request.AuthoritativeAnswer!.Origin);
        Assert.StartsWith($"ROLE_REPORT_EVIDENCE owner=developer task={scenario.Developer.Id.Value}", request.Answer);
        Assert.EndsWith(" requested_owner=worker", request.Answer);
        Assert.Contains("gate=AlphaTests@attempt G1,BetaTests@attempt G1", request.Answer);
        Assert.Empty(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id));
    }

    [Theory]
    [InlineData("missing-report", "developer-report-missing")]
    [InlineData("empty-report", "developer-report-missing")]
    [InlineData("missing-class", "class-not-in-passed-gate:BetaTests")]
    public void WorkerOwnedReportWait_MissingEvidence_KeepsWaitWithReason(string failure, string reason)
    {
        var scenario = CreateScenario(tests: failure == "missing-report" ? null :
            failure == "empty-report" ? "deferred - " : "deferred - AlphaTests, BetaTests");
        var request = CreateWait(scenario, "worker");

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "landing-C",
            name => failure == "missing-class" && name == "BetaTests" ? null : "attempt G1");

        var entry = Assert.Single(skipped);
        Assert.Equal(request.Id, entry.Key);
        Assert.Equal(reason, entry.Value);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.HumanInputReceived);
    }

    [Fact]
    public void WorkerOwnedNonReportCriterion_LeavesWaitForReviewerClosure()
    {
        var scenario = CreateScenario("The Developer implements the change.");
        var request = CreateWait(scenario, "worker");

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "landing-C",
            _ => throw new Exception("Non-report waits must not query gate evidence"));

        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.Empty(skipped);
        Assert.DoesNotContain(scenario.Goal.Timeline,
            item => item.Message.StartsWith("ROLE_REPORT_EVIDENCE_SKIPPED", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("developer")]
    [InlineData(" DEVELOPER ")]
    public void DeferredReport_PassedClasses_CloseWithWorkerReceipt(string owner)
    {
        var scenario = CreateScenario();
        var request = CreateWait(scenario, owner);
        var queried = new List<string>();

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "landing-C", name =>
        {
            queried.Add(name);
            return "attempt G1";
        });

        Assert.Empty(skipped);
        Assert.Equal(new[] { "AlphaTests", "BetaTests" }, queried);
        Assert.True(request.IsCompleted);
        Assert.Equal(HumanInputAnswerOrigin.Worker, request.AuthoritativeAnswer!.Origin);
        Assert.Equal(scenario.Goal.AuthoritativeBrief.Version, request.AuthoritativeAnswer.BriefVersion);
        Assert.StartsWith($"ROLE_REPORT_EVIDENCE owner=developer task={scenario.Developer.Id.Value}", request.Answer);
        Assert.Contains("criterion=1 candidate=B landing_candidate=landing-C", request.Answer);
        Assert.Contains("tests=deferred - AlphaTests, BetaTests", request.Answer);
        Assert.Contains("gate=AlphaTests@attempt G1,BetaTests@attempt G1", request.Answer);
        var received = Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputReceived));
        Assert.Equal(request.Answer, received.Message);
        Assert.Equal(request.TaskId, received.TaskId);
        Assert.Empty(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id));
        Assert.Empty(scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "landing-C", _ => throw new Exception("Already closed")));
        Assert.Single(request.AnswerHistory);
    }

    [Theory]
    [InlineData("failed", "owner-round-incomplete")]
    [InlineData("missing-class", "class-not-in-passed-gate:BetaTests")]
    [InlineData("missing-report", "developer-report-missing")]
    [InlineData("empty-report", "developer-report-missing")]
    [InlineData("different-set", "developer-report-differs")]
    [InlineData("not-report", "not-a-report-criterion")]
    public void DeferredReport_MissingEvidence_KeepsWaitWithReason(string failure, string reason)
    {
        var criterion = failure switch
        {
            "different-set" => "The Developer reports `tests: deferred - AlphaTests, GammaTests`.",
            "not-report" => "The Developer implements the change.",
            _ => ReportCriterion
        };
        var scenario = CreateScenario(criterion, failure == "missing-report" ? null :
            failure == "empty-report" ? "deferred - " : "deferred - AlphaTests, BetaTests");
        if (failure == "failed")
            scenario.Kernel.ReportWorkerTaskProgress(scenario.Goal.Id, scenario.Developer.Id,
                WorkTaskStatus.Failed, "Latest round failed.");
        var request = CreateWait(scenario, "developer");

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "C",
            name => failure == "missing-class" && name == "BetaTests" ? null : "attempt G1");

        Assert.Equal(reason, Assert.Single(skipped).Value);
        Assert.Equal(request.Id, Assert.Single(skipped).Key);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.Empty(request.AnswerHistory);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.HumanInputReceived);
        Assert.Same(request, Assert.Single(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id)));
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void RoleRound_CurrentCandidate_ClosesOwnWait(AgentRole role)
    {
        var scenario = CreateScenario();
        var task = RecordRole(scenario, role, "B");
        var owner = role.ToString().ToLowerInvariant();
        var request = CreateWait(scenario, owner);

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "C", _ => throw new Exception("Developer only"));

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Empty(skipped);
        Assert.True(request.IsCompleted);
        Assert.StartsWith($"ROLE_REPORT_EVIDENCE owner={owner} task={task.Id.Value}", request.Answer);
        Assert.Contains("candidate=B landing_candidate=C", request.Answer);
        if (role == AgentRole.Reviewer) Assert.Contains("verdict=pass", request.Answer);
        Assert.Equal(request.Answer, Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputReceived)).Message);
        Assert.Empty(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id));
    }

    [Theory]
    [InlineData(AgentRole.Reviewer, false)]
    [InlineData(AgentRole.Tester, true)]
    public void RoleRound_ConductorMergeAndRetighten_ClosesWait(AgentRole role, bool hermetic)
    {
        using var repo = new ConductorOnlyCandidateRangeTests.Repository();
        repo.Merge(hermetic ? "MCG Acceptance Gate" : "mcg-orchestrator",
            hermetic ? "acceptance-gate@localhost" : "mcg-orchestrator@localhost");
        repo.Ratchet(19, hermetic);
        var scenario = CreateScenario();
        RecordRole(scenario, role, repo.Reviewed);
        var request = CreateWait(scenario, role.ToString());
        var queried = new List<(string Reviewed, string Landing)>();

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, repo.Head,
            _ => throw new Exception("Developer only"), conductorOnlyRange: (reviewed, landing) =>
            {
                queried.Add((reviewed, landing));
                return ConductorOnlyCandidateRange.IsConductorOnly(repo.Root, "main", reviewed, landing);
            });

        Assert.Equal(new[] { (repo.Reviewed, repo.Head) }, queried);
        Assert.Empty(skipped);
        Assert.True(request.IsCompleted);
        Assert.Contains($"candidate={repo.Reviewed} landing_candidate={repo.Head}", request.Answer);
        Assert.Empty(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id));
    }

    [Theory]
    [InlineData(AgentRole.Reviewer, "worker")]
    [InlineData(AgentRole.Tester, "worker")]
    [InlineData(AgentRole.Reviewer, "goal-work")]
    [InlineData(AgentRole.Tester, "goal-work")]
    [InlineData(AgentRole.Reviewer, "ratchet-increase")]
    [InlineData(AgentRole.Tester, "ratchet-increase")]
    public void RoleRound_UnreviewedRange_KeepsCandidateDiffers(AgentRole role, string change)
    {
        using var repo = new ConductorOnlyCandidateRangeTests.Repository();
        repo.Merge(change == "worker" ? "worker" : "mcg-orchestrator",
            change == "worker" ? "worker@example.invalid" : "mcg-orchestrator@localhost");
        if (change == "goal-work")
        {
            repo.Write("goal.txt", "unreviewed Developer output\n");
            ConductorOnlyCandidateRangeTests.Commit(repo.Root);
        }
        if (change == "ratchet-increase") repo.Ratchet(21);
        var scenario = CreateScenario();
        RecordRole(scenario, role, repo.Reviewed);
        var request = CreateWait(scenario, role.ToString());

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, repo.Head, _ => null,
            conductorOnlyRange: (reviewed, landing) =>
                ConductorOnlyCandidateRange.IsConductorOnly(repo.Root, "main", reviewed, landing));

        Assert.Equal("owner-candidate-differs", skipped[request.Id]);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
    }

    [Theory]
    [InlineData("same-candidate")]
    [InlineData("incomplete")]
    [InlineData("missing-reviewed")]
    [InlineData("missing-landing")]
    public void RoleRound_PreservesOtherChecks_WithoutQueryingRange(string condition)
    {
        var scenario = CreateScenario();
        RecordRole(scenario, AgentRole.Reviewer,
            condition == "same-candidate" ? "B" : condition == "missing-reviewed" ? "" : "A",
            verdict: condition == "incomplete" ? "needs-work" : "pass");
        var request = CreateWait(scenario, "reviewer");

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id,
            condition == "missing-landing" ? "" : "C", _ => null,
            conductorOnlyRange: (_, _) => throw new Exception("Range must not bypass other checks"));

        if (condition == "same-candidate")
        {
            Assert.Empty(skipped);
            Assert.True(request.IsCompleted);
        }
        else
        {
            Assert.Equal(condition == "incomplete" ? "owner-round-incomplete" : "owner-candidate-differs", skipped[request.Id]);
            Assert.False(request.IsCompleted);
        }
    }

    [Theory]
    [InlineData(AgentRole.Reviewer, "A", "pass", WorkTaskStatus.Completed, "owner-candidate-differs")]
    [InlineData(AgentRole.Tester, "A", "pass", WorkTaskStatus.Completed, "owner-candidate-differs")]
    [InlineData(AgentRole.Reviewer, "B", "needs-work", WorkTaskStatus.Completed, "owner-round-incomplete")]
    [InlineData(AgentRole.Tester, "B", "needs-work", WorkTaskStatus.Completed, "owner-round-incomplete")]
    [InlineData(AgentRole.Reviewer, "B", "pass", WorkTaskStatus.Failed, "owner-round-incomplete")]
    [InlineData(AgentRole.Tester, "B", "pass", WorkTaskStatus.Failed, "owner-round-incomplete")]
    public void RoleRound_NotQualified_KeepsWait(AgentRole role, string candidate, string verdict,
        WorkTaskStatus status, string reason)
    {
        var scenario = CreateScenario();
        RecordRole(scenario, role, candidate, verdict, status);
        var request = CreateWait(scenario, role.ToString());

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "C", _ => "attempt G1");

        Assert.Equal(reason, skipped[request.Id]);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.HumanInputReceived);
    }

    [Fact]
    public void LatestDeveloper_FailedRound_DoesNotReuseEarlierCompletedReport()
    {
        var scenario = CreateScenario();
        var later = scenario.Kernel.AddTask(scenario.Goal.Id, AgentRole.Developer, "Revise candidate",
            AgentCatalog.Default().Agents);
        scenario.Kernel.RecordTaskDispatch(scenario.Goal.Id, later.Id, new TaskDispatchRecord(
            "codex-cli", "revise", "C:\\fixture", DispatchTime.AddMinutes(1), BaseCommit: "B"));
        scenario.Kernel.ReportWorkerTaskProgress(scenario.Goal.Id, later.Id, WorkTaskStatus.Failed, "Revision failed.");
        var request = CreateWait(scenario, "developer");

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "C", _ => "attempt G1");

        Assert.Equal(WorkTaskStatus.Completed, scenario.Developer.Status);
        Assert.Equal("owner-round-incomplete", skipped[request.Id]);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
    }

    [Fact]
    public void RequestScope_ExcludedAndStale_LeavesAnswersUntouched()
    {
        var scenario = CreateScenario();
        var excluded = new[] { CreateWait(scenario, "operator"), CreateWait(scenario, "acceptance-gate"),
            CreateWait(scenario, "developer", "post-landing") };
        var stale = CreateWait(scenario, "developer", version: scenario.Goal.AuthoritativeBrief.Version + 1);

        var skipped = scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "C", _ => "attempt G1");

        Assert.Equal("brief-version-differs", Assert.Single(skipped).Value);
        Assert.Equal(stale.Id, Assert.Single(skipped).Key);
        Assert.All(excluded.Append(stale), request =>
        {
            Assert.False(request.IsCompleted);
            Assert.Null(request.Answer);
        });
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.HumanInputReceived);
        Assert.Single(scenario.Goal.Timeline.Where(item => item.Message.StartsWith("ROLE_REPORT_EVIDENCE_SKIPPED", StringComparison.Ordinal)));
    }

    [Fact]
    public void SkipNotes_InterleavedAndChangedReasons_DeduplicatePerRequest()
    {
        var scenario = CreateScenario();
        var first = CreateWait(scenario, "developer");
        var second = CreateWait(scenario, "tester");
        scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "C", _ => null);
        scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "C", _ => null);
        scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "C", name => name == "AlphaTests" ? "attempt G1" : null);
        scenario.Kernel.CloseRoleReportEvidence(scenario.Goal.Id, "C", _ => null);

        var notes = scenario.Goal.Timeline.Where(item => item.Message.StartsWith("ROLE_REPORT_EVIDENCE_SKIPPED", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, notes.Count(item => item.Message.Contains(first.Id.Value[..8], StringComparison.Ordinal)));
        Assert.Single(notes.Where(item => item.Message.Contains(second.Id.Value[..8], StringComparison.Ordinal)));
    }

    private static Scenario CreateScenario(string criterion = ReportCriterion,
        string? tests = "deferred - AlphaTests, BetaTests", bool reviseBrief = false)
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan evidence", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
        var reviewer = new TaskSpec(TaskId.New(), "Inspect diff", AgentRole.Reviewer);
        var tester = new TaskSpec(TaskId.New(), "Verify candidate", AgentRole.Tester);
        var goal = kernel.CreateGoal("Close role reports at landing", [planner, developer, reviewer, tester]);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Bind role evidence", [criterion], VerificationClass.TestVerifiable, [], []));
        if (reviseBrief)
        {
            kernel.ReviseGoalBrief(goal.Id, $"Revised report wording\n\n## Acceptance criteria\n1. {criterion}",
                "Revise wording without changing criteria.");
            Assert.Equal(2, goal.AuthoritativeBrief.Version);
            Assert.Equal(1, Assert.Single(goal.RefinedSpecVersions).BriefVersion);
            Assert.Equal(1, goal.AuthoritativeRefinedSpecVersion!.BriefVersion);
            Assert.Equal(criterion, Assert.Single(goal.AuthoritativeRefinedSpecVersion.Spec.AcceptanceCriteria));
        }
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "codex-cli", "implement", "C:\\fixture", DispatchTime, BaseCommit: "A"));
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, "B");
        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
            "implement", "C:\\fixture", 0, DeveloperOutput(tests), string.Empty, DispatchTime.AddSeconds(1),
            WorkerResultPresent: true, HasCommittedChanges: true, DispatchStartedAt: DispatchTime));
        // Missing report fields are deliberately adjudicated Completed to isolate report validation.
        if (tests is null && developer.Status != WorkTaskStatus.Completed)
            kernel.ReportWorkerTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Completed, "Adjudicated implementation.");
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(VerificationEvidenceSource.Dispatch, developer.LastVerification!.EvidenceSource);
        return new(kernel, goal, planner, developer, reviewer, tester);
    }

    internal static string DeveloperOutput(string? tests) => string.Join(Environment.NewLine,
        new[] { "WORKER_RESULT:", "files: src/Candidate.cs", "commands: implement", tests is null ? null : $"tests: {tests}",
            "commit: B", "blockers: none", "assigned_scope_complete: true",
            "model_fit: fixture/model - adequate - deterministic implementation", "skills: none", "confidence: high", "END_WORKER_RESULT" }
            .OfType<string>());

    private static HumanInputRequest CreateWait(Scenario scenario, string owner,
        string availability = "post-implementation", int? version = null)
    {
        var key = $"report-{owner.Trim()}-{availability}";
        return scenario.Kernel.RequestHumanInputDeduplicated(scenario.Goal.Id, scenario.Planner.Id, $"Provide {key}",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: owner,
            plannerIdentity: new PlannerEvidenceIdentity(scenario.Goal.Id.Value, scenario.Planner.Id.Value,
                version ?? scenario.Goal.AuthoritativeBrief.Version, 1, key),
            plannerEvidence: new PlannerEvidencePayload(1, key, availability, owner,
                "Role report", "Role owns; Acceptance executes", null, null, null)).Request;
    }

    private static TaskSpec RecordRole(Scenario scenario, AgentRole role, string candidate,
        string verdict = "pass", WorkTaskStatus status = WorkTaskStatus.Completed)
    {
        var task = role == AgentRole.Reviewer ? scenario.Reviewer : scenario.Tester;
        var started = DispatchTime.AddSeconds(2);
        scenario.Kernel.RecordTaskDispatch(scenario.Goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "verify", "C:\\fixture", started, BaseCommit: candidate));
        scenario.Kernel.RecordTaskVerification(scenario.Goal.Id, task.Id, new TaskVerificationRecord(
            "verify", "C:\\fixture", 0, RoleOutput(verdict), string.Empty, started.AddSeconds(1), WorkerResultPresent: true));
        scenario.Kernel.ReportWorkerTaskProgress(scenario.Goal.Id, task.Id, status, "Role round adjudicated.");
        Assert.Equal(candidate, task.LastVerification!.ReviewedCommit);
        return task;
    }

    internal static string RoleOutput(string verdict) =>
        string.Join(Environment.NewLine, "WORKER_RESULT:", "files: none", "commands: inspect",
            "tests: deferred - GateProofTests", "commit: none", "blockers: none", "findings: []", "touched_anchors: []",
            "criteria_verdicts: [{\"criterion_index\":0,\"verdict\":\"not-verifiable\",\"evidence\":\"Acceptance executes\"}]",
            $"verdict: {verdict}", "model_fit: fixture/model - adequate - inspection", "skills: none", "confidence: high", "END_WORKER_RESULT");

    private sealed record Scenario(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Planner,
        TaskSpec Developer, TaskSpec Reviewer, TaskSpec Tester);
}
