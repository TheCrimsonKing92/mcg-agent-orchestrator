namespace Mcg.AgentOrchestrator.Infrastructure;

// Linked sources have compile consumers beyond their containing project.
internal static class AcceptancePolicyShardPlannerLinkedSources
{
    internal static readonly IReadOnlyDictionary<string, string[]> Table =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AssemblyTempRedirect.cs"] = [AcceptancePolicyShardPlanner.CliTestsProject, AcceptancePolicyShardPlanner.ExecutionTestsProject, AcceptancePolicyShardPlanner.AcceptanceTestsProject, AcceptancePolicyShardPlanner.ProviderEnvironmentTestsProject, AcceptancePolicyShardPlanner.StorageRetentionTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AssemblyTempRootCleanupFixture.cs"] = [AcceptancePolicyShardPlanner.CliTestsProject, AcceptancePolicyShardPlanner.ExecutionTestsProject, AcceptancePolicyShardPlanner.AcceptanceTestsProject, AcceptancePolicyShardPlanner.ProviderEnvironmentTestsProject, AcceptancePolicyShardPlanner.StorageRetentionTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AssemblyTempRootCleanupHolderDiagnostics.cs"] = [AcceptancePolicyShardPlanner.CliTestsProject, AcceptancePolicyShardPlanner.ExecutionTestsProject, AcceptancePolicyShardPlanner.AcceptanceTestsProject, AcceptancePolicyShardPlanner.ProviderEnvironmentTestsProject, AcceptancePolicyShardPlanner.StorageRetentionTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliChildProcessRunner.cs"] = [AcceptancePolicyShardPlanner.CliTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConsoleCapture.cs"] = [AcceptancePolicyShardPlanner.CliTestsProject, AcceptancePolicyShardPlanner.StorageRetentionTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FailureClusterFixtureData.cs"] = [AcceptancePolicyShardPlanner.CliTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FailureClusterTestData.cs"] = [AcceptancePolicyShardPlanner.CliTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FixtureProbeLocator.cs"] = [AcceptancePolicyShardPlanner.CliTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/InfrastructureTestSupport.cs"] = [AcceptancePolicyShardPlanner.StorageRetentionTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/InfrastructureTestSupportGitCommit.cs"] = [AcceptancePolicyShardPlanner.StorageRetentionTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/InfrastructureTestSupportGitProbeSeam.cs"] = [AcceptancePolicyShardPlanner.StorageRetentionTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RetentionReclaimFixture.cs"] = [AcceptancePolicyShardPlanner.StorageRetentionTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TestBuildOutputLocator.cs"] = [AcceptancePolicyShardPlanner.CliTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/VerifiedRepositoryRoot.cs"] = [AcceptancePolicyShardPlanner.CliTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsSeededRepositoryFactory.TemplateCache.cs"] = [AcceptancePolicyShardPlanner.StorageRetentionTestsProject],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsSeededRepositoryFactory.cs"] = [AcceptancePolicyShardPlanner.StorageRetentionTestsProject],
        };

    internal static IEnumerable<string> Consumers(string path) =>
        Table.TryGetValue(path.Replace('\\', '/'), out var projects) ? projects : [];
}
