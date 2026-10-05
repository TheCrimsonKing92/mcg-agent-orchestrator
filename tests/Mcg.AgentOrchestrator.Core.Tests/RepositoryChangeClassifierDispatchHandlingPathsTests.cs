using Mcg.AgentOrchestrator.Core;

public sealed class RepositoryChangeClassifierDispatchHandlingPathsTests
{
    [Xunit.Theory]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/AgentOutputDirectives.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.DeferredNoChange.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Execution/Processes/BackgroundDispatchRunner.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Execution/Processes/DispatchProcessHost.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Execution/Processes/GracefulDispatchDetacher.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Execution/Workers/WorkerResultParser.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Execution/Processes/ProcessLogReader.Decisions.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Execution/Processes/WorkerProcessJobs.cs")]
    [Xunit.InlineData("src\\Mcg.AgentOrchestrator.Execution\\Processes\\BackgroundDispatchContracts.cs")]
    public void DispatchAndResultHandlingChangesKeepTheFullDrain(string path) =>
        Assert.True(RepositoryChangeClassifier.TouchesDispatchResultHandling([path]), path);

    [Xunit.Theory]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs")]
    [Xunit.InlineData("docs/operator-runbook.md")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Execution/Processes/BackgroundDispatchNotes.md")]
    public void UnrelatedChangesAllowTheCappedDrain(string path) =>
        Assert.False(RepositoryChangeClassifier.TouchesDispatchResultHandling([path]), path);
}
