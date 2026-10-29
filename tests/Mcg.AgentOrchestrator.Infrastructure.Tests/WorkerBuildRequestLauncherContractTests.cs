using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each launcher has fixture-owned stores, repositories, slots and contained children.
public sealed class BuildRequestLauncherContractTests
{
    [Theory]
    [InlineData(false, WorkerBuildOutcome.Success, 0)]
    [InlineData(true, WorkerBuildOutcome.CompilerFailure, 1)]
    public async Task Execute_RealAndFake_PreserveOutcomeExitAndPhases(
        bool compilerFailure, WorkerBuildOutcome expectedOutcome, int expectedExit)
    {
        using var real = new WorkerBuildRequestFixture(compilerFailure,
            compilerMode: WorkerBuildRequestFixture.CompilerMode.Real);
        Assert.Null(real.LaunchOverride);
        var realInvocation = await real.RunHelper();
        Assert.Equal(expectedOutcome, realInvocation.Outcome);
        Assert.Equal(expectedExit, realInvocation.CompilerExitCode);
        Assert.Single(realInvocation.Children!);

        using var fake = new WorkerBuildRequestFixture(compilerFailure,
            compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        Assert.NotNull(fake.LaunchOverride);
        var fakeInvocation = await fake.RunHelper();
        Assert.Equal(expectedOutcome, fakeInvocation.Outcome);
        Assert.Equal(expectedExit, fakeInvocation.CompilerExitCode);
        Assert.Single(fakeInvocation.Children!);

        Assert.Equal(realInvocation.Outcome, fakeInvocation.Outcome);
        Assert.Equal(realInvocation.CompilerExitCode, fakeInvocation.CompilerExitCode);
        Assert.Equal(realInvocation.Observations.Select(o => o.Phase).ToArray(),
            fakeInvocation.Observations.Select(o => o.Phase).ToArray());
    }

    [Fact]
    public async Task Execute_Override_ReceivesBuildStartInfoAndLaunchesReplacement()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var launches = new List<ProcessStartInfo>();
        var host = new WorkerBuildRequestHost(fixture.Reference, storageRoot: fixture.Storage,
            launchOverride: (project, info) =>
            {
                Assert.Equal(WorkerBuildRequestFixture.Project, project);
                var gcEnvironment = BuildGcEnvironment.ForStatePath(fixture.StatePath);
                var baseline = WorkerBuildRequestHost.BuildStartInfo(fixture.RepositoryRoot, project, "Debug", [],
                    info.Environment["TEMP"]!, gcEnvironment);
                var expected = WorkerBuildRequestHost.BuildStartInfo(fixture.RepositoryRoot, project, "Debug",
                    info.ArgumentList.Skip(baseline.ArgumentList.Count).ToArray(), info.Environment["TEMP"]!, gcEnvironment);
                Assert.Equal(expected.FileName, info.FileName);
                Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(info.FileName), ignoreCase: true);
                Assert.Equal(expected.WorkingDirectory, info.WorkingDirectory);
                Assert.Equal(expected.UseShellExecute, info.UseShellExecute);
                Assert.Equal(expected.CreateNoWindow, info.CreateNoWindow);
                Assert.Equal(expected.ArgumentList.ToArray(), info.ArgumentList.ToArray());
                Assert.Equal(expected.Environment.OrderBy(p => p.Key).ToArray(), info.Environment.OrderBy(p => p.Key).ToArray());
                launches.Add(info);
                var replacement = fixture.LaunchOverride!(project, info);
                Assert.NotSame(info, replacement);
                Assert.Equal(info.WorkingDirectory, replacement.WorkingDirectory);
                Assert.Equal(info.Environment.OrderBy(p => p.Key).ToArray(), replacement.Environment.OrderBy(p => p.Key).ToArray());
                return replacement;
            });
        using var current = Process.GetCurrentProcess();
        var response = await host.ExecuteAsync(new WorkerBuildRequest(1, "launch-contract",
            [WorkerBuildRequestFixture.Project], "Debug"), WorkerBuildRequestChannel.ReadIdentity(current), CancellationToken.None);

        Assert.Equal(WorkerBuildOutcome.Success, response.Outcome);
        Assert.Single(launches);
        var invocation = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value)
            .GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
        Assert.Equal(0, invocation.CompilerExitCode);
        Assert.Single(invocation.Children!);
        fixture.AssertCompilerImages(invocation);
    }
}
