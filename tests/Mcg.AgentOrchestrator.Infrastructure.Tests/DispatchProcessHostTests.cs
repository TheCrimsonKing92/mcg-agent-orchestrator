using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DispatchProcessHostTests
{
    [Xunit.Fact(DisplayName = "DispatchProcessHost_parameters_round_trip_via_camelCase_json")]
    public void DispatchProcessHostParametersRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "dispatch.json");
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output ok",
                dir,
                Path.Combine(dir, "out.log"),
                Path.Combine(dir, "err.log"),
                Path.Combine(dir, "exit.txt"),
                Path.Combine(dir, "heartbeat.json"),
                ShutdownBuildServerOnExit: true,
                DisableSharedCompilation: true);

            DispatchProcessHost.WriteParameters(path, parameters);

            var json = File.ReadAllText(path);
            // The detached host reads this with a camelCase policy, so the keys must be camelCase.
            Assert.True(json.Contains("\"command\"", StringComparison.Ordinal));
            Assert.True(json.Contains("\"disableSharedCompilation\"", StringComparison.Ordinal));

            var roundTripped = JsonSerializer.Deserialize<DispatchProcessHost.DispatchRunParameters>(
                json,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.Equal(parameters, roundTripped);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_low_integrity_path_removes_windowsapps_and_prepends_shell_dir")]
    public void LowIntegrityPathRemovesWindowsAppsAndPrependsShellDir()
    {
        var shellDir = Path.Combine(Path.GetTempPath(), "real-powershell");
        var shell = Path.Combine(shellDir, OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh");
        var windowsApps = Path.Combine(Path.GetTempPath(), "Microsoft", "WindowsApps");
        var toolDir = Path.Combine(Path.GetTempPath(), "tooling");
        var originalPath = string.Join(Path.PathSeparator, windowsApps, toolDir, shellDir);

        var result = DispatchProcessHost.BuildLowIntegrityPath(originalPath, shell);
        var entries = result.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(shellDir, entries[0]);
        Assert.Contains(toolDir, entries);
        Assert.DoesNotContain(entries, DispatchProcessHost.IsWindowsAppsPathSegment);
        Assert.Equal(1, entries.Count(entry => string.Equals(entry, shellDir, StringComparison.OrdinalIgnoreCase)));
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_writes_exit_file_when_grandchild_holds_pipe_after_worker_exits")]
    public void DispatchProcessHostWritesExitFileWhenGrandchildHoldsPipeAfterWorkerExits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-drain-test", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        Process? hostProcess = null;
        try
        {
            // Command: spawn a long-running grandchild (inheriting the pipe handles),
            // then the worker exits immediately. The dispatch host must time-out the
            // drain and write the exit-code file rather than blocking forever.
            var hangCommand = OperatingSystem.IsWindows()
                ? "$psi = [System.Diagnostics.ProcessStartInfo]::new('ping.exe', '-n 30 127.0.0.1'); $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; [void][System.Diagnostics.Process]::Start($psi); Write-Output 'done'; exit 0"
                : "$psi = [System.Diagnostics.ProcessStartInfo]::new('sleep', '60'); $psi.UseShellExecute = $false; [void][System.Diagnostics.Process]::Start($psi); Write-Output 'done'; exit 0";

            var parametersPath = Path.Combine(dir, "dispatch.json");
            var stdoutPath = Path.Combine(dir, "out.log");
            var stderrPath = Path.Combine(dir, "err.log");
            var exitCodePath = Path.Combine(dir, "exit.txt");

            DispatchProcessHost.WriteParameters(parametersPath, new DispatchProcessHost.DispatchRunParameters(
                hangCommand,
                dir,
                stdoutPath,
                stderrPath,
                exitCodePath,
                null,
                ShutdownBuildServerOnExit: false,
                DisableSharedCompilation: false));

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll"));
            startInfo.ArgumentList.Add(DispatchProcessHost.SubcommandName);
            startInfo.ArgumentList.Add(parametersPath);

            hostProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start dispatch host.");

            // The exit file must appear within the drain timeout (~12 s) plus buffer.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (!File.Exists(exitCodePath) && DateTimeOffset.UtcNow < deadline)
                Thread.Sleep(200);

            // Kill the process tree (including any grandchildren that inherited pipe handles)
            // and wait for the host to fully exit before reading exit.txt. This removes the
            // file-handle race where a lingering grandchild holds an inherited handle while
            // we read. FileShare.ReadWrite + retry in ReadExitCodeWithRetry covers any
            // remaining window.
            try { hostProcess.Kill(entireProcessTree: true); } catch { }
            try { hostProcess.WaitForExit(5000); } catch { }

            Assert.True(File.Exists(exitCodePath));
            Assert.Equal("0", ReadExitCodeWithRetry(exitCodePath));
        }
        finally
        {
            try { hostProcess?.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ShouldReapWorker_spares_buffering_workers_until_maxRuntime")]
    public void ShouldReapWorkerSparesBufferingWorkers()
    {
        var maxRuntime = TimeSpan.FromMinutes(60);
        var maxIdle = TimeSpan.FromMinutes(20);

        // A worker that has produced NO output (e.g. claude-cli -p buffers to the end) is NOT reaped
        // on the idle cap even past it — only maxRuntime bounds it. This is the buffering-worker fix.
        Assert.False(DispatchProcessHost.ShouldReapWorker(TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(25), hasProducedOutput: false, maxRuntime, maxIdle));
        Assert.True(DispatchProcessHost.ShouldReapWorker(TimeSpan.FromMinutes(61), TimeSpan.FromMinutes(25), hasProducedOutput: false, maxRuntime, maxIdle));

        // A worker that streamed then went quiet past the idle cap IS reaped (stall detection preserved).
        Assert.True(DispatchProcessHost.ShouldReapWorker(TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(25), hasProducedOutput: true, maxRuntime, maxIdle));
        Assert.False(DispatchProcessHost.ShouldReapWorker(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), hasProducedOutput: true, maxRuntime, maxIdle));
    }

    [Xunit.Fact(DisplayName = "HasProgressed_returns_true_when_cpu_grows_above_epsilon_with_flat_bytes")]
    public void HasProgressedDetectsCpuGrowthWhenBytesFlat()
    {
        // CPU grows from 0 to 100ms (> 50ms epsilon), bytes flat
        Assert.True(DispatchProcessHost.HasProgressed(0, 0, 0, 100, 50));
    }

    [Xunit.Fact(DisplayName = "HasProgressed_returns_false_when_both_cpu_and_bytes_are_flat")]
    public void HasProgressedNoProgressWhenFlat()
    {
        // Both CPU and bytes flat
        Assert.False(DispatchProcessHost.HasProgressed(0, 0, 0, 0, 50));
    }

    [Xunit.Fact(DisplayName = "HasProgressed_returns_false_when_cpu_growth_equals_epsilon_exactly")]
    public void HasProgressedCpuAtEpsilonIsNotProgress()
    {
        // Delta = 50ms exactly at epsilon — not strictly greater, so not progress
        Assert.False(DispatchProcessHost.HasProgressed(0, 0, 0, 50, 50));
    }

    [Xunit.Fact(DisplayName = "HasProgressed_returns_true_when_bytes_grow_regardless_of_cpu")]
    public void HasProgressedDetectsByteGrowth()
    {
        Assert.True(DispatchProcessHost.HasProgressed(0, 10, 0, 0, 50));
    }

    [Xunit.Fact(DisplayName = "WaitForIntegrityLabeler_kills_helper_when_timeout_expires")]
    public void WaitForIntegrityLabelerKillsTimedOutHelper()
    {
        using var process = StartLongRunningHelper();

        var completed = DispatchProcessHost.WaitForIntegrityLabeler(process, TimeSpan.FromMilliseconds(100));

        Assert.False(completed);
        Assert.True(process.HasExited);
    }

    [Xunit.Fact(DisplayName = "WaitForIntegrityLabeler_returns_true_when_helper_exits_nonzero")]
    public void WaitForIntegrityLabelerReturnsTrueWhenHelperExitsNonZero()
    {
        using var process = StartNonZeroHelper();

        var completed = DispatchProcessHost.WaitForIntegrityLabeler(process, TimeSpan.FromSeconds(5));

        Assert.True(completed);
        Assert.True(process.HasExited);
        Assert.NotEqual(0, process.ExitCode);
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_low_integrity_setup_keeps_linked_worktree_git_file_medium")]
    public void LowIntegritySetupKeepsLinkedWorktreeGitFileMedium()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (GetCurrentProcessIntegrityRid() < MediumIntegrityRid)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-acl-test", Guid.NewGuid().ToString("n"));
        var repo = Path.Combine(root, "repo");
        var worktree = Path.Combine(root, "linked-worktree");
        Directory.CreateDirectory(root);
        try
        {
            CreateLinkedWorktree(repo, worktree);
            var gitFile = Path.Combine(worktree, ".git");
            var workerFile = Path.Combine(worktree, "worker.txt");
            File.WriteAllText(workerFile, "worker editable");
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            var parametersPath = Path.Combine(root, "dispatch.json");

            DispatchProcessHost.WriteParameters(parametersPath, new DispatchProcessHost.DispatchRunParameters(
                "Write-Output sandbox-ready",
                worktree,
                Path.Combine(logs, "out.log"),
                Path.Combine(logs, "err.log"),
                Path.Combine(logs, "exit.txt"),
                Path.Combine(logs, "heartbeat.json"),
                ShutdownBuildServerOnExit: false,
                DisableSharedCompilation: false,
                SandboxLowIntegrity: true,
                WorkerSandboxProvider.Codex));

            var exitCode = DispatchProcessHost.Run(parametersPath);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(gitFile));
            Assert.True(GetMandatoryIntegrityRid(gitFile) >= MediumIntegrityRid);
            Assert.Equal(LowIntegrityRid, GetMandatoryIntegrityRid(workerFile));

            var commonGitDir = RunGit(worktree, "rev-parse", "--git-common-dir");
            var commonGitDirPath = Path.IsPathRooted(commonGitDir)
                ? commonGitDir
                : Path.GetFullPath(Path.Combine(worktree, commonGitDir));
            Assert.True(GetMandatoryIntegrityRid(commonGitDirPath) >= MediumIntegrityRid);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static Process StartLongRunningHelper()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "ping.exe" : "sleep",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("30");
            startInfo.ArgumentList.Add("127.0.0.1");
        }
        else
        {
            startInfo.ArgumentList.Add("30");
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start long-running helper.");
    }

    private static Process StartNonZeroHelper()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("exit /b 5");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("exit 5");
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start nonzero helper.");
    }

    private static void CreateLinkedWorktree(string repo, string worktree)
    {
        Directory.CreateDirectory(repo);
        RunGit(repo, "init");
        RunGit(repo, "config", "user.email", "tests@example.invalid");
        RunGit(repo, "config", "user.name", "Tests");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
        RunGit(repo, "add", "seed.txt");
        RunGit(repo, "commit", "-m", "seed");
        RunGit(repo, "worktree", "add", "-b", "linked-test", worktree);
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"git {string.Join(' ', arguments)} timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit {process.ExitCode}: {stderr}");
        }

        return stdout.Trim();
    }

    private const int LowIntegrityRid = 0x1000;
    private const int MediumIntegrityRid = 0x2000;

    private static int GetMandatoryIntegrityRid(string path)
    {
        var error = GetNamedSecurityInfo(
            path,
            1,
            0x00000010,
            out _,
            out _,
            out _,
            out _,
            out var securityDescriptor);
        if (error != 0)
        {
            throw new Win32Exception((int)error);
        }

        try
        {
            if (!GetSecurityDescriptorSacl(securityDescriptor, out var saclPresent, out var sacl, out _) ||
                !saclPresent ||
                sacl == IntPtr.Zero)
            {
                throw new InvalidOperationException($"No mandatory label SACL found for '{path}'.");
            }

            var aceCount = Marshal.ReadInt16(sacl, 4);
            for (var i = 0; i < aceCount; i++)
            {
                if (!GetAce(sacl, i, out var ace))
                {
                    continue;
                }

                if (Marshal.ReadByte(ace) != 0x11)
                {
                    continue;
                }

                var sid = IntPtr.Add(ace, 8);
                if (!ConvertSidToStringSid(sid, out var sidStringPtr))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    var sidString = Marshal.PtrToStringUni(sidStringPtr)
                        ?? throw new InvalidOperationException("Integrity SID was empty.");
                    var lastDash = sidString.LastIndexOf('-');
                    return int.Parse(sidString[(lastDash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                }
                finally
                {
                    LocalFree(sidStringPtr);
                }
            }

            throw new InvalidOperationException($"No mandatory label ACE found for '{path}'.");
        }
        finally
        {
            LocalFree(securityDescriptor);
        }
    }

    private static int GetCurrentProcessIntegrityRid()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            GetTokenInformation(token, 25, IntPtr.Zero, 0, out var length);
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, length, out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var sid = Marshal.ReadIntPtr(buffer);
                if (!ConvertSidToStringSid(sid, out var sidStringPtr))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    var sidString = Marshal.PtrToStringUni(sidStringPtr)
                        ?? throw new InvalidOperationException("Token integrity SID was empty.");
                    var lastDash = sidString.LastIndexOf('-');
                    return int.Parse(sidString[(lastDash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                }
                finally
                {
                    LocalFree(sidStringPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static string ReadExitCodeWithRetry(string path, int attempts = 5, int delayMs = 100)
    {
        Exception? last = null;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException ex)
            {
                last = ex;
                if (i < attempts - 1) Thread.Sleep(delayMs);
            }
        }
        throw last!;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetNamedSecurityInfo(
        string pObjectName,
        int objectType,
        uint securityInfo,
        out IntPtr ppsidOwner,
        out IntPtr ppsidGroup,
        out IntPtr ppDacl,
        out IntPtr ppSacl,
        out IntPtr ppSecurityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(
        IntPtr pSecurityDescriptor,
        out bool lpbSaclPresent,
        out IntPtr pSacl,
        out bool lpbSaclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetAce(IntPtr pAcl, int dwAceIndex, out IntPtr pAce);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
