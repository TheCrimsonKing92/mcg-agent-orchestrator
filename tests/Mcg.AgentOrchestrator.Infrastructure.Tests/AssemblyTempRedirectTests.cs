using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class AssemblyTempRedirectTests
{
    [Fact]
    public void AssemblyFixtureCleanupRejectsFailedDeletionAndRetainsCause()
    {
        var failure = TempRootDeleteOutcome.Failure(
            "owned-root",
            "IOException",
            "owned-root/locked.file",
            readOnlyAttributesCleared: 0);

        var exception = Assert.Throws<InvalidOperationException>(
            () => AssemblyTempRootCleanupFixture.EnsureSuccessful(failure));

        Assert.Contains("IOException", exception.Message, StringComparison.Ordinal);
        Assert.Contains("locked.file", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssemblyFixtureCleanupAcceptsDeletedOrUnownedRoot()
    {
        AssemblyTempRootCleanupFixture.EnsureSuccessful(null);
        AssemblyTempRootCleanupFixture.EnsureSuccessful(TempRootDeleteOutcome.Deleted("owned-root"));
    }

    [Fact]
    public void AssemblyFixtureCleanupRetainsOnlySharedCompilerResidueWithReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"assembly-cleanup-{Guid.NewGuid():N}");
        var analyzerPath = Path.Combine(
            root,
            AssemblyTempRootCleanupFixture.SharedCompilerTempDirectoryName,
            "AnalyzerAssemblyLoader",
            "retained.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(analyzerPath)!);
        File.WriteAllText(analyzerPath, "retained");
        var receipts = new List<string>();

        try
        {
            AssemblyTempRootCleanupFixture.EnsureSuccessful(
                TempRootDeleteOutcome.Failure(root, "IOException", analyzerPath, readOnlyAttributesCleared: 0),
                receipts.Add);

            var receipt = Assert.Single(receipts);
            Assert.Contains("VBCSCompiler", receipt, StringComparison.Ordinal);
            Assert.Contains("shared-vbcscompiler-analyzer-shadow-copies", receipt, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void AssemblyFixtureCleanupRejectsSiblingResidueOutsideSharedCompilerDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"assembly-cleanup-{Guid.NewGuid():N}");
        var compilerPath = Path.Combine(
            root,
            AssemblyTempRootCleanupFixture.SharedCompilerTempDirectoryName,
            "AnalyzerAssemblyLoader",
            "retained.dll");
        var siblingPath = Path.Combine(root, "lane-owned", "retained.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(compilerPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(siblingPath)!);
        File.WriteAllText(compilerPath, "retained");
        File.WriteAllText(siblingPath, "retained");
        var receipts = new List<string>();

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                AssemblyTempRootCleanupFixture.EnsureSuccessful(
                    TempRootDeleteOutcome.Failure(root, "IOException", compilerPath, readOnlyAttributesCleared: 0),
                    receipts.Add));

            Assert.Contains("cleanup failed", exception.Message, StringComparison.Ordinal);
            Assert.Empty(receipts);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void RevalidateExitedRootsRetainsReplacementAndAmbiguousProcessInstances()
    {
        var exitedPid = 0x2a;
        var replacementPid = 0x2b;
        var inaccessiblePid = 0x2c;
        var calls = 0;

        var revalidated = AssemblyTempRedirect.RevalidateExitedRoots(
            [$"p{exitedPid:x}", $"p{replacementPid:x}", $"p{inaccessiblePid:x}"],
            requested =>
            {
                calls++;
                Assert.Equal(
                    [exitedPid, replacementPid, inaccessiblePid],
                    requested!.OrderBy(pid => pid).ToArray());
                return WindowsNativeProcessInspection.ProcessInspectionResult.Success(
                    new Dictionary<int, ProcessInspectionRecord>
                    {
                        [exitedPid] = new(
                            exitedPid, 0, string.Empty, null, null, null, ProcessInspectionStatus.Exited),
                        [replacementPid] = new(
                            replacementPid, 1, "testhost", @"C:\host\testhost.exe",
                            DateTimeOffset.Parse("2026-08-30T12:00:00Z"), "testhost", ProcessInspectionStatus.Available),
                        [inaccessiblePid] = new(
                            inaccessiblePid, 0, string.Empty, null, null, null, ProcessInspectionStatus.AccessDenied)
                    });
            });

        Assert.Equal(1, calls);
        var exited = Assert.Single(revalidated);
        Assert.Equal($"p{exitedPid:x}", exited.Name);
        Assert.Equal(exitedPid, exited.ProcessId);
        Assert.Equal(ProcessInspectionStatus.Exited, exited.Observation.Status);
    }

    [Fact]
    public void ProcessRootDerivation_DistinctPidsProduceDistinctPaths()
    {
        var sharedRoot = Path.Combine("shared", "mcg-tests");

        var first = AssemblyTempRedirect.BuildProcessTempRoot(sharedRoot, 0x1a2b);
        var second = AssemblyTempRedirect.BuildProcessTempRoot(sharedRoot, 0x1a2c);

        Assert.False(string.Equals(first, second, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(sharedRoot, "p1a2b"), first, ignoreCase: true);
        Assert.Equal(Path.Combine(sharedRoot, "p1a2c"), second, ignoreCase: true);
    }

    [Fact]
    public void ManagedMtpProbeLaunchSpecificationRejectsNativeTestApphost()
    {
        var hostFileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var directory = Path.Combine("probe", "output");
        var testAssembly = Path.Combine(directory, "Mcg.AgentOrchestrator.Infrastructure.Tests.dll");
        var nativeApphost = Path.Combine(directory, "Mcg.AgentOrchestrator.Infrastructure.Tests.exe");

        Assert.True(IsManagedMtpProbeLaunch(Path.Combine(directory, hostFileName), testAssembly));
        var exception = Record.Exception(() => BuildMtpProbeStartInfo(
            nativeApphost,
            testAssembly,
            Path.Combine(directory, "temp"),
            Path.Combine(directory, "receipt.json"),
            "ready",
            "release"));

        Assert.NotNull(exception);
        Assert.Contains("Refusing unsafe MTP probe launch", exception.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 60_000)]
    public async Task SingleTestHostUsesDerivedRootAndWritesOwnershipReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"mtp-temp-host-{Guid.NewGuid():N}");
        var executable = ResolveDotnetHostPath();
        var testAssembly = typeof(AssemblyTempRedirectTests).Assembly.Location;
        Directory.CreateDirectory(root);
        Assert.True(File.Exists(testAssembly), $"Missing MTP test assembly '{testAssembly}'.");

        var releaseName = $"Local\\mcg-mtp-temp-release-{Guid.NewGuid():N}";
        var readyName = $"Local\\mcg-mtp-temp-ready-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        MtpProbeProcess? process = null;
        try
        {
            process = StartMtpProbe(
                executable,
                testAssembly,
                root,
                Path.Combine(root, "receipt.json"),
                readyName,
                releaseName);

            var receipt = await WaitForProbeReceiptAsync(
                "single",
                process,
                ready,
                TestContext.Current.CancellationToken);
            Assert.Equal(process.Process.Id, receipt.ProcessId);
            Assert.Equal(executable, receipt.ProcessPath, ignoreCase: true);
            var expectedRoot = AssemblyTempRedirect.BuildProcessTempRoot(
                Path.GetDirectoryName(receipt.TempRoot)!,
                receipt.ProcessId);
            Assert.Equal(expectedRoot, receipt.TempRoot, ignoreCase: true);
            Assert.Equal(
                Path.Combine(receipt.TempRoot, "assembly-temp-redirect-fixture", "repository"),
                receipt.FixtureRepositoryPath,
                ignoreCase: true);
            Assert.Equal(receipt.ProcessId.ToString(), receipt.OwnerContents);

            release.Set();
            var result = await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(result.ExitCode == 0, FormatProcessFailure("single", result));
            Assert.Contains(
                $"assembly-temp-redirect selected={receipt.TempRoot}",
                result.Stderr,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "assembly-temp-cleanup owner=assembly-fixture phase=completed-before-runner-return",
                result.Stderr,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "[FATAL ERROR] Foreground threads were left running",
                result.Stderr,
                StringComparison.Ordinal);
            Assert.False(
                Directory.Exists(receipt.TempRoot),
                $"The completed MTP host retained its owned temp root '{receipt.TempRoot}'.");
        }
        finally
        {
            release.Set();
            if (process is not null)
            {
                await process.DisposeAsync();
            }
            DeleteDirectory(root);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task FailedTestHostStillCompletesAssemblyFixtureCleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"mtp-temp-failed-host-{Guid.NewGuid():N}");
        var executable = ResolveDotnetHostPath();
        var testAssembly = typeof(AssemblyTempRedirectTests).Assembly.Location;
        Directory.CreateDirectory(root);
        var releaseName = $"Local\\mcg-mtp-temp-release-{Guid.NewGuid():N}";
        var readyName = $"Local\\mcg-mtp-temp-ready-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        MtpProbeProcess? process = null;
        try
        {
            process = StartMtpProbe(
                executable,
                testAssembly,
                root,
                Path.Combine(root, "receipt.json"),
                readyName,
                releaseName,
                forceTestFailure: true);
            var receipt = await WaitForProbeReceiptAsync(
                "failed-test",
                process,
                ready,
                TestContext.Current.CancellationToken);

            release.Set();
            var result = await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "intentional temp-root cleanup probe failure",
                result.Stdout + result.Stderr,
                StringComparison.Ordinal);
            Assert.Contains(
                "assembly-temp-cleanup owner=assembly-fixture phase=completed-before-runner-return",
                result.Stderr,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "[FATAL ERROR] Foreground threads were left running",
                result.Stderr,
                StringComparison.Ordinal);
            Assert.False(
                Directory.Exists(receipt.TempRoot),
                $"The failed MTP host retained its owned temp root '{receipt.TempRoot}'.");
        }
        finally
        {
            release.Set();
            if (process is not null)
            {
                await process.DisposeAsync();
            }
            DeleteDirectory(root);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task KilledHostRootIsRemovedThroughSupervisorCleanupSeam()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"mtp-temp-kill-{Guid.NewGuid():N}");
        var executable = ResolveDotnetHostPath();
        var testAssembly = typeof(AssemblyTempRedirectTests).Assembly.Location;
        Directory.CreateDirectory(root);
        Assert.True(File.Exists(testAssembly), $"Missing MTP test assembly '{testAssembly}'.");
        var releaseName = $"Local\\mcg-mtp-temp-release-{Guid.NewGuid():N}";
        var readyName = $"Local\\mcg-mtp-temp-ready-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        MtpProbeProcess? process = null;
        try
        {
            process = StartMtpProbe(
                executable,
                testAssembly,
                root,
                Path.Combine(root, "receipt.json"),
                readyName,
                releaseName);
            var receipt = await WaitForProbeReceiptAsync(
                "killed",
                process,
                ready,
                TestContext.Current.CancellationToken);
            Assert.True(Directory.Exists(receipt.TempRoot));
            var capturedInspection = WindowsNativeProcessInspection.Read([receipt.ProcessId]);
            Assert.Null(capturedInspection.Failure);
            var capturedRoot = new TempRootJanitorOwnedRoot(
                receipt.ProcessId,
                Path.GetDirectoryName(receipt.TempRoot)!,
                Assert.Contains(receipt.ProcessId, capturedInspection.Records),
                "assembly-temp-probe");

            process.Process.Kill(entireProcessTree: true);
            _ = await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(
                Directory.Exists(receipt.TempRoot),
                "The kill unexpectedly ran ProcessExit, so the supervisor-cleanup seam was not exercised.");

            var outcome = Assert.Single(WorkerProcessJobs.ReapOwnedTempRoots(
                [capturedRoot]));

            Assert.Equal(TempRootJanitorReapDisposition.Deleted, outcome.Disposition);
            Assert.False(Directory.Exists(receipt.TempRoot));
        }
        finally
        {
            release.Set();
            if (process is not null)
            {
                await process.DisposeAsync();
            }
            _ = TempRootJanitor.DeleteTree(root);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ConcurrentTestHostsReceiveDistinctTempRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"mtp-temp-hosts-{Guid.NewGuid():N}");
        var executable = ResolveDotnetHostPath();
        var testAssembly = typeof(AssemblyTempRedirectTests).Assembly.Location;
        Directory.CreateDirectory(root);
        var syntheticLocalLow = Path.Combine(root, "LocalLow");
        Assert.False(
            Directory.Exists(syntheticLocalLow),
            $"The synthetic LocalLow precondition was not clean: '{syntheticLocalLow}'.");
        Assert.True(File.Exists(testAssembly), $"Missing MTP test assembly '{testAssembly}'.");

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
                testAssembly,
                root,
                Path.Combine(root, "first-receipt.json"),
                firstReadyName,
                releaseName);
            secondProcess = StartMtpProbe(
                executable,
                testAssembly,
                root,
                Path.Combine(root, "second-receipt.json"),
                secondReadyName,
                releaseName);

            var firstReceiptTask = WaitForProbeReceiptAsync(
                "first",
                firstProcess,
                firstReady,
                TestContext.Current.CancellationToken);
            var secondReceiptTask = WaitForProbeReceiptAsync(
                "second",
                secondProcess,
                secondReady,
                TestContext.Current.CancellationToken);
            var firstReceipt = await firstReceiptTask;
            var secondReceipt = await secondReceiptTask;
            Assert.True(
                Directory.Exists(syntheticLocalLow),
                $"The MTP apphosts did not establish the missing synthetic LocalLow base '{syntheticLocalLow}'.");
            Assert.Equal(firstProcess.Process.Id, firstReceipt.ProcessId);
            Assert.Equal(secondProcess.Process.Id, secondReceipt.ProcessId);
            Assert.False(string.Equals(
                firstReceipt.TempRoot,
                secondReceipt.TempRoot,
                StringComparison.OrdinalIgnoreCase));
            Assert.False(string.Equals(
                firstReceipt.FixtureRepositoryPath,
                secondReceipt.FixtureRepositoryPath,
                StringComparison.OrdinalIgnoreCase));

            await AssertProcessStillRunningAsync("first", firstProcess);
            await AssertProcessStillRunningAsync("second", secondProcess);
            Assert.True(
                Directory.Exists(firstReceipt.TempRoot),
                $"A concurrently starting host removed the first host's live root '{firstReceipt.TempRoot}'.");
            Assert.True(
                Directory.Exists(secondReceipt.TempRoot),
                $"A concurrently starting host removed the second host's live root '{secondReceipt.TempRoot}'.");

            release.Set();
            var results = await Task.WhenAll(
                firstProcess.WaitForExitAsync(TestContext.Current.CancellationToken),
                secondProcess.WaitForExitAsync(TestContext.Current.CancellationToken));
            Assert.True(results[0].ExitCode == 0, FormatProcessFailure("first", results[0]));
            Assert.True(results[1].ExitCode == 0, FormatProcessFailure("second", results[1]));
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

    [Fact(DisplayName = "Bounded sweep does not trade live-root safety for a population bound")]
    public void BoundedSweepPreservesLiveRootsBeyondPopulationBound()
    {
        const int reserve = 4;
        var names = Enumerable.Range(1, reserve * 2).Select(index => $"p{index:x}").ToArray();
        var reapable = AssemblyTempRedirect.SelectReapableRoots(
            names,
            currentProcessId: 0x30,
            isProcessAlive: _ => true);
        var selected = AssemblyTempRedirect.SelectBoundedReapRoots(
            reapable,
            _ => throw new InvalidOperationException("A live root must not reach timestamp ordering."),
            limit: names.Length);

        Assert.Empty(reapable);
        Assert.Empty(selected);
    }

    [Fact(DisplayName = "Bounded sweep never reads or removes roots owned by other live processes")]
    public void BoundedSweepNeverReadsOrRemovesOtherLiveProcessRoots()
    {
        var timestampReads = new List<string>();
        var reapable = AssemblyTempRedirect.SelectReapableRoots(
            ["p30", "p31", "p32"],
            currentProcessId: 0x30,
            isProcessAlive: processId => processId == 0x31);
        var selected = AssemblyTempRedirect.SelectBoundedReapRoots(
            reapable,
            name =>
            {
                timestampReads.Add(name);
                return DateTime.UnixEpoch;
            },
            limit: 32);

        Assert.Equal(["p32"], reapable);
        Assert.Equal(["p32"], selected);
        Assert.Equal(["p32"], timestampReads);
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

    internal static MtpProbeProcess StartMtpProbe(
        string executable,
        string testAssembly,
        string root,
        string receiptPath,
        string readyEventName,
        string releaseEventName,
        string? gateInvocationId = null,
        string? apparatusReceiptPath = null,
        string? apparatusParentPath = null,
        string? apparatusDeletionEventName = null,
        bool forceTestFailure = false)
    {
        var startInfo = BuildMtpProbeStartInfo(
            executable,
            testAssembly,
            root,
            receiptPath,
            readyEventName,
            releaseEventName,
            gateInvocationId,
            apparatusReceiptPath,
            apparatusParentPath,
            apparatusDeletionEventName,
            forceTestFailure);
        var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), $"Failed to start MTP assembly '{testAssembly}' with '{executable}'.");
        process.StandardInput.Close();
        return new MtpProbeProcess(
            process,
            receiptPath,
            process.StandardOutput.ReadToEndAsync(),
            process.StandardError.ReadToEndAsync());
    }

    private static ProcessStartInfo BuildMtpProbeStartInfo(
        string executable,
        string testAssembly,
        string root,
        string receiptPath,
        string readyEventName,
        string releaseEventName,
        string? gateInvocationId = null,
        string? apparatusReceiptPath = null,
        string? apparatusParentPath = null,
        string? apparatusDeletionEventName = null,
        bool forceTestFailure = false)
    {
        Assert.True(
            IsManagedMtpProbeLaunch(executable, testAssembly),
            $"Refusing unsafe MTP probe launch host='{executable}' assembly='{testAssembly}'.");
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
        startInfo.Environment[AssemblyTempRedirectChildSmokeTests.ParentProcessIdVariable] =
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(gateInvocationId) && !string.IsNullOrWhiteSpace(apparatusReceiptPath))
        {
            startInfo.Environment[TempRootApparatusLossReceiptStore.GateInvocationIdVariable] = gateInvocationId;
            startInfo.Environment[TempRootApparatusLossReceiptStore.ReceiptPathVariable] = apparatusReceiptPath;
        }
        if (!string.IsNullOrWhiteSpace(apparatusParentPath) &&
            !string.IsNullOrWhiteSpace(apparatusDeletionEventName))
        {
            startInfo.Environment[AssemblyTempRedirectChildSmokeTests.ApparatusParentPathVariable] = apparatusParentPath;
            startInfo.Environment[AssemblyTempRedirectChildSmokeTests.ApparatusDeletionEventVariable] =
                apparatusDeletionEventName;
        }
        if (forceTestFailure)
        {
            startInfo.Environment[AssemblyTempRedirectChildSmokeTests.ForceFailureVariable] = "1";
        }
        startInfo.ArgumentList.Add(testAssembly);
        startInfo.ArgumentList.Add("--filter-class");
        startInfo.ArgumentList.Add("*AssemblyTempRedirectChildSmokeTests*");
        return startInfo;
    }

    private static bool IsManagedMtpProbeLaunch(string executable, string testAssembly) =>
        string.Equals(
            Path.GetFileName(executable),
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetExtension(testAssembly), ".dll", StringComparison.OrdinalIgnoreCase);

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

    internal static async Task<TempRootProbeReceipt> WaitForProbeReceiptAsync(
        string label,
        MtpProbeProcess process,
        WaitHandle ready,
        CancellationToken cancellationToken)
    {
        using var signalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signalTask = WaitForSignalAsync(ready, signalCancellation.Token);
        var exitTask = process.Process.WaitForExitAsync(CancellationToken.None);
        var completed = await Task.WhenAny(signalTask, exitTask);

        if (completed == exitTask)
        {
            signalCancellation.Cancel();
            try
            {
                await signalTask;
            }
            catch (OperationCanceledException)
            {
                // The process exit is the diagnostic; cancellation just releases the registered wait.
            }

            var result = await process.WaitForExitAsync(CancellationToken.None);
            throw new Xunit.Sdk.XunitException(
                $"The {label} MTP apphost exited before publishing its temp-root receipt." +
                Environment.NewLine +
                FormatProcessFailure(label, result));
        }

        try
        {
            await signalTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException(
                $"The {label} MTP apphost {process.Process.Id} did not publish its temp-root receipt " +
                $"before the test hang detector fired; receipt_exists={File.Exists(process.ReceiptPath)}.");
        }

        return ReadProbeReceipt(process.ReceiptPath);
    }

    internal static async Task WaitForSignalAsync(
        WaitHandle waitHandle,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
            completion);
        var waitRegistration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, _) => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            completion,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);
        try
        {
            await completion.Task;
        }
        finally
        {
            waitRegistration.Unregister(null);
        }
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

    private static async Task AssertProcessStillRunningAsync(string label, MtpProbeProcess process)
    {
        if (!process.Process.HasExited)
        {
            return;
        }

        var result = await process.WaitForExitAsync(CancellationToken.None);
        throw new Xunit.Sdk.XunitException(
            $"The {label} MTP apphost exited before both live roots could be checked; " +
            "root absence cannot be attributed to the concurrent startup sweep." +
            Environment.NewLine +
            FormatProcessFailure(label, result));
    }

    internal static void DeleteDirectory(string path)
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

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class AssemblyTempRedirectChildSmokeTests
{
    internal const string ReceiptPathVariable = "MCG_MTP_TEMP_ROOT_RECEIPT";
    internal const string ReadyEventVariable = "MCG_MTP_TEMP_ROOT_READY_EVENT";
    internal const string ReleaseEventVariable = "MCG_MTP_TEMP_ROOT_RELEASE_EVENT";
    internal const string ParentProcessIdVariable = "MCG_MTP_TEMP_ROOT_PARENT_PROCESS_ID";
    internal const string ApparatusParentPathVariable = "MCG_MTP_APPARATUS_PARENT_PATH";
    internal const string ApparatusDeletionEventVariable = "MCG_MTP_APPARATUS_DELETION_EVENT";
    internal const string ForceFailureVariable = "MCG_MTP_TEMP_ROOT_FORCE_FAILURE";

    [Fact]
    public async Task ProcessTempRootSupportsAnExclusiveMutableFixtureRepository()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var apparatusParentPath = Environment.GetEnvironmentVariable(ApparatusParentPathVariable);
        if (!string.IsNullOrWhiteSpace(apparatusParentPath))
        {
            await RunApparatusVictimAsync(apparatusParentPath);
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
            var parentProcessIdText = Environment.GetEnvironmentVariable(ParentProcessIdVariable);
            Assert.False(string.IsNullOrWhiteSpace(readyEventName));
            Assert.False(string.IsNullOrWhiteSpace(releaseEventName));
            Assert.True(
                int.TryParse(
                    parentProcessIdText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parentProcessId),
                $"Missing or invalid parent process id '{parentProcessIdText}'.");
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
            Process? parentProcess = null;
            try
            {
                parentProcess = Process.GetProcessById(parentProcessId);
                await WaitForReleaseOrParentExitAsync(release, parentProcess);
            }
            catch (ArgumentException)
            {
                // The parent exited before its process handle could be opened; returning lets
                // this apphost run fixture and temp-root cleanup instead of becoming orphaned.
            }
            finally
            {
                parentProcess?.Dispose();
            }
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

        if (string.Equals(
                Environment.GetEnvironmentVariable(ForceFailureVariable),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Fail("intentional temp-root cleanup probe failure");
        }
    }

    private static async Task RunApparatusVictimAsync(string apparatusParentPath)
    {
        var receiptPath = Environment.GetEnvironmentVariable(ReceiptPathVariable);
        var readyEventName = Environment.GetEnvironmentVariable(ReadyEventVariable);
        var deletionEventName = Environment.GetEnvironmentVariable(ApparatusDeletionEventVariable);
        Assert.False(string.IsNullOrWhiteSpace(receiptPath));
        Assert.False(string.IsNullOrWhiteSpace(readyEventName));
        Assert.False(string.IsNullOrWhiteSpace(deletionEventName));

        var ownedRoot = TempRootJanitor.BuildOwnedRootPath(apparatusParentPath, Environment.ProcessId);
        var repositoryPath = Path.Combine(ownedRoot, "seeded-repository");
        var headCommit = SeedRepository(repositoryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        File.WriteAllText(
            receiptPath,
            JsonSerializer.Serialize(new TempRootProbeReceipt(
                Environment.ProcessId,
                Environment.ProcessPath ?? string.Empty,
                ownedRoot,
                repositoryPath,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                headCommit)));

        using var ready = EventWaitHandle.OpenExisting(readyEventName);
        using var deleted = EventWaitHandle.OpenExisting(deletionEventName);
        ready.Set();
        await AssemblyTempRedirectTests.WaitForSignalAsync(deleted, TestContext.Current.CancellationToken);

        Assert.True(
            Directory.Exists(repositoryPath),
            $"Seeded repository sentinel lost: RepositoryMissing path='{repositoryPath}' head='{headCommit}'.");
    }

    private static string SeedRepository(string repositoryPath)
    {
        Directory.CreateDirectory(repositoryPath);
        AssertGitSucceeded(repositoryPath, ["init", "--initial-branch=main"]);
        File.WriteAllText(Path.Combine(repositoryPath, "seed.txt"), "seeded apparatus victim");
        AssertGitSucceeded(repositoryPath, ["add", "seed.txt"]);
        AssertGitSucceeded(
            repositoryPath,
            [
                "-c", "user.name=apparatus-control",
                "-c", "user.email=apparatus-control@example.invalid",
                "commit", "-m", "seed"
            ]);
        var head = InfrastructureTestSupport.RunGitProbe(repositoryPath, ["rev-parse", "HEAD"]);
        Assert.True(head.Succeeded, head.ToString());
        return head.StandardOutput.Trim();
    }

    private static void AssertGitSucceeded(string repositoryPath, IReadOnlyList<string> arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(repositoryPath, arguments);
        Assert.True(result.Succeeded, result.ToString());
    }

    private static async Task WaitForReleaseOrParentExitAsync(WaitHandle release, Process parentProcess)
    {
        using var cancellation = new CancellationTokenSource();
        var releaseTask = AssemblyTempRedirectTests.WaitForSignalAsync(release, cancellation.Token);
        var parentExitTask = parentProcess.WaitForExitAsync(cancellation.Token);
        await Task.WhenAny(releaseTask, parentExitTask);
        cancellation.Cancel();

        try
        {
            await Task.WhenAll(releaseTask, parentExitTask);
        }
        catch (OperationCanceledException)
        {
            // Exactly one signal wins; cancellation releases the losing registered wait.
        }
    }

}

internal sealed record TempRootProbeReceipt(
    int ProcessId,
    string ProcessPath,
    string TempRoot,
    string FixtureRepositoryPath,
    string OwnerContents,
    string? HeadCommit = null);

internal sealed class MtpProbeProcess(
    Process process,
    string receiptPath,
    Task<string> stdout,
    Task<string> stderr) : IAsyncDisposable
{
    internal Process Process { get; } = process;

    internal string ReceiptPath { get; } = receiptPath;

    internal async Task<MtpProbeProcessResult> WaitForExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
