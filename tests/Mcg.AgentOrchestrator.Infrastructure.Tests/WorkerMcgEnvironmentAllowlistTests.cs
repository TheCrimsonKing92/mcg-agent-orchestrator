using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: reads repository source and immutable names; no process environment mutation.
public sealed class WorkerMcgEnvironmentAllowlistTests
{
    [Fact]
    public void Names_AreExactlyTheFourOrchestratorHandoffConstants()
    {
        string[] expected =
        [
            DispatchProcessHost.StartGatePathVariable,
            WorkerSandboxOptions.DispatchWorkerVariable,
            DotnetBuildStorageLayout.IsolatedRootOverrideVariable,
            "MCG_ORCHESTRATOR_DOTNET_PATH"
        ];

        Assert.Equal(4, WorkerMcgEnvironmentAllowlist.Names.Count);
        Assert.True(WorkerMcgEnvironmentAllowlist.Names.SetEquals(expected));
    }

    [Fact]
    public void DispatchProcessHost_ScrubsWorkerStartInfoBeforeMarkerAssignment()
    {
        var root = VerifiedRepositoryRoot.Find();
        var processDirectory = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Execution", "Processes");
        var lines = File.ReadAllLines(Path.Combine(processDirectory, "DispatchProcessHost.cs"))
            .Select(line => line.Trim()).ToArray();
        var scrubIndex = Array.IndexOf(lines,
            "WorkerMcgEnvironmentAllowlist.RemoveDisallowed(startInfo.Environment);");
        var markerIndex = Array.IndexOf(lines,
            "startInfo.Environment[WorkerSandboxOptions.DispatchWorkerVariable] = \"1\";");

        Assert.True(scrubIndex >= 0, "Worker launch must scrub its start-info environment.");
        Assert.Equal(scrubIndex + 1, markerIndex);

        var allowlistSource = File.ReadAllText(
            Path.Combine(processDirectory, "WorkerMcgEnvironmentAllowlist.cs"));
        Assert.DoesNotMatch(new Regex(@"\bpartial\b"), allowlistSource);
    }
}
