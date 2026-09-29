using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTests
{
    [Fact(DisplayName = "Error_mode_only_spawn_suppresses_child_error_dialogs_without_hidden_console_attachment")]
    public void ErrorModeOnlySpawnSuppressesDialogsWithoutHiddenConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var originalErrorMode = WindowsProbe.GetErrorMode();
        var dir = Path.Combine(Path.GetTempPath(), "mcg-error-mode-spawn-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var probePath = Path.Combine(dir, "error-mode.txt");
            var childScript = Path.Combine(dir, "probe-child.ps1");
            File.WriteAllText(
                childScript,
                """
                Add-Type -TypeDefinition @"
                using System.Runtime.InteropServices;

                public static class McgErrorModeProbe
                {
                    [DllImport("kernel32.dll")]
                    public static extern uint GetErrorMode();
                }
                "@
                [McgErrorModeProbe]::GetErrorMode() | Set-Content -LiteralPath $env:MCG_PROBE_OUTPUT -Encoding Ascii
                """);

            var clearedErrorMode = originalErrorMode & ~ProcessTreeGuiSuppression.SuppressedErrorModeFlags;
            _ = WindowsProbe.SetErrorMode(clearedErrorMode);
            var startInfo = WorkerProcessRunner.BuildPowerShellStartInfo(
                $"& '{childScript.Replace("'", "''", StringComparison.Ordinal)}'",
                dir,
                redirectStandardInput: false);
            startInfo.CreateNoWindow = true;
            startInfo.Environment.Remove("DOTNET_STARTUP_HOOKS");
            startInfo.Environment["MCG_PROBE_OUTPUT"] = probePath;

            Process process;
            using (var suppressedSpawn = ProcessTreeGuiSuppression.AcquireErrorModeForChildSpawn())
            {
                Assert.False(suppressedSpawn.ChildConsolePolicyApplied);
                Assert.Equal(
                    ProcessTreeGuiSuppression.SuppressedErrorModeFlags,
                    WindowsProbe.GetErrorMode() & ProcessTreeGuiSuppression.SuppressedErrorModeFlags);
                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Failed to start error-mode probe.");
            }

            Assert.Equal(clearedErrorMode, WindowsProbe.GetErrorMode());
            using (process)
            {
                var stdoutRead = process.StandardOutput.ReadToEndAsync();
                var stderrRead = process.StandardError.ReadToEndAsync();
                Assert.True(process.WaitForExit(15_000), "Timed out waiting for error-mode probe.");
                var stdout = stdoutRead.GetAwaiter().GetResult();
                var stderr = stderrRead.GetAwaiter().GetResult();
                Assert.Equal(0, process.ExitCode);
                Assert.True(File.Exists(probePath), $"Probe did not write output. stdout={stdout} stderr={stderr}");
            }

            var childErrorMode = uint.Parse(File.ReadAllText(probePath), CultureInfo.InvariantCulture);
            Assert.Equal(
                ProcessTreeGuiSuppression.SuppressedErrorModeFlags,
                childErrorMode & ProcessTreeGuiSuppression.SuppressedErrorModeFlags);
        }
        finally
        {
            _ = WindowsProbe.SetErrorMode(originalErrorMode);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact(DisplayName = "ProcessTreeGuiSuppression_sets_inherited_error_mode_and_hidden_console_for_descendants")]
    public void ProcessTreeGuiSuppressionSetsInheritedErrorModeAndHiddenConsoleForDescendants()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "mcg-gui-suppression-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var probePath = Path.Combine(dir, "probe.json");
            var childScript = Path.Combine(dir, "probe-child.ps1");
            File.WriteAllText(
                childScript,
                """
                Add-Type -TypeDefinition @"
                using System;
                using System.Runtime.InteropServices;

                public static class McgNativeProbe
                {
                    [DllImport("kernel32.dll")]
                    public static extern uint GetErrorMode();

                    [DllImport("kernel32.dll")]
                    public static extern IntPtr GetConsoleWindow();

                    [DllImport("kernel32.dll", SetLastError = true)]
                    public static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

                    [DllImport("user32.dll")]
                    public static extern bool IsWindowVisible(IntPtr hWnd);
                }
                "@

                $console = [McgNativeProbe]::GetConsoleWindow()
                $visible = if ($console -eq [IntPtr]::Zero) { $false } else { [McgNativeProbe]::IsWindowVisible($console) }
                $members = New-Object uint32[] 16
                $memberCount = [McgNativeProbe]::GetConsoleProcessList($members, [uint32]$members.Length)
                [pscustomobject]@{
                    errorMode = [uint32][McgNativeProbe]::GetErrorMode()
                    # Windowless consoles deliberately have HWND=0. Process membership is the
                    # reliable console-presence oracle for this descendant contract.
                    hasConsole = ($memberCount -gt 0)
                    consoleVisible = $visible
                } | ConvertTo-Json -Compress | Set-Content -LiteralPath $env:MCG_PROBE_OUTPUT -Encoding UTF8
                """);

            var rootCommand =
                """
                $psi = [System.Diagnostics.ProcessStartInfo]::new($env:MCG_PROBE_SHELL)
                $psi.UseShellExecute = $false
                $psi.RedirectStandardOutput = $true
                $psi.RedirectStandardError = $true
                # MCG_ALLOW_DEFAULT_WINDOW_SETTINGS_PROBE: this descendant intentionally uses default
                # window settings to prove the launcher supplied an inheritable hidden console.
                $psi.ArgumentList.Add('-NoProfile')
                $psi.ArgumentList.Add('-NonInteractive')
                $psi.ArgumentList.Add('-ExecutionPolicy')
                $psi.ArgumentList.Add('Bypass')
                $psi.ArgumentList.Add('-File')
                $psi.ArgumentList.Add($env:MCG_PROBE_SCRIPT)
                $child = [System.Diagnostics.Process]::Start($psi)
                $stdout = $child.StandardOutput.ReadToEnd()
                $stderr = $child.StandardError.ReadToEnd()
                $child.WaitForExit()
                if ($child.ExitCode -ne 0) {
                    Write-Error $stderr
                    exit $child.ExitCode
                }
                Write-Output $stdout
                """;

            var originalErrorMode = WindowsProbe.GetErrorMode();
            var parentAlreadyHasVisibleConsole = WindowsProbe.CurrentConsoleIsVisible();
            var startInfo = WorkerProcessRunner.BuildPowerShellStartInfo(rootCommand, dir, redirectStandardInput: false);
            startInfo.Environment.Remove("DOTNET_STARTUP_HOOKS");
            startInfo.Environment["MCG_PROBE_SHELL"] = WorkerShell.Executable;
            startInfo.Environment["MCG_PROBE_SCRIPT"] = childScript;
            startInfo.Environment["MCG_PROBE_OUTPUT"] = probePath;

            var result = TestChildProcessCapture.Run(startInfo, start: ProcessTreeGuiSuppression.Start);
            var stdout = result.Stdout;
            var stderr = result.Stderr;
            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(probePath), $"Probe did not write output. stdout={stdout} stderr={stderr}");
            Assert.Equal(originalErrorMode, WindowsProbe.GetErrorMode());

            using var document = JsonDocument.Parse(File.ReadAllText(probePath));
            var root = document.RootElement;
            var errorMode = root.GetProperty("errorMode").GetUInt32();
            Assert.Equal(
                ProcessTreeGuiSuppression.SuppressedErrorModeFlags,
                errorMode & ProcessTreeGuiSuppression.SuppressedErrorModeFlags);
            Assert.True(root.GetProperty("hasConsole").GetBoolean(), File.ReadAllText(probePath));
            if (!parentAlreadyHasVisibleConsole)
            {
                Assert.False(root.GetProperty("consoleVisible").GetBoolean(), File.ReadAllText(probePath));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact(DisplayName = "MTP build and runner spawns inherit fault-dialog suppression and restore parent mode")]
    public void MtpSpawnsInheritFaultDialogSuppressionAndRestoreParentMode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var originalErrorMode = WindowsProbe.GetErrorMode();
        var clearedErrorMode = originalErrorMode & ~ProcessTreeGuiSuppression.SuppressedErrorModeFlags;
        var dir = Path.Combine(Path.GetTempPath(), "mcg-mtp-error-mode-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var modulePath = Path.Combine(FindRepoRoot(), "scripts", "MtpTestRunner.psm1");
            var childScript = Path.Combine(dir, "read-error-mode.ps1");
            var outerScript = Path.Combine(dir, "exercise-mtp-spawns.ps1");
            var buildModePath = Path.Combine(dir, "build-mode.txt");
            var runnerModePath = Path.Combine(dir, "runner-mode.txt");
            var buildLog = Path.Combine(dir, "build.log");
            var runnerLog = Path.Combine(dir, "runner.log");

            File.WriteAllText(
                childScript,
                """
                Add-Type -TypeDefinition @"
                using System;
                using System.Runtime.InteropServices;
                public static class McgMtpChildErrorModeProbe
                {
                    [DllImport("kernel32.dll")]
                    public static extern uint GetErrorMode();
                    [DllImport("kernel32.dll")]
                    public static extern int WerGetFlags(IntPtr process, out uint flags);
                }
                "@
                $werFlags = [uint32]0
                $werResult = [McgMtpChildErrorModeProbe]::WerGetFlags([System.Diagnostics.Process]::GetCurrentProcess().Handle, [ref]$werFlags)
                [ordered]@{
                    errorMode = [McgMtpChildErrorModeProbe]::GetErrorMode()
                    werFlags = $werFlags
                    werResult = $werResult
                } | ConvertTo-Json -Compress | Set-Content -LiteralPath $env:MCG_PROBE_OUTPUT -Encoding Ascii
                """);
            File.WriteAllText(
                outerScript,
                """
                $ErrorActionPreference = 'Stop'
                Add-Type -TypeDefinition @"
                using System.Runtime.InteropServices;
                public static class McgMtpOuterErrorModeProbe
                {
                    [DllImport("kernel32.dll")]
                    public static extern uint GetErrorMode();
                }
                "@
                $before = [McgMtpOuterErrorModeProbe]::GetErrorMode()
                Import-Module $env:MCG_PROBE_MODULE -Force
                $module = Get-Module MtpTestRunner
                $startupHook = & $module { Resolve-MtpFaultDialogStartupHook }

                $env:MCG_PROBE_OUTPUT = $env:MCG_PROBE_BUILD_OUTPUT
                $build = & $module {
                    param($hook)
                    $arguments = [string[]]@($env:MCG_PROBE_SHELL, '-NoProfile', '-NonInteractive', '-File', $env:MCG_PROBE_SCRIPT)
                    Invoke-MtpBuildProcess -Executable $env:MCG_PROBE_SHELL -Arguments $arguments -OutputLog $env:MCG_PROBE_BUILD_LOG -WorkingDirectory $env:MCG_PROBE_WORKING -StartupHookPath $hook
                } $startupHook
                $afterBuild = [McgMtpOuterErrorModeProbe]::GetErrorMode()

                $env:MCG_PROBE_OUTPUT = $env:MCG_PROBE_RUNNER_OUTPUT
                $runner = & $module {
                    param($hook)
                    $arguments = [string[]]@($env:MCG_PROBE_SHELL, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $env:MCG_PROBE_SCRIPT)
                    Invoke-MtpAppHost -Executable $env:MCG_PROBE_SHELL -Arguments $arguments -OutputLog $env:MCG_PROBE_RUNNER_LOG -StartupHookPath $hook -AllowBreakaway -TestHostTimeoutSeconds 30
                } $startupHook
                $afterRunner = [McgMtpOuterErrorModeProbe]::GetErrorMode()

                $missing = & $module {
                    param($hook)
                    $arguments = [string[]]@($env:MCG_PROBE_MISSING)
                    Invoke-MtpBuildProcess -Executable $env:MCG_PROBE_MISSING -Arguments $arguments -OutputLog $env:MCG_PROBE_FAILURE_LOG -WorkingDirectory $env:MCG_PROBE_WORKING -StartupHookPath $hook
                } $startupHook
                $afterFailure = [McgMtpOuterErrorModeProbe]::GetErrorMode()

                [ordered]@{
                    before = $before
                    afterBuild = $afterBuild
                    afterRunner = $afterRunner
                    afterFailure = $afterFailure
                    buildStarted = $build.Started
                    runnerExit = $runner.ExitCode
                    missingStarted = $missing.Started
                } | ConvertTo-Json -Compress
                """);

            var startInfo = WorkerProcessRunner.BuildPowerShellStartInfo(
                $"& '{outerScript.Replace("'", "''", StringComparison.Ordinal)}'",
                dir,
                redirectStandardInput: false);
            startInfo.CreateNoWindow = true;
            startInfo.Environment.Remove("DOTNET_STARTUP_HOOKS");
            startInfo.Environment["MCG_PROBE_MODULE"] = modulePath;
            startInfo.Environment["MCG_PROBE_SHELL"] = WorkerShell.Executable;
            startInfo.Environment["MCG_PROBE_SCRIPT"] = childScript;
            startInfo.Environment["MCG_PROBE_BUILD_OUTPUT"] = buildModePath;
            startInfo.Environment["MCG_PROBE_RUNNER_OUTPUT"] = runnerModePath;
            startInfo.Environment["MCG_PROBE_BUILD_LOG"] = buildLog;
            startInfo.Environment["MCG_PROBE_RUNNER_LOG"] = runnerLog;
            startInfo.Environment["MCG_PROBE_FAILURE_LOG"] = Path.Combine(dir, "failure.log");
            startInfo.Environment["MCG_PROBE_WORKING"] = dir;
            startInfo.Environment["MCG_PROBE_MISSING"] = Path.Combine(dir, "missing-process.exe");

            _ = WindowsProbe.SetErrorMode(clearedErrorMode);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start standalone MTP error-mode probe.");
            var stdoutRead = process.StandardOutput.ReadToEndAsync();
            var stderrRead = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(30_000), "Timed out waiting for standalone MTP error-mode probe.");
            var stdout = stdoutRead.GetAwaiter().GetResult();
            var stderr = stderrRead.GetAwaiter().GetResult();
            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(buildModePath), $"Build child did not report error mode. stdout={stdout} stderr={stderr}");
            Assert.True(File.Exists(runnerModePath), $"Runner child did not report error mode. stdout={stdout} stderr={stderr}");

            using var document = JsonDocument.Parse(stdout.Trim());
            var result = document.RootElement;
            Assert.Equal(clearedErrorMode, result.GetProperty("before").GetUInt32());
            Assert.Equal(clearedErrorMode, result.GetProperty("afterBuild").GetUInt32());
            Assert.Equal(clearedErrorMode, result.GetProperty("afterRunner").GetUInt32());
            Assert.Equal(clearedErrorMode, result.GetProperty("afterFailure").GetUInt32());
            Assert.True(result.GetProperty("buildStarted").GetBoolean());
            Assert.Equal(0, result.GetProperty("runnerExit").GetInt32());
            Assert.False(result.GetProperty("missingStarted").GetBoolean());

            foreach (var probePath in new[] { buildModePath, runnerModePath })
            {
                using var childDocument = JsonDocument.Parse(File.ReadAllText(probePath));
                var child = childDocument.RootElement;
                var childMode = child.GetProperty("errorMode").GetUInt32();
                const uint retainedErrorModeFlags = 0x8001;
                const uint noGpFaultErrorBox = 0x0002;
                const uint werFaultReportingNoUi = 0x0020;
                Assert.Equal(
                    retainedErrorModeFlags,
                    childMode & retainedErrorModeFlags);
                Assert.Equal(0u, childMode & noGpFaultErrorBox);
                Assert.Equal(0, child.GetProperty("werResult").GetInt32());
                Assert.Equal(
                    werFaultReportingNoUi,
                    child.GetProperty("werFlags").GetUInt32() & werFaultReportingNoUi);
            }
        }
        finally
        {
            _ = WindowsProbe.SetErrorMode(originalErrorMode);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact(DisplayName = "Worker and gate root launches use process tree GUI suppression")]
    public void WorkerAndGateRootLaunchesUseProcessTreeGuiSuppression()
    {
        var root = FindRepoRoot();
        var dispatchHost = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Mcg.AgentOrchestrator.Infrastructure",
            "Processes",
            "DispatchProcessHost.cs"));
        var gateVerifier = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Mcg.AgentOrchestrator.Infrastructure",
            "Workspaces",
            "GoalAcceptanceVerifier.cs"));
        Assert.Contains("worker = ProcessTreeGuiSuppression.Start(startInfo);", dispatchHost, StringComparison.Ordinal);
        Assert.Contains(
            "process = StartAcceptanceProcess(startInfo, workingDirectory, registrationIdentityReader);",
            gateVerifier,
            StringComparison.Ordinal);
        Assert.Contains("return WorkerProcessJobs.StartRegisteredOwnedOrThrow(", gateVerifier, StringComparison.Ordinal);
    }

    [Fact]
    public void GrandchildFixtureLaunchUsesGuiSuppression()
    {
        var root = FindRepoRoot();
        var dispatchHostTests = File.ReadAllText(Path.Combine(
            root,
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "DispatchProcessHostTests.cs"));

        Assert.Contains(
            "StartGrandchildReapWrapper(startInfo, ProcessTreeGuiSuppression.Start);",
            dispatchHostTests,
            StringComparison.Ordinal);
        Assert.Contains("-NoNewWindow -PassThru -ErrorAction Stop", dispatchHostTests, StringComparison.Ordinal);
        Assert.DoesNotContain("burn" + "-cpu.ps1", dispatchHostTests, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Process tree GUI suppression keeps console ownership in the child launch policy")]
    public void ProcessTreeGuiSuppressionKeepsConsoleOwnershipInChildLaunchPolicy()
    {
        var root = FindRepoRoot();
        var suppression = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Mcg.AgentOrchestrator.Infrastructure",
            "Processes",
            "ProcessTreeGuiSuppression.cs"));
        var policy = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Mcg.AgentOrchestrator.Infrastructure",
            "Processes",
            "ChildConsoleLaunchPolicy.cs"));

        Assert.DoesNotContain("Free" + "Console", suppression, StringComparison.Ordinal);
        Assert.DoesNotContain("Attach" + "Console", suppression, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep", suppression, StringComparison.Ordinal);
        Assert.Contains("ChildConsoleLaunchPolicy.Prepare();", suppression, StringComparison.Ordinal);
        Assert.Contains("PrepareDelayHookForTests?.Invoke();", policy, StringComparison.Ordinal);
        Assert.True(
            policy.IndexOf("PrepareDelayHookForTests?.Invoke();", StringComparison.Ordinal) <
            policy.IndexOf("GetConsoleWindow", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Child console preparation preserves native errors and does not retain launch ownership")]
    public void ChildConsolePreparationPreservesNativeErrorsAndLeavesLaunchPathsUsable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int expectedNativeError = 1234;
        var expected = new Win32Exception(expectedNativeError, "Injected child-console preparation failure.");
        ChildConsoleLaunchPolicy.PrepareDelayHookForTests = () => throw expected;
        try
        {
            Assert.Same(expected, Assert.Throws<Win32Exception>(() =>
            {
                using var _ = ProcessTreeGuiSuppression.AcquireConsoleForChildSpawn();
            }));
            Assert.Same(expected, Assert.Throws<Win32Exception>(() =>
            {
                using var _ = ProcessTreeGuiSuppression.AcquireSuppressedChildSpawn();
            }));
            Assert.Same(expected, Assert.Throws<Win32Exception>(() =>
            {
                using var _ = ProcessTreeGuiSuppression.Start(CreateExitProcessStartInfo());
            }));
            Assert.Equal(expectedNativeError, expected.NativeErrorCode);
        }
        finally
        {
            ChildConsoleLaunchPolicy.PrepareDelayHookForTests = null;
        }

        using (ProcessTreeGuiSuppression.AcquireConsoleForChildSpawn()) { }
        using (ProcessTreeGuiSuppression.AcquireSuppressedChildSpawn()) { }
        using var process = ProcessTreeGuiSuppression.Start(CreateExitProcessStartInfo());
        Assert.True(process.WaitForExit(15_000), "Launch remained blocked after preparation failure cleanup.");
        Assert.Equal(0, process.ExitCode);
    }

    [Fact(DisplayName = "Child console preparation delay does not serialize an unrelated launch")]
    public async Task ChildConsolePreparationDelayDoesNotSerializeUnrelatedLaunch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var invocation = 0;
        ChildConsoleLaunchPolicy.PrepareDelayHookForTests = () =>
        {
            if (Interlocked.Increment(ref invocation) == 1)
            {
                entered.Set();
                release.Wait();
            }
        };

        Task? delayedLaunch = null;
        try
        {
            delayedLaunch = Task.Run(() =>
            {
                using var delayed = ProcessTreeGuiSuppression.Start(CreateExitProcessStartInfo());
                Assert.True(delayed.WaitForExit(15_000), "Delayed launch did not exit after its barrier released.");
                Assert.Equal(0, delayed.ExitCode);
            });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)), "Delayed launch never entered child-console preparation.");

            using var unrelated = ProcessTreeGuiSuppression.Start(CreateExitProcessStartInfo());
            Assert.True(unrelated.WaitForExit(15_000), "Unrelated launch did not complete while preparation was held.");
            Assert.Equal(0, unrelated.ExitCode);
            Assert.False(delayedLaunch.IsCompleted, "Delayed launch completed before its release barrier was opened.");

            release.Set();
            await delayedLaunch;
        }
        finally
        {
            release.Set();
            if (delayedLaunch is not null)
            {
                await delayedLaunch;
            }

            ChildConsoleLaunchPolicy.PrepareDelayHookForTests = null;
        }
    }

    private static ProcessStartInfo CreateExitProcessStartInfo() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
        "/d /q /c exit 0")
    {
        UseShellExecute = false,
        CreateNoWindow = true
    };

    private static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath) ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static class WindowsProbe
    {
        [DllImport("kernel32.dll")]
        internal static extern uint GetErrorMode();

        [DllImport("kernel32.dll")]
        internal static extern uint SetErrorMode(uint uMode);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        public static bool CurrentConsoleIsVisible()
        {
            var console = GetConsoleWindow();
            return console != IntPtr.Zero && IsWindowVisible(console);
        }
    }
}
