using Mcg.AgentOrchestrator.Core;

// Parallel-safe: deterministic path classification only.
public sealed class ExecutionConductorRelaunchPathsTests
{
    [Xunit.Fact]
    public void MovedRuntimeSources_RequireConductorRelaunch()
    {
        string[] paths =
        [
            "src/Mcg.AgentOrchestrator.Execution/Persistence/OperatorIntentStore.cs",
            "src/Mcg.AgentOrchestrator.Execution/Processes/BackgroundDispatchRunner.cs"
        ];
        foreach (var path in paths)
        {
            Assert.True(RepositoryChangeClassifier.Classify([path]).RequiresConductorRelaunch, path);
            var decision = RepositoryChangeClassifier.DecideConductorRelaunch([path]);
            Assert.True(decision.Required, path);
            Assert.Equal("conductor-source", decision.Classification);
        }
    }
}
