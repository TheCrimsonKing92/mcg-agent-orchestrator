using System.Diagnostics;
using static LauncherScriptTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class OrchestratorSnapshotStatusTimeoutTests
{
    // Keep the status timeout comfortably above the shim's deliberate pre-sentinel delay so startup variance cannot decide the test.
    private const int SnapshotStatusTimeoutSeconds = 20;
    private const int SnapshotShimSentinelDelaySeconds = 6;

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void PreparedRuntimeSnapshotBypassesMissingOrStaleResolver(bool staleResolver)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new PreparedRuntimeFixture();
        if (staleResolver)
            fixture.WriteResolver(Path.Combine(fixture.Root, "missing-default.dll"));
        else
            File.Delete(fixture.Script("Resolve-RepoProcessAppDll.ps1"));

        var goals = new[] { "alpha & ($literal);' [x]", "beta two" };
        var result = fixture.Invoke("Get-OrchestratorSnapshot.ps1",
            $"-AppDll {PsLiteral(fixture.RelativeRuntime)} -GoalPrefix {PsLiteral(goals[0])}, {PsLiteral(goals[1])}");

        AssertSuccessfulSnapshot(result);
        Assert.Contains("status ok " + goals[0], result.Stdout);
        Assert.Contains("status ok " + goals[1], result.Stdout);
        var calls = fixture.Calls();
        Assert.Equal(3, calls.Length);
        Assert.Equal(InventoryArguments(fixture.Runtime), calls[0]);
        Assert.Equal(new[] { fixture.Runtime, "status", goals[0] }, calls[1]);
        Assert.Equal(new[] { fixture.Runtime, "status", goals[1] }, calls[2]);
        fixture.AssertNoResolutionOrLauncher();
    }

    [Xunit.Fact]
    public void PreparedRuntimeProcessInfoPreservesAllCommandArguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new PreparedRuntimeFixture();
        var needle = "a & ($literal);' [x]";
        var result = fixture.Invoke("Get-RepoProcessInfo.ps1",
            $"-AppDll {PsLiteral(fixture.RelativeRuntime)} -Id 42, 43 -ParentId 7 -Name 'dotnet', 'worker two' " +
            $"-CommandContains {PsLiteral(needle)} -Newest 9 -ConductLoop -DispatchHost -IncludeChildren -Locks");

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        Assert.Equal(new[]
        {
            fixture.Runtime, "repo-process-info", "--id", "42", "--id", "43", "--parent-id", "7",
            "--name", "dotnet", "--name", "worker two", "--command-contains", needle,
            "--newest", "9", "--conduct-loop", "--dispatch-host", "--include-children", "--locks"
        }, Assert.Single(fixture.Calls()));
        fixture.AssertNoResolutionOrLauncher();
    }

    [Xunit.Theory]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" ")]
    [Xunit.InlineData("missing.dll")]
    [Xunit.InlineData(".")]
    [Xunit.InlineData("Env:PATH")]
    [Xunit.InlineData("invalid::path")]
    public void InvalidPreparedRuntimePreservesIndependentDataWithoutFallback(string selection)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new PreparedRuntimeFixture();
        var helper = fixture.Invoke("Get-RepoProcessInfo.ps1", $"-AppDll {PsLiteral(selection)}");
        Assert.NotEqual(0, helper.ExitCode);
        Assert.Contains("PROCESS_QUERY_UNAVAILABLE operation=script-wrapper", helper.Stdout);

        var snapshot = fixture.Invoke("Get-OrchestratorSnapshot.ps1",
            $"-AppDll {PsLiteral(selection)} -GoalPrefix ok");
        AssertSuccessfulSnapshot(snapshot);
        Assert.Contains("explicit App DLL unavailable:", snapshot.Stdout);
        Assert.Contains("status unavailable:", snapshot.Stdout);
        Assert.Contains("lock query unavailable:", snapshot.Stdout);
        Assert.DoesNotContain("build lock is FREE", snapshot.Stdout);
        Assert.Empty(fixture.Calls());
        fixture.AssertNoResolutionOrLauncher();
    }

    [Xunit.Theory]
    [Xunit.InlineData("Get-RepoProcessInfo.ps1")]
    [Xunit.InlineData("Get-OrchestratorSnapshot.ps1")]
    public void MissingPreparedRuntimeArgumentFailsBeforeFallback(string script)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new PreparedRuntimeFixture();
        var result = fixture.Invoke(script, "-AppDll");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("AppDll", result.Stderr);
        Assert.Empty(fixture.Calls());
        fixture.AssertNoResolutionOrLauncher();
    }

    [Xunit.Fact]
    public void UnsupportedPreparedProcessInfoRetainsStatusAndUnavailableLocks()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new PreparedRuntimeFixture();
        File.WriteAllText(Path.Combine(fixture.HostDirectory, "unsupported"), "");
        var result = fixture.Invoke("Get-OrchestratorSnapshot.ps1",
            $"-AppDll {PsLiteral(fixture.RelativeRuntime)} -GoalPrefix ok");

        AssertSuccessfulSnapshot(result);
        Assert.Contains("PROCESS_QUERY_UNAVAILABLE operation=inventory-acquire exit=23", result.Stdout);
        Assert.Contains("PROCESS_QUERY_UNAVAILABLE operation=fixture-unsupported", result.Stdout);
        Assert.Contains("status ok ok", result.Stdout);
        Assert.Contains("lock query unavailable:", result.Stdout);
        Assert.DoesNotContain("build lock is FREE", result.Stdout);
        var calls = fixture.Calls();
        Assert.Equal(2, calls.Length);
        Assert.Equal(InventoryArguments(fixture.Runtime), calls[0]);
        Assert.Equal(new[] { fixture.Runtime, "status", "ok" }, calls[1]);
        fixture.AssertNoResolutionOrLauncher();
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void OmittedPreparedRuntimePreservesResolverAndLauncherDefaults(bool resolverSucceeds)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new PreparedRuntimeFixture();
        fixture.WriteResolver(resolverSucceeds ? fixture.Runtime : null);
        var result = fixture.Invoke("Get-OrchestratorSnapshot.ps1", "-GoalPrefix ok");

        AssertSuccessfulSnapshot(result);
        Assert.Contains("status ok ok", result.Stdout);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "resolver.called")));
        Assert.Equal(!resolverSucceeds, File.Exists(Path.Combine(fixture.Root, "launcher.called")));
        var calls = fixture.Calls();
        Assert.Equal(resolverSucceeds ? 2 : 1, calls.Length);
        if (resolverSucceeds)
            Assert.Equal(InventoryArguments(fixture.Runtime), calls[0]);
        Assert.Equal(new[] { fixture.DefaultRuntime, "status", "ok" }, calls[^1]);
    }

    private static string[] InventoryArguments(string runtime) =>
    [
        runtime, "repo-process-info", "--name", "dotnet", "--name", "DispatchProcessHost",
        "--name", "Mcg.AgentOrchestrator*", "--command-contains", " ", "--newest", "2147483647"
    ];

    private static string PsLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static void AssertSuccessfulSnapshot(TestChildProcessCapture.Result result)
    {
        Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}\n{result.Stdout}\n{result.Stderr}");
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        Assert.Contains("fixture active goals", result.Stdout);
    }

    // Both monitoring scripts are copied unchanged; only their external execution boundaries are fake.
    // Acceptance owns RED/GREEN execution with the explicit-selection branches disabled/enabled.
    private sealed class PreparedRuntimeFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"snapshot prepared-{Guid.NewGuid():N}");
        public string HostDirectory => Path.Combine(Root, "configured host");
        public string Runtime => Path.Combine(Root, "prepared [literal] & ($value);' runtime.bin");
        public string RelativeRuntime => Path.Combine("..", Path.GetFileName(Runtime));
        public string DefaultRuntime => Path.Combine(Root, "src", "Mcg.AgentOrchestrator.App", "bin", "Debug", "net10.0", "Mcg.AgentOrchestrator.App.dll");
        private string CallerDirectory => Path.Combine(Root, "caller");
        private string Host => Path.Combine(HostDirectory, "fake dotnet.exe");
        public string Script(string name) => Path.Combine(Root, "scripts", name);

        public PreparedRuntimeFixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "scripts"));
            Directory.CreateDirectory(HostDirectory);
            Directory.CreateDirectory(CallerDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultRuntime)!);
            File.WriteAllText(Runtime, "prepared artifact");
            File.WriteAllText(DefaultRuntime, "default artifact");
            foreach (var name in new[] { "Get-RepoProcessInfo.ps1", "Get-OrchestratorSnapshot.ps1" })
                File.Copy(Path.Combine(FindLauncherSourceRoot(), "scripts", name), Script(name));
            WriteResolver(Runtime);
            File.WriteAllText(Script("Invoke-OrchestratorCommand.ps1"), """
                Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\launcher.called') -Value called
                Write-Output 'PROCESS_COUNT=0'
                exit 0
                """);
            File.WriteAllText(Script("Invoke-OrchestratorSqliteTool.ps1"), "Write-Output 'fixture active goals'\r\nexit 0\r\n");
            // A native argv recorder avoids cmd.exe reinterpreting metacharacters or hiding token splits.
            File.WriteAllText(Path.Combine(Root, "prepare-host.ps1"), """
                $ErrorActionPreference = 'Stop'
                Add-Type -OutputType ConsoleApplication -OutputAssembly (Join-Path $PSScriptRoot 'configured host\fake dotnet.exe') -TypeDefinition @'
                using System;
                using System.IO;
                using System.Text;
                public static class FakeDotnet {
                    public static int Main(string[] args) {
                        string root = AppDomain.CurrentDomain.BaseDirectory;
                        string[] encoded = Array.ConvertAll(args, a => Convert.ToBase64String(Encoding.UTF8.GetBytes(a)));
                        File.AppendAllText(Path.Combine(root, "argv.log"), String.Join("\t", encoded) + Environment.NewLine);
                        if (args.Length < 2) return 91;
                        if (args[1] == "repo-process-info") {
                            if (File.Exists(Path.Combine(root, "unsupported"))) {
                                Console.WriteLine("PROCESS_QUERY_UNAVAILABLE operation=fixture-unsupported command=repo-process-info");
                                return 23;
                            }
                            Console.WriteLine("PROCESS_COUNT=0");
                            return 0;
                        }
                        if (args[1] == "status" && args.Length == 3) {
                            Console.WriteLine("status ok " + args[2]);
                            return 0;
                        }
                        return 92;
                    }
                }
                '@
                """);
            var prepared = RunPowerShell(Path.Combine(Root, "prepare-host.ps1"));
            Assert.True(prepared.ExitCode == 0 && File.Exists(Host), $"Host fixture compilation failed:\n{prepared.Stdout}\n{prepared.Stderr}");
        }

        public void WriteResolver(string? runtime)
        {
            File.WriteAllText(Script("Resolve-RepoProcessAppDll.ps1"),
                "param([string]$RepositoryRoot)\r\n" +
                "Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\\resolver.called') -Value called\r\n" +
                (runtime is null ? "exit 1\r\n" : $"Write-Output {PsLiteral(runtime)}\r\nexit 0\r\n"));
        }

        public TestChildProcessCapture.Result Invoke(string script, string arguments)
        {
            var driver = Path.Combine(Root, "invoke.ps1");
            File.WriteAllText(driver,
                "$ErrorActionPreference = 'Stop'\r\n$global:LASTEXITCODE = 0\r\n" +
                $"Set-Location -LiteralPath {PsLiteral(CallerDirectory)}\r\n" +
                $"& {PsLiteral(Script(script))} {arguments}\r\n" +
                (script == "Get-RepoProcessInfo.ps1" ? "exit $LASTEXITCODE\r\n" : ""));
            return RunPowerShell(driver);
        }

        private TestChildProcessCapture.Result RunPowerShell(string script)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                WorkingDirectory = Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "COMSPEC" })
                if (Environment.GetEnvironmentVariable(name) is { } value)
                    startInfo.Environment[name] = value;
            startInfo.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
            startInfo.Environment["MCG_ORCHESTRATOR_DOTNET_PATH"] = Host;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
                startInfo.ArgumentList.Add(argument);
            return TestChildProcessCapture.Run(startInfo, TimeSpan.FromSeconds(120));
        }

        public string[][] Calls()
        {
            var log = Path.Combine(HostDirectory, "argv.log");
            return File.Exists(log)
                ? File.ReadAllLines(log).Select(line => line.Split('\t')
                    .Select(token => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token))).ToArray()).ToArray()
                : [];
        }

        public void AssertNoResolutionOrLauncher()
        {
            Assert.False(File.Exists(Path.Combine(Root, "resolver.called")), "Explicit selection invoked the freshness resolver.");
            Assert.False(File.Exists(Path.Combine(Root, "launcher.called")), "Explicit selection invoked the main-app launcher.");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Xunit.Fact(DisplayName = "GetOrchestratorSnapshot_status_timeout_kills_owned_status_process_and_reports_partial_data")]
    public void GetOrchestratorSnapshotStatusTimeoutKillsOwnedStatusProcessAndReportsPartialData()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = CreateSnapshotStatusSandbox();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = sandbox.RepositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.Environment["MCG_ORCHESTRATOR_DOTNET_PATH"] = sandbox.DotnetShimPath;
            startInfo.Environment["DOTNET_STATUS_SENTINEL"] = sandbox.SentinelPath;
            startInfo.Environment["DOTNET_STATUS_STARTED_AT"] = sandbox.StartedAtPath;
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(sandbox.RepositoryRoot, "scripts", "Get-OrchestratorSnapshot.ps1"));
            startInfo.ArgumentList.Add("-StatusTimeoutSeconds");
            startInfo.ArgumentList.Add(SnapshotStatusTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-GoalPrefix");
            startInfo.ArgumentList.Add("hang");
            startInfo.ArgumentList.Add("ok");
            startInfo.ArgumentList.Add("cleanup");

            var stopwatch = Stopwatch.StartNew();
            var result = RunProcess(startInfo, "Get-OrchestratorSnapshot.ps1");
            stopwatch.Stop();

            Assert.True(
                result.ExitCode == 0,
                $"exit={result.ExitCode}{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}{Environment.NewLine}elapsed:{stopwatch.Elapsed}");
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            Assert.Contains("partial hang", result.Stdout);
            Assert.Contains($"status timed out after {SnapshotStatusTimeoutSeconds}s; killed pid=", result.Stdout);
            Assert.Contains("status ok ok", result.Stdout);
            Assert.Contains("Cleanup backoff: reason=remove:branch-delete-failed", result.Stdout);
            Assert.Contains("Cleanup retry: conduct cleanup --loop", result.Stdout);
            WaitForFile(sandbox.SentinelPath, RealProcessFileHangGuard);
            var childPid = int.Parse(File.ReadAllText(sandbox.SentinelPath).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(!IsProcessRunning(childPid), $"Expected hung status child pid {childPid} to be reaped.");
        }
        finally
        {
            sandbox.KillRecordedChild();
        }
    }

    private static SnapshotStatusSandbox CreateSnapshotStatusSandbox()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), $"snapshot-status-{Guid.NewGuid():N}");
        var scriptsPath = Path.Combine(repositoryRoot, "scripts");
        var appPath = Path.Combine(repositoryRoot, "src", "Mcg.AgentOrchestrator.App", "bin", "Debug", "net10.0");
        var shimPath = Path.Combine(repositoryRoot, "shim");
        Directory.CreateDirectory(scriptsPath);
        Directory.CreateDirectory(appPath);
        Directory.CreateDirectory(shimPath);

        File.Copy(
            Path.Combine(FindLauncherSourceRoot(), "scripts", "Get-OrchestratorSnapshot.ps1"),
            Path.Combine(scriptsPath, "Get-OrchestratorSnapshot.ps1"));
        File.WriteAllText(
            Path.Combine(scriptsPath, "Invoke-OrchestratorSqliteTool.ps1"),
            "Write-Output 'No active goals in snapshot sandbox.'\r\nexit 0\r\n");
        File.WriteAllText(Path.Combine(appPath, "Mcg.AgentOrchestrator.App.dll"), "dummy");

        var dotnetShimPath = Path.Combine(shimPath, "dotnet.cmd");
        File.WriteAllText(
            dotnetShimPath,
            $"""
            @echo off
            if "%~3"=="hang" (
              echo partial hang
              powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Seconds {SnapshotShimSentinelDelaySeconds}; Set-Content -LiteralPath $env:DOTNET_STATUS_STARTED_AT -Value (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o'); Set-Content -LiteralPath $env:DOTNET_STATUS_SENTINEL -Value $PID; Start-Sleep -Seconds 60"
              exit /b 0
            )
            if "%~3"=="cleanup" (
              echo Cleanup backoff: reason=remove:branch-delete-failed skip_until_utc=2026-07-03T12:15:00.0000000+00:00 remaining_wait=00:15:00
              echo Cleanup retry: conduct cleanup --loop
              exit /b 0
            )
            echo status ok %~3
            exit /b 0
            """.Replace("\n", "\r\n", StringComparison.Ordinal));

        return new SnapshotStatusSandbox(
            repositoryRoot,
            dotnetShimPath,
            Path.Combine(repositoryRoot, "status-child.pid"),
            Path.Combine(repositoryRoot, "status-child.started-at"));
    }

    private sealed class SnapshotStatusSandbox(
        string repositoryRoot,
        string dotnetShimPath,
        string sentinelPath,
        string startedAtPath) : IDisposable
    {
        public string RepositoryRoot { get; } = repositoryRoot;
        public string DotnetShimPath { get; } = dotnetShimPath;
        public string SentinelPath { get; } = sentinelPath;
        public string StartedAtPath { get; } = startedAtPath;

        public void KillRecordedChild()
        {
            if (!File.Exists(SentinelPath) || !File.Exists(StartedAtPath))
            {
                return;
            }

            if (!int.TryParse(File.ReadAllText(SentinelPath).Trim(), out var pid))
            {
                return;
            }

            if (DateTimeOffset.TryParse(File.ReadAllText(StartedAtPath).Trim(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var startedAt))
                TestOwnedProcessStop.StopTreeIfSame(new(pid, startedAt));
        }

        public void Dispose()
        {
            KillRecordedChild();
            try
            {
                Directory.Delete(RepositoryRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

}
