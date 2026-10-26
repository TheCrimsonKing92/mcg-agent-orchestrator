using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Real git repositories use the existing landing runner's isolated fixture and cleanup.
[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorRoleReportEvidenceTests
{
    [Fact]
    public void Execute_WorkerOwnedDeveloperReport_LandsLikeIncident()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal, wait, candidate) = CreateCandidate(repo, owner: "worker");
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            var (attemptId, _, _) = ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory,
                goal, candidate, ProspectiveEvidenceGateFixture.PassingRows);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced);
            Assert.IsNotType<LandingDecision.Escalate>(result.Decision);
            var mainAfter = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, mainAfter);
            Assert.Equal(result.MergeCommitSha, mainAfter);
            Assert.Equal(0, GitCli.Run(repo, "merge-base", "--is-ancestor", candidate, "main").ExitCode);
            Assert.True(wait.IsCompleted);
            Assert.StartsWith("ROLE_REPORT_EVIDENCE owner=developer", wait.Answer);
            Assert.Contains($"landing_candidate={candidate}", wait.Answer);
            Assert.Contains($"{ProspectiveEvidenceGateFixture.IncidentClass}@attempt {attemptId}", wait.Answer);
            Assert.EndsWith(" requested_owner=worker", wait.Answer);
            Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Execute_WorkerOwnedEmptyReport_EscalatesAndKeepsWait()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal, wait, candidate) = CreateCandidate(repo, owner: "worker", tests: "deferred - ");
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory,
                goal, candidate, ProspectiveEvidenceGateFixture.PassingRows);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Equal(2, escalation.Decision!.Rung);
            Assert.Contains(wait.Id.Value[..8], escalation.Reason, StringComparison.Ordinal);
            Assert.Equal(mainBefore, LandingExecutorTests.ReadGit(repo, "rev-parse", "main"));
            Assert.False(wait.IsCompleted);
            Assert.Null(wait.Answer);
            Assert.Same(wait, Assert.Single(kernel.GetPendingHumanInput(goal.Id)));
            var notes = goal.Timeline.Where(item => item.Message.StartsWith("ROLE_REPORT_EVIDENCE_SKIPPED", StringComparison.Ordinal));
            Assert.Equal($"ROLE_REPORT_EVIDENCE_SKIPPED request={wait.Id.Value[..8]} reason=developer-report-missing",
                Assert.Single(notes).Message);
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Execute_PassedDeveloperReport_LandsAndClosesWait()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal, wait, candidate) = CreateCandidate(repo);
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            var (attemptId, _, _) = ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory,
                goal, candidate, ProspectiveEvidenceGateFixture.PassingRows);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced);
            Assert.IsNotType<LandingDecision.Escalate>(result.Decision);
            var mainAfter = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, mainAfter);
            Assert.Equal(result.MergeCommitSha, mainAfter);
            Assert.Equal(0, GitCli.Run(repo, "merge-base", "--is-ancestor", candidate, "main").ExitCode);
            Assert.True(wait.IsCompleted);
            Assert.StartsWith("ROLE_REPORT_EVIDENCE owner=developer", wait.Answer);
            Assert.Contains($"landing_candidate={candidate}", wait.Answer);
            Assert.Contains($"{ProspectiveEvidenceGateFixture.IncidentClass}@attempt {attemptId}", wait.Answer);
            Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Execute_FailedDeveloperRound_EscalatesAndDeduplicatesSkip()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal, wait, candidate) = CreateCandidate(repo, failed: true);
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory,
                goal, candidate, ProspectiveEvidenceGateFixture.PassingRows);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = LandingExecutor.Execute(kernel, goal, workspace);
                Assert.False(result.MainAdvanced);
                var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
                Assert.Equal(2, escalation.Decision!.Rung);
                Assert.Contains(wait.Id.Value[..8], escalation.Reason, StringComparison.Ordinal);
            }

            var notes = goal.Timeline.Where(item => item.Message.StartsWith("ROLE_REPORT_EVIDENCE_SKIPPED", StringComparison.Ordinal));
            Assert.Equal($"ROLE_REPORT_EVIDENCE_SKIPPED request={wait.Id.Value[..8]} reason=owner-round-incomplete",
                Assert.Single(notes).Message);
            Assert.Equal(mainBefore, LandingExecutorTests.ReadGit(repo, "rev-parse", "main"));
            Assert.False(wait.IsCompleted);
            Assert.Null(wait.Answer);
            Assert.Same(wait, Assert.Single(kernel.GetPendingHumanInput(goal.Id)));
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClassEvidence_GroupedReceipt_UsesCandidateBoundSource(bool cohort)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var receipt = ProspectiveEvidenceGateResolverGroupedReceiptTests.Save(fixture,
            GroupedGateTrxSamples.WriteIncidentAndRerun(fixture.Root), cohort);
        var source = RoleReportGateClassEvidence.For(fixture.OrchestratorDirectory, fixture.Goal.Id, fixture.CandidateSha);

        Assert.Equal($"{receipt.Kind} receipt {receipt.ReceiptId}", source(GroupedGateTrxSamples.Selectors[0]));
        Assert.Null(source("AbsentTests"));
        Assert.Null(RoleReportGateClassEvidence.For(fixture.OrchestratorDirectory, fixture.Goal.Id,
            new string('b', 40))(GroupedGateTrxSamples.Selectors[0]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Execute_WrongCandidateOrFailedClass_KeepsWait(bool wrongCandidate)
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal, wait, candidate) = CreateCandidate(repo);
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            var rows = ProspectiveEvidenceGateFixture.PassingRows.ToArray();
            if (!wrongCandidate) rows[^1] = (rows[^1].Name, "Failed");
            ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory,
                goal, wrongCandidate ? mainBefore : candidate, rows);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.False(wait.IsCompleted);
            Assert.Null(wait.Answer);
            Assert.Contains(goal.Timeline, item => item.Message.EndsWith(
                $"reason=class-not-in-passed-gate:{ProspectiveEvidenceGateFixture.IncidentClass}", StringComparison.Ordinal));
            Assert.Equal(mainBefore, LandingExecutorTests.ReadGit(repo, "rev-parse", "main"));
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }

    private static (OrchestratorWorkspace Workspace, AgentOrchestratorKernel Kernel, Goal Goal,
        HumanInputRequest Wait, string Candidate) CreateCandidate(string repo, bool failed = false,
        string owner = "developer", string? tests = null)
    {
        // Follow CreateAcceptedCandidate's git/journal sequence; its manual Developer cannot be re-dispatched.
        var workspace = OrchestratorWorkspace.ForDirectory(repo);
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
        var goal = kernel.CreateGoal("Close Developer report before landing", [developer]);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Role report evidence",
            [RoleReportEvidenceClosureTests.ReportCriterion], VerificationClass.TestVerifiable, [], []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        LandingExecutorTests.AddGoalBranchCommit(repo, GoalWorktrees.BranchName(goal.Id), "src/role-report.txt", "goal work");
        var candidate = LandingExecutorTests.ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
        var main = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
        var started = DateTimeOffset.UtcNow.AddMinutes(-2);
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "codex-cli", "implement", repo, started, BaseCommit: main));
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, candidate);
        var output = RoleReportEvidenceClosureTests.DeveloperOutput(
            tests ?? $"deferred - {ProspectiveEvidenceGateFixture.IncidentClass}").Replace("commit: B", $"commit: {candidate}", StringComparison.Ordinal);
        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
            "implement", repo, 0, output, string.Empty, started.AddSeconds(1),
            WorkerResultPresent: true, HasCommittedChanges: true, DispatchStartedAt: started));
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Empty(goal.CriterionEvidenceObligations);
        if (failed)
        {
            kernel.ReportWorkerTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Failed, "Latest Developer round failed.");
            // Model a passed acceptance candidate with an unsatisfied role report. Reporting failure
            // refreshes goal status; restore Verified so landing reaches the pending-wait hold.
            kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id) with { Status = GoalStatus.Verified });
            goal = kernel.GetGoal(goal.Id);
            Assert.Equal(WorkTaskStatus.Failed, goal.FindTask(developer.Id).Status);
        }
        Assert.Equal(GoalStatus.Verified, goal.Status);
        GoalWorktrees.Ensure(repo, goal.Id);
        GoalOperationJournal.AcceptancePassed(repo, goal, "conductor:acceptance", candidate, main,
            "Passed candidate gate", DateTimeOffset.UtcNow.AddMinutes(-1));
        var wait = kernel.RequestHumanInputDeduplicated(goal.Id, null, "Provide Developer report",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: owner,
            plannerIdentity: new PlannerEvidenceIdentity(goal.Id.Value, developer.Id.Value,
                goal.AuthoritativeBrief.Version, 1, "developer-report"),
            plannerEvidence: new PlannerEvidencePayload(1, "developer-report", "post-implementation", owner,
                "Deferred report", "Developer owns; Acceptance executes", null, null, null)).Request;
        return (workspace, kernel, goal, wait, candidate);
    }
}
