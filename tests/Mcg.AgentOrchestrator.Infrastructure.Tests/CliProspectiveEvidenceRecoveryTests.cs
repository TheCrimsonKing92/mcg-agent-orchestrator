using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliProspectiveEvidenceRecoveryTests : CliCommandTestBase
{
    [Xunit.Fact]
    public void RecoverPreservesProspectiveAcceptanceEvidence()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
        var goal = kernel.CreateGoal("Recover work without fabricating future evidence", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var prospective = kernel.RequestHumanInput(
            goal.Id,
            task.Id,
            "Observe the candidate after implementation.",
            HumanWaitKind.ProspectiveAcceptanceEvidence);

        var output = ExecuteCliAndCapture(
            ["recover", goal.Id.Value[..8], "recover implementation state only"],
            kernel,
            workspace);

        Xunit.Assert.False(prospective.IsCompleted);
        Xunit.Assert.DoesNotContain("answered human-input request", output, StringComparison.Ordinal);
        Xunit.Assert.Single(kernel.GetPendingHumanInput(goal.Id));
    }
}
