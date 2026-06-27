using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

public sealed class LauncherScriptTests
{
    private static readonly string[] JsonLineSeparators = ["\r\n", "\n"];
    private static readonly string[] ExpectedStartCommandJsonProperties = ["args", "pid", "stderrPath", "stdoutPath"];
    private static readonly string?[] ExpectedGoalsArgument = ["goals"];
    private static readonly string?[] ExpectedAcceptanceGoalArguments = ["acceptance", "goal"];
    private static readonly string?[] ExpectedDoubleDashArguments =
    [
        "backlog-intake",
        "Launcher double dash smoke",
        "--create-goal",
        "--loop",
        "--watch",
        "--policy",
        "Permissive",
        "--poll-seconds",
        "15",
        "--max-duration",
        "00:01:00"
    ];

    [Xunit.Fact(DisplayName = "LandVerifiedGoal_unknown_goal_mark_landed_command_exits_nonzero_without_done")]
    public void LandVerifiedGoalUnknownGoalMarkLandedCommandExitsNonzeroWithoutDone()
    {
        using var sandbox = CreateLandVerifiedGoalSandbox("""
            @echo off
            echo Unknown command.
            exit /b 0
            """);

        var result = RunLandVerifiedGoal(sandbox.RepositoryPath);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("[land] DONE", result.Stdout);
        Assert.Contains("goal-mark-landed", result.Stderr);
        Assert.Contains("Unknown command.", result.Stderr);
        Assert.False(File.Exists(Path.Combine(sandbox.RepositoryPath, "goal.txt")));
    }

    [Xunit.Fact(DisplayName = "LandVerifiedGoal_goal_mark_landed_nonzero_exit_exits_nonzero_without_done")]
    public void LandVerifiedGoalGoalMarkLandedNonzeroExitExitsNonzeroWithoutDone()
    {
        using var sandbox = CreateLandVerifiedGoalSandbox("""
            @echo off
            if "%2"=="" (
              echo Usage: goal-mark-landed ^<goal-prefix^> --confirm-goal-mark-landed
              exit /b 0
            )
            echo bookkeeping failed
            exit /b 7
            """);

        var result = RunLandVerifiedGoal(sandbox.RepositoryPath);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("[land] DONE", result.Stdout);
        Assert.Contains("goal-mark-landed", result.Stderr);
        Assert.Contains("Exit code: 7", result.Stderr);
        Assert.Contains("bookkeeping failed", result.Stderr);
    }

    [Xunit.Fact(DisplayName = "LandVerifiedGoal_goal_mark_landed_success_exits_zero_and_prints_done")]
    public void LandVerifiedGoalGoalMarkLandedSuccessExitsZeroAndPrintsDone()
    {
        using var sandbox = CreateLandVerifiedGoalSandbox("""
            @echo off
            if "%2"=="" (
              echo Usage: goal-mark-landed ^<goal-prefix^> --confirm-goal-mark-landed
              exit /b 0
            )
            echo marked
            exit /b 0
            """);

        var result = RunLandVerifiedGoal(sandbox.RepositoryPath);

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        Assert.Contains("[land] DONE", result.Stdout);
    }

