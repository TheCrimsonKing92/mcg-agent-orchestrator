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
        Xunit.Assert.Contains("tests: pass - build: 0 errors (Invoke-WorkerBuildCheck)", currentTask);
        Xunit.Assert.DoesNotContain("tests: build: 0 errors (Invoke-WorkerBuildCheck)", currentTask);
        Xunit.Assert.Contains("Do not run raw `dotnet test`", currentTask);
        Xunit.Assert.Contains("Compiling every changed project is required", currentTask);
        Xunit.Assert.Contains("tests belong to the acceptance gate", currentTask);
        Xunit.Assert.DoesNotContain("testhost firewall prompts", currentTask);
    }

    [Xunit.Theory(DisplayName = "WorkerContextArtifacts_role_worker_result_contract_contains_guardrail_fields")]
    [Xunit.InlineData(AgentRole.Researcher)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void WorkerContextArtifactsRoleWorkerResultContractContainsGuardrailFields(AgentRole role)
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Report role-specific output.", role);
        var goal = kernel.CreateGoal("Context role output contract", [task]);

        var contextDirectory = WorkerContextArtifacts.Write(goal, task, root);

        var currentTask = File.ReadAllText(Path.Combine(contextDirectory, "current-task.md"));
        foreach (var field in AgentOutputDirectives.RequiredWorkerResultFieldNamesForRole(role))
        {
            Xunit.Assert.Contains($"{field}:", currentTask);
        }
    }
}
