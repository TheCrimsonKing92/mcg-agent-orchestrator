using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the captured dispatch and prepared prompt use a case-owned temporary tree.
public sealed class PlannerBriefAmendmentReplayTests : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Amendment_with_evidence_waits_once_then_redispatches_with_ruling(bool reworded)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
            var developer = new TaskSpec(TaskId.New(), "Implement after the ruling.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Replay a Planner brief defect", [planner, developer]);
            kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
                "Preserve behavior pending a criterion ruling.", ["Preserve the existing behavior."],
                VerificationClass.TestVerifiable, [], []));
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
            var stdout = Path.Combine(root, "planner.out.log");
            var stderr = Path.Combine(root, "planner.err.log");
            var exit = Path.Combine(root, "planner.exit.txt");
            var plan = PlannerContractPlanFixture().Replace(PlannerContractAcceptanceMappingBody,
                "1. disposition=planned; plan=obtain a ruling on the criterion defect before implementation",
                StringComparison.Ordinal);
            File.WriteAllText(stdout, string.Join(Environment.NewLine,
                plan,
                """
                PLANNER_BRIEF_AMENDMENT: {"criterion_index":1,"defect":"The criterion contradicts an existing regression.","evidence":"seed.txt:1","proposed_text":"Preserve the existing behavior.","fallback_text":"Narrow the criterion."}
                PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"historical-receipt","availability":"retrievable","store":"goal-events","needed":"the historical receipt","reason":"worker cannot read the store"}
                PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"parser-tests","availability":"post-implementation","owner":"acceptance-gate","needed":"passing parser tests","reason":"implementation must exist first","tests":["PlannerBriefAmendmentRequestTests"]}
                """,
                WorkerResultBlock("none", "source inspection", "not-run - Planner role", commit: "none",
                    blockers: "exact-blocker - brief amendment requested for criterion 1")));
            File.WriteAllText(stderr, string.Empty);
            File.WriteAllText(exit, "0");
            var now = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner-worker", "planner.exe", root, now));
            kernel.RecordTaskProcessStarted(goal.Id, planner.Id,
                new TaskProcessRecord(4242, "planner.exe", root, stdout, stderr, exit, now, null, null));

            new BackgroundDispatchRunner(isStillRunning: _ => false).RefreshLatestProcess(kernel, goal.Id, planner.Id);

            Assert.Equal(WorkTaskStatus.WaitingForHuman, planner.Status);
            var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
            Assert.Equal(HumanWaitKind.SpecClarification, request.Kind);
            Assert.Equal(request.Id, Assert.Single(kernel.GetPendingBlockingHumanInput(goal.Id)).Id);
            Assert.Empty(goal.GateTestObligations);
            Assert.Equal(HumanWaitKind.SpecClarification, planner.LastVerification!.HumanInputKind);
            Assert.Equal(request.BlockerFingerprint, planner.LastVerification.HumanInputBlockerFingerprint);
            Assert.Null(Assert.Single(planner.LastVerification.PlannerEvidenceBatch!.Directives).PlannerEvidence);
            Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.TaskFailed);

            var original = planner.LastVerification;
            var repeatedOutput = reworded
                ? original.StandardOutput.Replace("seed.txt:1", "seed.txt:2", StringComparison.Ordinal)
                    .Replace("Preserve the existing behavior.", "Keep the regression behavior.", StringComparison.Ordinal)
                    .Replace("Narrow the criterion.", "Revise the criterion wording.", StringComparison.Ordinal)
                : original.StandardOutput;
            var repeatedBatch = AgentOutputDirectives.ParseHumanInputRequest(repeatedOutput, AgentRole.Planner);
            Assert.False(repeatedBatch.IsMalformed, repeatedBatch.Diagnostic);
            var repeatedDirective = Assert.Single(repeatedBatch.Directives);
            if (reworded) Assert.NotEqual(original.HumanInputQuestion, repeatedDirective.Question);
            var repeated = original with
            {
                CompletedAt = original.CompletedAt.AddSeconds(1),
                StandardOutput = repeatedOutput,
                AuthoritativeStandardOutput = repeatedOutput,
                PlannerEvidenceBatch = repeatedBatch,
                HumanInputQuestion = repeatedDirective.Question,
                HumanInputQuestionFingerprint = repeatedDirective.QuestionFingerprint,
                HumanInputBlockerFingerprint = repeatedDirective.BlockerFingerprint
            };
            kernel.RecordDispatchExecutionResult(goal.Id, planner.Id, repeated);

            Assert.Equal(request.Id, Assert.Single(kernel.GetPendingHumanInput(goal.Id)).Id);
            Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputRequested));
            Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.DuplicateHumanInputSuppressed));
            Assert.Empty(goal.GateTestObligations);
            const string ruling = "Preserve the existing behavior and narrow criterion 1 accordingly.";
            kernel.SubmitHumanInput(request.Id, ruling);

            Assert.Empty(kernel.GetPendingBlockingHumanInput(goal.Id));
            Assert.Equal(WorkTaskStatus.Assigned, planner.Status);
            kernel.RecordDispatchExecutionResult(goal.Id, planner.Id,
                repeated with { CompletedAt = original.CompletedAt.AddSeconds(2) });

            Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
            Assert.Equal(WorkTaskStatus.Assigned, planner.Status);
            Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputRequested));
            Assert.Equal(2, goal.Timeline.Count(item => item.Kind == ProgressKind.DuplicateHumanInputSuppressed));
            Assert.Contains(goal.Timeline, item => item.Kind == ProgressKind.DuplicateHumanInputSuppressed &&
                item.Message.Contains("state=answered", StringComparison.Ordinal));
            Assert.Empty(goal.GateTestObligations);
            Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.TaskFailed);
            var prepared = WorkerProfileDispatcher.PrepareTask(kernel, goal, planner,
                new WorkerProfile("codex-cli", "codex exec --cd {workingDirectory}"),
                Path.Combine(root, "prompts"), root, DateTimeOffset.UtcNow,
                providerName: "OpenAI", modelName: AgentCatalog.OpenAiSubscriptionModelAlias);
            var prompt = File.ReadAllText(prepared.PromptPath);
            Assert.Contains("## Resolved Human Input", prompt, StringComparison.Ordinal);
            Assert.Contains(ruling, prompt, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
