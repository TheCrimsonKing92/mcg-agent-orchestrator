using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

public sealed class OffWorkerEvidenceProgressionTests
{
    internal const string Candidate = "cf46ada32456e6d3d7d13fbcb27aa6bec90b75fd";
    internal const string StableId = "acceptance-lifecycle-live-bounce-missing";
    private const string NativeReceipt = "finding-evidence-f77de2a70ea38d6fe7184ba7";

    [Theory]
    [InlineData(AgentRole.Tester, CriterionEvidenceOwner.Operator)]
    [InlineData(AgentRole.Reviewer, CriterionEvidenceOwner.Operator)]
    [InlineData(AgentRole.Tester, CriterionEvidenceOwner.Acceptance)]
    [InlineData(AgentRole.Reviewer, CriterionEvidenceOwner.Acceptance)]
    public async Task RecordedScopeSurvivesSqliteReloadAndRoutesToOwner(
        AgentRole role, CriterionEvidenceOwner owner)
    {
        var root = Directory.CreateTempSubdirectory("off-worker-evidence-").FullName;
        try
        {
            var (kernel, goal) = RecordCompletedWorker(role, owner, Candidate, root);
            var repository = new SqliteOrchestratorStateRepository(Path.Combine(root, "state.db"));
            await repository.SaveAsync(kernel);
            kernel = await repository.LoadAsync();
            goal = kernel.GetGoal(goal.Id);
            Assert.True(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
            Assert.Equal(WorkTaskStatus.Completed, Assert.Single(goal.Tasks).Status);
            Assert.Equal(CriterionEvidenceState.Pending, Assert.Single(goal.CriterionEvidenceObligations).State);
            var retries = 0;
            var dispatches = 0;
            var landings = 0;
            var gateCalls = 0;
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); },
                retryTask: (goalId, taskId, message) =>
                {
                    retries++;
                    return kernel.RetryTask(goalId, taskId, message, RetryCause.ContractClarification);
                },
                runAcceptanceSummary: _ =>
                {
                    gateCalls++;
                    return new AcceptanceVerificationSummary(true, [], BranchHeadSha: Candidate);
                },
                resolveAcceptanceHeads: _ => (Candidate, new string('b', 40)),
                land: item =>
                {
                    landings++;
                    return new LandingResult(item.Id.Value, item.Id.Value[..8],
                        new LandingDecision.Promote(), "integration", true, "Landed");
                });
            driver.BeginTick(kernel, 1);

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(0, retries);
            Assert.Equal(0, dispatches);
            Assert.Equal(1, gateCalls);
            Assert.Equal(owner == CriterionEvidenceOwner.Operator ? 0 : 1, landings);
            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            Assert.Equal(owner, obligation.Owner);
            Assert.Equal(StableId, obligation.FindingStableId);
            Assert.Equal(Candidate, obligation.ExpectedCandidateSha);
            Assert.Single(ReviewFindings.GetOpenBlockingFindings(Assert.Single(goal.Tasks).LastVerification!.MergedReviewFindings!, goal.EffectiveAcceptanceCriteriaCorrections));
            if (owner == CriterionEvidenceOwner.Acceptance)
            {
                Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
                Assert.True(obligation.HasSatisfiedEvidenceFor(Candidate));
                return;
            }

            var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Contains("Operator:Pending", hold.Reason, StringComparison.Ordinal);
            Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
            Assert.Null(obligation.ReceiptId);
            foreach (var invalid in new[] { "owner", "candidate", "scope" })
                Assert.Throws<InvalidOperationException>(() => kernel.RecordCriterionEvidence(goal.Id, obligation.Id,
                    invalid == "owner" ? CriterionEvidenceOwner.Acceptance : owner,
                    invalid == "candidate" ? "foreign-candidate" : Candidate, "invalid-receipt",
                    invalid == "scope" ? "foreign-scope" : obligation.RequiredScope, true, "Invalid proof"));
            Assert.Equal(CriterionEvidenceState.Pending, Assert.Single(goal.CriterionEvidenceObligations).State);

