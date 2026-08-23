using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class DisposableTrialRootNegativeControlTests
{
    private const string DisableLowIntegrityMutation = "MCG_TRIAL_TEST_DISABLE_LOW_INTEGRITY";
    private const string DisableContainedJobMutation = "MCG_TRIAL_TEST_DISABLE_CONTAINED_JOB";

    public static bool IsWindows => OperatingSystem.IsWindows();

    public static bool IsWindowsAtMediumOrHigher =>
        OperatingSystem.IsWindows() && CurrentIntegrityRid() >= 8192;

    [Xunit.Fact(
        Skip = "Requires Windows at Medium integrity or above.",
        SkipUnless = nameof(IsWindowsAtMediumOrHigher))]
    public void OutsideWriteIsDeniedAndObservableWhenLowIntegrityIsEnabled()
    {
        var fixture = CreateFixtureRepository();
        var outsideFile = Path.Combine(fixture.Root, "outside.txt");
        File.WriteAllText(outsideFile, "unchanged");
        try
        {
            using var lease = CreateNegativeControlFactory().Create(new TrialRootRequest(
                fixture.Source,
                fixture.Commit,
                fixture.Trials,
                "outside-denial",
                [fixture.Source, outsideFile]));
            var command = PowerShellCommand(
                $"try {{ Set-Content -LiteralPath '{Ps(outsideFile)}' -Value changed -ErrorAction Stop; " +
                "Write-Output 'outside-write-unexpected'; exit 7 } " +
                "catch { Write-Output 'outside-write-denied'; exit 0 }");
            var process = lease.Start(command);
            Xunit.Assert.True(process.Process.WaitForExit(15_000), "Trial process did not exit.");
            Xunit.Assert.Equal(0, process.Process.ExitCode);
            Xunit.Assert.Contains("outside-write-denied", File.ReadAllText(process.StdoutPath));
            Xunit.Assert.Equal("unchanged", File.ReadAllText(outsideFile));

            var report = lease.Destroy();
            Xunit.Assert.Empty(report.OutsideWrites);
            Xunit.Assert.True(report.Clean);
            process.Dispose();
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    [Xunit.Fact(
        Skip = "Requires Windows at Medium integrity or above.",
        SkipUnless = nameof(IsWindowsAtMediumOrHigher))]
    public void CloneSourceRepositoryWriteIsDeniedAndByteIdenticalAfterward()
    {
        var fixture = CreateFixtureRepository();
        var tracked = Path.Combine(fixture.Source, "tracked.txt");
        var gitConfig = Path.Combine(fixture.Source, ".git", "config");
        var trackedBefore = File.ReadAllBytes(tracked);
        var gitConfigBefore = File.ReadAllBytes(gitConfig);
        try
        {
            using var lease = new DisposableTrialRoot().Create(new TrialRootRequest(
                fixture.Source,
                fixture.Commit,
                fixture.Trials,
                "source-denial"));
            var command = PowerShellCommand(
                "$denied=0; " +
                $"try {{ Set-Content -LiteralPath '{Ps(tracked)}' -Value changed -ErrorAction Stop }} catch {{ $denied++; Write-Output 'tracked-write-denied' }}; " +
                $"try {{ Add-Content -LiteralPath '{Ps(gitConfig)}' -Value changed -ErrorAction Stop }} catch {{ $denied++; Write-Output 'git-write-denied' }}; " +
                "if ($denied -ne 2) { exit 7 }; exit 0");
            var process = lease.Start(command);
            Xunit.Assert.True(process.Process.WaitForExit(15_000), "Trial process did not exit.");
            var stdout = File.ReadAllText(process.StdoutPath);
            Xunit.Assert.Equal(0, process.Process.ExitCode);
            Xunit.Assert.Contains("tracked-write-denied", stdout);
            Xunit.Assert.Contains("git-write-denied", stdout);
            Xunit.Assert.Equal(trackedBefore, File.ReadAllBytes(tracked));
            Xunit.Assert.Equal(gitConfigBefore, File.ReadAllBytes(gitConfig));
            Xunit.Assert.Empty(RunGit(lease.RootPath, "remote").Trim());

            var report = lease.Destroy();
            Xunit.Assert.Empty(report.OutsideWrites);
            Xunit.Assert.True(report.Clean);
            process.Dispose();
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    [Xunit.Fact]
    public void ProtectedPathMutationIsReportedAndMakesTeardownUnclean()
    {
        var fixture = CreateFixtureRepository();
        var tracked = Path.Combine(fixture.Source, "tracked.txt");
        try
        {
            using var lease = new DisposableTrialRoot().Create(new TrialRootRequest(
                fixture.Source,
                fixture.Commit,
                fixture.Trials,
                "outside-report"));
            File.WriteAllText(tracked, "mutated-by-control");

            var report = lease.Destroy();

            Xunit.Assert.Contains(Path.GetFullPath(tracked), report.OutsideWrites);
            Xunit.Assert.False(report.Clean);
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    [Xunit.Fact(
        Skip = "Requires Windows at Medium integrity or above.",
        SkipUnless = nameof(IsWindowsAtMediumOrHigher))]
    public void CancellationKillsChildAndGrandchildWhenContainedJobIsEnabled()
    {
        var fixture = CreateFixtureRepository();
        try
        {
            using var lease = CreateNegativeControlFactory().Create(new TrialRootRequest(
                fixture.Source,
                fixture.Commit,
                fixture.Trials,
                "cancel-tree"));
            var parentReady = Path.Combine(lease.HarnessStatePath, "parent.ready");
            var childReady = Path.Combine(lease.HarnessStatePath, "child.ready");
            var childPid = Path.Combine(lease.HarnessStatePath, "child.pid");
            var breakawayDenied = Path.Combine(lease.HarnessStatePath, "breakaway.denied");
            var childScript =
                $"$PID | Set-Content -LiteralPath '{Ps(childPid)}'; " +
                $"'ready' | Set-Content -LiteralPath '{Ps(childReady)}'; " +
                "while ($true) { Start-Sleep -Seconds 1 }";
            var encodedChild = Convert.ToBase64String(Encoding.Unicode.GetBytes(childScript));
            var breakawayProbe = BuildBreakawayProbeScript(breakawayDenied);
            var parentScript =
                $"$child=Start-Process powershell.exe -ArgumentList @('-NoProfile','-EncodedCommand','{encodedChild}') -PassThru; " +
                $"while (-not (Test-Path -LiteralPath '{Ps(childReady)}')) {{ Start-Sleep -Milliseconds 25 }}; " +
                breakawayProbe + "; " +
                $"'ready' | Set-Content -LiteralPath '{Ps(parentReady)}'; " +
                "while ($true) { Start-Sleep -Seconds 1 }";

            var parent = lease.Start(PowerShellCommand(parentScript));
            Xunit.Assert.True(WaitForFile(parentReady, TimeSpan.FromSeconds(15)), "Parent readiness was not observed.");
            Xunit.Assert.True(WaitForFile(childReady, TimeSpan.FromSeconds(2)), "Grandchild readiness was not observed.");
            Xunit.Assert.True(WaitForFile(breakawayDenied, TimeSpan.FromSeconds(2)),
                "An explicit breakaway launch was not observably denied.");
            var grandchildProcessId = int.Parse(File.ReadAllText(childPid).Trim(), System.Globalization.CultureInfo.InvariantCulture);

            var report = lease.Destroy();

            Xunit.Assert.Empty(report.SurvivingProcessIds);
            Xunit.Assert.Contains(parent.ProcessId, report.PreTeardownProcessIds);
            Xunit.Assert.Contains(grandchildProcessId, report.PreTeardownProcessIds);
            Xunit.Assert.True(report.Clean);
            parent.Dispose();
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    [Xunit.Fact]
    public void SyntheticSurvivorIsReportedRatherThanIgnored()
    {
        var fixture = CreateFixtureRepository();
        try
        {
            var factory = new DisposableTrialRoot(
                new IcaclsIntegrityLabeler(),
                new SyntheticInventory(424242));
            var lease = factory.Create(new TrialRootRequest(
                fixture.Source,
                fixture.Commit,
                fixture.Trials,
                "survivor-report"));

            var report = lease.Destroy();

            Xunit.Assert.Equal([424242], report.SurvivingProcessIds);
            Xunit.Assert.False(report.Clean);
            Xunit.Assert.Contains(report.Diagnostics, item => item.Contains("424242", StringComparison.Ordinal));
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    [Xunit.Fact(
        Skip = "Requires Windows at Medium integrity or above.",
        SkipUnless = nameof(IsWindowsAtMediumOrHigher))]
    public void CreationFailsClosedWhenIntegrityLabellingIsDisabled()
    {
        var fixture = CreateFixtureRepository();
        try
        {
            var factory = new DisposableTrialRoot(new DisabledIntegrityLabeler(), new SystemTrialProcessInventory());
            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                factory.Create(new TrialRootRequest(fixture.Source, fixture.Commit, fixture.Trials, "no-label")));
            Xunit.Assert.Contains("Low integrity", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    [Xunit.Fact(
        Skip = "Requires Windows at Medium integrity or above.",
        SkipUnless = nameof(IsWindowsAtMediumOrHigher))]
    public void PartialRootCleanupFailureReportsTheSurvivingPathAndBothFailures()
    {
        var fixture = CreateFixtureRepository();
        var labeler = new LockingDisabledIntegrityLabeler();
        try
        {
            var factory = new DisposableTrialRoot(labeler, new SystemTrialProcessInventory());

            var error = Xunit.Assert.Throws<AggregateException>(() =>
                factory.Create(new TrialRootRequest(
                    fixture.Source,
                    fixture.Commit,
                    fixture.Trials,
                    "locked-partial")));

            Xunit.Assert.Contains("partial root may survive", error.Message, StringComparison.OrdinalIgnoreCase);
            var lockedRoot = Xunit.Assert.IsType<string>(labeler.LockedRootPath);
            Xunit.Assert.Contains(lockedRoot, error.Message, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.True(Directory.Exists(lockedRoot));
            Xunit.Assert.Equal(2, error.InnerExceptions.Count);
            Xunit.Assert.Contains("Low integrity", error.InnerExceptions[0].Message, StringComparison.Ordinal);
            Xunit.Assert.True(error.InnerExceptions[1] is IOException or UnauthorizedAccessException);
        }
        finally
        {
            labeler.Dispose();
            DeleteFixture(fixture.Root);
        }
    }

    private static ProcessStartInfo PowerShellCommand(string script)
    {
        var command = new ProcessStartInfo { FileName = "powershell.exe" };
        command.ArgumentList.Add("-NoLogo");
        command.ArgumentList.Add("-NoProfile");
        command.ArgumentList.Add("-NonInteractive");
        command.ArgumentList.Add("-Command");
        command.ArgumentList.Add(script);
        return command;
    }

    // Acceptance can produce the required RED receipts without source mutation:
    // $env:MCG_TRIAL_TEST_DISABLE_LOW_INTEGRITY='1' makes the outside-write control fail.
    // $env:MCG_TRIAL_TEST_DISABLE_CONTAINED_JOB='1' makes the breakaway-denial control fail.
    private static DisposableTrialRoot CreateNegativeControlFactory() =>
        new(
            new IcaclsIntegrityLabeler(),
            new SystemTrialProcessInventory(),
            useContainedJob: !MutationEnabled(DisableContainedJobMutation),
            useLowIntegrityProcess: !MutationEnabled(DisableLowIntegrityMutation));

    private static bool MutationEnabled(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);

    private static string BuildBreakawayProbeScript(string deniedMarker)
    {
        var source = @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class McgBreakawayProbe {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct STARTUPINFO {
    public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
    public int dwX; public int dwY; public int dwXSize; public int dwYSize; public int dwXCountChars;
    public int dwYCountChars; public int dwFillAttribute; public int dwFlags; public short wShowWindow;
    public short cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError;
  }
  [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION {
    public IntPtr hProcess; public IntPtr hThread; public int dwProcessId; public int dwThreadId;
  }
  [DllImport(""kernel32.dll"", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
    uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
  public static int TryStart() {
    var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si); PROCESS_INFORMATION pi;
    var ok = CreateProcess(""cmd.exe"", new StringBuilder(""cmd.exe /c exit 0""), IntPtr.Zero, IntPtr.Zero,
      false, 0x01000000, IntPtr.Zero, Environment.CurrentDirectory, ref si, out pi);
    if (!ok) return -Marshal.GetLastWin32Error();
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess); return pi.dwProcessId;
  }
  [DllImport(""kernel32.dll"")] static extern bool CloseHandle(IntPtr handle);
}";
        var encodedSource = Convert.ToBase64String(Encoding.Unicode.GetBytes(source));
        return
            $"$src=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{encodedSource}')); " +
            "Add-Type -TypeDefinition $src; $breakaway=[McgBreakawayProbe]::TryStart(); " +
            $"if ($breakaway -lt 0) {{ 'denied' | Set-Content -LiteralPath '{Ps(deniedMarker)}' }}";
    }

    private static bool WaitForFile(string path, TimeSpan timeout)
    {
        if (File.Exists(path))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Readiness path has no directory.");
        using var observed = new ManualResetEventSlim();
        using var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        watcher.Created += (_, _) => observed.Set();
        return File.Exists(path) || observed.Wait(timeout) || File.Exists(path);
    }

    private static int CurrentIntegrityRid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

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
                if (!ConvertSidToStringSid(sid, out var sidStringPointer))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    var sidString = Marshal.PtrToStringUni(sidStringPointer)
                        ?? throw new InvalidOperationException("Token integrity SID was empty.");
                    return int.Parse(
                        sidString[(sidString.LastIndexOf('-') + 1)..],
                        System.Globalization.CultureInfo.InvariantCulture);
                }
                finally
                {
                    _ = LocalFree(sidStringPointer);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private static string Ps(string value) => value.Replace("'", "''");

    private static (string Root, string Source, string Trials, string Commit) CreateFixtureRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-trial-negative-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var trials = Path.Combine(root, "trials");
        Directory.CreateDirectory(source);
        RunGit(source, "init", "--initial-branch=main");
        RunGit(source, "config", "user.name", "Trial Root Test");
        RunGit(source, "config", "user.email", "trial-root@example.invalid");
        File.WriteAllText(Path.Combine(source, "tracked.txt"), "baseline");
        RunGit(source, "add", "tracked.txt");
        RunGit(source, "commit", "-m", "fixture");
        return (root, source, trials, RunGit(source, "rev-parse", "HEAD").Trim());
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var result = GitCli.Run(workingDirectory, arguments);
        Xunit.Assert.True(result.Succeeded, result.Error);
        return result.Output;
    }

    private static void DeleteFixture(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed class SyntheticInventory(params int[] survivors) : ITrialProcessInventory
    {
        public IReadOnlyList<int> FindSurvivors(IReadOnlyCollection<int> observedProcessIds) => survivors;
    }

    private sealed class DisabledIntegrityLabeler : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) => new(true, false, false);

        public bool SetIntegrity(string path, string level, bool recursive) => false;
    }

    private sealed class LockingDisabledIntegrityLabeler : IWorkerIntegrityLabeler, IDisposable
    {
        private FileStream? _lockedFile;

        public string? LockedRootPath { get; private set; }

        public IntegrityLabelState Query(string path) => new(true, false, false);

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            LockedRootPath = path;
            _lockedFile = new FileStream(
                Path.Combine(path, "tracked.txt"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return false;
        }

        public void Dispose() => _lockedFile?.Dispose();
    }

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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
