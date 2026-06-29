namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class RepositoryChangeClassifierTests
{
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
        Assert.Contains(check.Command, argument => argument.Contains("CliCommandTests", StringComparison.Ordinal));
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

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_falls_back_to_full_infrastructure_tests_for_multiple_app_subsystems")]
    public void RepositoryTestImpactPlannerFallsBackToFullInfrastructureTestsForMultipleAppSubsystems()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.Tasks.cs",
            "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.OperatorShell.cs"
        ]);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("infrastructure tests", check.Name);
        Assert.DoesNotContain("--filter", check.Command);
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
