using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class GoalAcceptanceEvidenceBundleTests
{
    [Xunit.Theory(DisplayName = "Acceptance_policy_recognizes_equivalent_aggregate_project_test_evidence")]
    [Xunit.InlineData(
        "full dotnet tests: infrastructure",
        "dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal",
        "infrastructure tests")]
    [Xunit.InlineData(
        "full dotnet tests: core",
        "dotnet test --project tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj --verbosity minimal",
        "core tests")]
    [Xunit.InlineData(
        "full dotnet tests: provider environment",
        "dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj --verbosity minimal",
        "provider environment tests")]
    [Xunit.InlineData(
        "full dotnet tests: cli",
        "dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj --verbosity minimal",
        "cli tests")]
    [Xunit.InlineData(
        "full dotnet tests: dashboard",
        "dotnet test tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj --verbosity minimal",
        "dashboard tests")]
    public void AcceptancePolicyRecognizesEquivalentAggregateProjectTestEvidence(
        string policyName,
        string commandLine,
        string aggregateName)
    {
        var policyCheck = new VerificationPolicyCheck(
            policyName,
            "dotnet-test",
            Required: true,
            commandLine,
            "Broad verification required.");

        var passed = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(
            policyCheck,
            [new AcceptanceCheckResult(aggregateName, true, 0, null)]);
        var failed = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(
            policyCheck,
            [new AcceptanceCheckResult(aggregateName, false, 1, "failed")]);

        Xunit.Assert.Equal("passed", passed);
        Xunit.Assert.Equal("failed", failed);
    }

    [Xunit.Fact(DisplayName = "Acceptance_policy_recognizes_solution_test_evidence_only_when_all_projects_pass")]
    public void AcceptancePolicyRecognizesSolutionTestEvidenceOnlyWhenAllProjectsPass()
    {
        var policyCheck = SolutionPolicyCheck();
        AcceptanceCheckResult[] allPassed =
        [
            new("core tests", true, 0, null),
            new("infrastructure tests", true, 0, null),
            new("provider environment tests", true, 0, null),
            new("cli tests", true, 0, null)
        ];

        var passed = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(policyCheck, allPassed);
        var failed = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(
            policyCheck,
            [.. allPassed[..3], new AcceptanceCheckResult("cli tests", false, 1, "failed")]);
        var missing = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(
            policyCheck,
            allPassed[..3]);

        Xunit.Assert.Equal("passed", passed);
        Xunit.Assert.Equal("failed", failed);
        Xunit.Assert.Equal("missing", missing);
    }

    [Xunit.Fact(DisplayName = "Acceptance_policy_does_not_treat_partition_check_as_full_aggregate")]
    public void AcceptancePolicyDoesNotTreatPartitionCheckAsFullAggregate()
    {
        var policyCheck = new VerificationPolicyCheck(
            "full dotnet tests: infrastructure",
            "dotnet-test",
            Required: true,
            "dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal",
            "Broad verification required.");

        var state = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(
            policyCheck,
            [new AcceptanceCheckResult("infrastructure tests: Cli", true, 0, null)]);

        Xunit.Assert.Equal("missing", state);
    }

    [Xunit.Fact(DisplayName = "Acceptance_policy_does_not_treat_advisory_check_as_full_aggregate")]
    public void AcceptancePolicyDoesNotTreatAdvisoryCheckAsFullAggregate()
    {
        var policyCheck = new VerificationPolicyCheck(
            "full dotnet tests: infrastructure",
            "dotnet-test",
            Required: true,
            "dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal",
            "Broad verification required.");

        var state = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(
            policyCheck,
            [new AcceptanceCheckResult("infrastructure tests", true, 0, null, Advisory: true)]);

        Xunit.Assert.Equal("missing", state);
    }

    private static VerificationPolicyCheck SolutionPolicyCheck() => new(
        "full dotnet tests",
        "dotnet-test",
        Required: true,
        "dotnet test Mcg.AgentOrchestrator.sln --verbosity minimal",
        "Broad verification required.");
}
