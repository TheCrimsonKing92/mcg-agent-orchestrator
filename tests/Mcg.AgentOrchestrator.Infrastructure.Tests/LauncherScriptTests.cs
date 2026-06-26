using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

public sealed class LauncherScriptTests
{
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

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        var pid = root.GetProperty("pid").GetInt32();
        var logPath = root.GetProperty("logPath").GetString();

        Assert.True(pid > 0);
        Assert.False(string.IsNullOrWhiteSpace(logPath));
        Assert.True(Path.IsPathFullyQualified(logPath!));

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
        while (!File.Exists(logPath) && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }

        Assert.True(File.Exists(logPath), $"Expected launcher log path to exist: {logPath}");
    }

    private static string FindLauncherSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "mcg-orchestrator.cmd")) &&
                File.Exists(Path.Combine(directory.FullName, "scripts", "Land-VerifiedGoal.ps1")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate launcher source root.");
    }
}
