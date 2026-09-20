using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;
using static DotnetBuildEnvironmentManagerTests;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsIsolatedDotnetScript
{
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
                if /I not "%~1"=="build" exit /b 0
                set "ARTIFACTS="
                :parse_artifacts
                if "%~1"=="" goto create_artifacts
                if /I "%~1"=="--artifacts-path" set "ARTIFACTS=%~2"
                shift
                goto parse_artifacts
                :create_artifacts
                set "TEST_DIR=%ARTIFACTS%\bin\Fake.Tests\debug"
                mkdir "%TEST_DIR%" >nul 2>nul
                > "%TEST_DIR%\Fake.Tests.dll" echo managed
                > "%TEST_DIR%\Fake.Tests.exe" echo apphost
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
            startInfo.EnvironmentVariables["MCG_BUILD_MAXCPUCOUNT"] = "7";
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
            Assert.True(log.Contains("args=build Fake.Tests.csproj --no-restore --artifacts-path ", StringComparison.Ordinal));
            Assert.True(log.Contains("-p:McgIsolatedArtifactsPath=", StringComparison.Ordinal));
            Assert.True(log.Contains("-maxcpucount:7 -p:BuildInParallel=false", StringComparison.Ordinal));
            Assert.Contains("args=", log, StringComparison.Ordinal);
            Assert.Contains("Fake.Tests.dll --filter-class *FocusedTests* --no-ansi --progress off", log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("args=test", log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--filter FullyQualifiedName~FocusedTests", log, StringComparison.Ordinal);
            Assert.True(log.Contains($"repo={workDirectory}", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("args=build-server shutdown", log, StringComparison.Ordinal);
            Assert.DoesNotContain("--disable-build-servers", log);
            Assert.DoesNotContain("-p:UseSharedCompilation=false", log);
            Assert.DoesNotContain("sandbox=1", log);
            Assert.DoesNotContain("account=sandbox-user", log);
            Assert.DoesNotContain("target=sandbox-target", log);
            Assert.True(File.Exists(Path.Combine(root, "isolated-dotnet", "build-slots", "build-0.lock")));
            Assert.False(File.Exists(Path.Combine(root, "isolated-dotnet", "build-slots", "build-1.lock")));
            Assert.Contains("Running managed test assembly", stdout, StringComparison.Ordinal);
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

    [Xunit.Theory(DisplayName = "InvokeIsolatedDotnet_rejects_unowned_managed_test_execution_before_build")]
    [Xunit.InlineData("Mcg.AgentOrchestrator.sln", false, "explicit test project path")]
    [Xunit.InlineData("Fake.Tests.csproj", true, "--no-build requires -ReuseArtifacts")]
    public void InvokeIsolatedDotnetRejectsUnownedManagedTestExecutionBeforeBuild(
        string target,
        bool noBuild,
        string expectedError)
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
            File.WriteAllText(
                Path.Combine(shimDirectory, "dotnet.cmd"),
                "@echo off" + Environment.NewLine +
                ">> \"%DOTNET_SHIM_LOG%\" echo args=%*" + Environment.NewLine +
                "exit /b 0" + Environment.NewLine);

            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-InputFormat",
                "None",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                scriptPath,
                "-GoalPrefix",
                "feedbeef",
                "test",
                target
            })
            {
                startInfo.ArgumentList.Add(argument);
            }
            if (noBuild)
            {
                startInfo.ArgumentList.Add("--no-build");
            }
            startInfo.Environment["PATH"] = shimDirectory + Path.PathSeparator +
                (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["DOTNET_SHIM_LOG"] = logPath;
            startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] =
                Path.Combine(root, "isolated-dotnet");
            startInfo.Environment.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "Invoke-IsolatedDotnet.ps1 did not exit within 10 seconds.");

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(expectedError, stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(logPath), "The dotnet shim must not run before request validation.");
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

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_goal_run_does_not_take_over_obsolete_slot_custody")]
    public void InvokeIsolatedDotnetGoalRunDoesNotTakeOverObsoleteSlotCustody()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string goalPrefix = "custody-live";
        const string attemptId = "numeric-running-attempt";
        var repoRoot = ResolveRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var isolatedRoot = Path.Combine(root, "isolated-dotnet");
        var slotName = StableSlotNameForScript(goalPrefix);
        var artifactsPath = Path.Combine(isolatedRoot, "slots", slotName, "artifacts");
        var evidencePath = Path.Combine(artifactsPath, "TestResults", "completed-lane.trx");
        var metadataPath = Path.Combine(root, $"{attemptId}.attempt.json");
        var shimDirectory = Path.Combine(root, "shim");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        Directory.CreateDirectory(shimDirectory);
        File.WriteAllText(evidencePath, "receipt");
        WriteForeignOwnerMarker(artifactsPath);
        WriteAttemptMetadata(metadataPath, attemptId, 0, Environment.ProcessId);
        File.WriteAllText(
            AcceptanceAttemptArtifactCustody.MarkerPath(artifactsPath),
            JsonSerializer.Serialize(new
            {
                version = 1,
                attemptId,
                livenessCheckHint = metadataPath,
                ownerProcessId = Environment.ProcessId,
                machineName = Environment.MachineName,
                acquiredAt = DateTimeOffset.UtcNow
            }));
        File.WriteAllText(
            Path.Combine(shimDirectory, "dotnet.cmd"),
            """
            @echo off
            if "%~1"=="build-server" exit /b 0
            if /I not "%~1"=="build" exit /b 0
            set "ARTIFACTS="
            :parse_artifacts
            if "%~1"=="" goto create_artifacts
            if /I "%~1"=="--artifacts-path" set "ARTIFACTS=%~2"
            shift
            goto parse_artifacts
            :create_artifacts
            set "TEST_DIR=%ARTIFACTS%\bin\Fake.Tests\debug"
            mkdir "%TEST_DIR%" >nul 2>nul
            > "%TEST_DIR%\Fake.Tests.dll" echo managed
            > "%TEST_DIR%\Fake.Tests.exe" echo apphost
            exit /b 0
            """);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-InputFormat",
                "None",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                scriptPath,
                "-GoalPrefix",
                goalPrefix,
                "-AttemptName",
                "Custody Test",
                "test",
                "Fake.Tests.csproj",
                "--no-restore"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["PATH"] =
                shimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = isolatedRoot;
            startInfo.Environment.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(10000), "Invoke-IsolatedDotnet.ps1 did not exit within 10 seconds.");
            var output = stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult();

            Assert.True(
                process.ExitCode == 0,
                $"Invoke-IsolatedDotnet.ps1 exited {process.ExitCode}.{Environment.NewLine}{output}");
            Assert.True(File.Exists(evidencePath));
            Assert.True(Directory.Exists(Path.Combine(isolatedRoot, "goals", goalPrefix, "artifacts")));
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
                if /I not "%~1"=="build" exit /b 0
                set "ARTIFACTS="
                :parse_artifacts
                if "%~1"=="" goto create_artifacts
                if /I "%~1"=="--artifacts-path" set "ARTIFACTS=%~2"
                shift
                goto parse_artifacts
                :create_artifacts
                set "TEST_DIR=%ARTIFACTS%\bin\Fake.Tests\debug"
                mkdir "%TEST_DIR%" >nul 2>nul
                > "%TEST_DIR%\Fake.Tests.dll" echo managed
                > "%TEST_DIR%\Fake.Tests.exe" echo apphost
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
            startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

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
                if "%~1"=="build-server" exit /b 0
                if /I not "%~1"=="build" exit /b 0
                set "ARTIFACTS="
                :parse_artifacts
                if "%~1"=="" goto create_artifacts
                if /I "%~1"=="--artifacts-path" set "ARTIFACTS=%~2"
                shift
                goto parse_artifacts
                :create_artifacts
                set "TEST_DIR=%ARTIFACTS%\bin\Fake.Tests\debug"
                mkdir "%TEST_DIR%" >nul 2>nul
                > "%TEST_DIR%\Fake.Tests.dll" echo managed
                > "%TEST_DIR%\Fake.Tests.exe" echo apphost
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
            startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

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

}
