using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorPreReviewBaselineArmChildTests
{
    [Theory]
    [InlineData("finding-batch", true)]
    [InlineData("finding-batch-baseline-arm", false)]
    public void DetachedBaselineBatchRunsBothArmsAndPublishesThem(string batchId, bool typedFlag)
    {
        var root = Path.Combine(Path.GetTempPath(), "baseline-child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var goal = new AgentOrchestratorKernel().CreateGoal("Run a detached baseline arm");
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal, 0, [], "abc1234", "def5678");
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root,
                isProcessAlive: processId => processId == 7102,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7102),
                acquireStableSlotLease: (_, _) => null);
            var context = new ConductorFocusedEvidenceRequestContext(
                "round-1", batchId, [], RunBaselineArm: typedFlag);
            var started = coordinator.EvaluateFocusedEvidence(
                candidate, ConductorAutonomyPolicy.Permissive,
                "Infrastructure.Tests: ConductorPreReviewBaselineArmChildTests",
                (_, request, _, _) => new FocusedEvidenceRunResult(request, true, true, "unused", []),
                context);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            Assert.Equal(typedFlag, started.Attempt.FocusedEvidenceRunsBaselineArm);

            bool? requestedBaseline = null;
            ConductorParallelAcceptanceAttemptCoordinator.RunPreReviewEvidenceAttempt(
                coordinator, started.Attempt, candidate, ConductorAutonomyPolicy.Permissive,
                (attemptCandidate, request, _, runBaselineArm, _) =>
                {
                    requestedBaseline = runBaselineArm;
                    return ConductorParallelAcceptanceRunResult.Focused(
                        attemptCandidate,
                        new FocusedEvidenceRunResult(
                            request, true, true, "both arms", [],
                            Arms:
                            [
                                new FocusedEvidenceArmRunResult(
                                    FindingEvidenceArm.Candidate, "abc1234", FindingEvidenceArmDisposition.Green,
                                    true, true, "candidate", []),
                                new FocusedEvidenceArmRunResult(
                                    FindingEvidenceArm.Baseline, "def5678", FindingEvidenceArmDisposition.Green,
                                    true, true, "merge-base", [])
                            ]));
                });

            Assert.Equal(true, requestedBaseline);
            using var result = JsonDocument.Parse(File.ReadAllText(started.Attempt.ResultPath));
            var arms = result.RootElement.GetProperty("focusedEvidence").GetProperty("arms");
            Assert.Equal(2, arms.GetArrayLength());
            Assert.Contains(arms.EnumerateArray(), arm =>
                arm.GetProperty("arm").GetString() == "Baseline" &&
                arm.GetProperty("sha").GetString() == "def5678");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