            kernel.RecordCriterionEvidence(goal.Id, obligation.Id, owner, Candidate, "owner-red",
                obligation.RequiredScope, false, "Owner observation failed");
            driver.BeginTick(kernel, 2);
            result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            Assert.Contains("Operator:Failed", Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome).Reason, StringComparison.Ordinal);
            Assert.Equal(0, landings);

            kernel.RecordCriterionEvidence(goal.Id, obligation.Id, owner, Candidate, "owner-green",
                obligation.RequiredScope, true, "Owner observation succeeded");
            await repository.SaveAsync(kernel);
            kernel = await repository.LoadAsync();
            goal = kernel.GetGoal(goal.Id);
            driver.BeginTick(kernel, 3);
            result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Equal(1, landings);
            Assert.Equal(0, retries);
            Assert.Equal(0, dispatches);
            Assert.Empty(goal.GetOutstandingCriterionEvidenceObligations(Candidate));
            Assert.Equal("owner-red", Assert.Single(Assert.Single(goal.CriterionEvidenceObligations).PriorReceipts!).ReceiptId);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    // Supplied eade shape: a source/test finding resolves from a counted native
    // exact-candidate receipt; the separately owned lifecycle proof stays open.
    internal static (AgentOrchestratorKernel Kernel, Goal Goal) RecordCompletedWorker(
        AgentRole role, CriterionEvidenceOwner owner, string candidate, string directory,
        Func<Goal, string>? prepareCandidate = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Inspect source/test evidence", role);
        var goal = kernel.CreateGoal("Retain owner proof after source verification", [task]);
        candidate = prepareCandidate?.Invoke(goal) ?? candidate;
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
            ["Current owner proof exists"], VerificationClass.TestVerifiable, [], []));
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, owner, "operator",
            findingStableId: StableId, expectedCandidateSha: candidate);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var residual = new ReviewFinding(StableId, ReviewFindingState.Open,
            new("docs/operator-runbook.md", "lifecycle-proof"), "Required owner proof is pending.",
            Category: owner == CriterionEvidenceOwner.Operator ? FindingCategory.OperatorOwned : FindingCategory.AcceptanceOwned);
        var source = new ReviewFinding("source-test-finding", ReviewFindingState.Open,
            new("tests/ExampleTests.cs", "FailurePath"), "Source/test finding requires native evidence.",
            Category: FindingCategory.TestCoverage);
        Record([source, residual]);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        var request = new FindingEvidenceRequest([new("tests/ExampleTests.csproj", "ExampleTests")]);
        var receipt = new FindingEvidenceReceipt(NativeReceipt, candidate, request, true, true, "10 executed/passed",
            Arms: [new(FindingEvidenceArm.Candidate, candidate, FindingEvidenceArmDisposition.Green, true, true,
                "10 executed/passed", ExecutedTestCount: 10)],
            RequestDispositions: [new(source.StableId, FindingEvidenceExecutionClassifier.BuildRequestIdentity(request),
                "executed-standalone", "native-receipt")]);
        kernel.RecordFindingEvidenceOutcome(goal.Id, task.Id, source.StableId,
            new(true, NativeReceipt, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence), receipt);
        kernel.RetryTask(goal.Id, task.Id, "Inspect the supplied current native receipt", RetryCause.ContractClarification);
        Record([source with { State = ReviewFindingState.Resolved }, residual], receipt);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.True(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
        var resolved = Assert.Single(task.LastVerification!.MergedReviewFindings!, item => item.StableId == source.StableId);
        Assert.Equal(ReviewFindingState.Resolved, resolved.State);
        Assert.Equal(NativeReceipt, resolved.EvidenceOutcome!.ReceiptId);
        Assert.Equal(10, Assert.Single(Assert.Single(task.LastVerification.FindingEvidenceReceipts!).Arms!).ExecutedTestCount);
        Assert.Equal(CriterionEvidenceState.Pending, Assert.Single(goal.CriterionEvidenceObligations).State);
        return (kernel, goal);

        void Record(ReviewFinding[] findings, FindingEvidenceReceipt? evidence = null)
        {
            var startedAt = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "review", directory,
                startedAt, BaseCommit: candidate));
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - OwnerProofTests",
                "commit: none", "blockers: Required owner proof remains pending.",
                "findings: " + JsonSerializer.Serialize(findings), "touched_anchors: []",
                "criteria_verdicts: [{\"criterion_index\":0,\"verdict\":\"not-verifiable\",\"evidence\":\"Owner proof pending\"}]",
                "verdict: needs-work", "model_fit: fixture/model - adequate - recording control", "skills: none",
                "confidence: high", "END_WORKER_RESULT");
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord("review", directory,
                0, output, "", DateTimeOffset.UtcNow, WorkerResultPresent: true, DispatchStartedAt: startedAt,
                FindingEvidenceReceipts: evidence is null ? null : [evidence]));
        }
    }
}
