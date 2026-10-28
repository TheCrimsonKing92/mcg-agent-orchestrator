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

    [Xunit.Theory(DisplayName = "TargetToolchainDetector_detects_dotnet_from_root_level_marker")]
    [Xunit.InlineData("MyApp.csproj")]
    [Xunit.InlineData("MyApp.fsproj")]
    [Xunit.InlineData("MyApp.vbproj")]
    [Xunit.InlineData("MyApp.slnx")]
    public void TargetToolchainDetectorDetectsDotnetFromRootLevelMarker(string marker)
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, marker), "");

        var result = TargetToolchainDetector.Detect(root);

        Xunit.Assert.Equal(Toolchain.Dotnet, result);
    }

    [Xunit.Fact(DisplayName = "TargetToolchainDetector_prefers_go_over_root_csproj")]
    public void TargetToolchainDetectorPrefersGoOverRootCsproj()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/myapp\n\ngo 1.21\n");
        File.WriteAllText(Path.Combine(root, "MyApp.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var result = TargetToolchainDetector.Detect(root);

        Xunit.Assert.Equal(Toolchain.Go, result);
    }

    [Xunit.Fact(DisplayName = "TargetToolchainDetector_prefers_node_over_root_csproj")]
    public void TargetToolchainDetectorPrefersNodeOverRootCsproj()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "package.json"), "{\"name\":\"my-app\"}");
        File.WriteAllText(Path.Combine(root, "MyApp.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var result = TargetToolchainDetector.Detect(root);

        Xunit.Assert.Equal(Toolchain.Node, result);
    }

    [Xunit.Fact(DisplayName = "TargetToolchainDetector_ignores_root_files_that_are_not_project_markers")]
    public void TargetToolchainDetectorIgnoresRootFilesThatAreNotProjectMarkers()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(root, "MyApp.csproj.user"), "<Project />");
        File.WriteAllText(Path.Combine(root, "global.json"), "{}");
        File.WriteAllText(Path.Combine(root, "README.md"), "# MyApp");

        var result = TargetToolchainDetector.Detect(root);

        Xunit.Assert.Equal(Toolchain.Unknown, result);
    }

    [Xunit.Fact(DisplayName = "TargetToolchainDetector_keeps_two_level_depth_bound")]
    public void TargetToolchainDetectorKeepsTwoLevelDepthBound()
    {
        var root = CreateTempDirectory();
        var deep = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "Deep.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var result = TargetToolchainDetector.Detect(root);

        Xunit.Assert.Equal(Toolchain.Unknown, result);
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

    [Xunit.Theory]
    [Xunit.InlineData("package.json", AgentRole.Developer, "npm test (or package.json test script)")]
    [Xunit.InlineData("pyproject.toml", AgentRole.Developer, "python -m pytest (or project test command)")]
    [Xunit.InlineData("", AgentRole.Developer, "run the project's test suite")]
    [Xunit.InlineData("go.mod", AgentRole.Planner, "go test ./...")]
    [Xunit.InlineData("go.mod", AgentRole.Researcher, "go test ./...")]
    public void WorkflowBrokers_OtherStacksAndRoles_PreserveChecklistGuidance(
        string marker, AgentRole role, string command)
    {
        var root = CreateTempDirectory();
        try
        {
            if (marker.Length > 0)
                File.WriteAllText(Path.Combine(root, marker), string.Empty);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Inspect broker instructions.", role);
            var goal = kernel.CreateGoal("Maintain broker guidance.", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var context = WorkerContextArtifacts.Write(goal, task, root);
            var broker = File.ReadAllText(Path.Combine(context, "workflow-brokers.md"));

            Xunit.Assert.Contains("- build-test-selection", broker, StringComparison.Ordinal);
            Xunit.Assert.Contains($"  Suggested command shape: `{command}`", broker, StringComparison.Ordinal);
            Xunit.Assert.Contains("- static-policy-checks", broker, StringComparison.Ordinal);
            Xunit.Assert.Contains("- acceptance-evidence", broker, StringComparison.Ordinal);
            if (role == AgentRole.Developer)
            {
                Xunit.Assert.Contains("  Artifact: current-task.md plus source-survey.md", broker, StringComparison.Ordinal);
                Xunit.Assert.Contains("  Artifact: source-survey.md plus diff-summary.md", broker, StringComparison.Ordinal);
                Xunit.Assert.Contains("  Artifact: current-task.md plus diff-summary.md", broker, StringComparison.Ordinal);
                Xunit.Assert.DoesNotContain("deterministic-verification.md", broker, StringComparison.Ordinal);
                Xunit.Assert.Contains("Local command-result reporting does not authorize a formal acceptance verdict", broker, StringComparison.Ordinal);
            }
            else
            {
                Xunit.Assert.Contains("  Artifact: deterministic-verification.md", broker, StringComparison.Ordinal);
                Xunit.Assert.Contains("  Artifact: deterministic-verification.md plus current-task.md", broker, StringComparison.Ordinal);
                Xunit.Assert.DoesNotContain("Local command-result reporting", broker, StringComparison.Ordinal);
            }
            Xunit.Assert.DoesNotContain("- build-only", broker, StringComparison.Ordinal);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
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
        var invocation = OrchestratorBuildEvidenceCheck.FormatScriptInvocation(OrchestratorBuildEvidenceCheck.ResolveScriptPath());
        Xunit.Assert.Contains(invocation + " <project.csproj> [project.csproj...]", verification, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(@".\scripts\Invoke-WorkerBuildCheck.ps1", verification, StringComparison.Ordinal);
        Xunit.Assert.Contains("for every project whose sources they changed", verification, StringComparison.Ordinal);
        Xunit.Assert.Contains("tests: pass - build: 0 errors (Invoke-WorkerBuildCheck)", verification, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Compiling every changed project is required through `{invocation}`", verification, StringComparison.Ordinal);
        Xunit.Assert.Contains("subscription workers must not run raw `dotnet test`", verification, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("raw `dotnet build`", verification, StringComparison.Ordinal);
        Xunit.Assert.Contains("does not run tests or spawn testhost", verification, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_deterministic_verification_requires_worker_build_check_for_root_only_csproj")]
    public void WorkerContextArtifactsDeterministicVerificationRequiresWorkerBuildCheckForRootOnlyCsproj()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "MyApp.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../something");

        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Fix tests in src/MyApp/MyApp.cs");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);

        var contextDir = WorkerContextArtifacts.Write(goal, task, root);
        var verification = File.ReadAllText(Path.Combine(contextDir, "deterministic-verification.md"));

        Xunit.Assert.Contains("## Worker Build Check", verification, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Run the project's standard build and test commands.", verification, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_workflow_brokers_uses_build_only_for_root_only_csproj")]
    public void WorkerContextArtifactsWorkflowBrokersUsesBuildOnlyForRootOnlyCsproj()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "MyApp.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../something");

        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Fix tests in src/MyApp/MyApp.cs");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);

        var contextDir = WorkerContextArtifacts.Write(goal, task, root);
        var brokers = File.ReadAllText(Path.Combine(contextDir, "workflow-brokers.md"));

        Xunit.Assert.Contains("- build-only", brokers.ReplaceLineEndings("\n").Split('\n'));
        Xunit.Assert.DoesNotContain("run the project's test suite", brokers, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "GoalObjectivePlanner_required_tools_emits_managed_test_helper_for_cs_file_objective")]
    public void GoalObjectivePlannerRequiredToolsEmitsManagedTestHelperForCsFileObjective()
    {
        var plan = GoalObjectivePlanner.Build(
            "Implement src/MyApp/Handler.cs with tests/MyApp.Tests/HandlerTests.cs coverage",
            simple: true);

        Xunit.Assert.Contains(plan.RequiredTools,
            item => item.Contains("Invoke-TestSummary", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(plan.RequiredTools,
            item => item.Contains("dotnet test", StringComparison.OrdinalIgnoreCase));
    }
}
