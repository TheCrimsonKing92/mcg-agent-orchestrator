using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptancePolicyShardPlannerTests
{
    private const string AppEvidence =
        "changed projects: App; dependency closure: App, Dashboard.Tests, Infrastructure.Cli.Tests, " +
        "Infrastructure.ProviderEnvironment.Tests, Infrastructure.Tests, TestSupport";

    [Fact]
    public void BuildPolicyShardPlan_PreservesFullShardReasonsAndPrecedence()
    {
        var previousFullShards = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS");
        var previousChangeScoped = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", "1");
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", "0");
            Assert.Equal(
                $"MCG_ACCEPTANCE_FULL_SHARDS=1; {AppEvidence}",
                AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
                    ["src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.cs"]).Evidence);

            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", null);
            Assert.Equal(
                $"MCG_ACCEPTANCE_CHANGE_SCOPED disabled; {AppEvidence}",
                AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
                    ["src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.cs"]).Evidence);

            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", "1");
            Assert.Equal(
                "build-system file changed: src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj; " +
                "changed projects: Core; dependency closure: App, Core, Core.Tests, Dashboard.Tests, " +
                "Infrastructure, Infrastructure.Acceptance.Tests, Infrastructure.Cli.Tests, Infrastructure.OperatorComms, " +
                "Infrastructure.ProviderEnvironment.Tests, Infrastructure.Providers, Infrastructure.Tests, TestSupport",
                AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
                    ["src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj"]).Evidence);
            Assert.Equal(
                "script changed: scripts/Invoke-IsolatedDotnet.ps1; changed projects: (none); dependency closure: (none)",
                AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
                    ["scripts/Invoke-IsolatedDotnet.ps1"]).Evidence);
            Assert.Equal(
                "changed files did not map to a known project; changed projects: (none); dependency closure: (none)",
                AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
                    ["src/Mcg.AgentOrchestrator.Dashboard/Foo.cs"]).Evidence);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", previousFullShards);
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", previousChangeScoped);
        }
    }

    [Fact]
    public void BuildPolicyShardPlan_PreservesEvidenceAndClosureOrdering()
    {
        var previousFullShards = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS");
        var previousChangeScoped = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", null);
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", "1");

            var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
                ["src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.cs"]);

            Assert.Equal(AppEvidence, plan.Evidence);
            Assert.Equal(
                "changed projects: (none); dependency closure: (none)",
                AcceptancePolicyShardPlanner.BuildPolicyShardEvidence(
                    [],
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", previousFullShards);
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", previousChangeScoped);
        }
    }

    [Fact]
    public void BuildPolicyShardPlan_MapsOperatorCommsAndItsReferencingProjects()
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
            ["src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/OperatorChannelFactory.cs"]);

        Assert.Equal(
            "changed projects: Infrastructure.OperatorComms; dependency closure: App, Dashboard.Tests, " +
            "Infrastructure.Cli.Tests, Infrastructure.OperatorComms, Infrastructure.ProviderEnvironment.Tests, " +
            "Infrastructure.Tests, TestSupport",
            plan.Evidence);
        Assert.DoesNotContain(
            AcceptancePolicyShardPlanner.InfrastructureProject,
            plan.DependencyClosure,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddPolicyShardReceiptResults_PreservesCompleteSkipReceipt()
    {
        var plan = PolicyShardPlan.Scoped(
            AppEvidence,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                AcceptancePolicyShardPlanner.AppProject
            });
        var manifestChecks = new[]
        {
            new GoalAcceptanceVerifier.AcceptanceManifestCheck
            {
                Name = "core tests",
                Type = "dotnet-test",
                Project = AcceptancePolicyShardPlanner.CoreTestsProject
            }
        };
        var results = new List<AcceptanceCheckResult>();

        AcceptancePolicyShardPlanner.AddPolicyShardReceiptResults(
            results,
            manifestChecks,
            [],
            plan);

        var receipt = Assert.Single(results);
        Assert.Equal(
            $"skipped: no changed file in dependency closure; shard project: Core.Tests; {AppEvidence}",
            receipt.ResultSummary);
    }

    [Fact]
    public void EnvironmentFlags_PreserveTruthTables()
    {
        var previousFullShards = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS");
        var previousChangeScoped = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED");
        try
        {
            foreach (var enabled in new string?[] { null, "", " ", "1", "true", "yes", "on" })
            {
                Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", enabled);
                Assert.True(AcceptancePolicyShardPlanner.ChangeScopedAcceptanceEnabled());
            }

            foreach (var disabled in new[] { "0", "false", "no", "off" })
            {
                Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", disabled);
                Assert.False(AcceptancePolicyShardPlanner.ChangeScopedAcceptanceEnabled());
            }

            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", "1");
            foreach (var enabled in new[] { "1", "true", "yes", "on" })
            {
                Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", enabled);
                Assert.True(AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
                    ["src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.cs"]).ForceFull);
            }

            foreach (var disabled in new string?[] { null, "", " ", "0", "false", "no", "off" })
            {
                Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", disabled);
                Assert.False(AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
                    ["src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.cs"]).ForceFull);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", previousFullShards);
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", previousChangeScoped);
        }
    }
}
