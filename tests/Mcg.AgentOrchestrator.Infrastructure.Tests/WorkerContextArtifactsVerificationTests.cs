using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerContextArtifactsVerificationTests
{
    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_warns_developers_not_to_run_dotnet_build_or_test")]
    public void WorkerContextArtifactsWarnsDevelopersNotToRunDotnetBuildOrTest()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement the change.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Context warning", [task]);

        var contextDirectory = WorkerContextArtifacts.Write(goal, task, root);

        var currentTask = File.ReadAllText(Path.Combine(contextDirectory, "current-task.md"));
        var doNotRunIndex = currentTask.IndexOf("## DO NOT RUN", StringComparison.Ordinal);
        var descriptionIndex = currentTask.IndexOf("## Description", StringComparison.Ordinal);
        Xunit.Assert.True(doNotRunIndex >= 0);
        Xunit.Assert.True(doNotRunIndex < descriptionIndex);
        Xunit.Assert.Contains("Do not run `dotnet test` or `dotnet build` yourself.", currentTask);
        Xunit.Assert.Contains("orchestrator acceptance gate performs all build/test verification", currentTask);
    }
}
