using Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class DotnetBuildEnvironmentManagerTests
{
    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_defers_compiler_lock_recovery_until_the_last_lease_is_released")]
    public void DotnetBuildEnvironmentManagerDefersCompilerLockRecoveryUntilTheLastLeaseIsReleased()
    {
        var shutdownCount = 0;
        Assert.NotNull(DotnetBuildEnvironmentManager.ShutdownBuildServersForTests);
        DotnetBuildEnvironmentManager.ShutdownBuildServersForTests = () => shutdownCount++;
        try
        {
            using var first = RootedDotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                StorageRoot,
                TimeSpan.Zero);
            using var second = RootedDotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                StorageRoot,
                TimeSpan.Zero);

            first.MarkCompilerLockRemediationRequired();
            first.Dispose();
            Assert.Equal(0, shutdownCount);

            second.Dispose();
            second.Dispose();
            Assert.Equal(1, shutdownCount);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ShutdownBuildServersForTests =
                AssemblyBuildServerShutdownIsolation.SafeDefault;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_keeps_shared_compilation_disabled_for_all_repository_builds")]
    public void DotnetBuildEnvironmentManagerKeepsSharedCompilationDisabledForAllRepositoryBuilds()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var project = Path.Combine(
            repositoryRoot,
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "Fixtures",
            "IsolatedDotnetProbe",
            "Mcg.AgentOrchestrator.IsolatedDotnetProbe.csproj");
        var isolatedArtifacts = Path.Combine(StorageRoot.RootPath, "property-evaluation");

        var repoTreeValue = RunCommand(
            "dotnet",
            repositoryRoot,
            "msbuild",
            project,
            "-getProperty:UseSharedCompilation",
            "-nologo");
        var isolatedValue = RunCommand(
            "dotnet",
            repositoryRoot,
            "msbuild",
            project,
            "-getProperty:UseSharedCompilation",
            "-nologo",
            $"-p:McgIsolatedArtifactsPath={isolatedArtifacts}");

        Assert.Equal("false", repoTreeValue.Trim());
        Assert.Equal("false", isolatedValue.Trim());
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_gate_cpu_limit_precedes_worker_limit_without_changing_worker_default")]
    public void DotnetBuildEnvironmentManagerGateCpuLimitPrecedesWorkerLimitWithoutChangingWorkerDefault()
    {
        using var workerLimit = EnvVarScope.ForVariable(DotnetBuildEnvironmentManager.BuildMaxCpuCountVariable, "7");
        using (EnvVarScope.ForVariable(DotnetBuildEnvironmentManager.GateBuildMaxCpuCountVariable, "2"))
        {
            Assert.Contains(
                "-maxcpucount:2",
                RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, null, "gate-limit").Arguments);
        }

        using (EnvVarScope.ForVariable(DotnetBuildEnvironmentManager.GateBuildMaxCpuCountVariable, null))
        {
            Assert.Contains(
                "-maxcpucount:7",
                RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, null, "worker-fallback").Arguments);
        }

        using (EnvVarScope.ForVariable(DotnetBuildEnvironmentManager.BuildMaxCpuCountVariable, null))
        using (EnvVarScope.ForVariable(DotnetBuildEnvironmentManager.GateBuildMaxCpuCountVariable, null))
        {
            Assert.Contains(
                $"-maxcpucount:{Math.Max(2, Environment.ProcessorCount / DotnetBuildEnvironmentManager.BuildConcurrencySlotCount)}",
                RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, null, "default-fallback").Arguments);
        }

        using (EnvVarScope.ForVariable(DotnetBuildEnvironmentManager.GateBuildMaxCpuCountVariable, "1"))
        {
            Assert.Contains(
                "-maxcpucount:7",
                RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, null, "invalid-gate-fallback").Arguments);
        }

        var script = File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), "scripts", "Invoke-IsolatedDotnet.ps1"));
        var maxCpuFunction = script[script.IndexOf("function Get-BuildMaxCpuCount", StringComparison.Ordinal)..
            script.IndexOf("function Get-HostTempBase", StringComparison.Ordinal)];
        Assert.Contains("$env:MCG_BUILD_MAXCPUCOUNT", maxCpuFunction, StringComparison.Ordinal);
        Assert.Contains("return 1", maxCpuFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("MCG_GATE_BUILD_MAXCPUCOUNT", maxCpuFunction, StringComparison.Ordinal);
    }
}
