using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceCriterionEvidenceRecoveryTests : CliCommandTestBase
{
    [Fact]
    public void PassedMergeTrainReceiptRecoversMergedVerifiedGoalAndWritesAuditDisposition()
    {
        var fixture = CreateFixture(withOperatorObligation: false);
        try
        {
            MergeCandidateIntoMain(fixture);
            SavePassedTrainReceipt(fixture, "recover-train-receipt", fixture.CandidateSha);

            var result = RunSweep(fixture);

            Assert.Equal(GoalStatus.Completed, fixture.Kernel.GetGoal(fixture.Goal.Id).Status);
            var obligation = Assert.Single(fixture.Goal.CriterionEvidenceObligations);
            Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
            Assert.Equal(fixture.CandidateSha, obligation.CandidateSha);
            var goalResult = Assert.Single(result.Goals);
            Assert.Contains(goalResult.Repairs, repair =>
                repair.Evidence.Contains("sourceId=recover-train-receipt", StringComparison.Ordinal));
            Assert.Contains(
                GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id).Entries,
                entry => entry.Operation == GoalOperationJournal.TerminalDispositionOperation &&
                    entry.Detail!.Contains("sourceId=recover-train-receipt", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public void PassedCohortReceiptRecoversMergedVerifiedGoalAndWritesAuditDisposition()
    {
        var fixture = CreateFixture(withOperatorObligation: false);
        try
        {
            MergeCandidateIntoMain(fixture);
            SavePassedCohortReceipt(fixture, "recover-cohort-receipt", fixture.CandidateSha);

            var result = RunSweep(fixture);

            Assert.Equal(GoalStatus.Completed, fixture.Kernel.GetGoal(fixture.Goal.Id).Status);
            var obligation = Assert.Single(fixture.Goal.CriterionEvidenceObligations);
            Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
            Assert.Equal(fixture.CandidateSha, obligation.CandidateSha);
            var goalResult = Assert.Single(result.Goals);
            Assert.Contains(goalResult.Repairs, repair =>
                repair.Evidence.Contains("sourceId=recover-cohort-receipt", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public void PassedAcceptanceAttemptRecoversWhenNoTrainReceiptExists()
    {
        var fixture = CreateFixture(withOperatorObligation: false);
        try
        {
            MergeCandidateIntoMain(fixture);
            WriteAttempt(fixture, "accepted-attempt", fixture.Goal.Id.Value, fixture.CandidateSha, passed: true);

            RunSweep(fixture);

            Assert.Equal(GoalStatus.Completed, fixture.Kernel.GetGoal(fixture.Goal.Id).Status);
            Assert.Contains(
                GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id).Entries,
                entry => entry.Operation == GoalOperationJournal.TerminalDispositionOperation &&
                    entry.Detail!.Contains("sourceId=accepted-attempt", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public void StandingLandedDispositionDoesNotSkipRecovery()
    {
        var fixture = CreateFixture(withOperatorObligation: false);
        try
        {
            MergeCandidateIntoMain(fixture);
            SavePassedTrainReceipt(fixture, "standing-disposition-receipt", fixture.CandidateSha);
            Assert.True(GoalWorktrees.Remove(fixture.Root, fixture.Goal.Id).IsComplete);
            GoalOperationJournal.RecordTerminalDisposition(
                fixture.Root,
                fixture.Goal,
                new GoalTerminalDisposition(GoalTerminalDispositionKind.Landed, "premature goal-mark-landed disposition"));

            RunSweep(fixture);

            Assert.Equal(GoalStatus.Completed, fixture.Kernel.GetGoal(fixture.Goal.Id).Status);
            Assert.Equal(CriterionEvidenceState.Satisfied, Assert.Single(fixture.Goal.CriterionEvidenceObligations).State);
            Assert.Contains(
                GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id).Entries,
                entry => entry.Operation == GoalOperationJournal.TerminalDispositionOperation &&
                    entry.Detail!.Contains("sourceId=standing-disposition-receipt", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public void FailedGateDoesNotRecover()
    {
        var fixture = CreateFixture(withOperatorObligation: false);
        try
        {
            MergeCandidateIntoMain(fixture);
            WriteAttempt(fixture, "failed-attempt", fixture.Goal.Id.Value, fixture.CandidateSha, passed: false);

            RunSweep(fixture);

            AssertStillPending(fixture);
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public void GateForDifferentGoalDoesNotRecover()
    {
        var fixture = CreateFixture(withOperatorObligation: false);
        try
        {
            MergeCandidateIntoMain(fixture);
            WriteAttempt(fixture, "wrong-goal-attempt", GoalId.New().Value, fixture.CandidateSha, passed: true);

            RunSweep(fixture);

            AssertStillPending(fixture);
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public void PassedGateWhoseCandidateIsNotOnMainDoesNotRecover()
    {
        var fixture = CreateFixture(withOperatorObligation: false);
        try
        {
            MergeCandidateIntoMain(fixture);
            var worktree = Assert.IsType<string>(GoalWorktrees.TryResolve(fixture.Root, fixture.Goal.Id));
            File.WriteAllText(Path.Combine(worktree, "not-on-main.txt"), "new candidate");
            RunGit(worktree, "add", "not-on-main.txt");
            RunGit(worktree, "commit", "-m", "Unmerged candidate");
            var unmergedCandidate = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
            WriteAttempt(fixture, "unmerged-attempt", fixture.Goal.Id.Value, unmergedCandidate, passed: true);

            RunSweep(fixture);

            AssertStillPending(fixture);
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public void PassedReceiptForMergedCandidateDoesNotRecoverAfterGoalBranchAdvancesOffMain()
    {
        var fixture = CreateFixture(withOperatorObligation: false);
        try
        {
            MergeCandidateIntoMain(fixture);
            SavePassedTrainReceipt(fixture, "stale-train-receipt", fixture.CandidateSha);
            var worktree = Assert.IsType<string>(GoalWorktrees.TryResolve(fixture.Root, fixture.Goal.Id));
            File.WriteAllText(Path.Combine(worktree, "new-unmerged-work.txt"), "new candidate");
            RunGit(worktree, "add", "new-unmerged-work.txt");
            RunGit(worktree, "commit", "-m", "Advance goal after older candidate landed");

            RunSweep(fixture);

            AssertStillPending(fixture);
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public void OperatorOwnedObligationRemainsPendingAfterAcceptanceEvidenceRecovery()
    {
        var fixture = CreateFixture(withOperatorObligation: true);
        try
        {
            MergeCandidateIntoMain(fixture);
            SavePassedTrainReceipt(fixture, "operator-hold-receipt", fixture.CandidateSha);

            var result = RunSweep(fixture);

            Assert.Equal(GoalStatus.Verified, fixture.Kernel.GetGoal(fixture.Goal.Id).Status);
            Assert.Equal(CriterionEvidenceState.Satisfied, fixture.Goal.CriterionEvidenceObligations[0].State);
            Assert.Equal(CriterionEvidenceState.Pending, fixture.Goal.CriterionEvidenceObligations[1].State);
            Assert.Equal(CriterionEvidenceOwner.Operator, fixture.Goal.CriterionEvidenceObligations[1].Owner);
            Assert.Contains(
                Assert.Single(result.Goals).Blockers,
                blocker => blocker.Kind == "criterion-evidence-recovery-held" &&
                    blocker.Evidence.Contains("criterion-v1-1:Operator:Pending", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    private static RecoveryFixture CreateFixture(bool withOperatorObligation)
    {
        var root = CreateAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Recover stranded criterion evidence", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        File.WriteAllText(Path.Combine(worktree, "recovered.txt"), "goal work");
        RunGit(worktree, "add", "recovered.txt");
        RunGit(worktree, "commit", "-m", "Goal work");
        var candidateSha = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", worktree, DateTimeOffset.UtcNow));
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            goal.Objective,
            withOperatorObligation
                ? ["Full gate passes", "Operator observes the native result"]
                : ["Full gate passes"],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.MapCriterionEvidenceOwner(
            goal.Id,
            0,
            1,
            CriterionEvidenceOwner.Acceptance,
            "test",
            CriterionEvidenceScopes.FullAcceptanceGate,
            expectedCandidateSha: new string('a', 40));
        if (withOperatorObligation)
        {
            kernel.MapCriterionEvidenceOwner(
                goal.Id,
                1,
                1,
                CriterionEvidenceOwner.Operator,
                "test",
                "operator:native-observation",
                expectedCandidateSha: candidateSha);
        }

        var workspace = OrchestratorWorkspace.ForDirectory(root);
        return new RecoveryFixture(
            root,
            workspace.OrchestratorDirectory,
            kernel,
            goal,
            candidateSha,
            new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db")),
            new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")));
    }

    private static void MergeCandidateIntoMain(RecoveryFixture fixture) =>
        RunGit(fixture.Root, "merge", "--ff-only", GoalWorktrees.BranchName(fixture.Goal.Id));

    private static void SavePassedTrainReceipt(RecoveryFixture fixture, string receiptId, string candidateSha)
    {
        var otherGoalId = GoalId.New();
        var members = new[]
        {
            Member(fixture.Goal.Id, candidateSha, "src/Recovered.cs", "resource:recovered"),
            Member(otherGoalId, candidateSha, "tests/Other.cs", "resource:other")
        };
        var mainSha = RunGitOutput(fixture.Root, "rev-parse", "main").Trim();
        var treeSha = RunGitOutput(fixture.Root, "rev-parse", "main^{tree}").Trim();
        var identity = MergeTrainIdentity.Create(members, mainSha, treeSha, "manifest-v1");
        fixture.Store.SaveGateReceipt(new MergeTrainReceipt(
            receiptId,
            identity,
            MergeTrainGateOutcome.Passed,
            DateTimeOffset.UtcNow,
            10,
            [],
            0,
            [WritePassingTrx(fixture.Root, $"{receiptId}.trx")],
            ValidForLanding: true));
    }

    private static void SavePassedCohortReceipt(RecoveryFixture fixture, string receiptId, string candidateSha)
    {
        var otherGoalId = GoalId.New();
        var members = new[]
        {
            new AcceptanceCohortMemberBinding(
                fixture.Goal.Id,
                candidateSha,
                candidateSha,
                ["src/Recovered.cs"],
                ["resource:recovered"],
                ChangeRiskTier.Behavior,
                ConductorTransitionDecision.Auto,
                GateReadyMergeStatus.Clean.ToString(),
                GateReadyMergeReason.NoConflictsDetected.ToString()),
            new AcceptanceCohortMemberBinding(
                otherGoalId,
                candidateSha,
                candidateSha,
                ["tests/Other.cs"],
                ["resource:other"],
                ChangeRiskTier.Behavior,
                ConductorTransitionDecision.Auto,
                GateReadyMergeStatus.Clean.ToString(),
                GateReadyMergeReason.NoConflictsDetected.ToString())
        };
        var mainSha = RunGitOutput(fixture.Root, "rev-parse", "main").Trim();
        var treeSha = RunGitOutput(fixture.Root, "rev-parse", "main^{tree}").Trim();
        var identity = AcceptanceCohortIdentity.Create(members, mainSha, treeSha, "manifest-v1");
        fixture.CohortStore.SaveGateReceipt(new AcceptanceCohortReceipt(
            receiptId,
            identity,
            AcceptanceCohortGateOutcome.Passed,
            DateTimeOffset.UtcNow,
            10,
            [],
            0,
            [WritePassingTrx(fixture.Root, $"{receiptId}.trx")],
            ValidForLanding: true));
    }

    private static MergeTrainMemberBinding Member(GoalId goalId, string candidateSha, string path, string resource) =>
        new MergeTrainMemberBinding(
            goalId,
            candidateSha,
            candidateSha,
            [path],
            [resource],
            ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto,
            GateReadyMergeStatus.Clean.ToString(),
            GateReadyMergeReason.NoConflictsDetected.ToString())
        .WithRebasedHead(candidateSha);

    private static string WritePassingTrx(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testId="1" testName="Passes" outcome="Passed" /></Results>
              <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
            </TestRun>
            """);
        return path;
    }

    private static void WriteAttempt(
        RecoveryFixture fixture,
        string attemptId,
        string goalId,
        string candidateSha,
        bool passed)
    {
        var directory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", fixture.Goal.Id.Value);
        Directory.CreateDirectory(directory);
        var prefix = Path.Combine(directory, attemptId);
        File.WriteAllText(prefix + ".attempt.json", JsonSerializer.Serialize(new
        {
            attemptId,
            goalId,
            branchHeadSha = candidateSha,
            kind = ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind,
            focusedEvidenceRequest = (string?)null,
            startedAt = DateTimeOffset.UtcNow,
            executionProtocol = "out-of-process"
        }));
        File.WriteAllText(prefix + ".result.json", JsonSerializer.Serialize(new
        {
            kind = passed ? "accepted" : "rejected",
            acceptance = new { passed }
        }));
        File.WriteAllText(prefix + ".exit.txt", passed ? "0" : "1");
    }

    private static TerminalGoalSweepResult RunSweep(RecoveryFixture fixture) =>
        TerminalGoalSweep.Run(
            fixture.Kernel,
            fixture.Root,
            fixture.Goal.Id,
            gitRunner: static (workingDirectory, args) => GitCli.Run(workingDirectory, args.ToArray()),
            orchestratorDirectory: fixture.OrchestratorDirectory,
            mergeTrainAcceptanceStore: fixture.Store);

    private static void AssertStillPending(RecoveryFixture fixture)
    {
        Assert.Equal(GoalStatus.Verified, fixture.Kernel.GetGoal(fixture.Goal.Id).Status);
        var obligation = Assert.Single(fixture.Goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Assert.Null(obligation.CandidateSha);
    }

    private static void Cleanup(RecoveryFixture fixture) =>
        CleanupAcceptanceRepository(fixture.Root, fixture.Goal.Id);

    private sealed record RecoveryFixture(
        string Root,
        string OrchestratorDirectory,
        AgentOrchestratorKernel Kernel,
        Goal Goal,
        string CandidateSha,
        MergeTrainAcceptanceStore Store,
        CohortAcceptanceStore CohortStore);
}
