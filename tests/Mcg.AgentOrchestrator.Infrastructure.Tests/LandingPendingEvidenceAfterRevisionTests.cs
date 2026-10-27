using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case owns its kernel, registry and temporary workspace.
public sealed class LandingPendingEvidenceAfterRevisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevisedCriteria_LegacyGateWait_DropsHoldAndNewWaitHolds(bool typed)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-revised-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var registry = new OrchestratorProjectRegistry(Path.Combine(root, "registry"), Path.Combine(root, "data"));
            var workspace = OrchestratorWorkspace.ForDirectory(root, null, null, registry);
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Original brief.");
            kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Original behavior", ["Original criterion."],
                VerificationClass.TestVerifiable, [], []));
            var legacy = Raise(kernel, goal, null);
            Assert.Null(legacy.PlannerIdentity);
            Assert.Contains(legacy, kernel.GetPendingHumanInput(goal.Id));
            var before = LandingExecutor.EvaluatePendingHumanInput(kernel, goal, workspace, null);
            Assert.NotNull(before.HoldDescription);
            Assert.Contains(legacy.Id.Value, before.HoldDescription);

            kernel.ReviseGoalBrief(goal.Id,
                "Revised brief.\n\n## Acceptance criteria\n\n1. Revised criterion.", "New evidence required");

            var after = LandingExecutor.EvaluatePendingHumanInput(kernel, goal, workspace, null);
            Assert.True(legacy.IsCompleted);
            Assert.Null(legacy.AuthoritativeAnswer);
            Assert.Null(after.HoldDescription);
            Assert.Empty(after.ObligationLines);
            var current = Raise(kernel, goal, typed ? goal.AuthoritativeBrief.Version : null);
            Assert.NotEqual(legacy.Id, current.Id);
            Assert.False(current.IsCompleted);
            var held = LandingExecutor.EvaluatePendingHumanInput(kernel, goal, workspace, null);
            Assert.NotNull(held.HoldDescription);
            Assert.Contains(current.Id.Value, held.HoldDescription);
            Assert.DoesNotContain(legacy.Id.Value, held.HoldDescription);
            Assert.Equal(current.Id, Assert.Single(kernel.GetPendingHumanInput(goal.Id)).Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static HumanInputRequest Raise(AgentOrchestratorKernel kernel, Goal goal, int? version) =>
        kernel.RequestHumanInputDeduplicated(goal.Id, null, "Run the candidate evidence class",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: "acceptance-gate",
            testNames: ["CandidateEvidenceTests"],
            plannerIdentity: version is { } number ? new(goal.Id.Value, "planner-task", number, 1, "candidate") : null,
            plannerEvidence: new(1, "candidate", "post-implementation", "acceptance-gate",
                "Run the candidate evidence class", "Candidate evidence required", null, null, ["CandidateEvidenceTests"])).Request;
}
