using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerContextArtifactsVerificationTests
{
    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_directs_developers_to_run_worker_build_check")]
    public void WorkerContextArtifactsDirectsDevelopersToRunWorkerBuildCheck()
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
        Xunit.Assert.Contains(".\\scripts\\Invoke-WorkerBuildCheck.ps1 <project.csproj> [project.csproj...]", currentTask);
        Xunit.Assert.Contains("for every project whose sources you changed", currentTask);
        Xunit.Assert.Contains("tests: build: 0 errors (Invoke-WorkerBuildCheck)", currentTask);
        Xunit.Assert.Contains("Do not run raw `dotnet test`", currentTask);
        Xunit.Assert.Contains("raw test execution can create per-worktree testhost firewall prompts", currentTask);
    }
}
