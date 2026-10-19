using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static ConductorDriverTests;

// Parallel-safe: real repositories/kernel state are local; builds and worker starts are injected.
public sealed class RatchetConflictAutoResolveScenarioTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeveloperDispatch_ResolvesRatchetOnlyAndEscalatesMixedConflict(bool extraConflict)
    {
        var (kernel, goal) = SimpleGoal();
        var developer = Assert.Single(goal.Tasks);
        Assert.Equal(AgentRole.Developer, developer.RequiredRole);
        kernel.RetryTask(goal.Id, developer.Id, "Integrate main", RetryCause.MainDriftConflict);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        using var fixture = new AdditiveConflictGitFixture(RatchetFiles(extraConflict), goal.Id);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
            ["merged rows", "developer allowed"], VerificationClass.TestVerifiable, [], []));
        for (var index = 0; index < 2; index++)
            kernel.MapCriterionEvidenceOwner(goal.Id, index, 1, CriterionEvidenceOwner.Acceptance, "seed",
                CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: fixture.OriginalHead);
        var builds = 0;
        var dispatches = 0;
        var escalations = new List<string>();
        var integration = new RatchetConflictDispatchIntegration(kernel, fixture.Repository, TrunkBranchName.Default,
            Path.Combine(fixture.Repository, ".orchestrator/logs/conduct-events.log"), _ =>
            { builds++; return new(true, 0, "passes"); });
        var driver = new ConductorDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), getRunningPaidWorkerCount: () => 0,
            createWorkspace: _ => throw new InvalidOperationException("Workspace already exists."),
            dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); },
            startRecordedDispatches: null, buildServerShutdown: null,
            runAcceptanceVerification: _ => throw new InvalidOperationException("Developer must dispatch first."),
            runAdvisorySemanticAcceptance: null, retryTask: null, recordTaskNote: null,
            recordCriterionRetryFeedback: null, clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => throw new InvalidOperationException("Landing must not run."),
            land: (_, _) => throw new InvalidOperationException("Landing must not run."),
            afterSuccessfulLanding: null, record: _ => { },
            cleanup: _ => throw new InvalidOperationException("Cleanup must not run."),
            writeEscalation: (_, _, reason) => escalations.Add(reason), classifyChangeRisk: _ => null,
            integrateMainBeforeDeveloperDispatch: integration.IntegrateMainBeforeDeveloperDispatch,
            recordPreDispatchIntegrationReceipt: new PreDispatchIntegrationReceiptRecorder(kernel).Record);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Empty(Git(fixture.Worktree, "status", "--porcelain=v1", "--untracked-files=all"));
        if (extraConflict)
        {
            Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
            var escalation = Assert.Single(escalations);
            Assert.Contains(SourceSizeRatchet.SourcePath, escalation);
            Assert.Contains("src/Other.cs", escalation);
            Assert.DoesNotContain("workspace rebase", escalation);
            fixture.AssertRestored();
            Assert.Equal(0, builds);
            Assert.Equal(0, dispatches);
            Assert.All(goal.CriterionEvidenceObligations, item => Assert.Equal(fixture.OriginalHead, item.ExpectedCandidateSha));
            Assert.Null(developer.PendingPreDispatchIntegrationReceipt);
        }
        else
        {
            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Empty(escalations);
            Assert.Equal(1, builds);
            Assert.Equal(1, dispatches);
            var head = Git(fixture.Worktree, "rev-parse", "HEAD").Trim();
            Assert.NotEqual(fixture.OriginalHead, head);
            Assert.Equal(new[] { head, fixture.OriginalHead, fixture.MainHead },
                Git(fixture.Worktree, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' '));
            Assert.Equal("1", Git(fixture.Worktree, "rev-list", "--count", "--first-parent", fixture.OriginalHead + "..HEAD").Trim());
            var measurement = SourceSizeRatchet.MeasureClasses(fixture.Worktree, ["Widget"], out var unreadable)["Widget"];
            Assert.Empty(unreadable);
            var fileLines = File.ReadLines(Path.Combine(fixture.Worktree, WidgetPath)).Count();
            Assert.Equal(RegistryText(FileRow(WidgetPath, fileLines, "goal A\n        // goal B"),
                ClassRow(measurement.TotalLines, measurement.PartialFiles, "goal A\n        // goal B")),
                fixture.Read(SourceSizeRatchet.SourcePath));
            Assert.Equal(2, goal.CriterionEvidenceObligations.Count);
            Assert.All(goal.CriterionEvidenceObligations, item => Assert.Equal(head, item.ExpectedCandidateSha));
            var receipt = Assert.IsType<PreDispatchIntegrationReceipt>(developer.PendingPreDispatchIntegrationReceipt);
            Assert.Equal(fixture.OriginalHead, receipt.OriginalCandidateSha);
            Assert.Equal(fixture.MainHead, receipt.IntegratedMainSha);
            Assert.Equal(head, receipt.ResultingCandidateSha);
        }
    }
}
