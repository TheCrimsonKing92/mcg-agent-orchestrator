using Mcg.AgentOrchestrator.App.Orchestration;

[Collection(TestCollections.CliProcessEnvironment)]
public sealed class OrchestratorDataRootCliProcessEnvironmentTests
{
    [Fact]
    public void RelativeEnvironmentRootRoutesProjectsAndPreservesDefaultPaths()
    {
        var temp = SharedTestSupport.CreateTempDirectory();
        var previous = Environment.GetEnvironmentVariable(OrchestratorDataRoot.EnvironmentVariable);
        try
        {
            var target = Path.Combine(temp, "target");
            var data = Path.Combine(temp, "data");
            Environment.SetEnvironmentVariable(OrchestratorDataRoot.EnvironmentVariable,
                Path.GetRelativePath(Environment.CurrentDirectory, data));
            var workspace = OrchestratorWorkspace.ForProject("alpha", target);
            Assert.Equal(Path.Combine(data, "projects", "alpha", "state.db"), workspace.SqliteStatePath);
            Assert.Equal(target, workspace.ExecutionDirectory);
            Assert.Equal(Path.Combine(data, "projects", "alpha", "tenants", "t1", "state.db"),
                OrchestratorWorkspace.ForProject("alpha", target, tenantName: "t1").SqliteStatePath);
            var defaults = OrchestratorWorkspace.ForProject("default", target);
            Assert.Equal(Path.Combine(target, ".orchestrator"), defaults.OrchestratorDirectory);
            Assert.Equal(Path.Combine(target, ".orchestrator", "state.db"), defaults.SqliteStatePath);
            Assert.Equal(Path.Combine(target, ".orchestrator", "backlog.db"), defaults.BacklogStorePath);
            Assert.Equal(Path.Combine(target, "continuation-watches.json"), defaults.ContinuationStorePath);
            Environment.SetEnvironmentVariable(OrchestratorDataRoot.EnvironmentVariable, null);
            Assert.Equal(OrchestratorDataRoot.ResolveDefaultDirectory(), OrchestratorDataRoot.Resolve().RootDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OrchestratorDataRoot.EnvironmentVariable, previous);
            SharedTestSupport.RemoveTempDirectory(temp);
        }
    }
}
