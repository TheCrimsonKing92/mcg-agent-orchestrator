namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class RepositoryChangeClassifierTests
{
    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_allows_candidate_lane_timeout_and_invocation_argument_changes")]
    public void RepositoryChangeClassifierAllowsCandidateLaneTimeoutAndInvocationArgumentChanges()
    {
        const string trusted = """
            {
              "engine": {
                "infrastructureTestLanes": [{ "name": "one", "filter": "A" }],
                "timeouts": { "defaultMinutes": 25 },
                "mtpInvocations": [{
                  "project": "tests/A.csproj",
                  "executablePathTemplate": "bin/{projectName}.exe",
                  "firewallExecutablePathTemplate": "bin/{projectName}.exe",
                  "arguments": ["{executable}", "--old"]
                }]
              }
            }
            """;
        const string candidate = """
            {
              "engine": {
                "infrastructureTestLanes": [{ "name": "two", "filter": "B" }],
                "timeouts": { "defaultMinutes": 5 },
                "mtpInvocations": [{
                  "project": "tests/A.csproj",
                  "executablePathTemplate": "bin/{projectName}.exe",
                  "firewallExecutablePathTemplate": "bin/{projectName}.exe",
                  "arguments": ["{executable}", "--new"]
                }]
              }
            }
            """;

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

        Assert.False(decision.RequiresTrustedReview);
        Assert.Empty(decision.SecurityCriticalChanges);
        Assert.StartsWith("positive evidence:", decision.Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_routes_manifest_executable_or_firewall_path_changes_to_trusted_review")]
    public void RepositoryChangeClassifierRoutesManifestExecutableOrFirewallPathChangesToTrustedReview()
    {
        const string trusted = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "executablePathTemplate": "bin/A.exe",
              "firewallExecutablePathTemplate": "bin/A.exe"
            }] } }
            """;
        const string candidate = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "executablePathTemplate": "../candidate.exe",
              "firewallExecutablePathTemplate": "../candidate.exe"
            }] } }
            """;

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

        Assert.True(decision.RequiresTrustedReview);
        Assert.Equal(2, decision.SecurityCriticalChanges.Count);
        Assert.Contains("trusted review required", decision.Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_routes_structural_coverage_disable_to_trusted_review")]
    public void RepositoryChangeClassifierRoutesStructuralCoverageDisableToTrustedReview()
    {
        const string trusted = """{ "engine": { "enforceStructuralCoverage": true } }""";
        const string candidate = """{ "engine": { "enforceStructuralCoverage": false } }""";

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

        Assert.True(decision.RequiresTrustedReview);
        Assert.Contains("engine.enforceStructuralCoverage", decision.SecurityCriticalChanges);
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_case_alias_matches_case_sensitive_engine_interpreter")]
    public void RepositoryChangeClassifierCaseAliasMatchesCaseSensitiveEngineInterpreter()
    {
        const string trusted = """{ "engine": { "enforceStructuralCoverage": true } }""";
        const string candidate =
            """{ "engine": { "enforceStructuralCoverage": true, "EnforceStructuralCoverage": false } }""";

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

        Assert.False(decision.RequiresTrustedReview);
        Assert.Empty(decision.SecurityCriticalChanges);
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_routes_MTP_command_token_change_to_trusted_review")]
    public void RepositoryChangeClassifierRoutesMtpCommandTokenChangeToTrustedReview()
    {
        const string trusted = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "arguments": ["{executable}", "--old"]
            }] } }
            """;
        const string candidate = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "arguments": ["candidate.exe", "{executable}"]
            }] } }
            """;

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

        Assert.True(decision.RequiresTrustedReview);
        Assert.Contains("engine.mtpInvocations[].arguments[0]", decision.SecurityCriticalChanges);
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_identifies_docs_only_changes")]
    public void RepositoryChangeClassifierIdentifiesDocsOnlyChanges()
    {
        var summary = RepositoryChangeClassifier.Classify([
            "README.md",
            "docs/usage.md"
        ]);

        Assert.True(summary.IsDocsOnly);
        Assert.False(summary.HasBehaviorChanges);
        Assert.False(summary.RequiresBroadVerification);
        Assert.True(summary.RecommendedVerification.Contains("No build required", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_flags_generated_and_shared_infrastructure_changes")]
    public void RepositoryChangeClassifierFlagsGeneratedAndSharedInfrastructureChanges()
    {
        var summary = RepositoryChangeClassifier.Classify([
            "src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/bin/Debug/generated.dll",
            "Directory.Build.props"
        ]);

        Assert.True(summary.HasGeneratedArtifacts);
        Assert.True(summary.HasBuildSystemChanges);
        Assert.True(summary.HasBehaviorChanges);
        Assert.True(summary.RequiresBroadVerification);
        Assert.Contains(summary.Files, file => file.Path == "src/Mcg.AgentOrchestrator.Infrastructure/bin/Debug/generated.dll" &&
            file.IsGeneratedArtifact);
        Assert.True(summary.RecommendedVerification.Contains("Remove generated artifacts", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_flags_policy_sensitive_configuration")]
    public void RepositoryChangeClassifierFlagsPolicySensitiveConfiguration()
    {
        var summary = RepositoryChangeClassifier.Classify([
            "src/Mcg.AgentOrchestrator.App/Dashboard/Api/AuthPolicy.cs",
            "config/provider-policy.json"
        ]);

        Assert.True(summary.HasSecuritySensitiveChanges);
        Assert.True(summary.RequiresBroadVerification);
        Assert.Contains(summary.Files, file => file.IsSecuritySensitive);
    }

    [Xunit.Theory(DisplayName = "RepositoryChangeClassifier_identifies_conductor_relaunch_changes")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure/Persistence/ModelFunctionCatalogStore.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure/Persistence/AgentCatalogStore.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Application/TaskComplexityEstimator.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/LandingExecutor.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/GoalRefinementGate.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/DispatchReadinessEvaluator.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/OrchestratorEntityResolver.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Dashboard/Api/GoalManagementCommandService.Dispatches.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/SubscriptionPlanning/SubscriptionPlanBuilder.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Application/VerificationPolicyCompiler.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Providers/ProviderRegistryFactory.cs")]
    [Xunit.InlineData("config/acceptance-manifest.json")]
    [Xunit.InlineData("Directory.Build.props")]
    [Xunit.InlineData("Directory.Build.rsp")]
    public void RepositoryChangeClassifierIdentifiesConductorRelaunchChanges(string path)
    {
        var summary = RepositoryChangeClassifier.Classify([path]);

        Assert.True(summary.RequiresConductorRelaunch);
    }

    [Xunit.Theory(DisplayName = "RepositoryChangeClassifier_does_not_infer_relaunch_from_similar_non_runtime_paths")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LandingExecutorTests.cs")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderRegistryFactoryTests.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/BuildStatusRenderer.cs")]
    public void RepositoryChangeClassifierDoesNotInferRelaunchFromSimilarNonRuntimePaths(string path)
    {
        var summary = RepositoryChangeClassifier.Classify([path]);

        Assert.False(summary.RequiresConductorRelaunch);
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_does_not_relaunch_for_non_infrastructure_changes")]
    public void RepositoryChangeClassifierDoesNotRelaunchForNonInfrastructureChanges()
    {
        var summary = RepositoryChangeClassifier.Classify([
            "docs/operator.md",
            "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.cs"
        ]);

        Assert.False(summary.RequiresConductorRelaunch);
    }

    [Xunit.Fact(DisplayName = "RepositoryOwnershipMap_classifies_high_risk_generated_dashboard_tests_and_docs")]
    public void RepositoryOwnershipMapClassifiesHighRiskGeneratedDashboardTestsAndDocs()
    {
        var guard = RepositoryOwnershipMap.GuardWriteSet([
            "src/Mcg.AgentOrchestrator.Core/Application/ParallelExecutionPlanner.cs",
            "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.cs",
            "tests/Mcg.AgentOrchestrator.Core.Tests/ParallelExecutionPlannerTests.cs",
            "docs/operator.md",
            "src/Mcg.AgentOrchestrator.App/bin/Debug/generated.dll"
        ]);

        Assert.Contains(guard.Paths, path => path.Area == RepositoryOwnershipArea.SharedInfrastructure && path.IsHighRisk);
        Assert.Contains(guard.Paths, path => path.Area == RepositoryOwnershipArea.DashboardUi && path.RequiresSerialization);
        Assert.Contains(guard.Paths, path => path.Area == RepositoryOwnershipArea.Test);
        Assert.Contains(guard.Paths, path => path.Area == RepositoryOwnershipArea.Documentation);
        Assert.Contains(guard.Paths, path => path.Area == RepositoryOwnershipArea.GeneratedOrNoisy && path.IsGeneratedOrNoisy);
        Assert.True(guard.RequiresOperatorApproval);
        Assert.Contains(guard.RequiredResources, resource => resource == "ownership:shared-infrastructure");
        Assert.Contains(guard.RequiredResources, resource => resource == "ownership:dashboard-ui");
        Assert.Contains(guard.Reasons, reason => reason.Contains("generated/noisy path", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_skips_build_for_docs_only_changes")]
    public void RepositoryTestImpactPlannerSkipsBuildForDocsOnlyChanges()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "README.md",
            "docs/operator.md"
        ]);

        Assert.False(plan.RequiresBuild);
        Assert.False(plan.RequiresBroadVerification);
        Assert.Single(plan.Checks);
        Assert.Empty(plan.Checks[0].Command);
        Assert.True(plan.Summary.Contains("Documentation-only", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_selects_infrastructure_tests_for_infrastructure_changes")]
    public void RepositoryTestImpactPlannerSelectsInfrastructureTestsForInfrastructureChanges()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs"
        ]);

        Assert.True(plan.RequiresBuild);
        var check = Assert.Single(plan.Checks);
        Assert.Equal("infrastructure tests", check.Name);
        Assert.True(check.Command.Any(argument => argument.Equals("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", StringComparison.Ordinal)));
        Assert.False(check.Command.Any(argument => argument.Equals("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_selects_focused_cli_filter_for_cli_only_changes")]
    public void RepositoryTestImpactPlannerSelectsFocusedCliFilterForCliOnlyChanges()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.Tasks.cs"
        ]);

        Assert.True(plan.RequiresBuild);
        Assert.False(plan.RequiresBroadVerification);
        var check = Assert.Single(plan.Checks);
        Assert.Equal("focused CLI infrastructure tests", check.Name);
        Assert.Contains("--filter", check.Command);
        Assert.Contains(check.Command, argument => argument.Contains("CliHelpTests", StringComparison.Ordinal));
        Assert.Contains(check.Command, argument => argument.Contains("CliCommandTests", StringComparison.Ordinal));
        Assert.False(check.Command.Any(argument => argument.Contains("FundamentalAliasTests", StringComparison.Ordinal)));
        Assert.False(check.Command.Any(argument => argument.Contains("Mcg.AgentOrchestrator.sln", StringComparison.OrdinalIgnoreCase)));
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_selects_focused_dashboard_filter_for_dashboard_only_changes")]
    public void RepositoryTestImpactPlannerSelectsFocusedDashboardFilterForDashboardOnlyChanges()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.OperatorShell.cs"
        ]);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("focused dashboard infrastructure tests", check.Name);
        Assert.Contains("--filter", check.Command);
        Assert.Contains(check.Command, argument => argument.Contains("DashboardRenderingTests", StringComparison.Ordinal));
        Assert.Contains(check.Command, argument => argument.Contains("DashboardHostTests", StringComparison.Ordinal));
        Assert.Contains(check.Command, argument => argument.Contains("Category!=HostIntegration", StringComparison.Ordinal));
        Assert.Contains(check.Command, argument => argument.Contains("DashboardValidationHarnessTests", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_falls_back_to_full_infrastructure_tests_for_shared_infrastructure")]
    public void RepositoryTestImpactPlannerFallsBackToFullInfrastructureTestsForSharedInfrastructure()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"
        ]);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("infrastructure tests", check.Name);
        Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", check.Command);
        Assert.DoesNotContain("--filter", check.Command);
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_selects_union_filter_for_multiple_mapped_app_subsystems")]
    public void RepositoryTestImpactPlannerSelectsUnionFilterForMultipleMappedAppSubsystems()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.Tasks.cs",
            "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.OperatorShell.cs"
        ]);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("focused CLI+dashboard infrastructure tests", check.Name);
        Assert.Contains("--filter", check.Command);
        var filterIndex = Array.FindIndex(check.Command.ToArray(), argument => argument == "--filter");
        Assert.True(filterIndex >= 0);
        Assert.Equal(
            "(FullyQualifiedName~CliCommandTests|FullyQualifiedName~CliHelpTests)|(FullyQualifiedName~DashboardRenderingTests|FullyQualifiedName~DashboardHostTests&Category!=HostIntegration|FullyQualifiedName~DashboardValidationHarnessTests)",
            check.Command[filterIndex + 1]);
        Assert.Contains(check.Command, argument => argument.Contains("CliCommandTests", StringComparison.Ordinal));
        Assert.Contains(check.Command, argument => argument.Contains("CliHelpTests", StringComparison.Ordinal));
        Assert.Contains(check.Command, argument => argument.Contains("DashboardRenderingTests", StringComparison.Ordinal));
        Assert.Contains(check.Command, argument => argument.Contains("DashboardHostTests", StringComparison.Ordinal));
        Assert.Contains(check.Command, argument => argument.Contains("DashboardValidationHarnessTests", StringComparison.Ordinal));
        Assert.False(check.Command.Any(argument => argument.Contains("Mcg.AgentOrchestrator.sln", StringComparison.OrdinalIgnoreCase)));
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_disables_focused_app_filter_when_shared_infrastructure_changes")]
    public void RepositoryTestImpactPlannerDisablesFocusedAppFilterWhenSharedInfrastructureChanges()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.ReportPreviews.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"
        ]);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("infrastructure tests", check.Name);
        Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", check.Command);
        Assert.DoesNotContain("--filter", check.Command);
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_disables_focused_app_filter_when_script_or_config_changes")]
    public void RepositoryTestImpactPlannerDisablesFocusedAppFilterWhenScriptOrConfigChanges()
    {
        var scriptPlan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.Tasks.cs",
            "scripts/Invoke-IsolatedDotnet.ps1"
        ]);
        var configPlan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.ReportPreviews.cs",
            "src/Mcg.AgentOrchestrator.App/appsettings.json"
        ]);

        foreach (var plan in new[] { scriptPlan, configPlan })
        {
            var check = Assert.Single(plan.Checks);
            Assert.Equal("infrastructure tests", check.Name);
            Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", check.Command);
            Assert.DoesNotContain("--filter", check.Command);
        }
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_selects_touched_infrastructure_test_class_filter")]
    public void RepositoryTestImpactPlannerSelectsTouchedInfrastructureTestClassFilter()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs"
        ]);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("focused changed infrastructure tests", check.Name);
        Assert.Contains("--filter", check.Command);
        Assert.Contains(check.Command, argument => argument.Contains("FullyQualifiedName~GoalAcceptanceVerifierTests", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_selects_core_tests_for_core_changes")]
    public void RepositoryTestImpactPlannerSelectsCoreTestsForCoreChanges()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.cs"
        ]);

        Assert.True(plan.RequiresBuild);
        Assert.False(plan.RequiresBroadVerification);
        var check = Assert.Single(plan.Checks);
        Assert.Equal("core tests", check.Name);
        Assert.True(check.Command.Any(argument => argument.Equals("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", StringComparison.Ordinal)));
        Assert.False(check.Command.Any(argument => argument.Equals("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_selects_core_and_infrastructure_tests_for_mixed_changes")]
    public void RepositoryTestImpactPlannerSelectsCoreAndInfrastructureTestsForMixedChanges()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs"
        ]);

        Assert.True(plan.RequiresBuild);
        Assert.False(plan.RequiresBroadVerification);
        Assert.Equal(2, plan.Checks.Count);
        Assert.True(plan.Checks.Any(check => check.Name == "core tests"));
        Assert.True(plan.Checks.Any(check => check.Name == "infrastructure tests"));
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_full_suite_is_two_per_project_checks_never_a_solution_run")]
    public void RepositoryTestImpactPlannerFullSuiteIsTwoPerProjectChecksNeverASolutionRun()
    {
        // Security-sensitive paths force the full suite. Both test projects are MTP, so a
        // project-less "dotnet test" always fails on .NET 10 with the VSTest-target error —
        // the full suite must be expressed as the two per-project runs the MTP runner can route.
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.Infrastructure/Sandbox/WorkerSandboxPolicy.cs"
        ]);

        Assert.True(plan.RequiresBuild);
        Assert.True(plan.RequiresBroadVerification);
        Assert.Equal(2, plan.Checks.Count);
        Assert.All(plan.Checks, check =>
            Assert.True(check.Command.Any(argument => argument.EndsWith(".csproj", StringComparison.Ordinal))));
        Assert.True(plan.Checks.Any(check =>
            check.Command.Contains("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj")));
        Assert.True(plan.Checks.Any(check =>
            check.Command.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")));
    }

    [Xunit.Fact(DisplayName = "VerificationPolicyCompiler_compiles_different_policies_from_task_risk_and_scope")]
    public void VerificationPolicyCompilerCompilesDifferentPoliciesFromTaskRiskAndScope()
    {
        var docsPolicy = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Update operator docs.",
            "Summarize the new workflow.",
            "Inspect rendered markdown.",
            ["docs/operator.md"]);
        var riskyDashboardPolicy = VerificationPolicyCompiler.Compile(
            AgentRole.Tester,
            "Verify dashboard auth policy.",
            "Run Playwright dashboard UI flow for auth policy rollback.",
            "Run focused tests and browser smoke.",
            [
                "src/Mcg.AgentOrchestrator.App/Dashboard/Api/AuthPolicy.cs",
                "src/Mcg.AgentOrchestrator.Dashboard/Components/AuthPolicy.tsx"
            ]);

        Assert.False(docsPolicy.RequiresTests);
        Assert.False(docsPolicy.RequiresHumanReview);
        Assert.True(riskyDashboardPolicy.RequiresTests);
        Assert.True(riskyDashboardPolicy.RequiresHumanReview);
        Assert.True(riskyDashboardPolicy.Checks.Any(check => check.Kind == "browser-smoke"));
        Assert.True(riskyDashboardPolicy.Checks.Any(check => check.Kind == "manual-risk-review"));
    }
}
