using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliOffWorkerEvidenceTests : GoalWorktreeTestBase
{
    [Theory]
    [InlineData(AgentRole.Tester, false)]
    [InlineData(AgentRole.Tester, true)]
    [InlineData(AgentRole.Reviewer, false)]
    [InlineData(AgentRole.Reviewer, true)]
    public void AcceptanceAndWorkspaceMergeRetainOperatorHoldAfterWorkerCompletion(AgentRole role, bool failed)
    {
        var repo = CreateSeededRepository();
        try
        {
            string? worktree = null;
            var (kernel, goal) = OffWorkerEvidenceProgressionTests.RecordCompletedWorker(
                role, CriterionEvidenceOwner.Operator, "prepared-below", repo,
                prepareCandidate: item =>
                {
                    worktree = GoalWorktrees.Ensure(repo, item.Id);
                    File.WriteAllText(Path.Combine(worktree, "feature.txt"), "goal feature");
                    RunGit(worktree, "add", "feature.txt");
                    RunGit(worktree, "commit", "-m", "Fixture candidate");
                    return RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
                });
            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            if (failed)
                kernel.RecordCriterionEvidence(goal.Id, obligation.Id, obligation.Owner, obligation.ExpectedCandidateSha!,
                    "owner-red", obligation.RequiredScope, false, "Owner proof failed");
            var mergeCalls = 0;
            var gateCalls = 0;
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var cleanupContext = CreateIsolatedCleanupContext(repo);
            var context = new CliExecutionContext(kernel, workspace, new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), goal,
                finalizeAcceptanceMerge: _ =>
                {
                    mergeCalls++;
                    throw new InvalidOperationException("Pending owner proof reached merge finalization.");
                },
                stopAcceptanceHosts: _ => AcceptanceHostStopResult.Success("No fixture hosts."))
            {
                CleanupContext = cleanupContext,
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed(() => gateCalls++),
                RunInjectedAcceptanceVerifierInCurrentProcess = true,
                StableSlotSelector = (_, _) => AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(
                    cleanupContext.Hooks.BuildStorageRoot!.RootPath)
            };

            var acceptanceOutput = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));
            var workspaceOutput = CaptureConsole(() => CliCommandHandlers.Execute(["workspace", "merge"], context));

            Assert.Equal(1, gateCalls);
            Assert.Equal(0, mergeCalls);
            var state = failed ? CriterionEvidenceState.Failed : CriterionEvidenceState.Pending;
            Assert.Contains("merge blocked by outstanding criterion evidence", acceptanceOutput, StringComparison.Ordinal);
            Assert.Contains($"criterion-v1-0:Operator:{state}", acceptanceOutput, StringComparison.Ordinal);
            Assert.Contains("Workspace merge blocked by outstanding criterion evidence", workspaceOutput, StringComparison.Ordinal);
            Assert.Contains($"criterion-v1-0:Operator:{state}", workspaceOutput, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.Equal(worktree, GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.Equal(WorkTaskStatus.Completed, Assert.Single(goal.Tasks).Status);
            Assert.Equal(state, Assert.Single(goal.CriterionEvidenceObligations).State);
            Assert.Single(ReviewFindings.GetOpenBlockingFindings(Assert.Single(goal.Tasks).LastVerification!.MergedReviewFindings!, goal.EffectiveAcceptanceCriteriaCorrections));
        }
        finally { DeleteDirectory(repo); }
    }
}
