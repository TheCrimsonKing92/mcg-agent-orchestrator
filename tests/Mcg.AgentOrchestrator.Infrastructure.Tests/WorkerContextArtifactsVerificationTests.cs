using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerContextArtifactsVerificationTests
{
    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_directs_developers_to_skip_dotnet_self_verify")]
    public void WorkerContextArtifactsDirectsDevelopersToSkipDotnetSelfVerify()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement the change.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Context warning", [task]);

        var contextDirectory = WorkerContextArtifacts.Write(goal, task, root);

        var currentTask = File.ReadAllText(Path.Combine(contextDirectory, "current-task.md"));
        var verificationIndex = currentTask.IndexOf("## Build/Test Verification", StringComparison.Ordinal);
        var descriptionIndex = currentTask.IndexOf("## Description", StringComparison.Ordinal);
        Xunit.Assert.True(verificationIndex >= 0);
        Xunit.Assert.True(verificationIndex < descriptionIndex);
        Xunit.Assert.Contains("Do not run raw `dotnet test` or `dotnet build` directly", currentTask);
        Xunit.Assert.Contains("Do not run `.\\scripts\\Invoke-IsolatedDotnet.ps1` from a subscription worker either", currentTask);
        Xunit.Assert.Contains("tests: not-run - orchestrator acceptance gate verifies via stable slots", currentTask);
    }
}
