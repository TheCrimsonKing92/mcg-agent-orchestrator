using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerBuildGuidanceContractTests
{
    [Xunit.Fact(DisplayName = "Selected_build_hygiene_skill_defaults_to_worker_build_helper")]
    public void SelectedBuildHygieneSkillDefaultsToWorkerBuildHelper()
    {
        var repositoryRoot = FindRepositoryRoot();
        var skill = File.ReadAllText(Path.Combine(repositoryRoot, ".agents", "skills", "dotnet-windows-build-hygiene", "SKILL.md"));

        Assert.Contains(@".\scripts\Invoke-WorkerBuildCheck.ps1", skill, StringComparison.Ordinal);
        Assert.Contains(@".\scripts\Invoke-WorkerBuildDiagnostic.ps1", skill, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"(?im)^\s*-\s+(?:run|use|build)\s+`(?:\.\\)?dotnet build\b", RegexOptions.CultureInvariant),
            skill);
    }

    [Xunit.Theory(DisplayName = "Developer_and_Tester_selected_guidance_names_worker_build_helper")]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void DeveloperAndTesterSelectedGuidanceNamesWorkerBuildHelper(AgentRole role)
    {
        var repositoryRoot = FindRepositoryRoot();
        var task = new TaskSpec(TaskId.New(), "Implement and verify the scoped .NET change.", role, "Run focused verification.");
        var goal = new AgentOrchestratorKernel().CreateGoal(
            new GoalId($"guidance-{role.ToString().ToLowerInvariant()}"),
            "Keep worker build output bounded.",
            [task]);

        var selected = new WorkerSkillSelector().BuildSelectedSkills(goal, task, repositoryRoot);

        Assert.Contains("Status: available", selected, StringComparison.Ordinal);
        Assert.Contains("Usage: Use Invoke-WorkerBuildCheck.ps1 for normal .NET builds", selected, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"(?im)^\s*Usage:\s*(?:run|use|build)\s+(?:\.\\)?dotnet build\b", RegexOptions.CultureInvariant),
            selected);
    }

    [Xunit.Fact(DisplayName = "Generated_worker_task_guidance_names_only_sanctioned_build_helper")]
    public void GeneratedWorkerTaskGuidanceNamesOnlySanctionedBuildHelper()
    {
        var repositoryRoot = FindRepositoryRoot();
        var task = new TaskSpec(TaskId.New(), "Implement the scoped .NET change.", AgentRole.Developer, "Run focused verification.");
        var method = typeof(WorkerArtifactWriter).GetMethod(
            "BuildCurrentTask",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(WorkerArtifactWriter).FullName, "BuildCurrentTask");

        var guidance = Assert.IsType<string>(method.Invoke(null, [task, repositoryRoot, false]));

        Assert.Contains(@".\scripts\Invoke-WorkerBuildCheck.ps1", guidance, StringComparison.Ordinal);
        Assert.Contains("only sanctioned worker-side .NET build check", guidance, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"(?im)^\s*-\s+(?:run|use|build)\s+`(?:\.\\)?dotnet build\b", RegexOptions.CultureInvariant),
            guidance);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
