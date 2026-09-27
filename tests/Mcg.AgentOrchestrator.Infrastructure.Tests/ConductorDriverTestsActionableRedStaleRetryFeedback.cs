using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsActionableRedStaleRetryFeedback
{
    private const string CandidateSha = "abc1234";
    private const string TestIdentity = "GateReadyCandidateProjectorTests.DefaultHarnessProducesSerializedResourceKey";
    private const string Earlier = "Earlier CRLF regex note";

    [Fact]
    public void ActionableCandidateRedRetryPublishesCurrentFailureInDeveloperBrief()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var trxPath = Path.Combine(root, "candidate.trx");
            ConductorDriverTestsActionableRedFailureDetail.WriteFailureTrx(trxPath, TestIdentity);
            var (kernel, goal) = SoftwareGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, developer.Id, Earlier, RetryCause.ContractClarification);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
                PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
            FailReviewerNeedsWork(kernel, goal, reviewer, "candidate RED must be actionable",
                findings: [EvidenceFindingWithRequest("Developer-owned fixture is RED.",
                    id: "fixture-red", classes: ["GateReadyCandidateProjectorTests"])]);

            var starts = 0;
            string? publishedPromptPath = null;
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                runFocusedEvidence: (_, request) =>
                {
                    var red = CandidateRedFindingEvidence(request, CandidateSha);
                    var candidate = red.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
                    var check = candidate.Checks.Single() with { TestResultPaths = [trxPath] };
                    return red with
                    {
                        Checks = [check],
                        Arms = red.Arms.Select(arm => arm.Arm == FindingEvidenceArm.Candidate
                            ? arm with { Checks = [check] } : arm).ToArray()
                    };
                },
                dispatchAndStart: dispatchGoal =>
                {
                    publishedPromptPath = WorkerProfileDispatcher.PrepareTask(
                        kernel,
                        dispatchGoal,
                        developer,
                        new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory} {promptPath}"),
                        Path.Combine(root, "prompts"),
                        root,
                        DateTimeOffset.UtcNow,
                        providerName: "OpenAI",
                        modelName: AgentCatalog.OpenAiSubscriptionModelAlias).PromptPath;
                    starts++;
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                    kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: roundKind),
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(1, starts);
            var feedback = Assert.Single(developer.CriterionRetryFeedback);
            Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", feedback, StringComparison.Ordinal);
            Assert.Contains(TestIdentity, feedback, StringComparison.Ordinal);
            Assert.Null(developer.AcceptedRetryFeedback);
            var publishedPrompt = File.ReadAllText(Assert.IsType<string>(publishedPromptPath));
            const string heading = "## Unmet acceptance criteria from the prior attempt - fix these:";
            var sectionStart = publishedPrompt.IndexOf(heading, StringComparison.Ordinal);
            Assert.True(sectionStart >= 0, "The published brief must include the current retry feedback section.");
            var nextSectionStart = publishedPrompt.IndexOf("\n## ", sectionStart + heading.Length, StringComparison.Ordinal);
            var currentFeedbackSection = publishedPrompt[sectionStart..(nextSectionStart < 0 ? publishedPrompt.Length : nextSectionStart)];
            Assert.Contains(TestIdentity, currentFeedbackSection, StringComparison.Ordinal);
            Assert.DoesNotContain(Earlier, currentFeedbackSection, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
