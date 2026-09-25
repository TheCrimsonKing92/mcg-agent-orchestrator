using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ScoutContextPackageTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void ScoutContextDirectsPlannerToSurveyAndFiveRoleKeepsResearchReadOrder()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var scout = new TaskSpec(TaskId.New(), "Inspect and plan.", AgentRole.Planner);
        var scoutGoal = kernel.CreateGoal("Update the named source file.", [scout]);
        var scoutDirectory = new WorkerArtifactWriter().Write(scoutGoal, scout, root);
        var scoutManifest = File.ReadAllText(Path.Combine(scoutDirectory, "manifest.md"));
        var scoutDigest = File.ReadAllText(Path.Combine(scoutDirectory, "digest.md"));

        Xunit.Assert.Contains("perform the current source survey yourself", scoutManifest, StringComparison.Ordinal);
        Xunit.Assert.Contains("perform the current source survey yourself", scoutDigest, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("consume the complete validated Researcher artifact", scoutManifest, StringComparison.Ordinal);
        Xunit.Assert.False(File.Exists(Path.Combine(scoutDirectory, "research-notes.md")));

        var researcher = new TaskSpec(TaskId.New(), "Inspect source.", AgentRole.Researcher);
        var planner = new TaskSpec(TaskId.New(), "Plan.", AgentRole.Planner);
        var fiveRoleGoal = kernel.CreateGoal("Update the named source file.", [researcher, planner]);
        CompleteResearcherArtifact(kernel, fiveRoleGoal);
        var fiveRoleDirectory = new WorkerArtifactWriter().Write(fiveRoleGoal, planner, root);
        var fiveRoleManifest = File.ReadAllText(Path.Combine(fiveRoleDirectory, "manifest.md"));

        Xunit.Assert.Contains("consume the complete validated Researcher artifact; do not repeat a broad source survey", fiveRoleManifest, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("perform the current source survey yourself", fiveRoleManifest, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ScoutDurableResearchMaterializesForDeveloper()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Scout the source.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Update the named source file.", [planner, developer]);
        var outputPath = Path.Combine(root, "scout.out.log");
        var research = ResearcherContractFixture();
        File.WriteAllText(outputPath, research);
        Xunit.Assert.True(ResearcherOutputContract.TryPersistDurableReceipt(
            outputPath, research, out var diagnostic), diagnostic);
        kernel.RecordTaskVerification(goal.Id, planner.Id, new TaskVerificationRecord(
            "scout fixture", root, 0, "Scout completed.", string.Empty,
            DateTimeOffset.UtcNow, StandardOutputPath: outputPath));

        var contextDirectory = new WorkerArtifactWriter().Write(goal, developer, root);
        var materialized = File.ReadAllText(Path.Combine(contextDirectory, "research-notes.md"));

        Xunit.Assert.Contains("CURRENT-SOURCE-RESEARCH-9182", materialized, StringComparison.Ordinal);
    }
}
