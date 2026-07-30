using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class GoalAcceptanceEvidenceBundleTests
{
    [Xunit.Theory(DisplayName = "Acceptance_policy_recognizes_equivalent_aggregate_project_test_evidence")]
    [Xunit.InlineData(
        "full dotnet tests: infrastructure",
        "dotnet test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal",
        "infrastructure tests")]
    [Xunit.InlineData(
        "full dotnet tests: core",
        "dotnet test tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj --verbosity minimal",
        "core tests")]
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

    [Xunit.Fact(DisplayName = "Acceptance_policy_does_not_treat_partition_check_as_full_aggregate")]
    public void AcceptancePolicyDoesNotTreatPartitionCheckAsFullAggregate()
    {
        var policyCheck = new VerificationPolicyCheck(
            "full dotnet tests: infrastructure",
            "dotnet-test",
            Required: true,
            "dotnet test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal",
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
            "dotnet test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal",
            "Broad verification required.");

        var state = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(
            policyCheck,
            [new AcceptanceCheckResult("infrastructure tests", true, 0, null, Advisory: true)]);

        Xunit.Assert.Equal("missing", state);
    }
}
