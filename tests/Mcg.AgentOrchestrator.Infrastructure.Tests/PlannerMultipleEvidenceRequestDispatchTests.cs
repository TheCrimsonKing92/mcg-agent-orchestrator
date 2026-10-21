using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Unique owned roots and injected process liveness; safe to run in parallel.
public sealed class PlannerMultipleEvidenceRequestDispatchTests
{
    [Fact]
    public void RefreshLatestProcess_MergedEvidence_CompletesPlannerAndAssignsDeveloper()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement the plan.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Merged prospective acceptance evidence", [planner, developer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Execute all candidate selections after implementation.",
            ["Acceptance executes the candidate tests."], VerificationClass.TestVerifiable, [], []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var root = Path.Combine(Path.GetTempPath(), $"mcg-planner-merged-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
            var stdout = Path.Combine(root, "planner.out.log");
            var stderr = Path.Combine(root, "planner.err.log");
            var exit = Path.Combine(root, "planner.exit.txt");
            var plan = WorkerDispatchTestSupport.PlannerContractPlanFixture().Replace(
                WorkerDispatchTestSupport.PlannerContractAcceptanceMappingBody,
                "1. disposition=planned; plan=Acceptance executes all selected candidate tests after implementation",
                StringComparison.Ordinal);
            File.WriteAllText(stdout, string.Join(Environment.NewLine,
                new string('H', VerificationTextBounds.PreviewHeadChars),
                WorkerDispatchTestSupport.ResearcherContractFixture(), plan,
                PlannerMultipleEvidenceRequestMergeTests.GateRequests,
                new string('T', VerificationTextBounds.PreviewTailChars + 1_000),
                "WORKER_RESULT:", "files: none", "commands: source inspection",
                "tests: not-run - Planner role", "commit: none", "blockers: none",
                "model_fit: OpenAI/gpt-6.1-sol - adequate - planning - mapped gate evidence",
                "skills: criterion-ownership-planning", "confidence: high", "END_WORKER_RESULT"));
            File.WriteAllText(stderr, string.Empty);
            File.WriteAllText(exit, "0");
            var now = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner-worker", "planner.exe", root, now));
            kernel.RecordTaskProcessStarted(goal.Id, planner.Id,
                new TaskProcessRecord(4244, "planner.exe", root, stdout, stderr, exit, now, null, null));

            var livenessChecked = false;
            new BackgroundDispatchRunner(isStillRunning: _ => { livenessChecked = true; return false; })
                .RefreshLatestProcess(kernel, goal.Id, planner.Id);

            Assert.True(livenessChecked);
            Assert.True(planner.LastVerification!.Succeeded,
                $"{planner.LastVerification.OrchestratorFailureReason}\n{planner.LastVerification.StandardError}");
            Assert.Equal(planner.LastDispatch!.DispatchedAt, planner.LastVerification.DispatchAttemptAt);
            Assert.Equal(WorkTaskStatus.Completed, planner.Status);
            Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
            Assert.Equal(2, goal.GateTestObligations.Count);
            Assert.All(goal.GateTestObligations, item =>
            {
                Assert.NotNull(item.PlannerIdentity);
                Assert.Equal(goal.AuthoritativeBrief.Version, item.PlannerIdentity.BriefVersion);
                Assert.Equal(planner.Id.Value, item.PlannerIdentity.RequestingTaskId);
            });
            var obligation = goal.GateTestObligations[0];
            Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
            Assert.Equal(new[] { "AlphaTests", "BetaTests" }, obligation.Selectors);
            Assert.Equal(new[] { "BetaTests", "GammaTests.Row" }, goal.GateTestObligations[1].Selectors);
            Assert.Empty(kernel.GetPendingBlockingHumanInput(goal.Id));
            Assert.NotNull(planner.LastVerification);
            Assert.True(planner.LastVerification.Succeeded);
            Assert.Equal(0, planner.LastVerification.ExitCode);
            Assert.Equal(2, planner.LastVerification.PlannerEvidenceBatch!.Directives.Count);
            Assert.All(planner.LastVerification.PlannerEvidenceBatch!.Directives, item => Assert.Equal("acceptance-gate", item.EvidenceOwner));
            var fullOutput = Assert.IsType<string>(planner.LastVerification.AuthoritativeStandardOutput);
            Assert.Contains(PlannerOutputContract.DurablePlanBeginMarker, fullOutput, StringComparison.Ordinal);
            Assert.Equal(4, fullOutput.Split("PLANNER_EVIDENCE_REQUEST:", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("PLANNER_EVIDENCE_REQUEST:", planner.LastVerification.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(AgentOutputDirectives.ParseHumanInputRequest(
                PlannerMultipleEvidenceRequestMergeTests.FirstRequest, AgentRole.Planner).Directive!.Question,
                obligation.Question);
            Assert.DoesNotContain("planner-output-contract-rejected", planner.LastVerification.CompletionVerdictRule ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("[orchestrator Planner output contract rejection]", planner.LastVerification.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_IndependentSelections_CanUseIndependentExactCandidateAttempts()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(
            PlannerMultipleEvidenceRequestMergeTests.GateRequests, AgentRole.Planner);
        Assert.False(parsed.IsMalformed);
        var waits = parsed.Directives.Select(directive => fixture.Kernel.RequestHumanInputDeduplicated(
            fixture.Goal.Id, null, directive.Question, questionFingerprint: directive.QuestionFingerprint,
            blockerFingerprint: directive.BlockerFingerprint, kind: directive.Kind,
            evidenceOwner: directive.EvidenceOwner, testNames: directive.TestNames).Request).ToArray();
        Assert.Equal(2, waits.Length);
        fixture.WriteAttempt([("AlphaTests.Check", "Passed"), ("BetaTests.Check", "Passed")]);
        var first = fixture.Resolve();
        Assert.Single(first.ResolvedRequestIds);
        Assert.True(waits[0].IsCompleted);
        Assert.False(waits[1].IsCompleted);
        fixture.WriteAttempt([("BetaTests.Check", "Passed"), ("GammaTests.Row", "Passed")]);
        var second = fixture.Resolve();
        Assert.Single(second.ResolvedRequestIds);
        Assert.All(waits, wait => Assert.True(wait.IsCompleted));
    }
}
