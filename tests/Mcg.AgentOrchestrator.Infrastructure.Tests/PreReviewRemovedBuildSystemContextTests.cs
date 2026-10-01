using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PreReviewRemovedBuildSystemContextTests
{
    [Xunit.Fact(DisplayName = "Pre-review retains full-suite mapping for removed build configuration")]
    public void RemovedBuildConfigurationKeepsFullSuiteMapping()
    {
        // Parallel-safe: all filesystem state belongs to this unique fixture root.
        var root = Path.Combine(Path.GetTempPath(), "pre-review-removed-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Write(root, ".git", "gitdir: isolated-fixture");
            Write(root, "Mcg.AgentOrchestrator.sln", "");
            foreach (var project in new[]
                     {
                         AcceptancePolicyShardPlanner.CoreTestsProject,
                         AcceptancePolicyShardPlanner.InfrastructureTestsProject,
                         AcceptancePolicyShardPlanner.AcceptanceTestsProject,
                         AcceptancePolicyShardPlanner.ProviderEnvironmentTestsProject,
                         AcceptancePolicyShardPlanner.CliTestsProject
                     })
            {
                Write(root, project, "<Project />");
            }
            Assert.False(CandidateTreeProbe.ForRepositoryRoot(root).Exists("Directory.Build.props"));

            var context = ConductorDriver.BuildPreReviewEvidenceContext(
                "candidate-sha", ["Directory.Build.props"], root);

            Assert.Contains("Build configuration changed", context.MappingReason);
            Assert.Contains("skipped removed path: Directory.Build.props (absent from candidate tree)",
                context.MappingReason);
            Assert.True(context.NoApplicableTests);
            Assert.False(context.MappingNeedsInput);
            Assert.Contains("project-wide checks are deferred to the acceptance gate", context.MappingReason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Write(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
