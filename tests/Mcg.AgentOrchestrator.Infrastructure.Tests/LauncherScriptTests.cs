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

    [Xunit.Fact(DisplayName = "LandVerifiedGoal_fails_closed_and_propagates_goal_mark_landed_exit_code")]
    public void LandVerifiedGoalFailsClosedAndPropagatesGoalMarkLandedExitCode()
    {
        var repoRoot = FindLauncherSourceRoot();
        var script = File.ReadAllText(Path.Combine(repoRoot, "scripts", "Land-VerifiedGoal.ps1"));

        Assert.True(script.Contains("$markLandedExitCode = $LASTEXITCODE", StringComparison.Ordinal));
        Assert.True(script.Contains("exit $markLandedExitCode", StringComparison.Ordinal));
        Assert.True(script.Contains("goal-mark-landed failed with exit code $markLandedExitCode", StringComparison.Ordinal));
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
