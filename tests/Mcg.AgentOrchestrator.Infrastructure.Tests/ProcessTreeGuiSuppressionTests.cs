using System.Diagnostics;
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
            startInfo.Environment["MCG_PROBE_OUTPUT"] = probePath;

            Process process;
            using (var suppressedSpawn = ProcessTreeGuiSuppression.AcquireErrorModeForChildSpawn())
            {
                Assert.False(suppressedSpawn.HiddenConsoleAcquired);
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

                    [DllImport("user32.dll")]
                    public static extern bool IsWindowVisible(IntPtr hWnd);
                }
                "@

                $console = [McgNativeProbe]::GetConsoleWindow()
                $visible = if ($console -eq [IntPtr]::Zero) { $false } else { [McgNativeProbe]::IsWindowVisible($console) }
                [pscustomobject]@{
                    errorMode = [uint32][McgNativeProbe]::GetErrorMode()
                    hasConsole = ($console -ne [IntPtr]::Zero)
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
            startInfo.Environment["MCG_PROBE_SHELL"] = WorkerShell.Executable;
            startInfo.Environment["MCG_PROBE_SCRIPT"] = childScript;
            startInfo.Environment["MCG_PROBE_OUTPUT"] = probePath;

            using var process = ProcessTreeGuiSuppression.Start(startInfo);
            Assert.True(process.WaitForExit(15_000), "Timed out waiting for GUI suppression probe.");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.Equal(0, process.ExitCode);
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
