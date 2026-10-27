using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PlannerMalformedBatchStewardTriggerTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void Malformed_batch_records_rejection_rule_and_arms_steward_trigger()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
            var developer = new TaskSpec(TaskId.New(), "Implement the plan.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Route a malformed Planner batch", [planner, developer]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var now = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner-worker", "planner.exe", root, now));
            var candidate = new string('a', 40);
            kernel.RecordDispatchBaseCommit(goal.Id, planner.Id, candidate);
            var output = string.Join(Environment.NewLine,
                "HUMAN_INPUT: Should criterion 1 change?",
                """
                PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"receipt","availability":"retrievable","store":"goal-events","needed":"the historical receipt","reason":"worker cannot read the store"}
                """,
                WorkerResultBlock("none", "source inspection", "not-run - Planner role", commit: "none",
                    blockers: "exact-blocker - criterion ruling needed"));

            // Direct recording isolates the missing kernel rule from runner classification.
            kernel.RecordDispatchExecutionResult(goal.Id, planner.Id,
                new TaskVerificationRecord("planner.exe", root, 0, output, string.Empty, now, WorkerResultPresent: true));

            Assert.Equal(WorkTaskStatus.Failed, planner.Status);
            Assert.Equal("Malformed PLANNER_EVIDENCE_REQUEST: substantive HUMAN_INPUT and Planner evidence are ambiguous; use evidence lines for every requirement.",
                planner.LastVerification!.OrchestratorFailureReason);
            Assert.Equal("planner-output-contract-rejected", planner.LastVerification.CompletionVerdictRule);
            Assert.False(planner.LastVerification.CompletionVerdictVerifiedSuccess);
            Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
            Assert.False(goal.IsTerminal);
            var trigger = Assert.Single(new ConductorStewardTriggerDetector().Detect(goal));
            Assert.Equal(ConductorStewardTriggerKind.PlannerOutputContractRejected, trigger.Kind);
            Assert.Equal(planner.Id.Value, trigger.TaskId);
            Assert.Equal(candidate, trigger.CandidateSha);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
