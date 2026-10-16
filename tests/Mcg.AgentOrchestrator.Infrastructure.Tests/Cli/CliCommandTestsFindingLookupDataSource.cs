using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Isolated temp roots; console capture follows the serialized CLI collection.
[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliCommandTestsFindingLookupDataSource : CliTaskQueryTestSupport
{
    [Fact]
    public void DecoyIdOnlyInWorkerOutputFiles_IsNotFound()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var logs = Path.Combine(workspace.OrchestratorDirectory, "logs");
            Directory.CreateDirectory(logs);
            const string decoy = "decoy-only-on-disk";
            File.WriteAllText(Path.Combine(logs, "worker.out.log"), decoy);
            File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "decoy-goal.json"),
                "{\"stable_id\":\"" + decoy + "\"}");
            var repository = new ProbeStateRepository(CliFindingTestFixture.CreateSeed()) { ThrowOnOutbox = true };
            Assert.Contains($"No finding matches {decoy}", Assert.Throws<KeyNotFoundException>(() =>
                CliFindingTestFixture.Execute(["finding", decoy], repository, workspace)).Message);
            Assert.Contains(CliFindingTestFixture.Id,
                CliFindingTestFixture.Execute(["finding", CliFindingTestFixture.Id], repository, workspace));
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.Equal(0, repository.MutationAttempts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NoGoalForm_LoadsListedGoalSnapshotsWithoutFullHydration()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = CliFindingTestFixture.CreateSeed();
            var repository = new ProbeStateRepository(seed) { ThrowOnOutbox = true };
            Assert.Contains(CliFindingTestFixture.Id, CliFindingTestFixture.Execute(["finding", "replay-"], repository,
                OrchestratorWorkspace.ForDirectory(root)));
            Assert.Equal(1, repository.ListGoalMetadataCount);
            Assert.Equal(1, repository.LoadGoalsCount);
            Assert.Equal(seed.Goals.Select(goal => goal.Id.Value).Order(), repository.LoadedGoalIds.Order());
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MergeSaveAttempts);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.False(Directory.Exists(Path.Combine(root, ".orchestrator")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindingLookup_PublicSurface_TakesOnlyInMemoryGoalsAndId()
    {
        var method = Assert.Single(typeof(FindingLookup).GetMethods().Where(method => method.Name == "Find"));
        Assert.Equal([typeof(IEnumerable<Goal>), typeof(string)], method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.True(typeof(FindingLookup).IsAbstract && typeof(FindingLookup).IsSealed);
    }
}
