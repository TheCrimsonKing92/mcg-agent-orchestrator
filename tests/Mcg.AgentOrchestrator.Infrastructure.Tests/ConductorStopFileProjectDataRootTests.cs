using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: project state, target files and lock/launch probes are isolated per test.
public sealed class ConductorStopFileProjectDataRootTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void ProjectStopWritesDataRootAndTheLoopObservesIt()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            var workspace = OrchestratorWorkspace.ForProject("alpha", target,
                dataRootDirectory: Path.Combine(root, "data"));
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            File.WriteAllText(Path.Combine(target, "owned.txt"), "unchanged");
            var before = Directory.GetFileSystemEntries(target).Order(StringComparer.Ordinal).ToArray();
            using var writer = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, CliConductorCommand.Run(["conductor", "stop"], workspace,
                lockProbe: new ConductorVerbStartTests.FixedProbe(7654), output: writer, error: error));
            Assert.Equal("", error.ToString());
            Assert.Equal(Path.Combine(workspace.OrchestratorDirectory, ConductorBatchLoop.StopFileName),
                workspace.ConductorStopFilePath);
            Assert.True(File.Exists(workspace.ConductorStopFilePath));
            Assert.Equal(before, Directory.GetFileSystemEntries(target).Order(StringComparer.Ordinal));
            Assert.Equal("unchanged", File.ReadAllText(Path.Combine(target, "owned.txt")));
            Assert.False(File.Exists(Path.Combine(target, ConductorBatchLoop.StopFileName)));

            var summary = new ConductorBatchLoop(workspace: workspace).Run(
                new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative,
                workspace.ConductorStopFilePath, keepAliveWhenIdle: true,
                sleepFunc: _ => throw new InvalidOperationException("Stop file was not observed before sleeping."));
            Assert.Equal("stop-file", summary.StopReason);

            var launcher = new ConductorVerbStartTests.RecordingLauncher();
            Assert.Equal(1, CliConductorCommand.Run(["conductor", "start"], workspace,
                launcher, new ConductorVerbStartTests.FixedProbe(null), output: writer, error: error,
                home: OrchestratorHome.Resolve(root, _ => null)));
            Assert.Empty(launcher.Requests);
            Assert.Contains(workspace.ConductorStopFilePath, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void DefaultProjectRetainsRepositoryRootStopPath()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Assert.Equal(Path.Combine(fixture.Workspace.ExecutionDirectory, ConductorBatchLoop.StopFileName),
            fixture.Workspace.ConductorStopFilePath);
    }
}
