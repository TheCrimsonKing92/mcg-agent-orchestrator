using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PlannerSamplingDispatchTests
{
    [Xunit.Fact]
    public void ConfiguredPlannerSampleCountProducesNCandidates()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        var plan = ReadPlannerFixture();
        File.WriteAllText(primaryPath, plan);
        var artifacts = PlannerSampleDispatcher.CreateArtifacts(primaryPath, 3);
        foreach (var sample in artifacts)
        {
            File.WriteAllText(sample.StandardOutputPath, plan + Environment.NewLine + $"<!-- sample {sample.Index} -->");
            DispatchExitArtifacts.Write(
                sample.ExitCodePath,
                DispatchExitArtifacts.Native(0, "test sample completed", DateTimeOffset.UtcNow));
        }

        var candidates = PlannerSampleDispatcher.CollectCandidates(primaryPath, 3);

        Xunit.Assert.Equal(3, candidates.Count);
        Xunit.Assert.Equal([0, 1, 2], candidates.Select(candidate => candidate.Index));
    }

    [Xunit.Fact]
    public void SingleSampleBypassesSiblingArtifactsAndPreservesCapturedBytes()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        var plan = ReadPlannerFixture();
        File.WriteAllText(primaryPath, plan);

        var artifacts = PlannerSampleDispatcher.CreateArtifacts(primaryPath, 1);
        var candidates = PlannerSampleDispatcher.CollectCandidates(primaryPath, 1);

        Xunit.Assert.Empty(artifacts);
        Xunit.Assert.Single(candidates);
        Xunit.Assert.Equal(PlannerOutputContract.ReadCapturedOutputTail(primaryPath), candidates[0].StandardOutput);
    }

    [Xunit.Fact]
    public void ClaudeSamplesUseIndependentProviderSessions()
    {
        const string primaryCommand = "claude -p --session-id primary-session";

        var first = PlannerSampleDispatcher.BuildIndependentCommand(primaryCommand, WorkerSandboxProvider.Claude);
        var second = PlannerSampleDispatcher.BuildIndependentCommand(primaryCommand, WorkerSandboxProvider.Claude);

        Xunit.Assert.DoesNotContain("primary-session", first, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("primary-session", second, StringComparison.Ordinal);
        Xunit.Assert.NotEqual(first, second);
    }

    private static string ReadPlannerFixture() => File.ReadAllText(Path.Combine(
        InfrastructureTestSupport.FindRepositoryRoot(),
        "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Fixtures", "PlannerOutputContract",
        "658501ce-f6708f44-20260805012800.out.txt"));

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "planner-sampling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
