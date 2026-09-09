using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsDeferredEvidenceTicks
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ContractValidDeferredReviewNeverRedispatchesAcrossUnchangedTicks(bool restart)
    {
        var (kernel, goal) = SimpleGoal("Keep deferred evidence at its owner");
        var candidateSha = new string('a', 40);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
            ["Full acceptance passed", "Operator observed the native behavior"], VerificationClass.TestVerifiable, [], []));
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "operator", findingStableId: "missing-full-gate", expectedCandidateSha: candidateSha);
        kernel.MapCriterionEvidenceOwner(goal.Id, 1, 1, CriterionEvidenceOwner.Operator,
            "operator", "operator:native", expectedCandidateSha: candidateSha);
        PassVerification(kernel, goal, goal.Tasks.Single(), hasCommittedChanges: true);
        var reviewer = kernel.AddTask(goal.Id, AgentRole.Reviewer, "Review source and defer external evidence", DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "fixture", "review", "C:\\fixture", DateTimeOffset.UtcNow, BaseCommit: candidateSha));
        const string output = """
            WORKER_RESULT:
            files: none
            commands: inspect source
            tests: not-run - modeled read-only reviewer
            commit: none
            blockers: Full acceptance evidence is pending at its owner.
            findings: [{"stable_id":"missing-full-gate","state":"open","location":{"file":"config/acceptance-manifest.json","region":"full-gate"},"description":"The acceptance executor has not produced its receipt.","severity":"blocking","category":"acceptance-owned"}]
            touched_anchors: []
            criteria_verdicts: [{"criterion_index":0,"verdict":"not-verifiable","evidence":"Acceptance executor owns this receipt"},{"criterion_index":1,"verdict":"not-verifiable","evidence":"Operator owns native observation"}]
            verdict: needs-work
            model_fit: fixture/model - deterministic contract control
            skills: none
            confidence: high
            END_WORKER_RESULT
            """;
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 0, output, "", DateTimeOffset.UtcNow, WorkerResultPresent: true));
        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);

        var starts = 0;
        var retries = 0;
        var landings = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [], BranchHeadSha: candidateSha),
            resolveAcceptanceHeads: _ => (candidateSha, new string('b', 40)),
            dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); },
            retryTaskWithRoundKind: (goalId, taskId, message, kind) =>
            {
                retries++;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: kind);
            },
            land: item =>
            {
                landings++;
                return new LandingResult(item.Id.Value, item.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "landed");
            });
        string? identity = null;
        for (var tick = 0; tick < 10; tick++)
        {
            if (restart && tick == 5)
            {
                kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
                    JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
                goal = kernel.GetGoal(goal.Id);
            }
            driver.BeginTick(kernel, tick + 1);
            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.True(held.StableIdentity?.StartsWith("criterion-evidence:", StringComparison.Ordinal) == true,
                JsonSerializer.Serialize(new { tick, held, goal.Status, tasks = goal.Tasks.Select(task => new { task.RequiredRole, task.Status }) }));
            identity ??= held.StableIdentity;
            Assert.Equal(identity, held.StableIdentity);
        }
        Assert.Equal(0, starts);
        Assert.Equal(0, retries);
        Assert.Equal(0, landings);
        Assert.Equal(CriterionEvidenceOwner.Operator, Assert.Single(goal.GetOutstandingCriterionEvidenceObligations(candidateSha)).Owner);

        candidateSha = new string('c', 40);
        driver.BeginTick(kernel, 11);
        var changed = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.NotEqual(identity, changed.StableIdentity);
    }
}