    [Xunit.Fact(DisplayName = "Launcher_rebuild_freshness_includes_git_head_marker")]
    public void LauncherRebuildFreshnessIncludesGitHeadMarker()
    {
        var repoRoot = FindLauncherSourceRoot();
        var launcher = File.ReadAllText(Path.Combine(repoRoot, "mcg-orchestrator.cmd"));

        Assert.True(launcher.Contains("App.dll.git-head", StringComparison.Ordinal));
        Assert.True(launcher.Contains("rev-parse HEAD", StringComparison.Ordinal));
        Assert.True(launcher.Contains("Set-Content -LiteralPath '%APP_HEAD%'", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "StartOrchestratorCommand_emits_json_pid_and_log_path_through_repo_script")]
    public void StartOrchestratorCommandEmitsJsonPidAndLogPathThroughRepoScript()
    {
        var repoRoot = Environment.GetEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            repoRoot = FindRepositoryRoot();
        }

        var appDll = Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repoRoot, "scripts", "Invoke-RepoScript.ps1"));
        startInfo.ArgumentList.Add("scripts\\Start-OrchestratorCommand.ps1");
        startInfo.ArgumentList.Add("-Name");
        startInfo.ArgumentList.Add("launcher-json-test");
        startInfo.ArgumentList.Add("-AppDll");
        startInfo.ArgumentList.Add(appDll);
        startInfo.ArgumentList.Add("goals");

        using var launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start launcher script.");
        var stdout = launcher.StandardOutput.ReadToEnd();
        var stderr = launcher.StandardError.ReadToEnd();
        Assert.True(launcher.WaitForExit(20000), "Launcher script did not exit within 20 seconds.");
        Assert.Equal(0, launcher.ExitCode);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);

        var outputLines = stdout.Split(
            JsonLineSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(outputLines);

        using var document = JsonDocument.Parse(outputLines[0]);
        var root = document.RootElement;
        var properties = root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(ExpectedStartCommandJsonProperties, properties);

        var pid = root.GetProperty("pid").GetInt32();
        var stdoutPath = root.GetProperty("stdoutPath").GetString();
        var stderrPath = root.GetProperty("stderrPath").GetString();
        var args = root.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()).ToArray();

        Assert.True(pid > 0);
        Assert.False(string.IsNullOrWhiteSpace(stdoutPath));
        Assert.False(string.IsNullOrWhiteSpace(stderrPath));
        Assert.True(Path.IsPathFullyQualified(stdoutPath!));
        Assert.True(Path.IsPathFullyQualified(stderrPath!));
        Assert.Equal(ExpectedGoalsArgument, args);

        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.True(child.WaitForExit(20000), $"Launched command pid {pid} did not exit within 20 seconds.");
        }
        catch (ArgumentException)
        {
            // Short commands can exit before the test reopens the emitted PID.
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!File.Exists(stdoutPath) && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }

        Assert.True(File.Exists(stdoutPath), $"Expected launcher log path to exist: {stdoutPath}");
        Assert.True(File.Exists(stderrPath), $"Expected launcher stderr path to exist: {stderrPath}");
    }

    [Xunit.Fact(DisplayName = "StartOrchestratorCommand_forwards_double_dash_arguments_to_background_process")]
    public void StartOrchestratorCommandForwardsDoubleDashArgumentsToBackgroundProcess()
    {
        var repoRoot = Environment.GetEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            repoRoot = FindRepositoryRoot();
        }

        using var sandbox = CreateDoubleDashLauncherSandbox();
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["MCG_ORCHESTRATOR_DOTNET_PATH"] = sandbox.HostPath;
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repoRoot, "scripts", "Invoke-RepoScript.ps1"));
        startInfo.ArgumentList.Add("scripts\\Start-OrchestratorCommand.ps1");
        startInfo.ArgumentList.Add("-Name");
        startInfo.ArgumentList.Add("launcher-double-dash-test");
        startInfo.ArgumentList.Add("-AppDll");
        startInfo.ArgumentList.Add(sandbox.EchoScriptPath);
        foreach (var argument in ExpectedDoubleDashArguments)
        {
            startInfo.ArgumentList.Add(argument!);
        }

        using var launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start launcher script.");
        var stdout = launcher.StandardOutput.ReadToEnd();
        var stderr = launcher.StandardError.ReadToEnd();
        Assert.True(launcher.WaitForExit(20000), "Launcher script did not exit within 20 seconds.");
        Assert.Equal(0, launcher.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);

        var outputLines = stdout.Split(
            JsonLineSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(outputLines);

        using var launcherDocument = JsonDocument.Parse(outputLines[0]);
        var launcherRoot = launcherDocument.RootElement;
        var pid = launcherRoot.GetProperty("pid").GetInt32();
        var stdoutPath = launcherRoot.GetProperty("stdoutPath").GetString()
            ?? throw new InvalidOperationException("Launcher did not emit stdoutPath.");
        var stderrPath = launcherRoot.GetProperty("stderrPath").GetString()
            ?? throw new InvalidOperationException("Launcher did not emit stderrPath.");
        var emittedArgs = launcherRoot.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()).ToArray();
        Assert.Equal(ExpectedDoubleDashArguments, emittedArgs);

        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.True(child.WaitForExit(20000), $"Launched command pid {pid} did not exit within 20 seconds.");
        }
        catch (ArgumentException)
        {
            // Short commands can exit before the test reopens the emitted PID.
        }

        WaitForFile(stdoutPath, TimeSpan.FromSeconds(10));
        WaitForFile(stderrPath, TimeSpan.FromSeconds(10));
        Assert.True(string.IsNullOrWhiteSpace(File.ReadAllText(stderrPath)), File.ReadAllText(stderrPath));

        using var childDocument = JsonDocument.Parse(File.ReadAllText(stdoutPath));
        var childArgs = childDocument.RootElement.EnumerateArray().Select(argument => argument.GetString()).ToArray();
        Assert.Equal(ExpectedDoubleDashArguments, childArgs);
    }

    [Xunit.Fact(DisplayName = "StartOrchestratorCommand_keeps_AppDll_named_only_and_forwards_remaining_arguments")]
    public void StartOrchestratorCommandKeepsAppDllNamedOnlyAndForwardsRemainingArguments()
    {
        var repoRoot = FindLauncherSourceRoot();
        var script = File.ReadAllText(Path.Combine(repoRoot, "scripts", "Start-OrchestratorCommand.ps1"));

        Assert.True(script.Contains("[CmdletBinding(PositionalBinding = $false)]", StringComparison.Ordinal));
        Assert.True(script.Contains("[Parameter(ValueFromRemainingArguments = $true)]", StringComparison.Ordinal));
        Assert.True(script.Contains("[string[]]$Arguments", StringComparison.Ordinal));
        Assert.True(script.Contains("$processArguments = @($resolvedAppDll) + $Arguments", StringComparison.Ordinal));
        Assert.False(script.Contains("Position =", StringComparison.OrdinalIgnoreCase));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            function Test-Binding {
                [CmdletBinding(PositionalBinding = $false)]
                param(
                    [string]$Name = 'command',
                    [string]$AppDll,
                    [Parameter(ValueFromRemainingArguments = $true)]
                    [string[]]$Arguments
                )
                [pscustomobject]@{
                    appDll = $AppDll
                    args = @($Arguments)
                } | ConvertTo-Json -Compress
            }
            Test-Binding -Name smoke acceptance goal
            """);

        using var bindingProbe = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start PowerShell binding probe.");
        var stdout = bindingProbe.StandardOutput.ReadToEnd();
        var stderr = bindingProbe.StandardError.ReadToEnd();
        Assert.True(bindingProbe.WaitForExit(20000), "PowerShell binding probe did not exit within 20 seconds.");
        Assert.Equal(0, bindingProbe.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal(string.Empty, root.GetProperty("appDll").GetString());
        Assert.Equal(
            ExpectedAcceptanceGoalArguments,
            root.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()).ToArray());
    }

    private static string FindLauncherSourceRoot([CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(sourceFilePath))
            ?? throw new DirectoryNotFoundException($"Could not resolve source directory from '{sourceFilePath}'."));
        while (directory is not null)
        {
            var launcherPath = Path.Combine(directory.FullName, "mcg-orchestrator.cmd");
            var landVerifiedGoalPath = Path.Combine(directory.FullName, "scripts", "Land-VerifiedGoal.ps1");

            if (File.Exists(launcherPath) && File.Exists(landVerifiedGoalPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate launcher source files from source file path '{sourceFilePath}'.");
    }

    private static DoubleDashLauncherSandbox CreateDoubleDashLauncherSandbox()
    {
        var sandboxPath = Path.Combine(Path.GetTempPath(), $"launcher-double-dash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandboxPath);

        var hostPath = Path.Combine(sandboxPath, "fake-dotnet.cmd");
        File.WriteAllText(hostPath, """
            @echo off
            powershell.exe -NoProfile -ExecutionPolicy Bypass -File %*
            """);

        var echoScriptPath = Path.Combine(sandboxPath, "echo-args.ps1");
        File.WriteAllText(echoScriptPath, """
            param(
                [Parameter(ValueFromRemainingArguments = $true)]
                [string[]]$Arguments
            )

            $Arguments | ConvertTo-Json -Compress
            """);

        return new DoubleDashLauncherSandbox(sandboxPath, hostPath, echoScriptPath);
    }

    private static LandVerifiedGoalSandbox CreateLandVerifiedGoalSandbox(string launcherBody)
    {
        const string goalPrefix = "abcdef12";
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"land-verified-goal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repositoryPath, "scripts"));
        File.Copy(
            Path.Combine(FindLauncherSourceRoot(), "scripts", "Land-VerifiedGoal.ps1"),
            Path.Combine(repositoryPath, "scripts", "Land-VerifiedGoal.ps1"));
        File.WriteAllText(Path.Combine(repositoryPath, "mcg-orchestrator.cmd"), launcherBody);

        RunGit(repositoryPath, "init", "--initial-branch=main");
        RunGit(repositoryPath, "config", "user.email", "test@example.invalid");
        RunGit(repositoryPath, "config", "user.name", "Launcher Script Test");
        File.WriteAllText(Path.Combine(repositoryPath, "README.md"), "base");
        RunGit(repositoryPath, "add", "README.md");
        RunGit(repositoryPath, "commit", "-m", "base");
        RunGit(repositoryPath, "checkout", "-b", $"goal/{goalPrefix}");
        File.WriteAllText(Path.Combine(repositoryPath, "goal.txt"), "goal change");
        RunGit(repositoryPath, "add", "goal.txt");
        RunGit(repositoryPath, "commit", "-m", "goal change");
        RunGit(repositoryPath, "checkout", "main");

        return new LandVerifiedGoalSandbox(repositoryPath);
    }

    private static ProcessResult RunLandVerifiedGoal(string repositoryPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repositoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repositoryPath, "scripts", "Land-VerifiedGoal.ps1"));
        startInfo.ArgumentList.Add("-GoalPrefix");
        startInfo.ArgumentList.Add("abcdef12");
        startInfo.ArgumentList.Add("-SkipAcceptance");

        return RunProcess(startInfo, "Land-VerifiedGoal.ps1");
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var result = RunProcess(startInfo, $"git {string.Join(' ', arguments)}");
        Assert.Equal(0, result.ExitCode);
    }

    private static ProcessResult RunProcess(ProcessStartInfo startInfo, string description)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {description}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30000), $"{description} did not exit within 30 seconds.");
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static void WaitForFile(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!File.Exists(path) && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }

        Assert.True(File.Exists(path), $"Expected file to exist: {path}");
    }

    private sealed class DoubleDashLauncherSandbox(
        string sandboxPath,
        string hostPath,
        string echoScriptPath) : IDisposable
    {
        public string HostPath { get; } = hostPath;
        public string EchoScriptPath { get; } = echoScriptPath;

        public void Dispose()
        {
            try
            {
                Directory.Delete(sandboxPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class LandVerifiedGoalSandbox(string repositoryPath) : IDisposable
    {
        public string RepositoryPath { get; } = repositoryPath;

        public void Dispose()
        {
            try
            {
                Directory.Delete(RepositoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private static string FindRepositoryRoot()
    {
        return TryFindRepositoryRoot(Environment.CurrentDirectory)
            ?? TryFindRepositoryRoot(AppContext.BaseDirectory)
            ?? InfrastructureTestSupport.FindRepositoryRoot();
    }

    private static string? TryFindRepositoryRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.GetFullPath(candidate));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
