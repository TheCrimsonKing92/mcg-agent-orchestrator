namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class RepositoryChangeClassifierTests
{
    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_allows_candidate_lane_timeout_and_safe_MTP_reporting_changes")]
    public void RepositoryChangeClassifierAllowsCandidateLaneTimeoutAndSafeMtpReportingChanges()
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
                  "arguments": [
                    "{executable}",
                    "--no-ansi",
                    "--progress", "off",
                    "--results-directory", "{resultsDirectory}",
                    "--report-trx",
                    "--report-trx-filename", "{trxFileName}",
                    "--long-running", "120"
                  ]
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
                  "arguments": [
                    "{executable}",
                    "--no-ansi",
                    "--progress", "off",
                    "--results-directory", "{resultsDirectory}",
                    "--report-trx",
                    "--report-trx-filename", "{trxFileName}",
                    "--long-running", "60"
                  ]
                }]
              }
            }
            """;

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

        Assert.False(decision.RequiresTrustedReview);
        Assert.Empty(decision.SecurityCriticalChanges);
        Assert.StartsWith("positive evidence:", decision.Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_allows_minimum_expected_tests_in_mtp_arguments")]
    public void RepositoryChangeClassifierAllowsMinimumExpectedTestsInMtpArguments()
    {
        const string trusted = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "executablePathTemplate": "bin/{projectName}.exe",
              "firewallExecutablePathTemplate": "bin/{projectName}.exe",
              "arguments": [
                "{executable}",
                "--no-ansi",
                "--progress", "off",
                "--results-directory", "{resultsDirectory}",
                "--report-trx",
                "--report-trx-filename", "{trxFileName}",
                "--long-running", "120"
              ]
            }] } }
            """;
        const string candidate = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "executablePathTemplate": "bin/{projectName}.exe",
              "firewallExecutablePathTemplate": "bin/{projectName}.exe",
              "arguments": [
                "{executable}",
                "--no-ansi",
                "--progress", "off",
                "--results-directory", "{resultsDirectory}",
                "--report-trx",
                "--report-trx-filename", "{trxFileName}",
                "--long-running", "120",
                "--minimum-expected-tests", "1"
              ]
            }] } }
            """;

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

        Assert.False(decision.RequiresTrustedReview);
        Assert.Empty(decision.SecurityCriticalChanges);
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_routes_manifest_executable_or_firewall_path_changes_to_trusted_review")]
    public void RepositoryChangeClassifierRoutesManifestExecutableOrFirewallPathChangesToTrustedReview()
    {
        const string trusted = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "executablePathTemplate": "bin/A.exe",
              "firewallExecutablePathTemplate": "bin/A.exe",
              "arguments": ["{executable}", "--report-trx"]
            }] } }
            """;
        const string candidate = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "executablePathTemplate": "../candidate.exe",
              "firewallExecutablePathTemplate": "../candidate.exe",
              "arguments": ["{executable}", "--report-trx"]
            }] } }
            """;

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

        Assert.True(decision.RequiresTrustedReview);
        Assert.Equal(2, decision.SecurityCriticalChanges.Count);
        Assert.Contains("trusted review required", decision.Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_allows_safe_MTP_project_addition_using_trusted_templates")]
    public void RepositoryChangeClassifierAllowsSafeMtpProjectAdditionUsingTrustedTemplates()
    {
        const string trusted = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "executablePathTemplate": "bin/{projectName}.exe",
              "firewallExecutablePathTemplate": "bin/{projectName}.exe",
              "arguments": ["{executable}", "--report-trx"]
            }] } }
            """;
        const string candidate = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "executablePathTemplate": "bin/{projectName}.exe",
              "firewallExecutablePathTemplate": "bin/{projectName}.exe",
              "arguments": ["{executable}", "--report-trx"]
            }, {
              "project": "tests/B.csproj",
              "executablePathTemplate": "bin/{projectName}.exe",
              "firewallExecutablePathTemplate": "bin/{projectName}.exe",
              "arguments": ["{executable}", "--report-trx", "--minimum-expected-tests", "1"]
            }] } }
            """;

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

        Assert.False(decision.RequiresTrustedReview);
        Assert.Empty(decision.SecurityCriticalChanges);
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_routes_MTP_project_removal_to_trusted_review")]
    public void RepositoryChangeClassifierRoutesMtpProjectRemovalToTrustedReview()
    {
        const string invocation = """
            {
              "project": "tests/A.csproj",
              "executablePathTemplate": "bin/{projectName}.exe",
              "firewallExecutablePathTemplate": "bin/{projectName}.exe",
              "arguments": ["{executable}", "--report-trx"]
            }
            """;
        var trusted = $$"""{ "engine": { "mtpInvocations": [{{invocation}}] } }""";
        const string candidate = """{ "engine": { "mtpInvocations": [] } }""";

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

        Assert.True(decision.RequiresTrustedReview);
        Assert.Contains("engine.mtpInvocations[].executablePathTemplate", decision.SecurityCriticalChanges);
        Assert.Contains("engine.mtpInvocations[].firewallExecutablePathTemplate", decision.SecurityCriticalChanges);
        Assert.Contains("engine.mtpInvocations[].arguments[0]", decision.SecurityCriticalChanges);
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_routes_MTP_project_addition_with_novel_template_to_trusted_review")]
    public void RepositoryChangeClassifierRoutesMtpProjectAdditionWithNovelTemplateToTrustedReview()
    {
        const string trusted = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "executablePathTemplate": "bin/{projectName}.exe",
              "firewallExecutablePathTemplate": "bin/{projectName}.exe",
              "arguments": ["{executable}", "--report-trx"]
            }] } }
            """;
        const string candidate = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "executablePathTemplate": "bin/{projectName}.exe",
              "firewallExecutablePathTemplate": "bin/{projectName}.exe",
              "arguments": ["{executable}", "--report-trx"]
            }, {
              "project": "tests/B.csproj",
              "executablePathTemplate": "../candidate.exe",
              "firewallExecutablePathTemplate": "../candidate.exe",
              "arguments": ["{executable}", "--report-trx"]
            }] } }
            """;

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

        Assert.True(decision.RequiresTrustedReview);
        Assert.Contains("engine.mtpInvocations[].executablePathTemplate", decision.SecurityCriticalChanges);
        Assert.Contains("engine.mtpInvocations[].firewallExecutablePathTemplate", decision.SecurityCriticalChanges);
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

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_routes_partition_verdict_full_rerun_cadence_change_to_trusted_review")]
    public void RepositoryChangeClassifierRoutesPartitionVerdictFullRerunCadenceChangeToTrustedReview()
    {
        const string trusted = """{ "engine": { "partitionVerdictFullRerunEveryN": 5 } }""";
        string[] candidates =
        [
            """{ "engine": { "partitionVerdictFullRerunEveryN": 10 } }""",
            """{ "engine": { } }"""
        ];

        foreach (var candidate in candidates)
        {
            var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

            Assert.True(decision.RequiresTrustedReview);
            Assert.Contains("engine.partitionVerdictFullRerunEveryN", decision.SecurityCriticalChanges);
        }

        var added = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(
            """{ "engine": { } }""",
            trusted);
        Assert.True(added.RequiresTrustedReview);
        Assert.Contains("engine.partitionVerdictFullRerunEveryN", added.SecurityCriticalChanges);
    }

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_accepts_unchanged_partition_verdict_full_rerun_cadence_with_positive_evidence")]
    public void RepositoryChangeClassifierAcceptsUnchangedPartitionVerdictFullRerunCadenceWithPositiveEvidence()
    {
        const string manifest = """{ "engine": { "partitionVerdictFullRerunEveryN": 5 } }""";

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(manifest, manifest);

        Assert.False(decision.RequiresTrustedReview);
        Assert.Empty(decision.SecurityCriticalChanges);
        Assert.Contains("partition-verdict full-rerun cadence", decision.Evidence, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "RepositoryChangeClassifier_routes_MTP_ignore_exit_code_to_trusted_review")]
    public void RepositoryChangeClassifierRoutesMtpIgnoreExitCodeToTrustedReview()
    {
        const string trusted = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "arguments": ["{executable}", "--report-trx"]
            }] } }
            """;
        const string candidate = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "arguments": ["{executable}", "--report-trx", "--ignore-exit-code"]
            }] } }
            """;

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

        Assert.True(decision.RequiresTrustedReview);
        Assert.Contains("engine.mtpInvocations[].arguments", decision.SecurityCriticalChanges);
    }

    [Xunit.Theory(DisplayName = "RepositoryChangeClassifier_routes_unknown_or_malformed_MTP_arguments_to_trusted_review")]
    [Xunit.InlineData("""["{executable}", "--unknown"]""")]
    [Xunit.InlineData("""["{executable}", "--results-directory", "../outside"]""")]
    [Xunit.InlineData("""["{executable}", "--progress"]""")]
    [Xunit.InlineData("""["{executable}", "extra-position"]""")]
    [Xunit.InlineData("""["{executable}", "--long-running", "60", "--long-running", "120"]""")]
    [Xunit.InlineData("""["{executable}", "--long-running", "120", "--long-running", "60"]""")]
    [Xunit.InlineData("""["{executable}", "--ignore-exit-code", "--ignore-exit-code=false"]""")]
    [Xunit.InlineData("""["{executable}", "--ignore-exit-code=false", "--ignore-exit-code"]""")]
    [Xunit.InlineData("""["{executable}", "--long-running", "0"]""")]
    [Xunit.InlineData("""["{executable}", "--long-running", "-1"]""")]
    [Xunit.InlineData("""["{executable}", "--long-running", "not-a-number"]""")]
    [Xunit.InlineData("""["{executable}", "--long-running", "2147483648"]""")]
    public void RepositoryChangeClassifierRoutesUnknownOrMalformedMtpArgumentsToTrustedReview(
        string candidateArguments)
    {
        const string trusted = """
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "arguments": ["{executable}", "--report-trx"]
            }] } }
            """;
        var candidate = $$"""
            { "engine": { "mtpInvocations": [{
              "project": "tests/A.csproj",
              "arguments": {{candidateArguments}}
            }] } }
            """;

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);

        Assert.True(decision.RequiresTrustedReview);
        Assert.Contains("engine.mtpInvocations[].arguments", decision.SecurityCriticalChanges);
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
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Application/TaskComplexityEstimator.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/LandingExecutor.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/GoalRefinementGate.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/DispatchReadinessEvaluator.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/OrchestratorEntityResolver.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Application/GoalDispatchOperations.cs")]
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
            "src/Mcg.AgentOrchestrator.App/README.md"
        ]);

        Assert.False(summary.RequiresConductorRelaunch);
    }

    [Xunit.Fact(DisplayName = "RepositoryOwnershipMap_classifies_high_risk_generated_dashboard_tests_and_docs")]
    public void RepositoryOwnershipMapClassifiesHighRiskGeneratedDashboardTestsAndDocs()
    {
        var guard = RepositoryOwnershipMap.GuardWriteSet([
            "src/Mcg.AgentOrchestrator.Core/Application/ParallelExecutionPlanner.cs",
            "tests/Mcg.AgentOrchestrator.Core.Tests/ParallelExecutionPlannerTests.cs",
            "docs/operator.md",
            "src/Mcg.AgentOrchestrator.App/bin/Debug/generated.dll"
        ]);

        Assert.Contains(guard.Paths, path => path.Area == RepositoryOwnershipArea.SharedInfrastructure && path.IsHighRisk);
        Assert.Contains(guard.Paths, path => path.Area == RepositoryOwnershipArea.Test);
        Assert.Contains(guard.Paths, path => path.Area == RepositoryOwnershipArea.Documentation);
        Assert.Contains(guard.Paths, path => path.Area == RepositoryOwnershipArea.GeneratedOrNoisy && path.IsGeneratedOrNoisy);
        Assert.True(guard.RequiresOperatorApproval);
        Assert.Contains(
            guard.RequiredResources,
            resource => resource == "ownership:shared-infrastructure:core/application");
        Assert.Contains(guard.Reasons, reason => reason.Contains("generated/noisy path", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RepositoryOwnershipMap_uses_distinct_conventional_test_project_reservations")]
    public void RepositoryOwnershipMapUsesDistinctConventionalTestProjectReservations()
    {
        var core = RepositoryOwnershipMap.Classify(
            "tests/Mcg.AgentOrchestrator.Core.Tests/RepositoryChangeClassifierTests.cs");
        var infrastructure = RepositoryOwnershipMap.Classify(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs");

        Assert.Equal(
            "test-project:tests/mcg.agentorchestrator.core.tests/repositorychangeclassifiertests.cs",
            core.ReservationKey);
        Assert.Equal(
            "test-project:tests/mcg.agentorchestrator.infrastructure.tests/workerdispatchtests.cs",
            infrastructure.ReservationKey);
        Assert.NotEqual(core.ReservationKey, infrastructure.ReservationKey);
    }

    [Xunit.Fact(DisplayName = "RepositoryOwnershipMap_treats_project_directory_as_conventional_test_scope")]
    public void RepositoryOwnershipMapTreatsProjectDirectoryAsConventionalTestScope()
    {
        const string projectDirectory = "tests/Mcg.AgentOrchestrator.Core.Tests";
        const string expectedReservation = "test-project:tests/mcg.agentorchestrator.core.tests";
        var ownedPath = RepositoryOwnershipMap.Classify(projectDirectory);
        var guard = RepositoryOwnershipMap.GuardWriteSet([
            projectDirectory,
            $"{projectDirectory}/RepositoryChangeClassifierTests.cs"
        ]);

        Assert.Equal(RepositoryOwnershipArea.Test, ownedPath.Area);
        Assert.Equal(expectedReservation, ownedPath.ReservationKey);
        Assert.Equal(
        [
            $"ownership:{expectedReservation}",
            $"ownership:{expectedReservation}/repositorychangeclassifiertests.cs"
        ],
            guard.RequiredResources);
    }

    [Xunit.Fact(DisplayName = "RepositoryOwnershipMap_normalizes_and_serializes_one_test_project")]
    public void RepositoryOwnershipMapNormalizesAndSerializesOneTestProject()
    {
        const string expectedFirstResource =
            "ownership:test-project:tests/mcg.agentorchestrator.core.tests/firsttests.cs";
        const string expectedSecondResource =
            "ownership:test-project:tests/mcg.agentorchestrator.core.tests/secondtests.cs";
        var first = RepositoryOwnershipMap.Classify(
            @"TESTS\Mcg.AgentOrchestrator.Core.Tests\FirstTests.cs");
        var repeated = RepositoryOwnershipMap.Classify(
            "tests/mcg.agentorchestrator.core.tests/FirstTests.cs");
        var guard = RepositoryOwnershipMap.GuardWriteSet([
            "tests/Mcg.AgentOrchestrator.Core.Tests/FirstTests.cs",
            "tests/Mcg.AgentOrchestrator.Core.Tests/SecondTests.cs"
        ]);
        var scope = RepositoryLandingScopeNormalization.Normalize([
            "tests/Mcg.AgentOrchestrator.Core.Tests/FirstTests.cs"
        ]);

        Assert.Equal(first.ReservationKey, repeated.ReservationKey);
        Assert.Equal([expectedFirstResource, expectedSecondResource], guard.RequiredResources);
        Assert.Equal(expectedFirstResource, Assert.Single(scope.ResourceKeys));
        Assert.DoesNotContain("ownership:tests", scope.ResourceKeys);
    }

    [Xunit.Theory(DisplayName = "RepositoryOwnershipMap_fails_closed_for_malformed_test_paths")]
    [Xunit.InlineData("tests/LooseTests.cs")]
    [Xunit.InlineData("tests/ /LooseTests.cs")]
    [Xunit.InlineData("tests/../LooseTests.cs")]
    [Xunit.InlineData("tests/Proj/Sub//X.cs")]
    [Xunit.InlineData("tests/Proj/Sub/./X.cs")]
    [Xunit.InlineData("tests/Proj/Sub/../X.cs")]
    [Xunit.InlineData("misc/LooseTests.cs")]
    [Xunit.InlineData("nested/tests/Project/LooseTests.cs")]
    public void RepositoryOwnershipMapFailsClosedForMalformedTestPaths(string path)
    {
        var ownedPath = RepositoryOwnershipMap.Classify(path);
        var guard = RepositoryOwnershipMap.GuardWriteSet([path]);

        Assert.Equal(RepositoryOwnershipArea.Unknown, ownedPath.Area);
        Assert.Equal("unknown-acceptance-scope", ownedPath.ReservationKey);
        Assert.Equal(
            RepositoryLandingScopeNormalization.UnknownAcceptanceScopeResourceKey,
            Assert.Single(guard.RequiredResources));
        Assert.DoesNotContain("ownership:tests", guard.RequiredResources);
    }

    [Xunit.Fact(DisplayName = "RepositoryOwnershipMap_uses_subsystem_reservations_within_shared_projects")]
    public void RepositoryOwnershipMapUsesSubsystemReservationsWithinSharedProjects()
    {
        var firstWorkspace = RepositoryOwnershipMap.Classify(
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBaseBuildCache.cs");
        var secondWorkspace = RepositoryOwnershipMap.Classify(
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs");
        var worker = RepositoryOwnershipMap.Classify(
            "src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs");

        Assert.Equal("shared-infrastructure:infrastructure/workspaces", firstWorkspace.ReservationKey);
        Assert.Equal(firstWorkspace.ReservationKey, secondWorkspace.ReservationKey);
        Assert.Equal("shared-infrastructure:infrastructure/workers", worker.ReservationKey);
        Assert.NotEqual(firstWorkspace.ReservationKey, worker.ReservationKey);
    }

    [Xunit.Fact(DisplayName = "RepositoryOwnershipMap_includes_project_in_shared_subsystem_reservation")]
    public void RepositoryOwnershipMapIncludesProjectInSharedSubsystemReservation()
    {
        var core = RepositoryOwnershipMap.Classify(
            "src/Mcg.AgentOrchestrator.Core/Persistence/OrchestratorSnapshots.cs");
        var infrastructure = RepositoryOwnershipMap.Classify(
            "src/Mcg.AgentOrchestrator.Infrastructure/Persistence/BacklogStore.cs");

        Assert.Equal("shared-infrastructure:core/persistence", core.ReservationKey);
        Assert.Equal("shared-infrastructure:infrastructure/persistence", infrastructure.ReservationKey);
        Assert.NotEqual(core.ReservationKey, infrastructure.ReservationKey);
    }

    [Xunit.Theory(DisplayName = "RepositoryOwnershipMap_preserves_shared_infrastructure_risk_and_serialization")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Application/RepositoryOwnershipMap.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure.Providers/ModelProviders.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/OperatorChannelFactory.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/AgentOutputDirectives.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure/NotASubsystem/Unknown.cs")]
    public void RepositoryOwnershipMapPreservesSharedInfrastructureRiskAndSerialization(string path)
    {
        var ownedPath = RepositoryOwnershipMap.Classify(path);

        Assert.Equal(RepositoryOwnershipArea.SharedInfrastructure, ownedPath.Area);
        Assert.True(ownedPath.IsHighRisk);
        Assert.True(ownedPath.RequiresSerialization);
    }

    [Xunit.Theory(DisplayName = "RepositoryOwnershipMap_fails_closed_for_unrecognized_shared_infrastructure_layouts")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/AgentOutputDirectives.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure.Providers/ModelProviders.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/../Boom.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/./Application/X.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure/ /X.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure/NotASubsystem/X.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Properties/AssemblyInfo.cs")]
    public void RepositoryOwnershipMapFailsClosedForUnrecognizedSharedInfrastructureLayouts(string path)
    {
        var ownedPath = RepositoryOwnershipMap.Classify(path);

        Assert.Equal(RepositoryOwnershipArea.SharedInfrastructure, ownedPath.Area);
        Assert.Equal("shared-infrastructure", ownedPath.ReservationKey);
    }

    [Xunit.Theory(DisplayName = "RepositoryOwnershipMap_keeps_generated_shared_infrastructure_paths_generated")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure/bin/Debug/generated.dll")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/obj/Debug/x.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/obj/Debug/x.cs")]
    public void RepositoryOwnershipMapKeepsGeneratedSharedInfrastructurePathsGenerated(string path)
    {
        var ownedPath = RepositoryOwnershipMap.Classify(path);

        Assert.Equal(RepositoryOwnershipArea.GeneratedOrNoisy, ownedPath.Area);
        Assert.True(ownedPath.IsGeneratedOrNoisy);
        Assert.Equal("generated-or-noisy", ownedPath.ReservationKey);
    }

    [Xunit.Fact(DisplayName = "RepositoryOwnershipMap_emits_distinct_resources_for_shared_subsystems")]
    public void RepositoryOwnershipMapEmitsDistinctResourcesForSharedSubsystems()
    {
        var guard = RepositoryOwnershipMap.GuardWriteSet([
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBaseBuildCache.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs"
        ]);

        Assert.Equal(
            [
                "ownership:shared-infrastructure:infrastructure/processes",
                "ownership:shared-infrastructure:infrastructure/workspaces"
            ],
            guard.RequiredResources);
        Assert.DoesNotContain("ownership:shared-infrastructure", guard.RequiredResources);
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

    [Xunit.Theory(DisplayName = "RepositoryTestImpactPlanner_selects_infrastructure_tests_for_extracted_infrastructure_changes")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure.Providers/ModelProviders.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/OperatorChannelFactory.cs")]
    public void RepositoryTestImpactPlannerSelectsInfrastructureTestsForExtractedInfrastructureChanges(string path)
    {
        var summary = RepositoryChangeClassifier.Classify([path]);
        var plan = RepositoryTestImpactPlanner.Plan(summary);

        Assert.True(summary.RequiresConductorRelaunch);
        Assert.True(summary.RequiresBroadVerification);
        Assert.True(plan.RequiresBuild);
        var check = Assert.Single(plan.Checks);
        Assert.Equal("infrastructure tests", check.Name);
        Assert.Contains(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            check.Command);
    }

    [Xunit.Theory(DisplayName = "RepositoryOwnershipMap_classifies_extracted_infrastructure_as_shared_infrastructure")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure.Providers/ModelProviders.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/OperatorChannelFactory.cs")]
    public void RepositoryOwnershipMapClassifiesExtractedInfrastructureAsSharedInfrastructure(string path)
    {
        var guard = RepositoryOwnershipMap.GuardWriteSet([path]);

        var ownedPath = Assert.Single(guard.Paths);
        Assert.Equal(RepositoryOwnershipArea.SharedInfrastructure, ownedPath.Area);
        Assert.True(ownedPath.IsHighRisk);
        Assert.True(ownedPath.RequiresSerialization);
        Assert.True(guard.RequiresOperatorApproval);
        Assert.Contains("ownership:shared-infrastructure", guard.RequiredResources);
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_runs_full_infrastructure_tests_for_provider_and_test_changes")]
    public void RepositoryTestImpactPlannerRunsFullInfrastructureTestsForProviderAndTestChanges()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.Infrastructure.Providers/ModelProviders.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TestHarness/InfrastructureProductionProjectGraphTests.cs"
        ]);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("infrastructure tests", check.Name);
        Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", check.Command);
        Assert.DoesNotContain("--filter", check.Command);
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

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_selects_focused_test_classes_for_orchestration_changes")]
    public void RepositoryTestImpactPlannerSelectsFocusedTestClassesForOrchestrationChanges()
    {
        var root = FindRepositoryRootFromSource();
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs",
            "src/Mcg.AgentOrchestrator.App/Orchestration/AutoReviewRetryConvergenceBriefBuilder.cs"
        ], root);

        Assert.True(plan.RequiresBuild);
        Assert.False(plan.RequiresBroadVerification);
        var check = Assert.Single(plan.Checks);
        Assert.Equal("focused orchestration infrastructure tests", check.Name);
        Assert.Contains("--filter", check.Command);
        Assert.Contains(check.Command, argument => argument.Contains("ConductorDriverTests", StringComparison.Ordinal));
        Assert.Contains(check.Command, argument => argument.Contains("AutoReviewRetryConvergenceBriefBuilderTests", StringComparison.Ordinal));
    }

    private static string FindRepositoryRootFromSource(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test source path.");
    }

    [Xunit.Fact(DisplayName = "Dashboard source no longer selects dashboard checks")]
    public void RepositoryTestImpactPlannerSelectsFocusedDashboardFilterForDashboardOnlyChanges()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.OperatorShell.cs"
        ]);

        Assert.DoesNotContain(plan.Checks, check =>
            check.CommandLine.Contains("Mcg.AgentOrchestrator.Dashboard.Tests", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Checks, check =>
            check.CommandLine.Contains("DashboardRenderingTests", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Dashboard test changes no longer select dashboard class filters")]
    public void RepositoryTestImpactPlannerSelectsTouchedDashboardTestClassFilter()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/DashboardDispatchStartFailureEndpointTests.cs"
        ]);

        Assert.DoesNotContain(plan.Checks, check =>
            check.CommandLine.Contains("Mcg.AgentOrchestrator.Dashboard.Tests", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Checks, check =>
            check.CommandLine.Contains("DashboardDispatchStartFailureEndpointTests", StringComparison.Ordinal));
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

        Assert.Single(plan.Checks);
        var cliCheck = Assert.Single(plan.Checks, check => check.Name == "focused CLI infrastructure tests");
        Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", cliCheck.Command);
        Assert.Contains(cliCheck.Command, argument => argument.Contains("CliCommandTests", StringComparison.Ordinal));
        Assert.Contains(cliCheck.Command, argument => argument.Contains("CliHelpTests", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Checks, check => check.CommandLine.Contains("Mcg.AgentOrchestrator.Dashboard.Tests", StringComparison.Ordinal));
        Assert.All(plan.Checks, check => Assert.DoesNotContain(check.Command, argument => argument.Contains("Mcg.AgentOrchestrator.sln", StringComparison.OrdinalIgnoreCase)));
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_disables_focused_app_filter_when_shared_infrastructure_changes")]
    public void RepositoryTestImpactPlannerDisablesFocusedAppFilterWhenSharedInfrastructureChanges()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.ReportPreviews.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"
        ]);

        Assert.Single(plan.Checks);
        var infrastructureCheck = Assert.Single(plan.Checks, check => check.Name == "infrastructure tests");
        Assert.DoesNotContain(plan.Checks, check => check.CommandLine.Contains("Mcg.AgentOrchestrator.Dashboard.Tests", StringComparison.Ordinal));
        Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", infrastructureCheck.Command);
        Assert.DoesNotContain("--filter", infrastructureCheck.Command);
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

        var scriptCheck = Assert.Single(scriptPlan.Checks);
        Assert.Equal("infrastructure tests", scriptCheck.Name);
        Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", scriptCheck.Command);
        Assert.DoesNotContain("--filter", scriptCheck.Command);

        Assert.Single(configPlan.Checks);
        var infrastructureCheck = Assert.Single(configPlan.Checks, check => check.Name == "infrastructure tests");
        Assert.DoesNotContain(configPlan.Checks, check => check.CommandLine.Contains("Mcg.AgentOrchestrator.Dashboard.Tests", StringComparison.Ordinal));
        Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", infrastructureCheck.Command);
        Assert.DoesNotContain("--filter", infrastructureCheck.Command);
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

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_ignores_non_source_fixtures_in_test_class_filter")]
    public void RepositoryTestImpactPlannerIgnoresNonSourceFixturesInTestClassFilter()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/PlannerOutputContract/485363d4-ba8e416a-20260805011642.out.txt",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PlannerOutputContractTests.cs"
        ]);

        var check = Assert.Single(plan.Checks);
        Assert.Contains("--filter", check.Command);
        Assert.Equal("FullyQualifiedName~PlannerOutputContractTests", check.Command[^1]);
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_falls_back_to_project_for_non_source_test_changes")]
    public void RepositoryTestImpactPlannerFallsBackToProjectForNonSourceTestChanges()
    {
        var plan = RepositoryTestImpactPlanner.Plan([
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/PlannerOutputContract/485363d4-ba8e416a-20260805011642.out.txt"
        ]);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("infrastructure tests", check.Name);
        Assert.DoesNotContain("--filter", check.Command);
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_keeps_unrelated_core_change_narrow")]
    public void RepositoryTestImpactPlannerKeepsUnrelatedCoreChangeNarrow()
    {
        var repositoryRoot = FindRepositoryRootFromSource();
        var plan = RepositoryTestImpactPlanner.Plan(
            ["src/Mcg.AgentOrchestrator.Core/Domain/GoalBriefRevision.cs"],
            repositoryRoot);

        Assert.True(plan.RequiresBuild);
        Assert.False(plan.RequiresBroadVerification);
        Assert.Equal(2, plan.Checks.Count);
        Assert.Contains(plan.Checks, check => check.Name == "core tests");
        var infrastructureCheck = Assert.Single(plan.Checks, check =>
            check.Command.Contains(
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
        var filter = RequiredTestImpactFilter(infrastructureCheck);
        Assert.Contains("FullyQualifiedName~TrialCompareCliCommandTests", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("RunGoalServiceTests", filter, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_selects_real_reverse_dependent_integration_consumer")]
    public void RepositoryTestImpactPlannerSelectsRealReverseDependentIntegrationConsumer()
    {
        var repositoryRoot = FindRepositoryRootFromSource();
        var plan = RepositoryTestImpactPlanner.Plan(
            ["src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs"],
            repositoryRoot);
        var repeatedPlan = RepositoryTestImpactPlanner.Plan(
            ["src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs"],
            repositoryRoot);

        Assert.True(plan.RequiresBuild);
        Assert.False(plan.RequiresBroadVerification);
        Assert.Equal(
            plan.Checks.Select(check => check.CommandLine),
            repeatedPlan.Checks.Select(check => check.CommandLine));
        Assert.Equal(2, plan.Checks.Count);
        Assert.Contains(plan.Checks, check =>
            check.Name == "core tests" &&
            check.Command.Contains(
                "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj"));
        var infrastructureCheck = Assert.Single(plan.Checks, check =>
            check.Command.Contains(
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
        Assert.Contains(
            "FullyQualifiedName~RunGoalServiceTests",
            RequiredTestImpactFilter(infrastructureCheck),
            StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "RepositoryTestImpactPlanner_full_suite_uses_MTP_routable_project_commands")]
    public void RepositoryTestImpactPlannerFullSuiteUsesMtpRoutableProjectCommands()
    {
        // Security-sensitive paths force the full suite. The test projects are MTP, so a
        // project-less "dotnet test" always fails on .NET 10 with the VSTest-target error —
        // the full suite must be expressed as per-project runs the MTP runner can route.
        var plan = RepositoryTestImpactPlanner.Plan([
            "src/Mcg.AgentOrchestrator.Infrastructure/Sandbox/WorkerSandboxPolicy.cs"
        ]);

        Assert.True(plan.RequiresBuild);
        Assert.True(plan.RequiresBroadVerification);
        Assert.Equal(5, plan.Checks.Count);
        Assert.All(plan.Checks, check =>
        {
            Assert.Equal("dotnet", check.Command[0]);
            Assert.Equal("test", check.Command[1]);
            Assert.Equal("--project", check.Command[2]);
            Assert.EndsWith(".csproj", check.Command[3], StringComparison.Ordinal);
            Assert.DoesNotContain("Mcg.AgentOrchestrator.sln", check.Command);
        });
        Assert.Equal(
            [
                "core tests",
                "infrastructure tests",
                "acceptance execution owner tests",
                "provider environment tests",
                "cli tests"
            ],
            plan.Checks.Select(check => check.Name));
        Assert.Equal(
            [
                "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Acceptance/Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.csproj",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj"
            ],
            plan.Checks.Select(check => check.Command[3]));
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
        Assert.DoesNotContain(riskyDashboardPolicy.Checks, check => check.Kind == "browser-smoke");
        Assert.True(riskyDashboardPolicy.Checks.Any(check => check.Kind == "manual-risk-review"));
    }
}
