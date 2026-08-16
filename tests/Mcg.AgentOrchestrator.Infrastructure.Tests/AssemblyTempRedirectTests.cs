using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class AssemblyTempRedirectTests
{
    [Fact(Timeout = 60_000)]
    public async Task ConcurrentTestHostsReceiveDistinctTempRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"mtp-temp-hosts-{Guid.NewGuid():N}");
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "Mcg.AgentOrchestrator.Infrastructure.Tests.exe");
        Directory.CreateDirectory(root);
        Assert.True(File.Exists(executable), $"Missing independently launchable MTP apphost '{executable}'.");

        var releaseName = $"Local\\mcg-mtp-temp-release-{Guid.NewGuid():N}";
        var firstReadyName = $"Local\\mcg-mtp-temp-ready-{Guid.NewGuid():N}";
        var secondReadyName = $"Local\\mcg-mtp-temp-ready-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var firstReady = new EventWaitHandle(false, EventResetMode.ManualReset, firstReadyName);
        using var secondReady = new EventWaitHandle(false, EventResetMode.ManualReset, secondReadyName);
        MtpProbeProcess? firstProcess = null;
        MtpProbeProcess? secondProcess = null;
        try
        {
            firstProcess = StartMtpProbe(
                executable,
                root,
                Path.Combine(root, "first-receipt.json"),
                firstReadyName,
                releaseName);
            secondProcess = StartMtpProbe(
                executable,
                root,
                Path.Combine(root, "second-receipt.json"),
                secondReadyName,
                releaseName);

            Assert.NotEqual(firstProcess.Process.Id, secondProcess.Process.Id);
            Assert.True(
                WaitHandle.WaitAll([firstReady, secondReady], TimeSpan.FromSeconds(30)),
                "Two independently launched MTP apphosts did not both reach the mutable-fixture gate.");

            var firstReceipt = ReadProbeReceipt(firstProcess.ReceiptPath);
            var secondReceipt = ReadProbeReceipt(secondProcess.ReceiptPath);
            Assert.Equal(firstProcess.Process.Id, firstReceipt.ProcessId);
            Assert.Equal(secondProcess.Process.Id, secondReceipt.ProcessId);
            Assert.Equal(executable, firstReceipt.ProcessPath, ignoreCase: true);
            Assert.Equal(executable, secondReceipt.ProcessPath, ignoreCase: true);
            Assert.False(string.Equals(
                firstReceipt.TempRoot,
                secondReceipt.TempRoot,
                StringComparison.OrdinalIgnoreCase));
            Assert.False(string.Equals(
                firstReceipt.FixtureRepositoryPath,
                secondReceipt.FixtureRepositoryPath,
                StringComparison.OrdinalIgnoreCase));
            var firstRelativeRepository = Path.GetRelativePath(
                firstReceipt.TempRoot,
                firstReceipt.FixtureRepositoryPath);
            var secondRelativeRepository = Path.GetRelativePath(
                secondReceipt.TempRoot,
                secondReceipt.FixtureRepositoryPath);
            Assert.Equal(firstRelativeRepository, secondRelativeRepository, ignoreCase: true);
            Assert.EndsWith(
                $"p{firstReceipt.ProcessId:x}",
                firstReceipt.TempRoot,
                StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(
                $"p{secondReceipt.ProcessId:x}",
                secondReceipt.TempRoot,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(firstReceipt.ProcessId.ToString(), firstReceipt.OwnerContents);
            Assert.Equal(secondReceipt.ProcessId.ToString(), secondReceipt.OwnerContents);

            release.Set();
            var results = await Task.WhenAll(firstProcess.WaitForExitAsync(), secondProcess.WaitForExitAsync());
            Assert.True(results[0].ExitCode == 0, FormatProcessFailure("first", results[0]));
            Assert.True(results[1].ExitCode == 0, FormatProcessFailure("second", results[1]));
            Assert.Contains(
                $"assembly-temp-redirect selected={firstReceipt.TempRoot}",
                results[0].Stderr,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                $"assembly-temp-redirect selected={secondReceipt.TempRoot}",
                results[1].Stderr,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            release.Set();
            if (firstProcess is not null)
            {
                await firstProcess.DisposeAsync();
            }
            if (secondProcess is not null)
            {
                await secondProcess.DisposeAsync();
            }
            DeleteDirectory(root);
        }
    }

    [Fact(DisplayName = "Reaper removes only roots whose owning process is gone")]
    public void ReapsOnlyRootsOwnedByDeadProcesses()
    {
        var alive = new HashSet<int> { 0x1a2b, 0x30 };

        var reapable = AssemblyTempRedirect.SelectReapableRoots(
            ["p1a2b", "p30", "pdead1", "pbeef", "p7fffffff"],
            currentProcessId: 0x30,
            isProcessAlive: pid => alive.Contains(pid));

        Assert.Equal(["pdead1", "pbeef", "p7fffffff"], reapable);
    }

    [Fact(DisplayName = "Age sweep removes a root whose PID is still live, since PID reuse cannot bound growth")]
    public void AgeSweepRemovesStaleRootEvenWhenPidLooksAlive()
    {
        var cutoff = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var ages = new Dictionary<string, DateTime>(StringComparer.Ordinal)
        {
            ["pbeef"] = cutoff.AddHours(-1),
            ["pdead1"] = cutoff.AddMinutes(1),
        };

        var abandoned = AssemblyTempRedirect.SelectRootsAbandonedByAge(
            ["pbeef", "pdead1"],
            currentProcessId: 0x30,
            lastWriteUtc: name => ages[name],
            cutoffUtc: cutoff);

        // pbeef predates the cutoff and goes, whatever its PID now refers to. pdead1 is newer than
        // the cutoff and stays. Liveness is deliberately not consulted here.
        Assert.Equal(["pbeef"], abandoned);
    }

    [Fact(DisplayName = "Age sweep keeps every root when none predates the cutoff")]
    public void AgeSweepKeepsRootsNewerThanCutoff()
    {
        var cutoff = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        var abandoned = AssemblyTempRedirect.SelectRootsAbandonedByAge(
            ["pbeef", "pdead1"],
            currentProcessId: 0x30,
            lastWriteUtc: _ => cutoff.AddMinutes(1),
            cutoffUtc: cutoff);

        Assert.Empty(abandoned);
    }

    [Fact(DisplayName = "Age sweep never removes the current process root however old it looks")]
    public void AgeSweepNeverRemovesCurrentProcessRoot()
    {
        var cutoff = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        var abandoned = AssemblyTempRedirect.SelectRootsAbandonedByAge(
            ["p30"],
            currentProcessId: 0x30,
            lastWriteUtc: _ => cutoff.AddYears(-1),
            cutoffUtc: cutoff);

        Assert.Empty(abandoned);
    }

    [Fact(DisplayName = "Reaper never removes the current process root even if reported dead")]
    public void NeverReapsCurrentProcessRoot()
    {
        var reapable = AssemblyTempRedirect.SelectReapableRoots(
            ["p30"],
            currentProcessId: 0x30,
            isProcessAlive: _ => false);

        Assert.Empty(reapable);
    }

    [Theory(DisplayName = "Reaper ignores directories that are not owned process roots")]
    [InlineData("scratch")]
    [InlineData("")]
    [InlineData("p")]
    [InlineData("pzzz")]
    [InlineData("p0")]
    [InlineData("1a2b")]
    public void IgnoresForeignDirectoryNames(string directoryName)
    {
        var reapable = AssemblyTempRedirect.SelectReapableRoots(
            [directoryName],
            currentProcessId: 0x30,
            isProcessAlive: _ => false);

        Assert.Empty(reapable);
        Assert.False(AssemblyTempRedirect.TryParseProcessTempRootName(directoryName, out _));
    }

    [Fact(DisplayName = "Reaped root name round-trips the process id that built it")]
    public void ReapableNameRoundTripsBuiltRoot()
    {
        var root = AssemblyTempRedirect.BuildProcessTempRoot(Path.Combine("shared", "mcg-tests"), 0x1a2b);

        Assert.True(
            AssemblyTempRedirect.TryParseProcessTempRootName(Path.GetFileName(root), out var processId));
        Assert.Equal(0x1a2b, processId);
    }

    [Fact]
    public void SkipsCandidateThatCannotCreateFiles()
    {
        var fileSystem = new RecordingTempRootFileSystem(failWrites: ["existing-but-denied"]);
        var labeler = new RecordingIntegrityLabeler();

        var selection = Select(
            [Candidate("existing-but-denied"), Candidate("writable-fallback")],
            isWindows: false,
            fileSystem,
            labeler);

        Assert.Equal("writable-fallback", selection.SelectedRoot);
        Assert.Equal(["existing-but-denied", "writable-fallback"], fileSystem.RootAttempts);
        Assert.Equal(TempRootRejectionReason.Write, Assert.Single(Assert.Single(selection.Rejections).Reasons));
    }

    [Fact]
    public void WindowsAppliesAndVerifiesLowLabelOnExactCandidateBeforeSelection()
    {
        var events = new List<string>();
        var fileSystem = new RecordingTempRootFileSystem(events: events);
        var labeler = new RecordingIntegrityLabeler(setResult: true, events: events);

        var selection = Select(
            [Candidate("preferred", requiresLowLabel: true), Candidate("fallback")],
            isWindows: true,
            fileSystem,
            labeler);

        Assert.Equal("preferred", selection.SelectedRoot);
        Assert.Equal(["preferred", "preferred"], labeler.QueryCalls);
        var setCall = Assert.Single(labeler.SetCalls);
        Assert.Equal(("preferred", AssemblyTempRedirect.LowInheritableLevel, false), setCall);
        Assert.Equal(["preferred"], fileSystem.RootAttempts);
        Assert.Equal(
            [
                "create:preferred",
                "label-query:preferred",
                "label-set:preferred",
                "label-query:preferred",
                "create-probe:preferred",
                "write:preferred",
                "delete-file:preferred",
                "delete-directory:preferred"
            ],
            events);
    }

    [Theory]
    [InlineData(false, true, false, "Label")]
    [InlineData(true, false, false, "Label")]
    [InlineData(true, true, true, "Write")]
    public void WindowsPreparationFailureFallsBackWithTypedReason(
        bool setResult,
        bool verifyAfterSet,
        bool failPostLabelWrite,
        string expectedReason)
    {
        var fileSystem = new RecordingTempRootFileSystem(
            failWrites: failPostLabelWrite ? ["preferred"] : []);
        var labeler = new RecordingIntegrityLabeler(setResult, verifyAfterSet);

        var selection = Select(
            [Candidate("preferred", requiresLowLabel: true), Candidate("fallback")],
            isWindows: true,
            fileSystem,
            labeler);

        Assert.Equal("fallback", selection.SelectedRoot);
        Assert.Equal(expectedReason, Assert.Single(Assert.Single(selection.Rejections).Reasons).ToString());
        Assert.Equal("preferred", Assert.Single(labeler.SetCalls).Path);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void WindowsLabelerExceptionFallsBackWithTypedLabelReason(
        bool throwOnQuery,
        bool throwOnSet)
    {
        var fileSystem = new RecordingTempRootFileSystem();
        var labeler = new RecordingIntegrityLabeler(
            throwOnQuery: throwOnQuery,
            throwOnSet: throwOnSet);

        var selection = Select(
            [Candidate("preferred", requiresLowLabel: true), Candidate("fallback")],
            isWindows: true,
            fileSystem,
            labeler);

        Assert.Equal("fallback", selection.SelectedRoot);
        Assert.Equal(TempRootRejectionReason.Label, Assert.Single(Assert.Single(selection.Rejections).Reasons));
        Assert.Equal("preferred", Assert.Single(labeler.QueryCalls));
        Assert.Equal(throwOnSet ? ["preferred"] : [], labeler.SetCalls.Select(call => call.Path));
    }

    [Fact]
    public void WindowsAlreadyLowInheritableCandidateSkipsLabelApplication()
    {
        var fileSystem = new RecordingTempRootFileSystem();
        var labeler = new RecordingIntegrityLabeler(alreadyLow: ["preferred"]);

        var selection = Select(
            [Candidate("preferred", requiresLowLabel: true), Candidate("fallback")],
            isWindows: true,
            fileSystem,
            labeler);

        Assert.Equal("preferred", selection.SelectedRoot);
        Assert.Equal(["preferred"], labeler.QueryCalls);
        Assert.Empty(labeler.SetCalls);
        Assert.Equal(["preferred"], fileSystem.RootAttempts);
    }

    [Fact]
    public void NonWindowsRetainsDeterministicProbeFallbackWithoutLabelTooling()
    {
        var fileSystem = new RecordingTempRootFileSystem(failWrites: ["preferred"]);
        var labeler = new RecordingIntegrityLabeler(setResult: true);

        var selection = Select(
            [Candidate("preferred", requiresLowLabel: true), Candidate("fallback", requiresLowLabel: true)],
            isWindows: false,
            fileSystem,
            labeler);

        Assert.Equal("fallback", selection.SelectedRoot);
        Assert.Equal(["preferred", "fallback"], fileSystem.RootAttempts);
        Assert.Empty(labeler.QueryCalls);
        Assert.Empty(labeler.SetCalls);
    }

    [Fact]
    public void DiagnosticDistinguishesAllBoundedRejectionReasonsAndCleanupPreventsSelection()
    {
        var fileSystem = new RecordingTempRootFileSystem(
            failCreates: ["create-root"],
            failWrites: ["write-root", "write-cleanup-root"],
            failCleanup: ["cleanup-root", "write-cleanup-root"]);
        var labeler = new RecordingIntegrityLabeler(setResult: false);

        var selection = Select(
            [
                Candidate("create-root"),
                Candidate("label-root", requiresLowLabel: true),
                Candidate("write-root"),
                Candidate("cleanup-root"),
                Candidate("write-cleanup-root")
            ],
            isWindows: true,
            fileSystem,
            labeler);

        Assert.Null(selection.SelectedRoot);
        Assert.Equal(
            "assembly-temp-redirect selected=<none> rejected=" +
            "create-root:create|label-root:label|write-root:write|cleanup-root:cleanup|" +
            "write-cleanup-root:write+cleanup",
            AssemblyTempRedirect.FormatDiagnostic(selection));
        Assert.Contains(Path.Combine("cleanup-root", "probe", "probe.tmp"), fileSystem.DeleteFileAttempts);
        Assert.Contains(Path.Combine("cleanup-root", "probe"), fileSystem.DeleteDirectoryAttempts);
    }

    [Fact]
    public void DiagnosticNamesSelectedFallbackAfterPriorRejection()
    {
        var selection = Select(
            [Candidate("rejected-root"), Candidate("selected-root")],
            isWindows: false,
            new RecordingTempRootFileSystem(failWrites: ["rejected-root"]),
            new RecordingIntegrityLabeler());

        Assert.Equal(
            "assembly-temp-redirect selected=selected-root rejected=rejected-root:write",
            AssemblyTempRedirect.FormatDiagnostic(selection));
    }

    private static TempRootSelectionResult Select(
        IEnumerable<TempRootCandidate> candidates,
        bool isWindows,
        RecordingTempRootFileSystem fileSystem,
        RecordingIntegrityLabeler labeler) =>
        AssemblyTempRedirect.SelectWritableRoot(
            candidates,
            candidate => AssemblyTempRedirect.TryPrepareRoot(
                candidate,
                isWindows,
                fileSystem,
                labeler,
                probeDirectoryName: "probe"));

    private static TempRootCandidate Candidate(string path, bool requiresLowLabel = false) =>
        new(path, requiresLowLabel);

    private static MtpProbeProcess StartMtpProbe(
        string executable,
        string root,
        string receiptPath,
        string readyEventName,
        string releaseEventName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        UseHermeticEnvironment(startInfo, root);
        startInfo.Environment[AssemblyTempRedirectChildSmokeTests.ReceiptPathVariable] = receiptPath;
        startInfo.Environment[AssemblyTempRedirectChildSmokeTests.ReadyEventVariable] = readyEventName;
        startInfo.Environment[AssemblyTempRedirectChildSmokeTests.ReleaseEventVariable] = releaseEventName;
        startInfo.ArgumentList.Add("--filter-class");
        startInfo.ArgumentList.Add("*AssemblyTempRedirectChildSmokeTests*");

        var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), $"Failed to start MTP apphost '{executable}'.");
        process.StandardInput.Close();
        return new MtpProbeProcess(
            process,
            receiptPath,
            process.StandardOutput.ReadToEndAsync(),
            process.StandardError.ReadToEndAsync());
    }

    private static void UseHermeticEnvironment(ProcessStartInfo startInfo, string root)
    {
        string[] allowedNames =
        [
            "COMSPEC", "DOTNET_ROOT", "DOTNET_ROOT_X64", "PATH", "PATHEXT", "PROGRAMDATA",
            "PROGRAMFILES", "PROGRAMFILES(X86)", "SYSTEMDRIVE", "SYSTEMROOT", "WINDIR"
        ];
        var allowed = allowedNames
            .Select(name => (
                Name: name,
                Value: startInfo.Environment.TryGetValue(name, out var value) ? value : null))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToArray();

        startInfo.Environment.Clear();
        foreach (var (name, value) in allowed)
        {
            startInfo.Environment[name] = value!;
        }
        startInfo.Environment["HOME"] = root;
        startInfo.Environment["USERPROFILE"] = root;
        startInfo.Environment["APPDATA"] = Path.Combine(root, "appdata");
        startInfo.Environment["LOCALAPPDATA"] = Path.Combine(root, "localappdata");
        startInfo.Environment["TEMP"] = Path.Combine(root, "inherited-temp");
        startInfo.Environment["TMP"] = Path.Combine(root, "inherited-temp");
    }

    private static TempRootProbeReceipt ReadProbeReceipt(string path)
    {
        Assert.True(File.Exists(path), $"MTP temp-root probe did not write receipt '{path}'.");
        return JsonSerializer.Deserialize<TempRootProbeReceipt>(File.ReadAllText(path))
            ?? throw new Xunit.Sdk.XunitException($"MTP temp-root probe receipt '{path}' was empty.");
    }

    private static string FormatProcessFailure(string label, MtpProbeProcessResult result) =>
        $"The {label} MTP apphost exited {result.ExitCode}.{Environment.NewLine}" +
        $"stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}" +
        $"stderr:{Environment.NewLine}{result.Stderr}";

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // The assertion receipt is authoritative; cleanup remains best effort on test failure.
        }
    }

    private sealed class RecordingTempRootFileSystem(
        IEnumerable<string>? failCreates = null,
        IEnumerable<string>? failWrites = null,
        IEnumerable<string>? failCleanup = null,
        ICollection<string>? events = null) : ITempRootFileSystem
    {
        private readonly HashSet<string> directories = [];
        private readonly HashSet<string> files = [];
        private readonly HashSet<string> createFailures = new(failCreates ?? []);
        private readonly HashSet<string> writeFailures = new(failWrites ?? []);
        private readonly HashSet<string> cleanupFailures = new(failCleanup ?? []);

        internal List<string> RootAttempts { get; } = [];

        internal List<string> DeleteFileAttempts { get; } = [];

        internal List<string> DeleteDirectoryAttempts { get; } = [];

        public void CreateDirectory(string path)
        {
            if (string.Equals(Path.GetFileName(path), "probe", StringComparison.Ordinal))
            {
                var root = Path.GetDirectoryName(path)!;
                events?.Add($"create-probe:{root}");
                directories.Add(path);
                if (writeFailures.Contains(root))
                {
                    throw new IOException("simulated probe-directory write denial");
                }

                return;
            }

            events?.Add($"create:{path}");
            RootAttempts.Add(path);
            if (createFailures.Contains(path))
            {
                throw new IOException("simulated candidate creation denial");
            }

            directories.Add(path);
        }

        public void CreateProbeFile(string path)
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;
            events?.Add($"write:{root}");
            files.Add(path);
            if (writeFailures.Contains(root))
            {
                throw new IOException("simulated probe-file write denial");
            }
        }

        public void DeleteFile(string path)
        {
            DeleteFileAttempts.Add(path);
            var root = Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;
            events?.Add($"delete-file:{root}");
            if (cleanupFailures.Contains(root))
            {
                throw new IOException("simulated probe-file cleanup denial");
            }

            files.Remove(path);
        }

        public void DeleteDirectory(string path)
        {
            DeleteDirectoryAttempts.Add(path);
            var root = Path.GetDirectoryName(path)!;
            events?.Add($"delete-directory:{root}");
            if (cleanupFailures.Contains(root) || files.Any(file => Path.GetDirectoryName(file) == path))
            {
                throw new IOException("simulated probe-directory cleanup denial");
            }

            directories.Remove(path);
        }
    }

    private sealed class RecordingIntegrityLabeler(
        bool setResult = true,
        bool verifyAfterSet = true,
        ICollection<string>? events = null,
        IEnumerable<string>? alreadyLow = null,
        bool throwOnQuery = false,
        bool throwOnSet = false) : IWorkerIntegrityLabeler
    {
        private readonly Dictionary<string, IntegrityLabelState> states =
            (alreadyLow ?? []).ToDictionary(
                path => path,
                _ => new IntegrityLabelState(Exists: true, Low: true, Inheritable: true));

        internal List<string> QueryCalls { get; } = [];

        internal List<(string Path, string Level, bool Recursive)> SetCalls { get; } = [];

        public IntegrityLabelState Query(string path)
        {
            events?.Add($"label-query:{path}");
            QueryCalls.Add(path);
            if (throwOnQuery)
            {
                throw new InvalidOperationException("simulated label query failure");
            }

            return states.GetValueOrDefault(
                path,
                new IntegrityLabelState(Exists: true, Low: false, Inheritable: false));
        }

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            events?.Add($"label-set:{path}");
            SetCalls.Add((path, level, recursive));
            if (throwOnSet)
            {
                throw new InvalidOperationException("simulated label application failure");
            }

            if (setResult && verifyAfterSet)
            {
                states[path] = new IntegrityLabelState(Exists: true, Low: true, Inheritable: true);
            }

            return setResult;
        }
    }
}

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class AssemblyTempRedirectChildSmokeTests
{
    internal const string ReceiptPathVariable = "MCG_MTP_TEMP_ROOT_RECEIPT";
    internal const string ReadyEventVariable = "MCG_MTP_TEMP_ROOT_READY_EVENT";
    internal const string ReleaseEventVariable = "MCG_MTP_TEMP_ROOT_RELEASE_EVENT";

    [Fact(Timeout = 45_000)]
    public void ProcessTempRootSupportsAnExclusiveMutableFixtureRepository()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Environment.GetEnvironmentVariable("TMP");
        Assert.False(string.IsNullOrWhiteSpace(tempRoot));
        Assert.Equal(tempRoot, Environment.GetEnvironmentVariable("TEMP"), ignoreCase: true);
        Assert.EndsWith($"p{Environment.ProcessId:x}", tempRoot, StringComparison.OrdinalIgnoreCase);

        var fixtureRoot = Path.Combine(tempRoot, "assembly-temp-redirect-fixture");
        var repositoryPath = Path.Combine(fixtureRoot, "repository");
        var ownerPath = Path.Combine(repositoryPath, "owner.lock");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            using var owner = new FileStream(ownerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var ownerBytes = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
            owner.Write(ownerBytes);
            owner.Flush(flushToDisk: true);
            owner.Position = 0;
            using var reader = new StreamReader(owner, Encoding.UTF8, leaveOpen: true);
            var ownerContents = reader.ReadToEnd();
            Assert.Equal(Environment.ProcessId.ToString(), ownerContents);

            var receiptPath = Environment.GetEnvironmentVariable(ReceiptPathVariable);
            if (string.IsNullOrWhiteSpace(receiptPath))
            {
                return;
            }

            var readyEventName = Environment.GetEnvironmentVariable(ReadyEventVariable);
            var releaseEventName = Environment.GetEnvironmentVariable(ReleaseEventVariable);
            Assert.False(string.IsNullOrWhiteSpace(readyEventName));
            Assert.False(string.IsNullOrWhiteSpace(releaseEventName));
            Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
            File.WriteAllText(
                receiptPath,
                JsonSerializer.Serialize(new TempRootProbeReceipt(
                    Environment.ProcessId,
                    Environment.ProcessPath ?? string.Empty,
                    tempRoot,
                    repositoryPath,
                    ownerContents)));

            using var ready = EventWaitHandle.OpenExisting(readyEventName);
            using var release = EventWaitHandle.OpenExisting(releaseEventName);
            ready.Set();
            Assert.True(
                release.WaitOne(TimeSpan.FromSeconds(30)),
                "Parent MTP temp-root probe did not release the mutable-fixture gate.");
        }
        finally
        {
            try
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
            catch
            {
                // Parent assertions report child output and the retained receipt on failure.
            }
        }
    }
}

internal sealed record TempRootProbeReceipt(
    int ProcessId,
    string ProcessPath,
    string TempRoot,
    string FixtureRepositoryPath,
    string OwnerContents);

internal sealed class MtpProbeProcess(
    Process process,
    string receiptPath,
    Task<string> stdout,
    Task<string> stderr) : IAsyncDisposable
{
    internal Process Process { get; } = process;

    internal string ReceiptPath { get; } = receiptPath;

    internal async Task<MtpProbeProcessResult> WaitForExitAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await Process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
            await Process.WaitForExitAsync(CancellationToken.None);
            throw new Xunit.Sdk.XunitException(
                $"MTP apphost {Process.Id} did not exit after its release event.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{await stdout}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{await stderr}");
        }

        return new MtpProbeProcessResult(Process.ExitCode, await stdout, await stderr);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync(CancellationToken.None);
            }
            _ = await stdout;
            _ = await stderr;
        }
        finally
        {
            Process.Dispose();
        }
    }
}

internal sealed record MtpProbeProcessResult(int ExitCode, string Stdout, string Stderr);
