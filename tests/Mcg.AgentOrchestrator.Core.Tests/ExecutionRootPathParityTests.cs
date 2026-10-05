namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class ExecutionRootPathParityTests
{
    private const string InfrastructureRoot = "src/Mcg.AgentOrchestrator.Infrastructure/";
    private const string ExecutionRoot = "src/Mcg.AgentOrchestrator.Execution/";

    [Xunit.Fact]
    public void ClassifierAndOwnershipTreatExecutionTwinsLikeInfrastructure()
    {
        string[] relativePaths =
        [
            "Processes/GitCli.cs",
            "Workers/WorkerSourceSurvey.cs",
            "Persistence/BacklogStore.cs",
            "Verification/ManualVerificationRecorder.cs"
        ];

        foreach (var relativePath in relativePaths)
        {
            var infrastructurePath = InfrastructureRoot + relativePath;
            var executionPath = ExecutionRoot + relativePath;
            var infrastructureSummary = RepositoryChangeClassifier.Classify([infrastructurePath]);
            var executionSummary = RepositoryChangeClassifier.Classify([executionPath]);
            var infrastructureFile = Assert.Single(infrastructureSummary.Files);
            var executionFile = Assert.Single(executionSummary.Files);

            Assert.Equal(new[] { RepositoryChangeCategory.Source }, infrastructureFile.Categories);
            Assert.Equal(infrastructureFile.Categories, executionFile.Categories);
            Assert.True(infrastructureFile.RequiresBroadVerification);
            Assert.Equal(infrastructureFile.RequiresBroadVerification, executionFile.RequiresBroadVerification);
            Assert.Equal(infrastructureSummary.RequiresBroadVerification, executionSummary.RequiresBroadVerification);

            var infrastructureOwner = RepositoryOwnershipMap.Classify(infrastructurePath);
            var executionOwner = RepositoryOwnershipMap.Classify(executionPath);
            Assert.Equal(RepositoryOwnershipArea.SharedInfrastructure, infrastructureOwner.Area);
            Assert.Equal(infrastructureOwner.Area, executionOwner.Area);
            Assert.Equal(
                "shared-infrastructure:infrastructure/" + relativePath.Split('/')[0].ToLowerInvariant(),
                infrastructureOwner.ReservationKey);
            Assert.Equal(infrastructureOwner.ReservationKey, executionOwner.ReservationKey);
            Assert.True(infrastructureOwner.IsHighRisk);
            Assert.Equal(infrastructureOwner.IsHighRisk, executionOwner.IsHighRisk);
        }
    }

    [Xunit.Fact]
    public void TestImpactPlanTreatsExecutionTwinLikeInfrastructure()
    {
        // Each invocation owns its fixture directory; no collection or shared state is needed.
        using var repository = new FixtureRepository();
        const string relativePath = "Processes/GitCli.cs";
        const string appPath = "src/Mcg.AgentOrchestrator.App/Cli/CliCommandDispatcher.cs";
        const string testPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GitCliTests.cs";
        repository.Write(InfrastructureRoot + relativePath, "public sealed class GitCli { }");
        repository.Write(ExecutionRoot + relativePath, "public sealed class GitCli { }");
        repository.Write(appPath, "public sealed class CliCommandDispatcher { }");
        repository.Write(testPath,
            "public sealed class GitCliTests { [Xunit.Fact] public void Runs() { } }");

        // Prove that the fixture can select focused App filters when no infrastructure source changes.
        var appOnlyPlan = RepositoryTestImpactPlanner.Plan([appPath], repository.Root);
        Assert.True(UsesFocusedAppFilters(appOnlyPlan));

        string[][] companionPaths = [[], [appPath], [testPath]];
        foreach (var companions in companionPaths)
        {
            var infrastructurePlan = RepositoryTestImpactPlanner.Plan(
                new[] { InfrastructureRoot + relativePath }.Concat(companions), repository.Root);
            var executionPlan = RepositoryTestImpactPlanner.Plan(
                new[] { ExecutionRoot + relativePath }.Concat(companions), repository.Root);

            var infrastructureCheck = Assert.Single(infrastructurePlan.Checks);
            Assert.Equal(RepositoryTestProject.Infrastructure, infrastructureCheck.TestProject);
            Assert.Equal("infrastructure tests", infrastructureCheck.Name);
            Assert.DoesNotContain("--filter", infrastructureCheck.Command);
            Assert.Equal(
                infrastructurePlan.Checks.Select(check => check.TestProject).Distinct().OrderBy(project => project),
                executionPlan.Checks.Select(check => check.TestProject).Distinct().OrderBy(project => project));
            Assert.False(UsesFocusedAppFilters(infrastructurePlan));
            Assert.Equal(UsesFocusedAppFilters(infrastructurePlan), UsesFocusedAppFilters(executionPlan));
            Assert.Equal(
                infrastructurePlan.Checks.Select(check => check.Name),
                executionPlan.Checks.Select(check => check.Name));
            Assert.Equal(infrastructureCheck.Command, Assert.Single(executionPlan.Checks).Command);
        }
    }

    private static bool UsesFocusedAppFilters(RepositoryTestImpactPlan plan) =>
        plan.Checks.Any(check => check.Name.StartsWith("focused CLI", StringComparison.Ordinal));

    private sealed class FixtureRepository : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-execution-parity-{Guid.NewGuid():N}");

        internal FixtureRepository()
        {
            string[] testProjects =
            [
                "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Acceptance/Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.csproj",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj"
            ];
            foreach (var testProject in testProjects)
            {
                Write(testProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            }
        }

        internal void Write(string relativePath, string contents)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
