using System.Diagnostics;
using System.Runtime.InteropServices;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Serialized: these facts launch real redirected powershell children and canary capture fixtures, so
// they must not overlap another class's process spawning in the same host. An unrelated concurrent
// CreateProcess with inherited handles is exactly the condition two of them deliberately create.
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class PostLandingCanaryCaptureAvailabilityTests
{
    [Xunit.Fact]
    public void IncompleteCapture_ReportsReleaseAndAvailabilityEvidence()
    {
        var observation = new RetainedCaptureObservation(
            ExclusiveOpenSucceeded: false,
            Attempts: 7,
            Elapsed: TimeSpan.FromMilliseconds(350),
            LastErrorCode: "0x80070020");
        var release = new WorkerProcessJobReleaseEvidence(
            RegistrationFound: true,
            TerminationRequested: true,
            JobExitConfirmed: false);

        // The holder read is supplied per call, so this fact substitutes it without any process-global
        // seam that a concurrent fact in another class could observe.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PostLandingCanaryRunner.EnsureRetainedCaptureReadable(
                "fixture.err.log",
                observation,
                release,
                [401, 402],
                _ => new FileHandleHolderObservation([new FileHandleHolder(4242, "powershell")], null)));

        Assert.Contains("retained-capture-incomplete", exception.Message, StringComparison.Ordinal);
        Assert.Equal("fixture.err.log", ReadKey(exception.Message, "path"));
        Assert.Equal("false", ReadKey(exception.Message, "exclusive-open"));
        Assert.Equal("0x80070020", ReadKey(exception.Message, "last-error"));
        Assert.Equal("true", ReadKey(exception.Message, "termination-requested"));
        Assert.Equal("false", ReadKey(exception.Message, "job-exit-confirmed"));
        Assert.Equal("401,402", ReadKey(exception.Message, "surviving-descendants"));
        Assert.Equal("4242:powershell", ReadKey(exception.Message, "capture-holders"));
        Assert.Equal("unknown", ReadKey(exception.Message, "stable-length"));
        Assert.Single(exception.Message.Split('\n'));

        // A holder read that was refused is reported as data, not as a second failure, and never masks
        // the incomplete outcome.
        var unnamedHolder = Assert.Throws<InvalidOperationException>(() =>
            PostLandingCanaryRunner.EnsureRetainedCaptureReadable(
                "fixture.err.log",
                observation with { FirstObservedLength = 4096, LastObservedLength = 4096 },
                release,
                [],
                _ => FileHandleHolderObservation.Failed("NtQueryInformationFile-0xC0000008")));
        Assert.Equal(
            "unavailable(NtQueryInformationFile-0xC0000008)",
            ReadKey(unnamedHolder.Message, "capture-holders"));
        Assert.Equal("stable(4096)", ReadKey(unnamedHolder.Message, "stable-length"));

        // Readable but unproven ownership is still incomplete: a foreign holder can never stand in for
        // proof that everything this process launched has exited.
        var readableButUnconfirmed = observation with
        {
            ExclusiveOpenSucceeded = true,
            LastErrorCode = null
        };
        var unconfirmedException = Assert.Throws<InvalidOperationException>(() =>
            PostLandingCanaryRunner.EnsureRetainedCaptureReadable(
                "fixture.err.log",
                readableButUnconfirmed,
                release,
                [],
                _ => new FileHandleHolderObservation([], null)));
        Assert.Equal("true", ReadKey(unconfirmedException.Message, "exclusive-open"));
        Assert.Equal("false", ReadKey(unconfirmedException.Message, "job-exit-confirmed"));
        Assert.Equal("none", ReadKey(unconfirmedException.Message, "capture-holders"));

        // A failed descendant observation is an UNKNOWN surviving set, not an empty one. Every other
        // release fact here is proven, so this arm isolates the one difference: null surviving ids must
        // still be incomplete, because "nothing was seen" is not "nothing survives".
        var fullyReleased = new WorkerProcessJobReleaseEvidence(
            RegistrationFound: true,
            TerminationRequested: true,
            JobExitConfirmed: true);
        var unobserved = Assert.Throws<InvalidOperationException>(() =>
            PostLandingCanaryRunner.EnsureRetainedCaptureReadable(
                "fixture.err.log",
                readableButUnconfirmed,
                fullyReleased,
                null,
                _ => new FileHandleHolderObservation([], null)));
        Assert.Equal("true", ReadKey(unobserved.Message, "exclusive-open"));
        Assert.Equal("true", ReadKey(unobserved.Message, "job-exit-confirmed"));
        Assert.Equal("observation-failed", ReadKey(unobserved.Message, "surviving-descendants"));

        // Control: the same call with an observed-empty set is the only shape that is allowed to pass.
        PostLandingCanaryRunner.EnsureRetainedCaptureReadable(
            "fixture.err.log",
            readableButUnconfirmed,
            fullyReleased,
            [],
            _ => new FileHandleHolderObservation([], null));
    }

    // Controlled comparison for the acceptance-observed signature
    // (exclusive-open=false; last-error=0x80070020; job-exit-confirmed=true; surviving-descendants=none).
    // The only variable between the two arms is whether the capture handle is inheritable while an
    // UNRELATED redirected child is launched from this process. .NET's Process.Start with redirected
    // streams calls CreateProcess with bInheritHandles=TRUE and no PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
    // so it inherits every inheritable handle the process owns at that instant.
    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void CaptureHandle_InheritableWindow_DeterminesUnrelatedChildHolder(bool inheritable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateFixtureRoot();
        var capturePath = Path.Combine(root, "capture.out.log");
        var releasePath = Path.Combine(root, "release.signal");
        Process? unrelated = null;
        try
        {
            var securityAttributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                bInheritHandle = inheritable
            };
            var captureHandle = CreateFileW(
                capturePath,
                GenericWrite,
                FileShareRead | FileShareDelete,
                ref securityAttributes,
                CreateAlways,
                FileAttributeNormal,
                IntPtr.Zero);
            Assert.False(captureHandle.IsInvalid);

            // Launched while our capture handle is open: this is the leak window a native capture seam
            // holds open between CreateFileW and the CreateProcess that consumes the handle.
            unrelated = StartUnrelatedRedirectedChild(root, releasePath);
            captureHandle.Dispose();

            var observation = PostLandingCanaryCaptureAvailability.ObserveOnce(capturePath);
            var holders = FileHandleHolders.Read(capturePath);
            Assert.Null(holders.Failure);
            var holderProcessIds = holders.Holders.Select(holder => holder.ProcessId).ToArray();
            Console.WriteLine(
                $"inheritable={inheritable.ToString().ToLowerInvariant()}; " +
                $"self-pid={Environment.ProcessId}; unrelated-child-pid={unrelated.Id}; " +
                $"unrelated-child-image=powershell.exe; capture-access=GENERIC_WRITE; " +
                "capture-share=FILE_SHARE_READ|FILE_SHARE_DELETE; probe-share=FileShare.Read; " +
                $"exclusive-open={observation.ExclusiveOpenSucceeded.ToString().ToLowerInvariant()}; " +
                $"last-error={observation.LastErrorCode ?? "none"}; " +
                $"capture-holders={holders.Format()}");

            if (inheritable)
            {
                Assert.False(observation.ExclusiveOpenSucceeded);
                Assert.Equal("0x80070020", observation.LastErrorCode);
                Assert.Contains(unrelated.Id, holderProcessIds);
                Assert.Equal(holders.Format(), observation.Holders?.Format());
            }
            else
            {
                Assert.True(observation.ExclusiveOpenSucceeded);
                Assert.DoesNotContain(unrelated.Id, holderProcessIds);
            }

            File.WriteAllText(releasePath, "release");
            Assert.True(unrelated.WaitForExit(TimeSpan.FromSeconds(30)), "Unrelated fixture child did not exit.");
            Assert.True(
                SpinWait.SpinUntil(
                    () => PostLandingCanaryCaptureAvailability.ObserveOnce(capturePath).ExclusiveOpenSucceeded,
                    TimeSpan.FromSeconds(15)),
                "Capture stayed unreadable after the unrelated holder exited.");
        }
        finally
        {
            ReleaseUnrelatedChild(unrelated, releasePath);
            DeleteFixtureRoot(root);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(23)]
    public async Task RunProcess_TerminalExitLeavesCompleteReadableCaptures(int exitCode)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateFixtureRoot();
        try
        {
            var scriptPath = Path.Combine(root, "terminal.ps1");
            var logDirectory = Path.Combine(root, "logs");
            File.WriteAllText(
                scriptPath,
                "param([int]$ExitCode)\n" +
                "[Console]::Out.Write('terminal stdout')\n" +
                "[Console]::Error.Write('terminal stderr')\n" +
                "exit $ExitCode\n");

            var result = await PostLandingCanaryRunner.RunProcessForTestsAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-File", scriptPath, exitCode.ToString()],
                root,
                logDirectory,
                $"terminal-{exitCode}",
                CancellationToken.None);

            Assert.Equal(exitCode, result.ExitCode);
            Assert.Equal("terminal stdout", result.Stdout);
            Assert.Equal("terminal stderr", result.Stderr);

            // The contract the terminal consumers actually depend on: File.ReadAllText opens
            // FileShare.Read, so a capture the runner reported as complete must open that way.
            Assert.All(Directory.GetFiles(logDirectory, "*.log"), path => File.ReadAllText(path));
        }
        finally
        {
            DeleteFixtureRoot(root);
        }
    }

    // Reproduces the acceptance failure at its worst case. The unrelated redirected child is launched at
    // the one instant the canary launch has inheritable capture handles in this process's handle table,
    // so it inherits a write handle it never writes through and holds it past the canary's own confirmed
    // job exit. That is exactly the acceptance signature - exclusive-open=false, last-error=0x80070020,
    // job-exit-confirmed=true, surviving-descendants=none - and the runner must stay loud about it while
    // naming the process responsible, because the terminal consumers still cannot read that capture.
    [Xunit.Fact]
    public async Task RunProcess_UnrelatedRedirectedSpawnInLaunchWindowIsReportedIncompleteNamingHolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateFixtureRoot();
        var releasePath = Path.Combine(root, "release.signal");
        var logDirectory = Path.Combine(root, "logs");
        Process? unrelated = null;
        try
        {
            var scriptPath = Path.Combine(root, "terminal.ps1");
            File.WriteAllText(
                scriptPath,
                "[Console]::Out.Write('window stdout')\n" +
                "[Console]::Error.Write('window stderr')\n" +
                "exit 0\n");

            // The observer is per-launch, so only this fact's own canary launch can spawn the unrelated
            // child: a concurrent capture launch in another class never inherits this hook.
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PostLandingCanaryRunner.RunProcessForTestsAsync(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-File", scriptPath],
                    root,
                    logDirectory,
                    "launch-window",
                    CancellationToken.None,
                    inheritableWindowObserver: () =>
                        unrelated ??= StartUnrelatedRedirectedChild(root, releasePath)));

            Assert.NotNull(unrelated);
            Assert.False(unrelated!.HasExited, "The unrelated fixture child must outlive the canary run.");

            Console.WriteLine($"unrelated-child-pid={unrelated.Id}; failure={failure.Message}");
            Assert.Contains("retained-capture-incomplete", failure.Message, StringComparison.Ordinal);
            Assert.Equal("false", ReadKey(failure.Message, "exclusive-open"));
            Assert.Equal("true", ReadKey(failure.Message, "job-exit-confirmed"));

            // The point of the whole change: the message names the process that holds the log.
            Assert.Contains(
                $"{unrelated.Id}:",
                ReadKey(failure.Message, "capture-holders"),
                StringComparison.Ordinal);

            // And the reason a broad-sharing read may not decide completeness: it succeeds on exactly
            // the capture that the terminal consumers still cannot open.
            var heldCapture = ReadKey(failure.Message, "path");
            Assert.True(PostLandingCanaryCaptureAvailability.CanOpenForConsumerRead(heldCapture));
            Assert.Throws<IOException>(() => File.ReadAllText(heldCapture));
        }
        finally
        {
            ReleaseUnrelatedChild(unrelated, releasePath);
            DeleteFixtureRoot(root);
        }
    }

    private static string ReadKey(string message, string key)
    {
        var marker = key + "=";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The message carried no '{key}' key: {message}");
        start += marker.Length;
        var end = message.IndexOf(';', start);
        return end < 0 ? message[start..] : message[start..end];
    }

    private static Process StartUnrelatedRedirectedChild(string workingDirectory, string releasePath)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            $"while (-not [IO.File]::Exists('{Ps(releasePath)}')) {{ Start-Sleep -Milliseconds 10 }}");
        var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Unrelated redirected fixture child did not start.");
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        return process;
    }

    private static void ReleaseUnrelatedChild(Process? unrelated, string releasePath)
    {
        if (unrelated is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(releasePath, "release");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            if (!unrelated.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                unrelated.Kill(entireProcessTree: true);
                unrelated.WaitForExit(TimeSpan.FromSeconds(15));
            }
        }
        catch (InvalidOperationException)
        {
        }

        unrelated.Dispose();
    }

    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareDelete = 0x00000004;
    private const uint CreateAlways = 2;
    private const uint FileAttributeNormal = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    private static string CreateFixtureRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-canary-capture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteFixtureRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Ps(string path) => path.Replace("'", "''", StringComparison.Ordinal);
}
