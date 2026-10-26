using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Runs real dotnet builds with isolated source, cache, store and policy directories.
[Xunit.Collection("ProcessSpawningHostBound")]
public sealed class ConductorSelfRelaunchTestsRelaunchBuildQosOptOutTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void StoreBuild_UsesOnlyTheSuppliedPolicy(bool enabled, bool supplyStatePath)
    {
        using var fixture = new BuildFixture(enabled);
        var calls = new ConcurrentQueue<(int Pid, uint Control, uint State, int? Parent)>();
        var buildCalls = 0;
        var store = new LandingAppBuildStore(Path.Combine(fixture.Root, "store"), build: (request, ct) =>
        {
            buildCalls++;
            return ConductorSelfRelaunch.RunAppBuildProcess("dotnet", request, ct,
                supplyStatePath ? fixture.StatePath : null,
                (pid, control, state) =>
                {
                    calls.Enqueue((pid, control, state, ProcessParentIdResolver.TryGetParentProcessId(pid)));
                    return true;
                });
        });

        var output = store.GetOrBuild(fixture.Root, new string('a', 40), TimeSpan.FromSeconds(90),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, buildCalls); // A reused store entry must not make a disabled assertion vacuous.
        Assert.True(File.Exists(Path.Combine(output, LandingAppBuildStore.AppDllName)));
        Assert.True(File.Exists(Path.Combine(output, LandingAppBuildStore.CompleteMarkerName)));
        if (enabled && supplyStatePath)
        {
            Assert.NotEmpty(calls);
            var first = calls.First();
            // The first synchronous setter must target the build root, a direct child of this host.
            Assert.Equal(Environment.ProcessId, first.Parent);
            Assert.True(first.Pid > 0);
            Assert.All(calls, call => Assert.Equal((1u, 0u), (call.Control, call.State)));
            var line = Assert.Single(File.ReadAllLines(fixture.LogPath)
                .Where(line => line.Contains("BUILD_QOS kind=relaunch-build", StringComparison.Ordinal)));
            Assert.Contains($"opted_out={calls.Count} tried={calls.Count}", line, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(calls);
            Assert.False(File.Exists(fixture.LogPath));
        }
    }

    private sealed class BuildFixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("r-").FullName;
        private string ProjectDirectory => Path.Combine(Root, "src", "Mcg.AgentOrchestrator.App");
        private string ProjectPath => Path.Combine(ProjectDirectory, "Mcg.AgentOrchestrator.App.csproj");
        private string OutputDirectory => Path.Combine(Root, "output");
        internal string StatePath => Path.Combine(Root, "isolated-state", "state.db");
        internal string LogPath => Path.Combine(Path.GetDirectoryName(StatePath)!, "logs", ConductEventLogWriter.CurrentFileName);

        internal BuildFixture(bool enabled)
        {
            Directory.CreateDirectory(ProjectDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var repository = InfrastructureTestSupport.FindRepositoryRoot();
            File.Copy(Path.Combine(repository, "Directory.Build.props"), Path.Combine(Root, "Directory.Build.props"));
            File.Copy(Path.Combine(repository, "Directory.Build.rsp"), Path.Combine(Root, "Directory.Build.rsp"));
            File.WriteAllText(ProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <NuGetAudit>false</NuGetAudit>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(ProjectDirectory, "Class.cs"), "public class Sample { }");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(StatePath)!, "conductor-policy.json"),
                (ConductorAutonomyPolicy.Conservative with
                {
                    AcceptanceChildrenOptOutExecutionThrottling = enabled
                }).ToJson());
        }

        public void Dispose()
        {
            var cacheRoot = ConductorSelfRelaunch.CreateAppBuildCommand(ProjectPath, OutputDirectory).IsolatedArtifactsRoot;
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
            Directory.Delete(Root, recursive: true);
        }
    }
}
