using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ToolchainDetectionTests
{
    [Xunit.Fact(DisplayName = "TargetToolchainDetector_detects_go_from_go_mod")]
    public void TargetToolchainDetectorDetectsGoFromGoMod()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/myapp\n\ngo 1.21\n");

        var result = TargetToolchainDetector.Detect(root);

        Xunit.Assert.Equal(Toolchain.Go, result);
    }

    [Xunit.Fact(DisplayName = "TargetToolchainDetector_detects_node_from_package_json")]
    public void TargetToolchainDetectorDetectsNodeFromPackageJson()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "package.json"), "{\"name\":\"my-app\"}");

        var result = TargetToolchainDetector.Detect(root);

        Xunit.Assert.Equal(Toolchain.Node, result);
    }

    [Xunit.Fact(DisplayName = "TargetToolchainDetector_detects_dotnet_from_sln")]
    public void TargetToolchainDetectorDetectsDotnetFromSln()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "MyApp.sln"), "");

        var result = TargetToolchainDetector.Detect(root);

        Xunit.Assert.Equal(Toolchain.Dotnet, result);
    }

    [Xunit.Fact(DisplayName = "TargetToolchainDetector_detects_dotnet_from_csproj_in_subdirectory")]
    public void TargetToolchainDetectorDetectsDotnetFromCsprojInSubdirectory()
    {
        var root = CreateTempDirectory();
        var src = Path.Combine(root, "src", "MyApp");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "MyApp.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var result = TargetToolchainDetector.Detect(root);

        Xunit.Assert.Equal(Toolchain.Dotnet, result);
    }

    [Xunit.Fact(DisplayName = "TargetToolchainDetector_detects_python_from_pyproject_toml")]
    public void TargetToolchainDetectorDetectsPythonFromPyprojectToml()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[tool.poetry]\nname = \"myapp\"\n");

        var result = TargetToolchainDetector.Detect(root);

        Xunit.Assert.Equal(Toolchain.Python, result);
    }

    [Xunit.Fact(DisplayName = "TargetToolchainDetector_detects_python_from_requirements_txt")]
    public void TargetToolchainDetectorDetectsPythonFromRequirementsTxt()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "requirements.txt"), "requests\nflask\n");

        var result = TargetToolchainDetector.Detect(root);

        Xunit.Assert.Equal(Toolchain.Python, result);
    }

    [Xunit.Fact(DisplayName = "TargetToolchainDetector_returns_unknown_for_empty_directory")]
    public void TargetToolchainDetectorReturnsUnknownForEmptyDirectory()
    {
        var root = CreateTempDirectory();

        var result = TargetToolchainDetector.Detect(root);

        Xunit.Assert.Equal(Toolchain.Unknown, result);
    }

    [Xunit.Fact(DisplayName = "TargetToolchainDetector_prefers_go_over_node_when_both_present")]
    public void TargetToolchainDetectorPrefersGoOverNodeWhenBothPresent()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/myapp\n\ngo 1.21\n");
        File.WriteAllText(Path.Combine(root, "package.json"), "{\"name\":\"my-app\"}");

        var result = TargetToolchainDetector.Detect(root);

        Xunit.Assert.Equal(Toolchain.Go, result);
    }

    [Xunit.Fact(DisplayName = "TargetToolchainDetector_go_extensions_include_dot_go")]
    public void TargetToolchainDetectorGoExtensionsIncludeDotGo()
    {
        var extensions = TargetToolchainDetector.GetSourceExtensions(Toolchain.Go);

        Xunit.Assert.Contains(".go", extensions);
    }

    [Xunit.Fact(DisplayName = "TargetToolchainDetector_dotnet_extensions_include_cs_and_csproj")]
    public void TargetToolchainDetectorDotnetExtensionsIncludeCsAndCsproj()
    {
        var extensions = TargetToolchainDetector.GetSourceExtensions(Toolchain.Dotnet);

        Xunit.Assert.Contains(".cs", extensions);
        Xunit.Assert.Contains(".csproj", extensions);
        Xunit.Assert.Contains(".sln", extensions);
    }

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_source_survey_indexes_go_files_for_go_repo")]
    public void WorkerContextArtifactsSourceSurveyIndexesGoFilesForGoRepo()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/myapp\n\ngo 1.21\n");
        var cmdDir = Path.Combine(root, "cmd", "server");
        Directory.CreateDirectory(cmdDir);
        File.WriteAllText(Path.Combine(cmdDir, "main.go"), "package main\n\nfunc main() {}\n");
        var internalDir = Path.Combine(root, "internal", "handler");
        Directory.CreateDirectory(internalDir);
        File.WriteAllText(Path.Combine(internalDir, "handler.go"), "package handler\n");
        File.WriteAllText(Path.Combine(internalDir, "handler_test.go"), "package handler\n");
        // This .cs file should NOT appear since toolchain is Go
        File.WriteAllText(Path.Combine(root, "stray.cs"), "class Stray {}");

        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Fix the handler in cmd/server/main.go");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);

        var contextDir = WorkerContextArtifacts.Write(goal, task, root);
        var survey = File.ReadAllText(Path.Combine(contextDir, "source-survey.md"));

        Xunit.Assert.Contains("cmd/server/main.go", survey, StringComparison.Ordinal);
        Xunit.Assert.Contains("internal/handler/handler.go", survey, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("stray.cs", survey, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_deterministic_verification_uses_go_commands_for_go_repo")]
    public void WorkerContextArtifactsDeterministicVerificationUsesGoCommandsForGoRepo()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/myapp\n\ngo 1.21\n");
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../something");

        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Fix tests in the go package");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);

        var contextDir = WorkerContextArtifacts.Write(goal, task, root);
        var verification = File.ReadAllText(Path.Combine(contextDir, "deterministic-verification.md"));

        Xunit.Assert.Contains("go test", verification, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Invoke-IsolatedDotnet", verification, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("scripts/Invoke-IsolatedDotnet", verification, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_workflow_brokers_uses_go_commands_for_go_repo")]
    public void WorkerContextArtifactsWorkflowBrokersUsesGoCommandsForGoRepo()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/myapp\n\ngo 1.21\n");
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../something");

        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Add unit tests to the Go handler package");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);

        var contextDir = WorkerContextArtifacts.Write(goal, task, root);
        var brokers = File.ReadAllText(Path.Combine(contextDir, "workflow-brokers.md"));

        Xunit.Assert.Contains("go test", brokers, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Invoke-IsolatedDotnet", brokers, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_deterministic_verification_requires_worker_build_check")]
    public void WorkerContextArtifactsDeterministicVerificationRequiresWorkerBuildCheck()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "MyApp.sln"), "");
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../something");

        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Fix tests in src/MyApp/MyApp.cs");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);

        var contextDir = WorkerContextArtifacts.Write(goal, task, root);
        var verification = File.ReadAllText(Path.Combine(contextDir, "deterministic-verification.md"));

        Xunit.Assert.Contains("## Worker Build Check", verification, StringComparison.Ordinal);
        Xunit.Assert.Contains(".\\scripts\\Invoke-WorkerBuildCheck.ps1 <project.csproj> [project.csproj...]", verification, StringComparison.Ordinal);
        Xunit.Assert.Contains("for every project whose sources they changed", verification, StringComparison.Ordinal);
        Xunit.Assert.Contains("tests: pass - build: 0 errors (Invoke-WorkerBuildCheck)", verification, StringComparison.Ordinal);
        Xunit.Assert.Contains("Subscription workers must not run raw `dotnet test`", verification, StringComparison.Ordinal);
        Xunit.Assert.Contains("does not run tests or spawn testhost", verification, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalObjectivePlanner_required_tools_does_not_leak_dotnet_script_for_generic_build_goal")]
    public void GoalObjectivePlannerRequiredToolsDoesNotLeakDotnetScriptForGenericBuildGoal()
    {
        var plan = GoalObjectivePlanner.Build(
            "Fix the build failure and add unit tests for the handler package",
            simple: false);

        Xunit.Assert.DoesNotContain(plan.RequiredTools,
            item => item.Contains("scripts/Invoke-IsolatedDotnet", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "GoalObjectivePlanner_required_tools_emits_go_command_for_go_objective")]
    public void GoalObjectivePlannerRequiredToolsEmitsGoCommandForGoObjective()
    {
        var plan = GoalObjectivePlanner.Build(
            "Fix the golang build in cmd/server/main.go",
            simple: false);

        Xunit.Assert.Contains(plan.RequiredTools,
            item => item.Contains("go", StringComparison.OrdinalIgnoreCase) && item.Contains("test", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "GoalObjectivePlanner_required_tools_emits_dotnet_command_for_cs_file_objective")]
    public void GoalObjectivePlannerRequiredToolsEmitsDotnetCommandForCsFileObjective()
    {
        var plan = GoalObjectivePlanner.Build(
            "Implement src/MyApp/Handler.cs with tests/MyApp.Tests/HandlerTests.cs coverage",
            simple: true);

        Xunit.Assert.Contains(plan.RequiredTools,
            item => item.Contains("Invoke-IsolatedDotnet", StringComparison.Ordinal));
    }
}
