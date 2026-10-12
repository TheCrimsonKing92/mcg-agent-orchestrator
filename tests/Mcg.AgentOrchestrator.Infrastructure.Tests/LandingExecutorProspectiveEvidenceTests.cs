using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorProspectiveEvidenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_WriterFails_EscalatesWithFailureAndKeepsMain(bool blockEventFile)
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/writer-failure.txt");
            var candidate = LandingExecutorTests.ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, candidate);
            var wait = kernel.RequestHumanInputDeduplicated(goal.Id, goal.Tasks.Single().Id,
                "Named gate results", kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
                evidenceOwner: "acceptance-gate", testNames: [ProspectiveEvidenceGateFixture.IncidentClass]).Request;
            ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory,
                goal, candidate, ProspectiveEvidenceGateFixture.PassingRows);
            var eventsDirectory = Path.Combine(workspace.OrchestratorDirectory, "blocked-events");
            var writer = new GoalLifecycleEventWriter(eventsDirectory);
            var blockedPath = blockEventFile ? writer.EventFilePath(goal.Id) : eventsDirectory;
            if (blockEventFile)
                Directory.CreateDirectory(blockedPath);
            else
                File.WriteAllText(blockedPath, "A file cannot be an events directory.");
            kernel.SetEventWriter(writer);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            Assert.Equal(mainBefore, LandingExecutorTests.ReadGit(repo, "rev-parse", "main"));
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Equal(2, escalation.Decision!.Rung);
            Assert.Contains(wait.Id.Value[..8], escalation.Reason, StringComparison.Ordinal);
            Assert.Contains(nameof(HumanWaitKind.ProspectiveAcceptanceEvidence), escalation.Reason, StringComparison.Ordinal);
            Assert.Contains("submission failed:", escalation.Reason, StringComparison.Ordinal);
            Assert.Contains(blockEventFile ? nameof(UnauthorizedAccessException) : nameof(IOException), escalation.Reason, StringComparison.Ordinal);
            Assert.Contains(blockedPath, escalation.Reason, StringComparison.Ordinal);
            Assert.False(wait.IsCompleted);
            Assert.Null(wait.Answer);
            Assert.Empty(wait.AnswerHistory);
            Assert.Equal(wait.Id, Assert.Single(kernel.GetPendingHumanInput(goal.Id)).Id);
            Assert.DoesNotContain(goal.Timeline, entry => entry.Kind == ProgressKind.HumanInputReceived);
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void PassingNamedGateEvidenceLandsCandidateWithoutRungTwoEscalation()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/named-evidence.txt");
            var candidate = LandingExecutorTests.ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, candidate);
            var wait = kernel.RequestHumanInputDeduplicated(goal.Id, goal.Tasks.Single().Id,
                "Named gate results", kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
                evidenceOwner: "acceptance-gate", testNames: [ProspectiveEvidenceGateFixture.IncidentClass]).Request;
            var (attemptId, trx, _) = ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory,
                goal, candidate, ProspectiveEvidenceGateFixture.PassingRows);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced);
            Assert.IsNotType<LandingDecision.Escalate>(result.Decision);
            var mainAfter = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, mainAfter);
            Assert.Equal(result.MergeCommitSha, mainAfter);
            Assert.Equal(0, GitCli.Run(repo, "merge-base", "--is-ancestor", candidate, "main").ExitCode);
            Assert.Equal("goal work", LandingExecutorTests.ReadGit(repo, "show", "main:src/named-evidence.txt"));
            Assert.True(wait.IsCompleted);
            Assert.Contains(attemptId, wait.Answer, StringComparison.Ordinal);
            Assert.Contains(trx, wait.Answer, StringComparison.Ordinal);
            Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void FailedNamedRowEscalatesAtRungTwoWithWaitAndTestDetailsAndKeepsMain()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/failed-evidence.txt");
            var candidate = LandingExecutorTests.ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            var names = ProspectiveEvidenceGateFixture.IncidentRows;
            var wait = kernel.RequestHumanInputDeduplicated(goal.Id, goal.Tasks.Single().Id,
                "Named gate results", kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
                evidenceOwner: "acceptance-gate", testNames: names).Request;
            var rows = ProspectiveEvidenceGateFixture.PassingRows.ToArray();
            rows[^1] = (rows[^1].Name, "Failed");
            ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory, goal, candidate, rows);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            Assert.Equal(mainBefore, LandingExecutorTests.ReadGit(repo, "rev-parse", "main"));
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Equal(2, escalation.Decision!.Rung);
            Assert.Contains(wait.Id.Value[..8], escalation.Reason, StringComparison.Ordinal);
            Assert.Contains(nameof(HumanWaitKind.ProspectiveAcceptanceEvidence), escalation.Reason, StringComparison.Ordinal);
            Assert.Contains(names[^1], escalation.Reason, StringComparison.Ordinal);
            Assert.False(wait.IsCompleted);
            Assert.Null(wait.Answer);
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }
}
