using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;

// These tests mutate the process-global MCG_DOTNET_ISOLATED_ROOT env var (via EnvVarScope). xUnit
// runs distinct test classes in parallel, so without a shared collection they clobber each other's
// root and flake. Pinning every env-var-mutating class to one non-parallel collection serializes them.
[Xunit.CollectionDefinition("IsolatedDotnetRoot", DisableParallelization = true)]
public sealed class IsolatedDotnetRootCollection : Xunit.ICollectionFixture<IsolatedDotnetRootFixture>
{
}

// Pins MCG_DOTNET_ISOLATED_ROOT to an ephemeral per-run temp root for the ENTIRE IsolatedDotnetRoot
// collection, so lease-acquiring tests (GoalAcceptanceVerifier/LocalProcessVerifier, which do NOT set
// their own per-test scope) never touch the shared firewall slots (e.g. slot-0). Without this, when the
// suite itself runs inside a slot-routed harness holding slot-0, those tests deadlock waiting for the
// lease execution lock their own host process already holds. Per-test EnvVarScope overrides nest cleanly
// on top of this baseline (they save/restore whatever value is current).
public sealed class IsolatedDotnetRootFixture : IDisposable
{
    private readonly string? _originalValue;
    private readonly string _root;

    public IsolatedDotnetRootFixture()
    {
        _originalValue = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        _root = Path.Combine(Path.GetTempPath(), $"{DotnetBuildEnvironmentManager.RootDirectoryName}-collection-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _originalValue);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}

[Xunit.Collection("IsolatedDotnetRoot")]
public sealed class DotnetBuildEnvironmentManagerTests
{
    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reuses_goal_lease_with_metadata_and_cleanup")]
    public void DotnetBuildEnvironmentManagerReusesGoalLeaseWithMetadataAndCleanup()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("feedbeeffeedbeeffeedbeeffeedbeef");
        try
        {
            var first = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "Acceptance");
            var second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "Acceptance");

            Assert.Equal(DotnetBuildEnvironmentManager.GoalRoot(goalId), first.RootPath);
            Assert.Equal("goal-feedbeef", first.LeaseId);
            Assert.Equal(first.LeaseId, second.LeaseId);
            Assert.Equal(first.RootPath, second.RootPath);
            Assert.Equal(first.ArtifactsPath, second.ArtifactsPath);
            Assert.True(second.ReusedGoalLease);
            Assert.Equal(DotnetBuildEnvironmentManager.GoalArtifactsPath(goalId), first.ArtifactsPath);
            Assert.True(first.ArtifactsPath.Contains(Path.Combine("slots", "slot-"), StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(first.ArtifactsPath));
            Assert.True(Directory.Exists(second.ArtifactsPath));
            Assert.False(string.IsNullOrWhiteSpace(second.LeaseMetadataPath));
            Assert.True(File.Exists(second.LeaseMetadataPath));
            Assert.True(first.Arguments.Contains("--artifacts-path"));
            Assert.False(first.Arguments.Contains("--disable-build-servers"));
            Assert.DoesNotContain(first.Arguments, argument => argument.Equals("-p:UseSharedCompilation=false", StringComparison.Ordinal));
            Assert.Contains(first.Arguments, argument => argument.StartsWith("-maxcpucount:", StringComparison.Ordinal) && !argument.Equals("-maxcpucount:1", StringComparison.Ordinal));
            Assert.True(first.Arguments.Contains(first.ArtifactsPath));
            var otherGoalId = new GoalId("cafebabecafebabecafebabecafebabe");
            var other = DotnetBuildEnvironmentManager.CreateAttempt(otherGoalId, "Acceptance");
            Assert.True(other.ArtifactsPath.Contains(Path.Combine("slots", "slot-"), StringComparison.OrdinalIgnoreCase));
            Assert.True(DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(otherGoalId));
            using var metadata = JsonDocument.Parse(File.ReadAllText(second.LeaseMetadataPath!));
            Assert.Equal(goalId.Value, metadata.RootElement.GetProperty("goalId").GetString());
            Assert.Equal(first.LeaseId, metadata.RootElement.GetProperty("leaseId").GetString());
            Assert.Equal(second.ArtifactsPath, metadata.RootElement.GetProperty("artifactsPath").GetString());

            Assert.True(DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId));
            Assert.False(Directory.Exists(first.RootPath));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reuses_stable_manual_slot")]
    public void DotnetBuildEnvironmentManagerReusesStableManualSlot()
    {
        var first = DotnetBuildEnvironmentManager.CreateAttempt(null, "Acceptance");
        var second = DotnetBuildEnvironmentManager.CreateAttempt(null, "Retry");

        Assert.Equal("run-slot-manual", first.LeaseId);
        Assert.Equal(first.RootPath, second.RootPath);
        Assert.Equal(first.ArtifactsPath, second.ArtifactsPath);
        Assert.True(first.ArtifactsPath.Contains(Path.Combine("slots", "manual", "artifacts"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(first.ExecutionLockPath, second.ExecutionLockPath);
        Assert.True(first.Arguments.Contains(first.ArtifactsPath));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_detects_stale_locks_and_rotates_goal_lease")]
    public void DotnetBuildEnvironmentManagerDetectsStaleLocksAndRotatesGoalLease()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("decafbaddecafbaddecafbaddecafbad");
        try
        {
            var first = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "test");
            File.WriteAllText(Path.Combine(first.RootPath, "lease", "lease.lock"), "999999");

            var second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "retry");

            Assert.True(second.StaleLockCleared);
            Assert.Equal(first.ArtifactsPath, second.ArtifactsPath);
            Assert.True(DotnetBuildEnvironmentManager.TryRotateGoalLease(goalId, "corrupt-cache"));
            var third = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "after-rotate");
            Assert.False(third.ReusedGoalLease);
            Assert.Equal(first.LeaseId, third.LeaseId);
            Assert.True(Directory.Exists(Path.Combine(third.RootPath, "rotated-leases")));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_inspects_and_cleans_orphaned_goal_lease")]
    public void DotnetBuildEnvironmentManagerInspectsAndCleansOrphanedGoalLease()
    {
        using var envScope = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("0badcafe0badcafe0badcafe0badcafe");
        try
        {
            var environment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "test");
            var active = DotnetBuildEnvironmentManager.InspectGoalLease(goalId);
            Assert.Equal(environment.LeaseId, active.LeaseId);
            Assert.True(active.OwnerProcessAlive);
            Assert.False(active.CanCleanup);
            Assert.False(DotnetBuildEnvironmentManager.TryCleanupOrphanedGoalLease(goalId, out _, out var activeDetail));
            Assert.True(activeDetail.Contains("Refusing to delete active build lease", StringComparison.Ordinal));

            using (var metadata = JsonDocument.Parse(File.ReadAllText(environment.LeaseMetadataPath!)))
            {
                var orphanedJson = metadata.RootElement.GetRawText().Replace(
                    $"\"ownerProcessId\": {Environment.ProcessId}",
                    "\"ownerProcessId\": 999999",
                    StringComparison.Ordinal);
                File.WriteAllText(environment.LeaseMetadataPath!, orphanedJson);
            }

            var orphaned = DotnetBuildEnvironmentManager.InspectGoalLease(goalId);
            Assert.False(orphaned.OwnerProcessAlive);
            Assert.True(orphaned.CanCleanup);
            Assert.True(orphaned.Detail.Contains("orphaned", StringComparison.Ordinal));
            Assert.True(DotnetBuildEnvironmentManager.TryCleanupOrphanedGoalLease(goalId, out _, out var cleanupDetail));
            Assert.True(cleanupDetail.Contains("Deleted orphaned build lease", StringComparison.Ordinal));
            Assert.False(Directory.Exists(environment.RootPath));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_serializes_same_goal_lease_execution")]
    public async Task DotnetBuildEnvironmentManagerSerializesSameGoalLeaseExecution()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("1234abcd1234abcd1234abcd1234abcd");
        try
        {
            var first = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "developer");
            var second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "tester");
            using var firstLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(first);
            var secondLockTask = Task.Run(() => DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(second));
            var earlyWinner = await Task.WhenAny(secondLockTask, Task.Delay(200));
            Xunit.Assert.NotEqual(secondLockTask, earlyWinner);

            firstLock.Dispose();
            using var secondLock = await secondLockTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(first.ExecutionLockPath, second.ExecutionLockPath);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reuses_artifacts_for_same_goal_slot")]
    public void DotnetBuildEnvironmentManagerReusesArtifactsForSameGoalSlot()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("10293847102938471029384710293847");
        try
        {
            var first = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "first");
            using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(first))
            {
                File.WriteAllText(Path.Combine(first.ArtifactsPath, "warm-cache.txt"), "keep");
                Directory.CreateDirectory(Path.Combine(first.ArtifactsPath, "obj"));
                File.WriteAllText(Path.Combine(first.ArtifactsPath, "obj", "stale-cache.txt"), "delete");
                Directory.CreateDirectory(Path.Combine(first.ArtifactsPath, "bin"));
                File.WriteAllText(Path.Combine(first.ArtifactsPath, "bin", "stale.dll"), "delete");
            }

            var second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "second");
            using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(second))
            {
                Assert.True(File.Exists(Path.Combine(second.ArtifactsPath, "warm-cache.txt")));
            }

            File.WriteAllText(Path.Combine(second.RootPath, "lease", "lease.lock"), "999999");
            var stale = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "stale-owner");
            using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(stale))
            {
                Assert.False(File.Exists(Path.Combine(stale.ArtifactsPath, "warm-cache.txt")));
                Assert.False(File.Exists(Path.Combine(stale.ArtifactsPath, "obj", "stale-cache.txt")));
                Assert.False(File.Exists(Path.Combine(stale.ArtifactsPath, "bin", "stale.dll")));
            }
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stable_slot_build_arguments_are_firewall_covered")]
    public void DotnetBuildEnvironmentManagerStableSlotBuildArgumentsAreFirewallCovered()
    {
        var firewallPaths = DotnetBuildEnvironmentManager.StableSlotTesthostFirewallPaths()
            .Select(path => Path.GetFullPath(path.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var slot = 0; slot < DotnetBuildEnvironmentManager.StableSlotCount; slot++)
        {
            var arguments = DotnetBuildEnvironmentManager.StableSlotBuildArguments(slot);
            var artifactsPath = ArgumentValue(arguments, "--artifacts-path");
            foreach (var project in new[] { "Mcg.AgentOrchestrator.Core.Tests", "Mcg.AgentOrchestrator.Infrastructure.Tests" })
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var derivedTesthostPath = Path.GetFullPath(Path.Combine(
                        artifactsPath,
                        "bin",
                        project,
                        $"{configuration.ToLowerInvariant()}_net10.0",
                        "testhost.exe"));

                    Assert.True(firewallPaths.Contains(derivedTesthostPath));
                }
            }
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_caps_msbuild_parallelism_per_slot")]
    public void DotnetBuildEnvironmentManagerCapsMsbuildParallelismPerSlot()
    {
        using var defaultScope = EnvVarScope.ForVariable(DotnetBuildEnvironmentManager.BuildMaxCpuCountVariable, null);
        var defaultArguments = DotnetBuildEnvironmentManager.StableSlotBuildArguments(0);
        var expectedDefault = Math.Max(2, Environment.ProcessorCount / DotnetBuildEnvironmentManager.StableSlotCount);

        Assert.Equal($"-maxcpucount:{expectedDefault}", MaxCpuCountArgument(defaultArguments));
        Assert.NotEqual("-maxcpucount:1", MaxCpuCountArgument(defaultArguments));
        Assert.Contains("-p:BuildInParallel=false", defaultArguments);

        using var configuredScope = EnvVarScope.ForVariable(DotnetBuildEnvironmentManager.BuildMaxCpuCountVariable, "7");
        var configuredArguments = DotnetBuildEnvironmentManager.StableSlotBuildArguments(0);

        Assert.Equal("-maxcpucount:7", MaxCpuCountArgument(configuredArguments));
        Assert.Contains("-p:BuildInParallel=false", configuredArguments);
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_forwards_args_when_operator_sandbox_config_is_inherited")]
    public void InvokeIsolatedDotnetForwardsArgsWhenOperatorSandboxConfigIsInherited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = ResolveRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var shimDirectory = Path.Combine(root, "shim");
        var workDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(workDirectory);
        try
        {
            var logPath = Path.Combine(root, "dotnet.log");
            var shimPath = Path.Combine(shimDirectory, "dotnet.cmd");
            File.WriteAllText(
                shimPath,
                """
                @echo off
                >> "%DOTNET_SHIM_LOG%" echo cwd=%CD%
                >> "%DOTNET_SHIM_LOG%" echo args=%*
                >> "%DOTNET_SHIM_LOG%" echo repo=%MCG_ORCHESTRATOR_REPOSITORY_ROOT%
                >> "%DOTNET_SHIM_LOG%" echo sandbox=%MCG_WORKER_SANDBOX%
                >> "%DOTNET_SHIM_LOG%" echo account=%MCG_WORKER_ACCOUNT%
                >> "%DOTNET_SHIM_LOG%" echo target=%MCG_WORKER_CREDENTIAL_TARGET%
                exit /b 0
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-InputFormat");
            startInfo.ArgumentList.Add("None");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-GoalPrefix");
            startInfo.ArgumentList.Add("feedbeef");
            startInfo.ArgumentList.Add("-AttemptName");
            startInfo.ArgumentList.Add("Shim Test");
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add("Fake.Tests.csproj");
            startInfo.ArgumentList.Add("--no-restore");
            startInfo.ArgumentList.Add("--filter");
            startInfo.ArgumentList.Add("FullyQualifiedName~FocusedTests");
            startInfo.EnvironmentVariables["PATH"] = shimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.EnvironmentVariables["DOTNET_SHIM_LOG"] = logPath;
            startInfo.EnvironmentVariables[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(root, "isolated-dotnet");
            startInfo.EnvironmentVariables[WorkerSandboxOptions.EnabledVariable] = "1";
            startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.DispatchWorkerVariable);
            startInfo.EnvironmentVariables[WorkerSandboxOptions.AccountVariable] = "sandbox-user";
            startInfo.EnvironmentVariables[WorkerSandboxOptions.CredentialTargetVariable] = "sandbox-target";

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "Invoke-IsolatedDotnet.ps1 did not exit within 10 seconds.");
            Assert.True(
                process.ExitCode == 0,
                $"Invoke-IsolatedDotnet.ps1 exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");

            var log = File.ReadAllText(logPath);
            Assert.True(log.Contains($"cwd={workDirectory}", StringComparison.OrdinalIgnoreCase));
            Assert.True(log.Contains("args=test Fake.Tests.csproj --no-restore --filter FullyQualifiedName~FocusedTests --artifacts-path ", StringComparison.Ordinal));
            Assert.True(log.Contains("-maxcpucount:", StringComparison.Ordinal));
            Assert.True(log.Contains("-p:BuildInParallel=false", StringComparison.Ordinal));
            Assert.True(log.Contains($"repo={workDirectory}", StringComparison.OrdinalIgnoreCase));
            Assert.True(log.Contains("args=build-server shutdown", StringComparison.Ordinal));
            Assert.DoesNotContain("--disable-build-servers", log);
            Assert.DoesNotContain("-p:UseSharedCompilation=false", log);
            Assert.DoesNotContain("sandbox=1", log);
            Assert.DoesNotContain("account=sandbox-user", log);
            Assert.DoesNotContain("target=sandbox-target", log);
            Assert.True(string.IsNullOrWhiteSpace(stdout), stdout);
            Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_updates_AppDll_git_head_marker_after_successful_rebuild")]
    public void InvokeIsolatedDotnetUpdatesAppDllGitHeadMarkerAfterSuccessfulRebuild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = ResolveRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var shimDirectory = Path.Combine(root, "shim");
        var workDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(workDirectory);
        try
        {
            RunCommand("git", workDirectory, "init", "--initial-branch=main");
            RunCommand("git", workDirectory, "config", "user.email", "test@example.invalid");
            RunCommand("git", workDirectory, "config", "user.name", "Isolated Dotnet Test");
            File.WriteAllText(Path.Combine(workDirectory, "README.md"), "base");
            RunCommand("git", workDirectory, "add", "README.md");
            RunCommand("git", workDirectory, "commit", "-m", "base");
            var expectedHead = RunCommand("git", workDirectory, "rev-parse", "HEAD").Trim();

            var shimPath = Path.Combine(shimDirectory, "dotnet.cmd");
            File.WriteAllText(
                shimPath,
                """
                @echo off
                if "%~1"=="build-server" exit /b 0
                set "APP_DIR=%CD%\src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0"
                mkdir "%APP_DIR%" >nul 2>nul
                echo rebuilt>"%APP_DIR%\Mcg.AgentOrchestrator.App.dll"
                exit /b 0
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-InputFormat");
            startInfo.ArgumentList.Add("None");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-GoalPrefix");
            startInfo.ArgumentList.Add("feedbeef");
            startInfo.ArgumentList.Add("-AttemptName");
            startInfo.ArgumentList.Add("Marker Test");
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add("Fake.Tests.csproj");
            startInfo.ArgumentList.Add("--no-restore");
            startInfo.EnvironmentVariables["PATH"] = shimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.EnvironmentVariables[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(root, "isolated-dotnet");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "Invoke-IsolatedDotnet.ps1 did not exit within 10 seconds.");
            Assert.True(
                process.ExitCode == 0,
                $"Invoke-IsolatedDotnet.ps1 exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");

            var markerPath = Path.Combine(
                workDirectory,
                "src",
                "Mcg.AgentOrchestrator.App",
                "bin",
                "Debug",
                "net10.0",
                "Mcg.AgentOrchestrator.App.dll.git-head");
            Assert.Equal(expectedHead, File.ReadAllText(markerPath).Trim());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_does_not_update_stale_AppDll_marker_after_non_App_success")]
    public void InvokeIsolatedDotnetDoesNotUpdateStaleAppDllMarkerAfterNonAppSuccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = ResolveRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var shimDirectory = Path.Combine(root, "shim");
        var workDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(workDirectory);
        try
        {
            RunCommand("git", workDirectory, "init", "--initial-branch=main");
            RunCommand("git", workDirectory, "config", "user.email", "test@example.invalid");
            RunCommand("git", workDirectory, "config", "user.name", "Isolated Dotnet Test");
            File.WriteAllText(Path.Combine(workDirectory, "README.md"), "base");
            RunCommand("git", workDirectory, "add", "README.md");
            RunCommand("git", workDirectory, "commit", "-m", "base");
            var currentHead = RunCommand("git", workDirectory, "rev-parse", "HEAD").Trim();
            Assert.NotEqual("stale-test-head", currentHead);

            var appOutputPath = Path.Combine(
                workDirectory,
                "src",
                "Mcg.AgentOrchestrator.App",
                "bin",
                "Debug",
                "net10.0");
            Directory.CreateDirectory(appOutputPath);
            var appDllPath = Path.Combine(appOutputPath, "Mcg.AgentOrchestrator.App.dll");
            var markerPath = appDllPath + ".git-head";
            File.WriteAllText(appDllPath, "stale app host");
            File.WriteAllText(markerPath, "stale-test-head");
            var appDllLastWriteTime = File.GetLastWriteTimeUtc(appDllPath);

            var shimPath = Path.Combine(shimDirectory, "dotnet.cmd");
            File.WriteAllText(
                shimPath,
                """
                @echo off
                exit /b 0
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-InputFormat");
            startInfo.ArgumentList.Add("None");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-GoalPrefix");
            startInfo.ArgumentList.Add("feedbeef");
            startInfo.ArgumentList.Add("-AttemptName");
            startInfo.ArgumentList.Add("Non App Marker Test");
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add("Fake.Tests.csproj");
            startInfo.ArgumentList.Add("--no-restore");
            startInfo.EnvironmentVariables["PATH"] = shimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.EnvironmentVariables[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(root, "isolated-dotnet");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "Invoke-IsolatedDotnet.ps1 did not exit within 10 seconds.");
            Assert.True(
                process.ExitCode == 0,
                $"Invoke-IsolatedDotnet.ps1 exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");

            Assert.Equal("stale-test-head", File.ReadAllText(markerPath).Trim());
            Assert.Equal(appDllLastWriteTime, File.GetLastWriteTimeUtc(appDllPath));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_blocks_worker_dispatch_before_dotnet_launch")]
    public void InvokeIsolatedDotnetBlocksWorkerDispatchBeforeDotnetLaunch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = ResolveRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var shimDirectory = Path.Combine(root, "shim");
        var workDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(workDirectory);
        try
        {
            var logPath = Path.Combine(root, "dotnet.log");
            var shimPath = Path.Combine(shimDirectory, "dotnet.cmd");
            File.WriteAllText(
                shimPath,
                """
                @echo off
                >> "%DOTNET_SHIM_LOG%" echo args=%*
                exit /b 0
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-InputFormat");
            startInfo.ArgumentList.Add("None");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-GoalPrefix");
            startInfo.ArgumentList.Add("feedbeef");
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add("Fake.Tests.csproj");
            startInfo.Environment["PATH"] = shimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["DOTNET_SHIM_LOG"] = logPath;
            startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(root, "isolated-dotnet");
            startInfo.Environment[WorkerSandboxOptions.DispatchWorkerVariable] = "1";

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "Invoke-IsolatedDotnet.ps1 did not exit within 10 seconds.");

            Assert.NotEqual(0, process.ExitCode);
            Assert.True(!File.Exists(logPath), "dotnet shim should not be invoked for worker-side self-verification.");
            Assert.True(stderr.Contains("Worker-side .NET self-verification is disabled", StringComparison.Ordinal), stderr);
            Assert.True(string.IsNullOrWhiteSpace(stdout), stdout);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (IsRepositoryRoot(directory.FullName))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        var environmentRoot = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
        if (IsRepositoryRoot(environmentRoot))
        {
            return Path.GetFullPath(environmentRoot!);
        }

        throw new InvalidOperationException("Could not resolve repository root for Invoke-IsolatedDotnet.ps1.");
    }

    private static bool IsRepositoryRoot(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            File.Exists(Path.Combine(path, "scripts", "Invoke-IsolatedDotnet.ps1"));
    }

    private static string RunCommand(string fileName, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10000), $"{fileName} did not exit within 10 seconds.");
        Assert.True(
            process.ExitCode == 0,
            $"{fileName} {string.Join(' ', arguments)} exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        return stdout;
    }

    [Xunit.Fact(DisplayName = "ProcessSpawnGuard_clears_inheritable_state_db_file_handles")]
    public void ProcessSpawnGuardClearsInheritableStateDbFileHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"mcg-state-handle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");
        try
        {
            using var handle = CreateInheritableFileHandle(path);
            Assert.True(ProcessSpawnGuard.IsHandleInheritable(handle.DangerousGetHandle()));

            var cleared = ProcessSpawnGuard.ClearInheritableFileHandles("state.db");

            Assert.True(cleared >= 1);
            Assert.False(ProcessSpawnGuard.IsHandleInheritable(handle.DangerousGetHandle()));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string ArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        var index = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Equals(name, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        Assert.True(index >= 0 && index <= arguments.Count - 2);
        return arguments[index + 1];
    }

    private static string MaxCpuCountArgument(IReadOnlyList<string> arguments)
    {
        var argument = arguments.SingleOrDefault(argument => argument.StartsWith("-maxcpucount:", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(argument));
        return argument!;
    }

    private static SafeFileHandle CreateInheritableFileHandle(string path)
    {
        var securityAttributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true
        };
        var handle = CreateFile(
            path,
            0x40000000,
            0x00000001 | 0x00000002,
            ref securityAttributes,
            2,
            0x80,
            IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;
        private readonly string? _root;

        private EnvVarScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            _root = value;
            Environment.SetEnvironmentVariable(name, value);
        }

        public static EnvVarScope ForIsolatedDotnetRoot()
        {
            return new EnvVarScope(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                Path.Combine(Path.GetTempPath(), $"{DotnetBuildEnvironmentManager.RootDirectoryName}-test-{Guid.NewGuid():N}"));
        }

        public static EnvVarScope ForVariable(string name, string? value)
        {
            return new EnvVarScope(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
            if (string.IsNullOrWhiteSpace(_root))
            {
                return;
            }

            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // Best effort; a failed test may leave a stream open for failure inspection.
            }
        }
    }
}
