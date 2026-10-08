using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("EnvMutation")]
public sealed class WorkerProcessJobsTests : IDisposable
{
    private const string NegativeWaitAssertionExemption = "test-design-discipline: allow-negative-wait";
    private static readonly Regex NegativeWaitAssertionPattern = new(
        @"\b(?:Xunit\.)?" + "Assert" + @"\s*\.\s*" + "False" +
        @"\s*\(\s*(?:(?!;)[\s\S]){0,500}?\b(?:" + "WaitForExit" +
        @"(?:Async)?|" + "SpinUntil" + "|" + "Wait" + @")\s*\(",
        RegexOptions.CultureInvariant);

    private readonly string? _originalProtectedPid = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_PROTECTED_PID");

    public WorkerProcessJobsTests()
    {
        Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_PROTECTED_PID", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_PROTECTED_PID", _originalProtectedPid);
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_supervisor_cleanup_retains_identityless_owned_root")]
    public void WorkerProcessJobsSupervisorCleanupRetainsIdentitylessOwnedRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sharedRoot = Path.Combine(Path.GetTempPath(), $"worker-temp-reap-{Guid.NewGuid():N}");
        var processId = int.MaxValue;
        var ownedRoot = TempRootJanitor.BuildOwnedRootPath(sharedRoot, processId);
        Directory.CreateDirectory(ownedRoot);
        try
        {
            var result = Assert.Single(WorkerProcessJobs.ReapOwnedTempRoots(
                [processId],
                [sharedRoot]));

            Assert.Equal(TempRootJanitorReapDisposition.RetainedUnknownOwner, result.Disposition);
            Assert.True(Directory.Exists(ownedRoot));
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_supervisor_cleanup_captures_and_reaps_test_tmp_fallback_root")]
    public void WorkerProcessJobsSupervisorCleanupCapturesAndReapsTestTmpFallbackRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appBase = Path.Combine(Path.GetTempPath(), $"worker-fallback-reap-{Guid.NewGuid():N}");
        var processId = int.MaxValue;
        var sharedRoot = Path.Combine(appBase, ".test-tmp");
        var ownedRoot = TempRootJanitor.BuildOwnedRootPath(sharedRoot, processId);
        Directory.CreateDirectory(ownedRoot);
        var capturedOwner = AvailableProcess(processId) with
        {
            ExecutablePath = Path.Combine(appBase, "testhost.exe")
        };
        try
        {
            var captured = Assert.Single(
                TempRootJanitor.SnapshotOwnedRoots(
                    [processId],
                    _ => Inspected(capturedOwner)),
                root => string.Equals(
                    root.SharedRoot,
                    sharedRoot,
                    StringComparison.OrdinalIgnoreCase));

            var result = Assert.Single(TempRootJanitor.ReapOwnedRoots(
                [captured],
                _ => Inspected(ExitedProcess(processId)),
                TempRootJanitor.DeleteTree));

            Assert.Equal(TempRootJanitorReapDisposition.Deleted, result.Disposition);
            Assert.False(Directory.Exists(ownedRoot));
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(appBase);
        }
    }

    [Xunit.Theory(DisplayName = "TempRootJanitor_live_recycled_or_ambiguous_owner_is_retained")]
    [Xunit.InlineData("live")]
    [Xunit.InlineData("recycled")]
    [Xunit.InlineData("inaccessible")]
    public void TempRootJanitorLiveRecycledOrAmbiguousOwnerIsRetained(string observation)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var processId = 4242;
        var sharedRoot = Path.Combine(Path.GetTempPath(), $"janitor-retain-{Guid.NewGuid():N}");
        var ownedRootPath = TempRootJanitor.BuildOwnedRootPath(sharedRoot, processId);
        Directory.CreateDirectory(ownedRootPath);
        var captured = AvailableProcess(processId);
        var observed = observation switch
        {
            "live" => captured,
            "recycled" => captured with { StartedAt = captured.StartedAt!.Value.AddSeconds(1) },
            _ => captured with { Status = ProcessInspectionStatus.AccessDenied }
        };
        var deleteCalls = 0;
        try
        {
            var result = Assert.Single(TempRootJanitor.ReapOwnedRoots(
                [new TempRootJanitorOwnedRoot(processId, sharedRoot, captured, "retention-matrix")],
                _ => Inspected(observed),
                path =>
                {
                    deleteCalls++;
                    return TempRootJanitor.DeleteTree(path);
                }));

            var expected = observation switch
            {
                "live" => TempRootJanitorReapDisposition.RetainedLiveOwner,
                "recycled" => TempRootJanitorReapDisposition.RetainedRecycledPid,
                _ => TempRootJanitorReapDisposition.RetainedUnknownOwner
            };
            Assert.Equal(expected, result.Disposition);
            Assert.Equal(0, deleteCalls);
            Assert.True(Directory.Exists(ownedRootPath));
            Assert.Contains("captured=Available/", TempRootJanitor.FormatDiagnostic(result), StringComparison.Ordinal);
            Assert.Contains($"observed={observed.Status}/", TempRootJanitor.FormatDiagnostic(result), StringComparison.Ordinal);
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }
    }

    [Xunit.Fact(DisplayName = "TempRootJanitor_immediate_revalidation_retains_reused_pid")]
    public void TempRootJanitorImmediateRevalidationRetainsReusedPid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var processId = 4243;
        var sharedRoot = Path.Combine(Path.GetTempPath(), $"janitor-reuse-{Guid.NewGuid():N}");
        var ownedRootPath = TempRootJanitor.BuildOwnedRootPath(sharedRoot, processId);
        Directory.CreateDirectory(ownedRootPath);
        var captured = AvailableProcess(processId);
        var replacement = captured with
        {
            StartedAt = captured.StartedAt!.Value.AddMinutes(1),
            ExecutablePath = @"C:\replacement\testhost.exe"
        };
        var reads = 0;
        try
        {
            var result = Assert.Single(TempRootJanitor.ReapOwnedRoots(
                [new TempRootJanitorOwnedRoot(processId, sharedRoot, captured, "pre-delete-revalidation")],
                _ =>
                {
                    reads++;
                    return Inspected(replacement);
                },
                _ => throw new Xunit.Sdk.XunitException("Delete must not run for a replacement process instance.")));

            Assert.Equal(1, reads);
            Assert.Equal(TempRootJanitorReapDisposition.RetainedRecycledPid, result.Disposition);
            Assert.True(Directory.Exists(ownedRootPath));
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }
    }

    [Xunit.Fact(DisplayName = "TempRootJanitor_dead_matching_owner_deletes_and_continues_after_failure")]
    public void TempRootJanitorDeadMatchingOwnerDeletesAndContinuesAfterFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sharedRoot = Path.Combine(Path.GetTempPath(), $"janitor-dead-{Guid.NewGuid():N}");
        var failedId = 4244;
        var deletedId = 4245;
        Directory.CreateDirectory(TempRootJanitor.BuildOwnedRootPath(sharedRoot, failedId));
        Directory.CreateDirectory(TempRootJanitor.BuildOwnedRootPath(sharedRoot, deletedId));
        var owners = new[]
        {
            new TempRootJanitorOwnedRoot(failedId, sharedRoot, AvailableProcess(failedId), "dead-matrix"),
            new TempRootJanitorOwnedRoot(deletedId, sharedRoot, AvailableProcess(deletedId), "dead-matrix")
        };
        try
        {
            var results = TempRootJanitor.ReapOwnedRoots(
                owners,
                ids => Inspected(ids.Select(ExitedProcess).ToArray()),
                path => path.EndsWith($"p{failedId:x}", StringComparison.OrdinalIgnoreCase)
                    ? new TempRootJanitorDeleteResult(
                        path,
                        TempRootJanitorDeleteStatus.Failed,
                        nameof(IOException),
                        path,
                        0)
                    : TempRootJanitor.DeleteTree(path));

            Assert.Collection(
                results,
                result => Assert.Equal(TempRootJanitorReapDisposition.DeleteFailed, result.Disposition),
                result => Assert.Equal(TempRootJanitorReapDisposition.Deleted, result.Disposition));
            Assert.True(Directory.Exists(TempRootJanitor.BuildOwnedRootPath(sharedRoot, failedId)));
            Assert.False(Directory.Exists(TempRootJanitor.BuildOwnedRootPath(sharedRoot, deletedId)));
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }
    }

    [Xunit.Fact(DisplayName = "TempRootJanitor_dead_owner_reports_absent_and_rejects_escaped_candidate")]
    public void TempRootJanitorDeadOwnerReportsAbsentAndRejectsEscapedCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sharedRoot = Path.Combine(Path.GetTempPath(), $"janitor-path-{Guid.NewGuid():N}");
        var absentId = 4247;
        var escapedId = 4248;
        var escapedPath = Path.GetFullPath(Path.Combine(sharedRoot, "..", $"escaped-{Guid.NewGuid():N}"));
        var deleteCalls = 0;
        try
        {
            var results = TempRootJanitor.ReapOwnedRoots(
                [
                    new TempRootJanitorOwnedRoot(
                        absentId,
                        sharedRoot,
                        AvailableProcess(absentId),
                        "absent-control"),
                    new TempRootJanitorOwnedRoot(
                        escapedId,
                        sharedRoot,
                        AvailableProcess(escapedId),
                        "containment-control",
                        escapedPath)
                ],
                ids => Inspected(ids.Select(ExitedProcess).ToArray()),
                path =>
                {
                    deleteCalls++;
                    return TempRootJanitor.DeleteTree(path);
                });

            Assert.Collection(
                results,
                result => Assert.Equal(TempRootJanitorReapDisposition.AlreadyAbsent, result.Disposition),
                result =>
                {
                    Assert.Equal(TempRootJanitorReapDisposition.RejectedPath, result.Disposition);
                    Assert.Equal(escapedPath, result.Path);
                });
            Assert.Equal(1, deleteCalls);
            Assert.False(Directory.Exists(escapedPath));
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_emits_one_bounded_receipt_per_reap_candidate")]
    public void WorkerProcessJobsEmitsOneBoundedReceiptPerReapCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var owner = AvailableProcess(4249);
        var results = TempRootJanitor.ReapOwnedRoots(
            [
                new TempRootJanitorOwnedRoot(
                    owner.ProcessId,
                    new string('s', 500),
                    owner,
                    new string('c', 100)),
                new TempRootJanitorOwnedRoot(
                    owner.ProcessId + 1,
                    "shared",
                    AvailableProcess(owner.ProcessId + 1),
                    "second")
            ],
            ids => Inspected(ids.Select(id => AvailableProcess(id)).ToArray()),
            _ => throw new Xunit.Sdk.XunitException("Live owners must not reach deletion."));
        var receipts = new List<string>();

        WorkerProcessJobs.EmitTempRootReapResults(results, receipts.Add);

        Assert.Equal(2, receipts.Count);
        Assert.All(receipts, receipt =>
        {
            Assert.StartsWith("temp-root-janitor ", receipt, StringComparison.Ordinal);
            Assert.True(receipt.Length <= 600, $"Receipt was not bounded: {receipt.Length}");
        });
    }

    [Xunit.Fact]
    public void WorkerProcessJobs_PostMortemDeletedOwners_DoNotPersistApparatusLossReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-typed-reap-receipt", Guid.NewGuid().ToString("N"));
        var receiptPath = Path.Combine(root, "receipts.jsonl");
        var startedAt = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var first = AvailableProcess(4249) with { StartedAt = startedAt };
        var second = AvailableProcess(4250) with { StartedAt = startedAt.AddSeconds(1) };
        var results = new[]
        {
            DeletedResult(root, first),
            DeletedResult(root, second)
        };

        using (TempRootApparatusLossReceiptStore.PushScope("attempt-a", receiptPath))
        {
            WorkerProcessJobs.EmitTempRootReapResults(results, _ => { });
        }

        Assert.Empty(TempRootApparatusLossReceiptStore.Read(receiptPath));
    }

    [Xunit.Fact]
    public void TempRootReceiptScope_AppliesAttemptLocalChildContract()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [TempRootApparatusLossReceiptStore.GateInvocationIdVariable] = "ambient-attempt",
            [TempRootApparatusLossReceiptStore.ReceiptPathVariable] = "ambient-path"
        };
        var receiptPath = Path.Combine(Path.GetTempPath(), "attempt-local", "receipt.jsonl");

        using (TempRootApparatusLossReceiptStore.PushScope("attempt-local", receiptPath))
        {
            TempRootApparatusLossReceiptStore.ApplyCurrentScope(environment);
            Assert.Equal("attempt-local", environment[TempRootApparatusLossReceiptStore.GateInvocationIdVariable]);
            Assert.Equal(receiptPath, environment[TempRootApparatusLossReceiptStore.ReceiptPathVariable]);
        }

        TempRootApparatusLossReceiptStore.ApplyCurrentScope(environment);
        Assert.DoesNotContain(TempRootApparatusLossReceiptStore.GateInvocationIdVariable, environment.Keys);
        Assert.DoesNotContain(TempRootApparatusLossReceiptStore.ReceiptPathVariable, environment.Keys);
    }

    private static ProcessInspectionRecord AvailableProcess(int processId) =>
        new(
            processId,
            ParentProcessId: 100,
            Name: "testhost",
            ExecutablePath: @"C:\host\testhost.exe",
            StartedAt: DateTimeOffset.Parse("2026-08-30T12:00:00Z"),
            CommandLine: "testhost controlled-probe",
            ProcessInspectionStatus.Available);

    private static TempRootJanitorReapResult DeletedResult(
        string sharedRoot,
        ProcessInspectionRecord owner)
    {
        var path = TempRootJanitor.BuildOwnedRootPath(sharedRoot, owner.ProcessId);
        return new TempRootJanitorReapResult(
            owner.ProcessId,
            sharedRoot,
            path,
            "test-owned-deletion",
            owner,
            owner with { Status = ProcessInspectionStatus.Exited },
            TempRootJanitorReapDisposition.Deleted,
            new TempRootJanitorDeleteResult(
                path,
                TempRootJanitorDeleteStatus.Deleted,
                null,
                null,
                0));
    }

    private static ProcessInspectionRecord ExitedProcess(int processId) =>
        new(
            processId,
            ParentProcessId: 0,
            Name: string.Empty,
            ExecutablePath: null,
            StartedAt: null,
            CommandLine: null,
            ProcessInspectionStatus.Exited);

    private static WindowsNativeProcessInspection.ProcessInspectionResult Inspected(
        params ProcessInspectionRecord[] records) =>
        WindowsNativeProcessInspection.ProcessInspectionResult.Success(
            records.ToDictionary(record => record.ProcessId));

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_startup_sweep_retains_worker_owned_by_live_process")]
    public void WorkerProcessJobsStartupSweepRetainsWorkerOwnedByLiveProcess()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? owned = null;
        Process? sentinel = null;
        try
        {
            owned = StartLongRunningShell();
            sentinel = StartLongRunningShell();
            Assert.True(SpawnProcessIdentityReader.TryRead(owned, out var identity));

            new SpawnRegistry(dbPath).Register("prior-dispatch", identity);
            WorkerProcessJobs.ConfigureRegistry(dbPath);

            var reaped = WorkerProcessJobs.SweepStartupOrphans();

            Assert.Equal(0, reaped);
            Assert.True(IsRunning(owned.Id));
            Assert.True(IsRunning(sentinel.Id));
            var retained = Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Equal(Environment.ProcessId, retained.OwnerProcessId);
            Assert.NotNull(retained.OwnerProcessStartedAt);
            Assert.False(string.IsNullOrWhiteSpace(retained.OwnerProcessImagePath));
            Assert.Contains("retain-live-owner", retained.LastDiagnostic, StringComparison.Ordinal);
            Assert.Contains($"sweeper_pid={Environment.ProcessId}", retained.LastDiagnostic, StringComparison.Ordinal);

            var firstDiagnostic = retained.LastDiagnostic;
            Assert.Equal(0, WorkerProcessJobs.SweepStartupOrphans());
            Assert.Equal(firstDiagnostic, Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests()).LastDiagnostic);
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (owned is not null)
            {
                try { owned.Kill(entireProcessTree: true); } catch { }
                owned.Dispose();
            }

            if (sentinel is not null)
            {
                try { sentinel.Kill(entireProcessTree: true); } catch { }
                sentinel.Dispose();
            }

            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_startup_sweep_reaps_worker_only_after_owner_is_dead")]
    public void WorkerProcessJobsStartupSweepReapsWorkerOnlyAfterOwnerIsDead()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? worker = null;
        Process? owner = null;
        try
        {
            worker = StartLongRunningShell();
            owner = StartLongRunningShell();
            Assert.True(SpawnProcessIdentityReader.TryRead(worker, out var workerIdentity));
            Assert.True(SpawnProcessIdentityReader.TryRead(owner, out var ownerIdentity));
            owner.Kill(entireProcessTree: true);
            Assert.True(owner.WaitForExit(5000));

            new SpawnRegistry(dbPath).Register("abandoned-dispatch", workerIdentity, ownerIdentity);
            WorkerProcessJobs.ConfigureRegistry(dbPath);

            var reaped = WorkerProcessJobs.SweepStartupOrphans();

            Assert.Equal(1, reaped);
            Assert.True(WaitUntilNotRunning(worker.Id, TimeSpan.FromSeconds(5)));
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            using var connection = StateDbConnectionFactory.Open(dbPath, StateDbConnectionProfile.QueryOnlyRead);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT last_diagnostic FROM spawn_registry WHERE owner_id = 'abandoned-dispatch'";
            var receipt = Assert.IsType<string>(command.ExecuteScalar());
            Assert.Contains("startup-reaped", receipt, StringComparison.Ordinal);
            Assert.Contains("owner_liveness=DeadOrRecycled", receipt, StringComparison.Ordinal);
            Assert.Contains($"sweeper_pid={Environment.ProcessId}", receipt, StringComparison.Ordinal);
            Assert.Contains("sweeper_argv=", receipt, StringComparison.Ordinal);
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (worker is not null)
            {
                try { worker.Kill(entireProcessTree: true); } catch { }
                worker.Dispose();
            }

            owner?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_startup_sweep_retains_worker_with_unknown_legacy_owner")]
    public void WorkerProcessJobsStartupSweepRetainsWorkerWithUnknownLegacyOwner()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? worker = null;
        try
        {
            worker = StartLongRunningShell();
            Assert.True(SpawnProcessIdentityReader.TryRead(worker, out var workerIdentity));
            new SpawnRegistry(dbPath).Register("legacy-dispatch", workerIdentity, ownerIdentity: null);
            WorkerProcessJobs.ConfigureRegistry(dbPath);

            var reaped = WorkerProcessJobs.SweepStartupOrphans();

            Assert.Equal(0, reaped);
            Assert.True(IsRunning(worker.Id));
            var retained = Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Contains("retain-unknown-owner", retained.LastDiagnostic, StringComparison.Ordinal);
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (worker is not null)
            {
                try { worker.Kill(entireProcessTree: true); } catch { }
                worker.Dispose();
            }

            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_startup_sweep_retains_worker_when_owner_image_mismatches")]
    public void WorkerProcessJobsStartupSweepRetainsWorkerWhenOwnerImageMismatches()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? worker = null;
        Process? owner = null;
        try
        {
            worker = StartLongRunningShell();
            owner = StartLongRunningShell();
            Assert.True(SpawnProcessIdentityReader.TryRead(worker, out var workerIdentity));
            Assert.True(SpawnProcessIdentityReader.TryRead(owner, out var ownerIdentity));
            var unverifiedOwnerIdentity = ownerIdentity with { ImagePath = ownerIdentity.ImagePath + ".different" };
            new SpawnRegistry(dbPath).Register("image-mismatch", workerIdentity, unverifiedOwnerIdentity);
            WorkerProcessJobs.ConfigureRegistry(dbPath);

            var reaped = WorkerProcessJobs.SweepStartupOrphans();

            Assert.Equal(0, reaped);
            Assert.True(IsRunning(worker.Id));
            var retained = Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Contains("retain-unknown-owner", retained.LastDiagnostic, StringComparison.Ordinal);
            Assert.Contains("image mismatch", retained.LastDiagnostic, StringComparison.Ordinal);
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (worker is not null)
            {
                try { worker.Kill(entireProcessTree: true); } catch { }
                worker.Dispose();
            }

            if (owner is not null)
            {
                try { owner.Kill(entireProcessTree: true); } catch { }
                owner.Dispose();
            }

            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_startup_sweep_retains_exact_worker_when_victim_image_mismatches")]
    public void WorkerProcessJobsStartupSweepRetainsExactWorkerWhenVictimImageMismatches()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? worker = null;
        Process? owner = null;
        try
        {
            worker = StartLongRunningShell();
            owner = StartLongRunningShell();
            Assert.True(SpawnProcessIdentityReader.TryRead(worker, out var workerIdentity));
            Assert.True(SpawnProcessIdentityReader.TryRead(owner, out var ownerIdentity));
            owner.Kill(entireProcessTree: true);
            Assert.True(owner.WaitForExit(5000));
            var unverifiedWorkerIdentity = workerIdentity with { ImagePath = workerIdentity.ImagePath + ".different" };
            new SpawnRegistry(dbPath).Register("victim-image-mismatch", unverifiedWorkerIdentity, ownerIdentity);
            WorkerProcessJobs.ConfigureRegistry(dbPath);

            var reaped = WorkerProcessJobs.SweepStartupOrphans();

            Assert.Equal(0, reaped);
            Assert.True(IsRunning(worker.Id));
            var retained = Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Contains("retain-unknown-victim", retained.LastDiagnostic, StringComparison.Ordinal);
            Assert.Contains("image mismatch", retained.LastDiagnostic, StringComparison.Ordinal);
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (worker is not null)
            {
                try { worker.Kill(entireProcessTree: true); } catch { }
                worker.Dispose();
            }

            owner?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_concurrent_startup_sweeps_claim_exact_worker_once")]
    public void WorkerProcessJobsConcurrentStartupSweepsClaimExactWorkerOnce()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? worker = null;
        Process? owner = null;
        try
        {
            worker = StartLongRunningShell();
            owner = StartLongRunningShell();
            Assert.True(SpawnProcessIdentityReader.TryRead(worker, out var workerIdentity));
            Assert.True(SpawnProcessIdentityReader.TryRead(owner, out var ownerIdentity));
            owner.Kill(entireProcessTree: true);
            Assert.True(owner.WaitForExit(5000));
            new SpawnRegistry(dbPath).Register("concurrent-sweep", workerIdentity, ownerIdentity);
            WorkerProcessJobs.ConfigureRegistry(dbPath);

            using var ready = new CountdownEvent(2);
            using var start = new ManualResetEventSlim(false);
            int SweepAfterStartGate()
            {
                ready.Signal();
                start.Wait();
                return WorkerProcessJobs.SweepStartupOrphans();
            }

            var sweeps = new[]
            {
                Task.Factory.StartNew(
                    SweepAfterStartGate,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default),
                Task.Factory.StartNew(
                    SweepAfterStartGate,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
            };
            var bothReady = ready.Wait(TimeSpan.FromSeconds(5));
            start.Set();
            Assert.True(bothReady, "Timed out waiting for both startup sweeps to reach the start gate.");
            Task.WaitAll(sweeps);

            Assert.Equal(1, sweeps.Sum(task => task.Result));
            Assert.True(WaitUntilNotRunning(worker.Id, TimeSpan.FromSeconds(5)));
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            using var connection = StateDbConnectionFactory.Open(dbPath, StateDbConnectionProfile.QueryOnlyRead);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT last_diagnostic FROM spawn_registry WHERE owner_id = 'concurrent-sweep'";
            var terminalDiagnostic = Assert.IsType<string>(command.ExecuteScalar());
            Assert.True(
                terminalDiagnostic.Contains("startup-reaped", StringComparison.Ordinal) ||
                terminalDiagnostic.Contains("already-dead-or-recycled", StringComparison.Ordinal),
                $"Unexpected terminal sweep diagnostic: {terminalDiagnostic}");
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (worker is not null)
            {
                try { worker.Kill(entireProcessTree: true); } catch { }
                worker.Dispose();
            }

            owner?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_register_writes_durable_pid_identity")]
    public void WorkerProcessJobsRegisterWritesDurablePidIdentity()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? wrapper = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            wrapper = StartLongRunningShell();

            Assert.True(WorkerProcessJobs.TryRegister(wrapper, "dispatch-1"));
            var entry = Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Equal("dispatch-1", entry.OwnerId);
            Assert.Equal(wrapper.Id, entry.ProcessId);
            Assert.False(string.IsNullOrWhiteSpace(entry.ImagePath));
            Assert.Equal(Environment.ProcessId, entry.OwnerProcessId);
            Assert.NotNull(entry.OwnerProcessStartedAt);
            Assert.False(string.IsNullOrWhiteSpace(entry.OwnerProcessImagePath));

            WorkerProcessJobs.Release(wrapper.Id);
            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(5)));
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (wrapper is not null)
            {
                try { wrapper.Kill(entireProcessTree: true); } catch { }
                wrapper.Dispose();
            }

            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_register_fails_and_rolls_back_when_durable_registry_write_fails")]
    public void WorkerProcessJobsRegisterFailsAndRollsBackWhenDurableRegistryWriteFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(root, "state.db");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(dbPath, []);
        Process? wrapper = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            wrapper = StartLongRunningShell();

            Assert.False(WorkerProcessJobs.TryRegister(
                wrapper,
                "failed-durable-registration",
                out var registrationFailure));
            Assert.Contains("worker-process-registration-failed", registrationFailure, StringComparison.Ordinal);
            Assert.Contains("stage=durable-registry-write", registrationFailure, StringComparison.Ordinal);
            Assert.False(WorkerProcessJobs.HasRegisteredJob(wrapper.Id));
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (wrapper is not null)
            {
                try { wrapper.Kill(entireProcessTree: true); } catch { }
                wrapper.Dispose();
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_duplicate_pid_registration_fails_closed")]
    public void WorkerProcessJobsDuplicatePidRegistrationFailsClosed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WorkerProcessJobs.ClearRegistryForTests();
        Process? wrapper = null;
        int? processId = null;
        try
        {
            wrapper = StartLongRunningShell();
            processId = wrapper.Id;
            Assert.True(WorkerProcessJobs.TryRegister(wrapper, "first-registration"));

            Assert.False(WorkerProcessJobs.TryRegister(
                wrapper,
                "duplicate-registration",
                out var registrationFailure));

            Assert.Contains("worker-process-registration-failed", registrationFailure, StringComparison.Ordinal);
            Assert.Contains("stage=duplicate-or-recycled-pid", registrationFailure, StringComparison.Ordinal);
            Assert.True(WaitUntilNotRunning(processId.Value, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (processId is { } registeredProcessId)
            {
                try { WorkerProcessJobs.Release(registeredProcessId); } catch { }
            }

            WorkerProcessJobs.ClearRegistryForTests();
            wrapper?.Dispose();
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_register_victim_identity_failure_preserves_attached_process_tree")]
    public void WorkerProcessJobsRegisterVictimIdentityFailurePreservesAttachedProcessTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(root, "state.db");
        var marker = Path.Combine(root, "child.pid");
        var startSignal = Path.Combine(root, "start-child");
        Directory.CreateDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? wrapper = null;
        int? childPid = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            wrapper = Process.Start(new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }.WithArguments(
                WorkerShell.BaseArguments().Concat([
                    $"while (-not (Test-Path -LiteralPath '{startSignal}')) {{ Start-Sleep -Milliseconds 25 }}; " +
                    "$p = Start-Process ping.exe -ArgumentList '-n 9999 127.0.0.1' -PassThru -WindowStyle Hidden; " +
                    $"Set-Content -LiteralPath '{marker}' -Value $p.Id; " +
                    "Start-Sleep -Seconds 9999"
                ])))
                ?? throw new InvalidOperationException("Failed to start wrapper process.");

            using var ownerProcess = Process.GetCurrentProcess();
            Assert.True(SpawnProcessIdentityReader.TryRead(ownerProcess, out var ownerIdentity));
            var victimAttempts = 0;
            var retryDelays = new List<int>();
            Assert.True(WorkerProcessJobs.TryRegister(
                wrapper,
                "identity-read-failure",
                _ =>
                {
                    victimAttempts++;
                    File.WriteAllText(startSignal, "go");
                    childPid = WaitForPidFile(marker);
                    return null;
                },
                _ => ownerIdentity,
                retryDelays.Add,
                out var registrationDiagnostic));
            Assert.Equal(10, victimAttempts);
            Assert.Equal(Enumerable.Repeat(25, 9), retryDelays);
            Assert.Contains("stage=victim-identity-read", registrationDiagnostic, StringComparison.Ordinal);
            Assert.Contains("attempts=10", registrationDiagnostic, StringComparison.Ordinal);
            Assert.Contains("outcome=durable-registration-skipped-process-preserved", registrationDiagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("attached-process-tree-termination-requested", registrationDiagnostic, StringComparison.Ordinal);
            Assert.True(WorkerProcessJobs.HasRegisteredJob(wrapper.Id));
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Contains(
                new SqliteRunEventStore(dbPath, ensureSchema: false).ReadSinceAsync().GetAwaiter().GetResult(),
                evt => evt.Operation == "WORKER_PROCESS_REGISTRATION_DEGRADED" &&
                       evt.Status == "degraded" &&
                       evt.Detail == registrationDiagnostic);
            Assert.False(wrapper.HasExited);
            Assert.True(IsRunning(Assert.IsType<int>(childPid)));

            WorkerProcessJobs.Release(wrapper.Id);
            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(5)));
            Assert.True(WaitUntilNotRunning(Assert.IsType<int>(childPid), TimeSpan.FromSeconds(5)));
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (wrapper is not null)
            {
                try { wrapper.Kill(entireProcessTree: true); } catch { }
                wrapper.Dispose();
            }

            if (childPid is { } remainingChildPid)
            {
                try { WorkerProcessJobs.TryKillOrFallback(remainingChildPid); } catch { }
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "SpawnProcessIdentityReader_registration_retry_is_bounded_and_deterministic")]
    public void SpawnProcessIdentityReaderRegistrationRetryIsBoundedAndDeterministic()
    {
        using var process = Process.GetCurrentProcess();
        Assert.True(SpawnProcessIdentityReader.TryRead(process, out var expectedIdentity));

        var transientAttempts = 0;
        var transientDelays = new List<int>();
        var transientResult = SpawnProcessIdentityReader.ReadForRegistration(
            process,
            _ => ++transientAttempts == 3 ? expectedIdentity : null,
            transientDelays.Add,
            maxAttempts: 5,
            delayMilliseconds: 17);

        Assert.True(transientResult.Succeeded);
        Assert.Equal(expectedIdentity, transientResult.Identity);
        Assert.Equal(3, transientResult.Attempts);
        Assert.Equal([17, 17], transientDelays);
        Assert.Equal("status=read attempts=3", transientResult.Evidence);

        var permanentAttempts = 0;
        var permanentDelays = new List<int>();
        var permanentResult = SpawnProcessIdentityReader.ReadForRegistration(
            process,
            _ =>
            {
                permanentAttempts++;
                return null;
            },
            permanentDelays.Add,
            maxAttempts: 4,
            delayMilliseconds: 23);

        Assert.False(permanentResult.Succeeded);
        Assert.Null(permanentResult.Identity);
        Assert.Equal(4, permanentResult.Attempts);
        Assert.Equal(4, permanentAttempts);
        Assert.Equal([23, 23, 23], permanentDelays);
        Assert.Equal("status=unavailable attempts=4 reason=identity-unavailable", permanentResult.Evidence);
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_register_without_durable_registry_skips_identity_reads")]
    public void WorkerProcessJobsRegisterWithoutDurableRegistrySkipsIdentityReads()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WorkerProcessJobs.ClearRegistryForTests();
        Process? wrapper = null;
        try
        {
            wrapper = StartLongRunningShell();

            // A bare Assert.True discarded the registration diagnostic when this failed the gate on
            // 2026-09-04 (first failure on record). Carry the reason so the next one names its stage.
            Func<Process, SpawnProcessIdentity?> forbiddenReader =
                _ => throw new InvalidOperationException("Identity reader must not run without a registry.");
            var registered = WorkerProcessJobs.TryRegister(
                wrapper,
                "in-memory-only",
                forbiddenReader,
                forbiddenReader,
                _ => { },
                out var registrationDiagnostic);
            Assert.True(registered, $"TryRegister failed: {registrationDiagnostic}");
            Assert.True(WorkerProcessJobs.HasRegisteredJob(wrapper.Id));

            WorkerProcessJobs.Release(wrapper.Id);
            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (wrapper is not null)
            {
                try { wrapper.Kill(entireProcessTree: true); } catch { }
                wrapper.Dispose();
            }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_register_retries_owner_identity_and_writes_complete_owner")]
    public void WorkerProcessJobsRegisterRetriesOwnerIdentityAndWritesCompleteOwner()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(root, "state.db");
        Directory.CreateDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? wrapper = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            wrapper = StartLongRunningShell();
            Assert.True(SpawnProcessIdentityReader.TryRead(wrapper, out var victimIdentity));

            var ownerAttempts = 0;
            var ownerDelays = new List<int>();
            Assert.True(WorkerProcessJobs.TryRegister(
                wrapper,
                "owner-retry",
                _ => victimIdentity,
                candidate =>
                {
                    ownerAttempts++;
                    return ownerAttempts >= 2 && SpawnProcessIdentityReader.TryRead(candidate, out var ownerIdentity)
                        ? ownerIdentity
                        : null;
                },
                ownerDelays.Add,
                out var registrationDiagnostic));

            var entry = Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Equal(Environment.ProcessId, entry.OwnerProcessId);
            Assert.NotNull(entry.OwnerProcessStartedAt);
            Assert.False(string.IsNullOrWhiteSpace(entry.OwnerProcessImagePath));
            Assert.Equal(2, ownerAttempts);
            Assert.Equal([25], ownerDelays);
            Assert.Equal(string.Empty, registrationDiagnostic);
            Assert.False(wrapper.HasExited);

            WorkerProcessJobs.Release(wrapper.Id);
            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(5)));
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (wrapper is not null)
            {
                try { wrapper.Kill(entireProcessTree: true); } catch { }
                wrapper.Dispose();
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_register_owner_identity_failure_preserves_attached_process")]
    public void WorkerProcessJobsRegisterOwnerIdentityFailurePreservesAttachedProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(root, "state.db");
        Directory.CreateDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? wrapper = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            wrapper = StartLongRunningShell();
            Assert.True(SpawnProcessIdentityReader.TryRead(wrapper, out var victimIdentity));

            var ownerAttempts = 0;
            var retryDelays = new List<int>();
            Assert.True(WorkerProcessJobs.TryRegister(
                wrapper,
                "owner-identity-failure",
                _ => victimIdentity,
                _ =>
                {
                    ownerAttempts++;
                    return null;
                },
                retryDelays.Add,
                out var registrationDiagnostic));
            Assert.Equal(10, ownerAttempts);
            Assert.Equal(Enumerable.Repeat(25, 9), retryDelays);
            Assert.Contains("stage=owner-identity-read", registrationDiagnostic, StringComparison.Ordinal);
            Assert.Contains("attempts=10", registrationDiagnostic, StringComparison.Ordinal);
            Assert.Contains("outcome=durable-registration-skipped-process-preserved", registrationDiagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("attached-process-tree-termination-requested", registrationDiagnostic, StringComparison.Ordinal);
            Assert.True(WorkerProcessJobs.HasRegisteredJob(wrapper.Id));
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Contains(
                new SqliteRunEventStore(dbPath, ensureSchema: false).ReadSinceAsync().GetAwaiter().GetResult(),
                evt => evt.Operation == "WORKER_PROCESS_REGISTRATION_DEGRADED" &&
                       evt.Status == "degraded" &&
                       evt.Detail == registrationDiagnostic);
            Assert.False(wrapper.HasExited);

            Assert.True(WorkerProcessJobs.TryDetachForGracefulStop(wrapper.Id, out var detachFailure), detachFailure);
            Assert.False(WorkerProcessJobs.HasRegisteredJob(wrapper.Id));
            Assert.True(IsRunning(wrapper.Id));
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (wrapper is not null)
            {
                try { wrapper.Kill(entireProcessTree: true); } catch { }
                wrapper.Dispose();
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_register_identity_exception_preserves_attached_process")]
    public void WorkerProcessJobsRegisterIdentityExceptionPreservesAttachedProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(root, "state.db");
        Directory.CreateDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? wrapper = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            wrapper = StartLongRunningShell();

            var victimAttempts = 0;
            Assert.True(WorkerProcessJobs.TryRegister(
                wrapper,
                "identity-read-exception",
                _ =>
                {
                    victimAttempts++;
                    throw new Win32Exception(5, "Synthetic identity read denial.");
                },
                _ => throw new InvalidOperationException("Owner reader must not run after victim degradation."),
                _ => { },
                out var registrationDiagnostic));

            Assert.Equal(10, victimAttempts);
            Assert.Contains("attempts=10", registrationDiagnostic, StringComparison.Ordinal);
            Assert.Contains("stage=victim-identity-read", registrationDiagnostic, StringComparison.Ordinal);
            Assert.Contains("exception=Win32Exception", registrationDiagnostic, StringComparison.Ordinal);
            Assert.Contains("operation_message=Synthetic identity read denial.", registrationDiagnostic, StringComparison.Ordinal);
            Assert.Contains("outcome=durable-registration-skipped-process-preserved", registrationDiagnostic, StringComparison.Ordinal);
            Assert.True(WorkerProcessJobs.HasRegisteredJob(wrapper.Id));
            Assert.Contains(
                new SqliteRunEventStore(dbPath, ensureSchema: false).ReadSinceAsync().GetAwaiter().GetResult(),
                evt => evt.Operation == "WORKER_PROCESS_REGISTRATION_DEGRADED" &&
                       evt.Status == "degraded" &&
                       evt.Detail == registrationDiagnostic);
            Assert.False(wrapper.HasExited);

            WorkerProcessJobs.Release(wrapper.Id);
            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (wrapper is not null)
            {
                try { wrapper.Kill(entireProcessTree: true); } catch { }
                wrapper.Dispose();
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_protected_parent_allows_registration_but_sweep_retains_descendant")]
    public void WorkerProcessJobsProtectedParentAllowsRegistrationButSweepRetainsDescendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(root, "state.db");
        Directory.CreateDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? registeredChild = null;
        Process? sweptChild = null;
        Process? deadOwner = null;
        try
        {
            Environment.SetEnvironmentVariable(
                "MCG_ORCHESTRATOR_PROTECTED_PID",
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using (var self = Process.GetCurrentProcess())
                Environment.SetEnvironmentVariable(ProtectedProcessIdentity.StartTicksVariable,
                    self.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            WorkerProcessJobs.ConfigureRegistry(dbPath);

            registeredChild = StartLongRunningShell();
            Assert.True(WorkerProcessJobs.TryRegister(registeredChild, "protected-parent-child"));
            using (var protectedProcess = Process.GetCurrentProcess())
            {
                Assert.False(WorkerProcessJobs.TryRegister(protectedProcess, "protected-process"));
            }

            WorkerProcessJobs.Release(registeredChild.Id);
            Assert.True(WaitUntilNotRunning(registeredChild.Id, TimeSpan.FromSeconds(5)));

            sweptChild = StartLongRunningShell();
            deadOwner = StartLongRunningShell();
            Assert.True(SpawnProcessIdentityReader.TryRead(sweptChild, out var sweptIdentity));
            Assert.True(SpawnProcessIdentityReader.TryRead(deadOwner, out var deadOwnerIdentity));
            new SpawnRegistry(dbPath).Register("protected-descendant", sweptIdentity, deadOwnerIdentity);
            deadOwner.Kill(entireProcessTree: true);
            Assert.True(deadOwner.WaitForExit(5000));

            Assert.Equal(0, WorkerProcessJobs.SweepStartupOrphans());
            Assert.False(sweptChild.HasExited);
            var retained = Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Contains("refused-protected", retained.LastDiagnostic, StringComparison.Ordinal);
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_PROTECTED_PID", null);
            Environment.SetEnvironmentVariable(ProtectedProcessIdentity.StartTicksVariable, null);
            foreach (var process in new[] { registeredChild, sweptChild, deadOwner })
            {
                if (process is null)
                {
                    continue;
                }

                try { process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "ProgramStartupLifecycle_non_cleanup_command_configures_registry_and_retains_live_worker")]
    public void ProgramStartupLifecycleNonCleanupCommandConfiguresRegistryAndRetainsLiveWorker()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(root, "state.db");
        Directory.CreateDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? newlyRegisteredWorker = null;
        var registeredWorkerId = 0;
        try
        {
            ProgramStartupLifecycle.InitializeWorkerProcessTracking(
                runsStartupCleanup: false,
                authorityTransferRequested: false,
                dbPath,
                root);

            newlyRegisteredWorker = StartLongRunningShell();
            Assert.True(WorkerProcessJobs.TryRegister(newlyRegisteredWorker, "new-dispatch"));
            registeredWorkerId = newlyRegisteredWorker.Id;
            var registered = Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Equal("new-dispatch", registered.OwnerId);
            Assert.Equal(Environment.ProcessId, registered.OwnerProcessId);
        }
        finally
        {
            try
            {
                if (newlyRegisteredWorker is not null)
                {
                    try
                    {
                        if (registeredWorkerId != 0) WorkerProcessJobs.Release(registeredWorkerId);
                    }
                    finally
                    {
                        try { newlyRegisteredWorker.Kill(entireProcessTree: true); } catch { }
                        newlyRegisteredWorker.Dispose();
                    }
                }
            }
            finally
            {
                WorkerProcessJobs.ClearRegistryForTests();
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        Assert.False(WorkerProcessJobs.HasRegisteredJob(registeredWorkerId));
    }

    [Xunit.Fact(DisplayName = "ProgramStartupLifecycle_handoff_configures_registry_without_sweeping_incumbent_processes")]
    public void ProgramStartupLifecycleHandoffConfiguresRegistryWithoutSweepingIncumbentProcesses()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(root, "state.db");
        Directory.CreateDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? incumbentWorker = null;
        Process? successorWorker = null;
        var successorWorkerId = 0;
        try
        {
            incumbentWorker = StartLongRunningShell();
            Assert.True(SpawnProcessIdentityReader.TryRead(incumbentWorker, out var incumbentIdentity));
            new SpawnRegistry(dbPath).Register("incumbent-dispatch", incumbentIdentity);

            ProgramStartupLifecycle.InitializeWorkerProcessTracking(
                runsStartupCleanup: true,
                authorityTransferRequested: true,
                dbPath,
                root);

            Assert.True(IsRunning(incumbentWorker.Id));
            successorWorker = StartLongRunningShell();
            Assert.True(WorkerProcessJobs.TryRegister(successorWorker, "successor-dispatch"));
            successorWorkerId = successorWorker.Id;
            var activeEntries = WorkerProcessJobs.ListActiveRegistryEntriesForTests();
            Assert.Contains(activeEntries, entry => entry.OwnerId == "incumbent-dispatch");
            Assert.Contains(activeEntries, entry => entry.OwnerId == "successor-dispatch");
        }
        finally
        {
            try
            {
                if (successorWorker is not null)
                {
                    try
                    {
                        if (successorWorkerId != 0) WorkerProcessJobs.Release(successorWorkerId);
                    }
                    finally
                    {
                        try { successorWorker.Kill(entireProcessTree: true); } catch { }
                        successorWorker.Dispose();
                    }
                }
            }
            finally
            {
                if (incumbentWorker is not null)
                {
                    try { incumbentWorker.Kill(entireProcessTree: true); } catch { }
                    incumbentWorker.Dispose();
                }

                WorkerProcessJobs.ClearRegistryForTests();
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        Assert.False(WorkerProcessJobs.HasRegisteredJob(successorWorkerId));
    }

    [Xunit.Fact(DisplayName = "ProgramStartupLifecycle_authority_transfer_signal_suppresses_cleanup_for_every_command")]
    public void ProgramStartupLifecycleAuthorityTransferSignalSuppressesCleanupForEveryCommand()
    {
        const string variable = "MCG_ORCHESTRATOR_HANDOFF_READY_PATH";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "successor.ready");

            Assert.True(ProgramStartupLifecycle.IsAuthorityTransferRequested(["conduct", "--loop"]));
            Assert.True(ProgramStartupLifecycle.IsAuthorityTransferRequested(["status"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_registers_and_releases_wrapper_job")]
    public void WorkerProcessJobsRegistersAndReleasesWrapperJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Process? wrapper = null;
        try
        {
            wrapper = Process.Start(new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }.WithArguments(
                WorkerShell.BaseArguments().Concat([
                    "Start-Sleep -Seconds 9999"
                ])))
                ?? throw new InvalidOperationException("Failed to start wrapper process.");

            Assert.True(WorkerProcessJobs.TryRegister(wrapper));
            Assert.True(WorkerProcessJobs.HasRegisteredJob(wrapper.Id));
            WorkerProcessJobs.Release(wrapper.Id);

            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            if (wrapper is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(wrapper.Id); } catch { }
                wrapper.Dispose();
            }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_registration_failure_preserves_original_native_receipt_before_cleanup")]
    public void WorkerProcessJobsRegistrationFailurePreservesOriginalNativeReceiptBeforeCleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Process? candidate = null;
        try
        {
            candidate = StartLongRunningShell();
            Assert.False(WorkerProcessJobs.TryRegisterWithAttachmentForTests(
                candidate,
                "native-receipt",
                _ => throw new OwnedProcessAttachmentException(
                    5,
                    "Synthetic assignment operation.\r\n",
                    "candidate_has_exited=false; owner_in_job=true; candidate_in_job=true; owner_job_limit_flags=0x00000000"),
                out var failure));

            Assert.Contains("stage=owned-process-group-attachment", failure, StringComparison.Ordinal);
            Assert.Contains("cleanup=process-tree-termination-requested", failure, StringComparison.Ordinal);
            Assert.Contains("exception=OwnedProcessAttachmentException", failure, StringComparison.Ordinal);
            Assert.Contains("native_error_code=5", failure, StringComparison.Ordinal);
            Assert.Contains("native_message=", failure, StringComparison.Ordinal);
            Assert.Contains("operation_message=Synthetic assignment operation.", failure, StringComparison.Ordinal);
            Assert.Contains("candidate_has_exited=false", failure, StringComparison.Ordinal);
            Assert.Contains("owner_in_job=true", failure, StringComparison.Ordinal);
            Assert.Contains("candidate_in_job=true", failure, StringComparison.Ordinal);
            Assert.Contains("owner_job_limit_flags=0x00000000", failure, StringComparison.Ordinal);
            Assert.True(WaitUntilNotRunning(candidate.Id, TimeSpan.FromSeconds(5)));
            Assert.True(candidate.HasExited);
            Assert.False(WorkerProcessJobs.HasRegisteredJob(candidate.Id));
        }
        finally
        {
            if (candidate is not null)
            {
                try { candidate.Kill(entireProcessTree: true); } catch { }
                candidate.Dispose();
            }
        }
    }

    [Xunit.Fact(DisplayName = "OwnedProcessGroup_diagnostic_probe_failures_preserve_later_available_evidence")]
    public void OwnedProcessGroupDiagnosticProbeFailuresPreserveLaterAvailableEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var exited = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start diagnostic probe process.");
        Assert.True(exited.WaitForExit(5000));
        exited.Dispose();

        var evidence = OwnedProcessGroup.CaptureAssignmentFailureEvidenceForTests(exited);

        Assert.Contains("candidate_has_exited=unknown", evidence, StringComparison.Ordinal);
        Assert.Contains("candidate_in_job=unknown", evidence, StringComparison.Ordinal);
        Assert.Contains("owner_job_limit_flags=", evidence, StringComparison.Ordinal);
        Assert.Contains("owner_job_ui_restrictions=", evidence, StringComparison.Ordinal);
        Assert.Contains("owned_job_limit_flags=", evidence, StringComparison.Ordinal);
        Assert.Contains("owned_job_ui_restrictions=", evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_atomic_launch_failure_preserves_native_and_job_receipt")]
    public void WorkerProcessJobsAtomicLaunchFailurePreservesNativeAndJobReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkerProcessJobs.StartRegisteredOrThrow(
                new ProcessStartInfo
                {
                    FileName = $"missing-{Guid.NewGuid():N}.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                "atomic-launch-failure"));

        Assert.Contains("stage=owned-process-group-launch", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exception=OwnedProcessLaunchException", exception.Message, StringComparison.Ordinal);
        Assert.Contains("native_error_code=", exception.Message, StringComparison.Ordinal);
        Assert.Contains("native_message=", exception.Message, StringComparison.Ordinal);
        Assert.Contains("operation_message=Failed to start suspended process", exception.Message, StringComparison.Ordinal);
        Assert.Contains("owner_in_job=", exception.Message, StringComparison.Ordinal);
        Assert.Contains("owner_job_limit_flags=", exception.Message, StringComparison.Ordinal);
        Assert.Contains("owner_job_ui_restrictions=", exception.Message, StringComparison.Ordinal);
        Assert.Contains("owned_job_limit_flags=", exception.Message, StringComparison.Ordinal);
        Assert.Contains("owned_job_ui_restrictions=", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_non_windows_registration_failure_disposes_started_process")]
    public void WorkerProcessJobsNonWindowsRegistrationFailureDisposesStartedProcess()
    {
        var candidate = StartLongRunningShell();
        var processId = candidate.Id;
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                WorkerProcessJobs.StartAndRegisterNonWindows(
                    new ProcessStartInfo { CreateNoWindow = true },
                    "non-windows-disposal",
                    _ => candidate,
                    (_, _) => throw new InvalidOperationException("synthetic registration failure")));

            Assert.Equal("synthetic registration failure", exception.Message);
            var disposed = Assert.Throws<InvalidOperationException>(() => _ = candidate.Handle);
            Assert.Contains("No process is associated", disposed.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                using var cleanup = Process.GetProcessById(processId);
                cleanup.Kill(entireProcessTree: true);
                cleanup.WaitForExit(5000);
            }
            catch (ArgumentException)
            {
            }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_resume_rollback_preserves_resume_failure_and_releases_durable_registration")]
    public void WorkerProcessJobsResumeRollbackPreservesResumeFailureAndReleasesDurableRegistration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        WorkerProcessJobs.ConfigureRegistry(dbPath);
        try
        {
            using var launch = OwnedProcessGroup.StartSuspended(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c ping.exe -n 9999 127.0.0.1 > nul",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            var processId = launch.Process.Id;
            var releaseAttempts = 0;
            var syntheticStartedAt = DateTimeOffset.UtcNow;

            Assert.False(WorkerProcessJobs.TryRegisterSuspendedForTests(
                launch.Process,
                "resume-rollback",
                launch.Group,
                _ => new SpawnProcessIdentity(processId, syntheticStartedAt, "synthetic-candidate.exe"),
                owner => new SpawnProcessIdentity(owner.Id, syntheticStartedAt, "synthetic-owner.exe"),
                () => throw new Win32Exception(31, "Synthetic resume failure."),
                (_, _, _) =>
                {
                    releaseAttempts++;
                    throw new IOException("Synthetic first release failure.");
                },
                out var failure));

            Assert.Equal(1, releaseAttempts);
            Assert.Contains("stage=process-resume", failure, StringComparison.Ordinal);
            Assert.Contains("exception=Win32Exception", failure, StringComparison.Ordinal);
            Assert.Contains("native_error_code=31", failure, StringComparison.Ordinal);
            Assert.Contains("operation_message=Synthetic resume failure.", failure, StringComparison.Ordinal);
            Assert.DoesNotContain("Synthetic first release failure", failure, StringComparison.Ordinal);
            Assert.False(WorkerProcessJobs.HasRegisteredJob(processId));
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.True(WaitUntilNotRunning(processId, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_atomic_owned_start_closes_fast_exit_registration_window")]
    public void WorkerProcessJobsAtomicOwnedStartClosesFastExitRegistrationWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Process? oldFast = null;
        Process? oldLong = null;
        try
        {
            oldFast = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c exit 0",
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Failed to start fast negative-control process.");
            Assert.True(oldFast.WaitForExit(5000));
            Assert.False(WorkerProcessJobs.TryRegister(oldFast, "old-fast", out var fastFailure));
            Assert.Contains("stage=owned-process-group-attachment", fastFailure, StringComparison.Ordinal);

            oldLong = StartLongRunningShell();
            Assert.True(WorkerProcessJobs.TryRegister(oldLong, "old-long", out var longFailure), longFailure);
            WorkerProcessJobs.Release(oldLong.Id);

            for (var iteration = 0; iteration < 12; iteration++)
            {
                using var atomicFast = WorkerProcessJobs.StartRegisteredOrThrow(
                    new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/d /c exit 0",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    },
                    $"atomic-fast:{iteration}");
                var processId = atomicFast.Id;
                Assert.True(WorkerProcessJobs.HasRegisteredJob(processId));
                Assert.True(atomicFast.WaitForExit(5000));
                WorkerProcessJobs.Release(processId);
                Assert.False(WorkerProcessJobs.HasRegisteredJob(processId));
            }
        }
        finally
        {
            if (oldLong is not null)
            {
                try { WorkerProcessJobs.Release(oldLong.Id); } catch { }
                oldLong.Dispose();
            }

            oldFast?.Dispose();
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "recycled-pid", "start-time,image")]
    [Xunit.InlineData(false, "duplicate-registration", "none")]
    public void DuplicateRegistrationProjectsIdentityDecision(
        bool identityMismatch,
        string expectedConflict,
        string expectedMismatch)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "mcg-worker-job-tests",
            Guid.NewGuid().ToString("n"),
            "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        WorkerProcessJobs.ConfigureRegistry(dbPath);
        Process? candidate = null;
        try
        {
            candidate = StartLongRunningShell();
            var observedStartedAt = new DateTimeOffset(
                candidate.StartTime.ToUniversalTime(),
                TimeSpan.Zero);
            var observedImage = candidate.MainModule?.FileName
                ?? throw new InvalidOperationException("Candidate image was unavailable before registration.");
            var recordedStartedAt = identityMismatch ? observedStartedAt.AddMinutes(-1) : observedStartedAt;
            var recordedImage = identityMismatch
                ? Path.Combine("recorded", "synthetic-candidate.exe")
                : observedImage;

            Assert.True(WorkerProcessJobs.TryRegister(
                candidate,
                "diagnostic-incumbent",
                process => new SpawnProcessIdentity(process.Id, recordedStartedAt, recordedImage),
                owner => new SpawnProcessIdentity(owner.Id, recordedStartedAt, "synthetic-owner.exe"),
                _ => { },
                out var firstDiagnostic), firstDiagnostic);

            var operationStarted = Stopwatch.GetTimestamp();
            var registered = WorkerProcessJobs.TryRegister(candidate, "diagnostic-candidate", out var failure);
            var operationElapsed = Stopwatch.GetElapsedTime(operationStarted);
            Assert.False(registered);

            Assert.Contains($"conflict={expectedConflict}", failure, StringComparison.Ordinal);
            Assert.Contains($"recorded_started_at={recordedStartedAt:O}", failure, StringComparison.Ordinal);
            Assert.Contains($"recorded_image={Path.GetFileName(recordedImage)}", failure, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"observed_started_at={observedStartedAt:O}", failure, StringComparison.Ordinal);
            Assert.Contains($"observed_image={Path.GetFileName(observedImage)}", failure, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"mismatch={expectedMismatch}", failure, StringComparison.Ordinal);
            Assert.DoesNotContain(recordedImage, failure, StringComparison.OrdinalIgnoreCase);
            Assert.True(WorkerProcessJobs.HasActiveJobForTests(candidate.Id));
            Assert.True(WaitUntilNotRunning(candidate.Id, TimeSpan.FromSeconds(5)));
            Console.WriteLine(
                $"process-lifecycle-receipt seam=registration variant={(identityMismatch ? "conflicting-identity" : "matching-identity")} " +
                $"candidate_pid={candidate.Id} candidate_started_at={observedStartedAt:O} " +
                $"recorded_started_at={recordedStartedAt:O} operation=TryRegister " +
                $"elapsed_ms={operationElapsed.TotalMilliseconds:F3} result={failure}");
        }
        finally
        {
            if (candidate is not null)
            {
                try { WorkerProcessJobs.Release(candidate.Id); } catch { }
                try { if (!candidate.HasExited) candidate.Kill(entireProcessTree: true); } catch { }
                candidate.Dispose();
            }

            WorkerProcessJobs.ClearRegistryForTests();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public void RegistrationWithoutConflictProducesHarnessReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Process? candidate = null;
        try
        {
            candidate = StartLongRunningShell();
            var candidateStartedAt = new DateTimeOffset(
                candidate.StartTime.ToUniversalTime(),
                TimeSpan.Zero);
            var candidateImage = Path.GetFileName(candidate.MainModule?.FileName);
            var operationStarted = Stopwatch.GetTimestamp();

            var registered = WorkerProcessJobs.TryRegister(
                candidate,
                "diagnostic-isolated",
                out var failure);
            var operationElapsed = Stopwatch.GetElapsedTime(operationStarted);

            Assert.True(registered, failure);
            Assert.True(WorkerProcessJobs.HasActiveJobForTests(candidate.Id));
            Console.WriteLine(
                $"process-lifecycle-receipt seam=registration variant=isolated " +
                $"candidate_pid={candidate.Id} candidate_started_at={candidateStartedAt:O} " +
                $"candidate_image={candidateImage} operation=TryRegister " +
                $"elapsed_ms={operationElapsed.TotalMilliseconds:F3} result=registered");
        }
        finally
        {
            if (candidate is not null)
            {
                try { WorkerProcessJobs.Release(candidate.Id); } catch { }
                try { if (!candidate.HasExited) candidate.Kill(entireProcessTree: true); } catch { }
                candidate.Dispose();
            }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_owned_child_uses_retained_handle_for_fast_exit_and_metadata")]
    public async Task WorkerProcessJobsOwnedChildUsesRetainedHandleForFastExitAndMetadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /c exit 23",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        using var child = WorkerProcessJobs.StartRegisteredOwnedOrThrow(
            startInfo,
            "owned-child-fast-exit");
        var processId = child.Id;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await child.WaitForExitAsync(timeout.Token);

            Assert.Equal(23, child.ExitCode);
            Assert.Equal("cmd.exe", child.StartMetadata.FileName);
            Assert.Equal("/d /c exit 23", child.StartMetadata.Arguments);
            Assert.Equal(Environment.CurrentDirectory, child.StartMetadata.WorkingDirectory);
            Assert.True(child.OwnsRegisteredJob);
            Assert.True(WorkerProcessJobs.HasRegisteredJob(processId));

            WorkerProcessJobs.Release(processId);

            Assert.True(child.OwnsRegisteredJob);
            Assert.True(WorkerProcessJobs.HasRegisteredJob(processId));
            Assert.True(child.HasOpenNativeHandle);
            Assert.True(child.Release(out _));
            Assert.False(child.Release(out _));
            Assert.True(child.HasOpenNativeHandle);
        }
        finally
        {
            child.Dispose();
        }

        Assert.False(WorkerProcessJobs.HasRegisteredJob(processId));
        Assert.False(child.HasOpenNativeHandle);
        Assert.True(child.IsDisposed);
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_suspended_child_registers_native_identity_before_readiness")]
    public async Task WorkerProcessJobsSuspendedChildRegistersNativeIdentityBeforeReadiness()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(root, "state.db");
        var readyPath = Path.Combine(root, "child-ready.pid");
        Directory.CreateDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        WorkerProcessJobs.ConfigureRegistry(dbPath);
        RegisteredOwnedProcess? child = null;
        try
        {
            var startInfo = CreateIsolatedDotnetProbeStartInfo(readyPath);
            var negativeControl = OwnedProcessGroup.StartSuspendedContained(startInfo);
            var negativeControlProcessId = negativeControl.Process.Id;
            using (negativeControl)
            {
                var mainModuleFailure = Assert.Throws<Win32Exception>(
                    () => _ = negativeControl.Process.MainModule?.FileName);
                Assert.Equal(299, mainModuleFailure.NativeErrorCode);

                var nativeIdentity = negativeControl.ReadLifecycleIdentity(negativeControl.Process.Id);
                Assert.Equal(ProcessInspectionStatus.Available, nativeIdentity.Status);
                Assert.NotNull(nativeIdentity.StartedAt);
                Assert.False(string.IsNullOrWhiteSpace(nativeIdentity.ExecutablePath));
                Assert.False(File.Exists(readyPath));
                negativeControl.Group.Kill();
                Assert.True(negativeControl.WaitForOwnedExit(TestHangGuard.Bound),
                    "Suspended negative-control child did not exit after kill.");
            }
            var closedHandleIdentity = negativeControl.ReadLifecycleIdentity(negativeControlProcessId);
            Assert.Equal(ProcessInspectionStatus.NativeFailure, closedHandleIdentity.Status);
            Assert.Equal("process-handle-validation", closedHandleIdentity.Operation);

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(root, Path.GetFileName(readyPath));
            watcher.Created += (_, _) => ready.TrySetResult();
            watcher.Renamed += (_, _) => ready.TrySetResult();
            watcher.Error += (_, _) => { if (File.Exists(readyPath)) ready.TrySetResult(); };
            watcher.EnableRaisingEvents = true;
            var observedRegistry = false;
            var observedNoReadiness = false;
            child = WorkerProcessJobs.StartRegisteredOwnedOrThrow(
                CreateIsolatedDotnetProbeStartInfo(readyPath),
                "suspended-native-identity",
                resumeObserver: processId =>
                {
                    observedRegistry = WorkerProcessJobs.ListActiveRegistryEntriesForTests()
                        .Any(candidate => candidate.ProcessId == processId);
                    observedNoReadiness = !File.Exists(readyPath);
                });
            Assert.True(observedRegistry, "Durable registry must list the suspended child before resume.");
            Assert.True(observedNoReadiness, "Readiness sentinel must be absent before resume.");
            var entry = Assert.Single(
                WorkerProcessJobs.ListActiveRegistryEntriesForTests(),
                candidate => candidate.ProcessId == child.Id);

            if (File.Exists(readyPath)) ready.TrySetResult();
            await TestHangGuard.WaitAsync(ready.Task, "suspended child readiness sentinel");

            Assert.True(File.Exists(readyPath), "Suspended child did not publish its readiness sentinel after resume.");
            var identity = Assert.IsType<SpawnProcessIdentity>(child.Identity);
            Assert.Equal(1, child.IdentityReadAttempts);
            Assert.Equal(child.Id, identity.ProcessId);
            Assert.Equal(entry.ProcessStartedAt, identity.StartedAt);
            Assert.Equal(entry.ImagePath, identity.ImagePath, ignoreCase: true);
            Assert.Equal(
                SpawnTrackedProcessStatus.LiveMatch,
                SpawnProcessIdentityReader.EvaluateTrackedProcess(entry, out var observedProcess, out _));
            Assert.IsType<Process>(observedProcess).Dispose();
            Console.WriteLine(
                $"suspended-registration-receipt pid={child.Id} identity_attempts={child.IdentityReadAttempts} " +
                $"registered_at={entry.RegisteredAt:O} image={Path.GetFileName(entry.ImagePath)}");
        }
        finally
        {
            if (child is not null)
            {
                try { child.Kill(entireProcessTree: true); } catch { }
                try
                {
                    await TestHangGuard.WaitAsync(child.WaitForExitAsync(CancellationToken.None),
                        "suspended child cleanup exit");
                }
                finally
                {
                    try { _ = child.Release(out _); } catch { }
                    child.Dispose();
                }
            }

            WorkerProcessJobs.ClearRegistryForTests();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_atomic_redirected_owned_start_closes_fast_exit_registration_window")]
    public async Task WorkerProcessJobsAtomicRedirectedOwnedStartClosesFastExitRegistrationWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string marker = "atomic-redirected-fast-exit";
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("echo");
        startInfo.ArgumentList.Add(marker);
        using var atomicFast = WorkerProcessJobs.StartRegisteredOwnedRedirectedOrThrow(
            startInfo,
            "atomic-redirected-fast");
        atomicFast.CompleteInput();

        Assert.True(WorkerProcessJobs.HasRegisteredJob(atomicFast.Id));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await atomicFast.WaitForExitAsync(timeout.Token);
        Assert.Contains(marker, await atomicFast.StandardOutput.ReadToEndAsync(timeout.Token), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void WorkerProcessJobsPidAssociatedStartInfoThrowsPredictedDiagnostic()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Process? child = null;
        var processId = 0;
        try
        {
            child = WorkerProcessJobs.StartRegisteredOrThrow(
                new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/d /c exit 0",
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                "pid-associated-start-info-red");
            processId = child.Id;

            var exception = Assert.Throws<InvalidOperationException>(() => _ = child.StartInfo);

            Assert.Equal(
                "Process was not started by this object, so requested information cannot be determined.",
                exception.Message);
            Assert.Contains("System.Diagnostics.Process.get_StartInfo", exception.StackTrace, StringComparison.Ordinal);
        }
        finally
        {
            if (processId != 0)
            {
                WorkerProcessJobs.Release(processId);
            }

            child?.Dispose();
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_release_waits_for_gate_owned_child_process_tree")]
    public void WorkerProcessJobsReleaseWaitsForGateOwnedChildProcessTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests");
        var marker = Path.Combine(directory, Guid.NewGuid().ToString("n") + ".pid");
        var startSignal = Path.Combine(directory, Guid.NewGuid().ToString("n") + ".go");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        Process? wrapper = null;
        try
        {
            wrapper = Process.Start(new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }.WithArguments(
                WorkerShell.BaseArguments().Concat([
                    $"while (-not (Test-Path -LiteralPath '{startSignal}')) {{ Start-Sleep -Milliseconds 25 }}; " +
                    "$p = Start-Process ping.exe -ArgumentList '-n 9999 127.0.0.1' -PassThru -WindowStyle Hidden; " +
                    $"Set-Content -LiteralPath '{marker}' -Value $p.Id; " +
                    "Start-Sleep -Seconds 9999"
                ])))
                ?? throw new InvalidOperationException("Failed to start wrapper process.");

            Assert.True(WorkerProcessJobs.TryRegister(wrapper, "acceptance:test-slot"));
            File.WriteAllText(startSignal, "go");
            var childPid = WaitForPidFile(marker);
            WorkerProcessJobs.Release(wrapper.Id);

            Assert.False(WorkerProcessJobs.HasRegisteredJob(wrapper.Id));
            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(2)));
            Assert.True(WaitUntilNotRunning(childPid, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            if (wrapper is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(wrapper.Id); } catch { }
                wrapper.Dispose();
            }

            try { File.Delete(marker); } catch { }
            try { File.Delete(startSignal); } catch { }
        }
    }

    [Xunit.Theory(DisplayName = "Conductor detach controls missing-exit requeue by worker-result evidence")]
    [Xunit.InlineData("unavailable", 1, WorkTaskStatus.Assigned)]
    [Xunit.InlineData("absent", 1, WorkTaskStatus.Assigned)]
    [Xunit.InlineData("present", 0, WorkTaskStatus.Failed)]
    public void ConductorDetach_WorkerResultEvidence_ControlsRequeue(
        string workerResultState,
        int expectedRequeued,
        WorkTaskStatus expectedStatus)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? worker = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            worker = StartLongRunningShell();
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "recover from durable detached lifecycle");
            var task = goal.Tasks.Single();
            var ownerId = $"{goal.Id.Value}:{task.Id.Value}";
            Assert.True(WorkerProcessJobs.TryRegister(worker, ownerId));
            var processRecordedAt = DateTimeOffset.UtcNow;
            var processRecord = new TaskProcessRecord(
                worker.Id,
                "worker.exe",
                Path.GetDirectoryName(dbPath)!,
                Path.ChangeExtension(dbPath, ".out.log"),
                Path.ChangeExtension(dbPath, ".err.log"),
                Path.ChangeExtension(dbPath, ".exit.txt"),
                processRecordedAt,
                null,
                null,
                OwnedProcessIds: [worker.Id]);
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord("test-worker", "worker.exe", Path.GetDirectoryName(dbPath)!, processRecordedAt));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);

            Assert.True(WorkerProcessJobs.TryDetachForGracefulStop(worker.Id, out var failure), failure);

            Assert.False(WorkerProcessJobs.HasRegisteredJob(worker.Id));
            Assert.True(IsRunning(worker.Id));
            var detached = Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Equal(SpawnRegistryLifecycle.ConductorDetached, detached.Lifecycle);
            Assert.True(WorkerProcessJobs.WasDetachedByConductor(ownerId, worker.Id, processRecordedAt));
            Assert.Equal(0, WorkerProcessJobs.SweepStartupOrphans());
            Assert.True(IsRunning(worker.Id));
            Assert.Contains(
                "retain-gracefully-detached",
                Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests()).LastDiagnostic,
                StringComparison.Ordinal);

            worker.Kill(entireProcessTree: true);
            Assert.True(worker.WaitForExit(5000));
            Assert.Equal(0, WorkerProcessJobs.SweepStartupOrphans());
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.True(WorkerProcessJobs.WasDetachedByConductor(ownerId, worker.Id, processRecordedAt));

            var failedAt = DateTimeOffset.UtcNow;
            var syntheticFailure = processRecord with
            {
                CompletedAt = failedAt,
                ExitCode = 1,
                ExitArtifactOrigin = DispatchExitArtifactOrigin.Synthetic,
                ExitArtifactReason = "successor synthesized completion before detached task marker checkpointed"
            };
            var verification = workerResultState == "unavailable"
                ? null
                : new TaskVerificationRecord(
                    syntheticFailure.Command,
                    syntheticFailure.WorkingDirectory,
                    1,
                    string.Empty,
                    "dispatch host disappeared before writing its exit artifact",
                    failedAt,
                    WorkerResultPresent: workerResultState == "present");
            kernel.RecordTaskProcessRefreshed(
                goal.Id,
                task.Id,
                syntheticFailure,
                verification);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "synthetic missing-exit outcome");

            var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
            Assert.Equal(expectedRequeued, runner.RequeueInterruptedDispatches(kernel));
            Assert.Equal(expectedStatus, kernel.GetTask(goal.Id, task.Id).Status);
        }
        finally
        {
            if (worker is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(worker.Id); } catch { }
                worker.Dispose();
            }

            WorkerProcessJobs.ClearRegistryForTests();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_runtime_handoff_survives_job_close_and_startup_sweep")]
    public void WorkerProcessJobsRuntimeHandoffSurvivesJobCloseAndStartupSweep()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? worker = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            worker = StartLongRunningShell();
            Assert.True(WorkerProcessJobs.TryRegister(worker, "runtime-handoff:test"));

            Assert.True(WorkerProcessJobs.TryHandOffToRuntimeOwnership(worker.Id, out var failure), failure);

            Assert.False(WorkerProcessJobs.HasRegisteredJob(worker.Id));
            Assert.True(IsRunning(worker.Id));
            var detached = Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Equal(SpawnRegistryLifecycle.RuntimeOwned, detached.Lifecycle);
            Assert.Contains("runtime-owned", detached.LastDiagnostic, StringComparison.Ordinal);
            Assert.Equal(0, WorkerProcessJobs.SweepStartupOrphans());
            Assert.True(IsRunning(worker.Id));

            Assert.True(WorkerProcessJobs.TryKillOrFallbackAndWait(worker.Id, TimeSpan.FromSeconds(5)));
            Assert.False(IsRunning(worker.Id));
        }
        finally
        {
            if (worker is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(worker.Id); } catch { }
                worker.Dispose();
            }

            WorkerProcessJobs.ClearRegistryForTests();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Theory(DisplayName = "Runtime handoff synthetic missing exit does not authorize requeue")]
    [Xunit.InlineData("unavailable")]
    [Xunit.InlineData("absent")]
    [Xunit.InlineData("present")]
    public void RuntimeHandoff_SyntheticMissingExit_DoesNotAuthorizeRequeue(string workerResultState)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? worker = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            worker = StartLongRunningShell();
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "runtime handoff is not retry authority");
            var task = goal.Tasks.Single();
            var ownerId = $"{goal.Id.Value}:{task.Id.Value}";
            Assert.True(WorkerProcessJobs.TryRegister(worker, ownerId));
            var processRecordedAt = DateTimeOffset.UtcNow;
            var processRecord = new TaskProcessRecord(
                worker.Id,
                "worker.exe",
                Path.GetDirectoryName(dbPath)!,
                Path.ChangeExtension(dbPath, ".out.log"),
                Path.ChangeExtension(dbPath, ".err.log"),
                Path.ChangeExtension(dbPath, ".exit.txt"),
                processRecordedAt,
                null,
                null,
                OwnedProcessIds: [worker.Id]);
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord("test-worker", "worker.exe", Path.GetDirectoryName(dbPath)!, processRecordedAt));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);

            Assert.True(WorkerProcessJobs.TryHandOffToRuntimeOwnership(worker.Id, out var failure), failure);
            Assert.True(WorkerProcessJobs.TryKillOrFallbackAndWait(worker.Id, TimeSpan.FromSeconds(5)));
            var failedAt = DateTimeOffset.UtcNow;
            var syntheticFailure = processRecord with
            {
                CompletedAt = failedAt,
                ExitCode = 1,
                ExitArtifactOrigin = DispatchExitArtifactOrigin.Synthetic,
                ExitArtifactReason = "runtime owner synthesized completion without an exit artifact"
            };
            var verification = workerResultState == "unavailable"
                ? null
                : new TaskVerificationRecord(
                    syntheticFailure.Command,
                    syntheticFailure.WorkingDirectory,
                    1,
                    string.Empty,
                    "dispatch host disappeared before writing its exit artifact",
                    failedAt,
                    WorkerResultPresent: workerResultState == "present");
            kernel.RecordTaskProcessRefreshed(
                goal.Id,
                task.Id,
                syntheticFailure,
                verification);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "synthetic missing-exit outcome");

            Assert.Equal(
                workerResultState == "unavailable" ? null : workerResultState == "present",
                kernel.GetTask(goal.Id, task.Id).LastVerification?.WorkerResultPresent);

            var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
            Assert.Equal(0, runner.RequeueInterruptedDispatches(kernel));
            Assert.Equal(WorkTaskStatus.Failed, kernel.GetTask(goal.Id, task.Id).Status);
        }
        finally
        {
            if (worker is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(worker.Id); } catch { }
                worker.Dispose();
            }

            WorkerProcessJobs.ClearRegistryForTests();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "Conductor detach authority follows latest identity-bound registry attempt")]
    public void DetachAuthority_LatestRegistryAttempt_IsIdentityBound()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        const string ownerId = "goal-id:task-id";
        const int reusedProcessId = 4242;
        var registry = new SpawnRegistry(dbPath);
        try
        {
            var interruptedIdentity = new SpawnProcessIdentity(
                reusedProcessId,
                DateTimeOffset.Parse("2026-09-06T12:00:00Z"),
                "C:\\workers\\first.exe");
            registry.Register(ownerId, interruptedIdentity, ownerIdentity: null);
            Assert.True(registry.TryMarkConductorDetached(interruptedIdentity, "conductor interrupted first attempt"));
            var interruptedRecordedAt = DateTimeOffset.UtcNow;

            Assert.True(registry.WasDetachedByConductor(ownerId, reusedProcessId, interruptedRecordedAt));
            registry.MarkReleased(reusedProcessId, "first attempt released");
            Assert.True(registry.WasDetachedByConductor(ownerId, reusedProcessId, interruptedRecordedAt));

            var runtimeIdentity = interruptedIdentity with
            {
                StartedAt = interruptedIdentity.StartedAt.AddMinutes(1),
                ImagePath = "C:\\workers\\second.exe"
            };
            registry.Register(ownerId, runtimeIdentity, ownerIdentity: null);
            Assert.True(registry.TryMarkRuntimeOwned(runtimeIdentity, "runtime owns reused pid"));
            var runtimeRecordedAt = DateTimeOffset.UtcNow;

            Assert.False(registry.WasDetachedByConductor(ownerId, reusedProcessId, runtimeRecordedAt));
            registry.MarkReleased(reusedProcessId, "runtime attempt released");

            var legacyIdentity = runtimeIdentity with
            {
                StartedAt = runtimeIdentity.StartedAt.AddMinutes(1),
                ImagePath = "C:\\workers\\legacy.exe"
            };
            registry.Register(ownerId, legacyIdentity, ownerIdentity: null);
            using (var connection = StateDbConnectionFactory.Open(dbPath, StateDbConnectionProfile.ReadWrite))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE spawn_registry
                    SET lifecycle = $legacy
                    WHERE process_id = $process_id
                      AND process_started_at = $process_started_at
                      AND image_path = $image_path
                    """;
                command.Parameters.AddWithValue("$legacy", SpawnRegistryLifecycle.GracefullyDetached.ToString());
                command.Parameters.AddWithValue("$process_id", legacyIdentity.ProcessId);
                command.Parameters.AddWithValue("$process_started_at", legacyIdentity.StartedAt.ToString("O"));
                command.Parameters.AddWithValue("$image_path", legacyIdentity.ImagePath);
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var legacyRecordedAt = DateTimeOffset.UtcNow;
            Assert.Equal(
                SpawnRegistryLifecycle.GracefullyDetached,
                Assert.Single(registry.ListActive()).Lifecycle);
            Assert.False(registry.WasDetachedByConductor(ownerId, reusedProcessId, legacyRecordedAt));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_runtime_handoff_joins_ambient_state_write")]
    public void WorkerProcessJobsRuntimeHandoffJoinsAmbientStateWrite()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? worker = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            worker = StartLongRunningShell();
            using (var connection = StateDbConnectionFactory.Open(dbPath, StateDbConnectionProfile.ReadWrite))
            {
                using (var begin = connection.CreateCommand())
                {
                    begin.CommandText = "BEGIN IMMEDIATE";
                    begin.ExecuteNonQuery();
                }

                using (StateDbWriteSession.Enter(dbPath, connection))
                {
                    Assert.True(WorkerProcessJobs.TryRegister(worker, "runtime-handoff:ambient-write"));
                    Assert.True(WorkerProcessJobs.TryHandOffToRuntimeOwnership(worker.Id, out var failure), failure);
                }

                using var commit = connection.CreateCommand();
                commit.CommandText = "COMMIT";
                commit.ExecuteNonQuery();
            }

            Assert.False(WorkerProcessJobs.HasRegisteredJob(worker.Id));
            var detached = Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Equal(SpawnRegistryLifecycle.RuntimeOwned, detached.Lifecycle);
            Assert.Contains("runtime-owned", detached.LastDiagnostic, StringComparison.Ordinal);
            Assert.True(IsRunning(worker.Id));
        }
        finally
        {
            if (worker is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(worker.Id); } catch { }
                worker.Dispose();
            }

            WorkerProcessJobs.ClearRegistryForTests();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_runtime_handoff_registry_failure_is_loud_and_reaps_job")]
    public void WorkerProcessJobsRuntimeHandoffRegistryFailureIsLoudAndReapsJob()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? worker = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            worker = StartLongRunningShell();
            Assert.True(WorkerProcessJobs.TryRegister(worker, "runtime-handoff:registry-failure"));
            File.WriteAllText(dbPath, "not a sqlite database");

            Assert.False(WorkerProcessJobs.TryHandOffToRuntimeOwnership(worker.Id, out var failure));

            Assert.Contains("worker-process-handoff-failed", failure, StringComparison.Ordinal);
            Assert.Contains("durable-lifecycle-transition", failure, StringComparison.Ordinal);
            Assert.False(WorkerProcessJobs.HasRegisteredJob(worker.Id));
            Assert.True(WaitUntilNotRunning(worker.Id, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (worker is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(worker.Id); } catch { }
                worker.Dispose();
            }

            WorkerProcessJobs.ClearRegistryForTests();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_graceful_detach_registry_exception_reaps_job_and_returns_failure")]
    public void WorkerProcessJobsGracefulDetachRegistryExceptionReapsJobAndReturnsFailure()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? worker = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            worker = StartLongRunningShell();
            Assert.True(WorkerProcessJobs.TryRegister(worker, "graceful-stop:registry-failure"));
            File.WriteAllText(dbPath, "not a sqlite database");

            Assert.False(WorkerProcessJobs.TryDetachForGracefulStop(worker.Id, out var failure));

            Assert.Contains("durable-lifecycle-transition", failure, StringComparison.Ordinal);
            Assert.False(WorkerProcessJobs.HasRegisteredJob(worker.Id));
            Assert.True(WaitUntilNotRunning(worker.Id, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (worker is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(worker.Id); } catch { }
                worker.Dispose();
            }

            WorkerProcessJobs.ClearRegistryForTests();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_detach_registry_failure_records_requeueable_conductor_cancellation")]
    public void BackgroundDispatchRunnerDetachRegistryFailureRecordsRequeueableConductorCancellation()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? worker = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            worker = StartLongRunningShell();
            Assert.True(WorkerProcessJobs.TryRegister(worker, "graceful-stop:fallback-cancel"));
            Assert.True(WorkerProcessJobs.TryGetRegisteredIdentity(worker.Id, out var registeredIdentity));

            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "recover detach registry failure");
            var task = goal.Tasks.Single();
            var now = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord("test-worker", "worker.exe", Path.GetDirectoryName(dbPath)!, now));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(
                    worker.Id,
                    "worker.exe",
                    Path.GetDirectoryName(dbPath)!,
                    Path.ChangeExtension(dbPath, ".out.log"),
                    Path.ChangeExtension(dbPath, ".err.log"),
                    Path.ChangeExtension(dbPath, ".exit.txt"),
                    now,
                    null,
                    null,
                    OwnedProcessIds: [worker.Id],
                    ProcessIdentityStartedAt: registeredIdentity.StartedAt));
            File.WriteAllText(dbPath, "not a sqlite database");

            var runner = new BackgroundDispatchRunner();
            Assert.Equal(0, runner.DetachRunningProcessesForGoal(kernel, goal.Id));

            var cancelledTask = kernel.GetTask(goal.Id, task.Id);
            Assert.Equal(WorkTaskStatus.Cancelled, cancelledTask.Status);
            Assert.True(cancelledTask.LastProcess!.WasCancelled);
            Assert.True(cancelledTask.LastProcess.WasCancelledByConductor);
            Assert.Contains(goal.Timeline, entry => entry.TaskId == task.Id &&
                entry.Message.Contains("stage=durable-lifecycle-transition; error=SqliteException", StringComparison.Ordinal));
            Assert.True(WaitUntilNotRunning(worker.Id, TimeSpan.FromSeconds(5)));

            Assert.Equal(1, runner.RequeueInterruptedDispatches(kernel));
            Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, task.Id).Status);
        }
        finally
        {
            if (worker is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(worker.Id); } catch { }
                worker.Dispose();
            }

            WorkerProcessJobs.ClearRegistryForTests();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_fallback_taskkill_tree_kills_unregistered_wrapper_and_grandchild")]
    public void WorkerProcessJobsFallbackTaskkillTreeKillsUnregisteredWrapperAndGrandchild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var marker = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n") + ".pid");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        Process? wrapper = null;
        try
        {
            wrapper = Process.Start(new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }.WithArguments(
                WorkerShell.BaseArguments().Concat([
                    "$p = Start-Process ping.exe -ArgumentList '-n 9999 127.0.0.1' -PassThru -WindowStyle Hidden; " +
                    $"Set-Content -LiteralPath '{marker}' -Value $p.Id; " +
                    "Start-Sleep -Seconds 9999"
                ])))
                ?? throw new InvalidOperationException("Failed to start wrapper process.");
            var grandchildPid = WaitForPidFile(marker);

            Assert.True(WorkerProcessJobs.TryKillOrFallback(wrapper.Id));

            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(2)));
            Assert.True(WaitUntilNotRunning(grandchildPid, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            if (wrapper is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(wrapper.Id); } catch { }
                wrapper.Dispose();
            }

            try { File.Delete(marker); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_kill_or_fallback_and_wait_returns_after_wrapper_exit")]
    public void WorkerProcessJobsKillOrFallbackAndWaitReturnsAfterWrapperExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Process? wrapper = null;
        try
        {
            wrapper = StartLongRunningShell();

            Assert.True(WorkerProcessJobs.TryKillOrFallbackAndWait(wrapper.Id, TimeSpan.FromSeconds(5)));
            Assert.False(IsRunning(wrapper.Id));
        }
        finally
        {
            if (wrapper is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(wrapper.Id); } catch { }
                wrapper.Dispose();
            }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_kill_or_fallback_and_wait_refuses_current_process_without_protected_pid")]
    public void WorkerProcessJobsKillOrFallbackAndWaitRefusesCurrentProcessWithoutProtectedPid()
    {
        using var protectedPidEnvironment = InfrastructureTestSupport.ClearProtectedPidEnvironment();
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        var killCalled = false;
        WorkerProcessJobs.TryKillPidTree = _ =>
        {
            killCalled = true;
            return true;
        };

        try
        {
            Assert.False(WorkerProcessJobs.TryKillOrFallbackAndWait(Environment.ProcessId, TimeSpan.Zero));
            Assert.False(killCalled);
            Assert.True(IsRunning(Environment.ProcessId));
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_kill_or_fallback_and_wait_refuses_current_process_ancestor")]
    public void WorkerProcessJobsKillOrFallbackAndWaitRefusesCurrentProcessAncestor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var parentProcessId = ProcessParentIdResolver.TryGetParentProcessId(Environment.ProcessId);
        Assert.NotNull(parentProcessId);

        using var protectedPidEnvironment = InfrastructureTestSupport.ClearProtectedPidEnvironment();
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        var killCalled = false;
        WorkerProcessJobs.TryKillPidTree = _ =>
        {
            killCalled = true;
            return true;
        };

        try
        {
            Assert.False(WorkerProcessJobs.TryKillOrFallbackAndWait(parentProcessId.Value, TimeSpan.Zero));
            Assert.False(killCalled);
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
        }
    }

    // The 2026-09-15 chain: protected daemon 1000 ← supervisor 900 ← cmd 800 ← pwsh 700, where 700 had
    // exited hours earlier. Windows keeps 700 in 800's parent-pid record, and had just handed pid 700 to
    // a freshly spawned gate child.
    private const int ProtectedDaemonPid = 1000;
    private const int SupervisorPid = 900;
    private const int LauncherShellPid = 800;
    private const int RecycledShellPid = 700;

    private static readonly DateTime ChainStartedAt = new(2026, 9, 15, 4, 50, 0, DateTimeKind.Utc);

    private static readonly Dictionary<int, (int ParentProcessId, DateTime StartTimeUtc)> ProtectedChain = new()
    {
        [ProtectedDaemonPid] = (SupervisorPid, ChainStartedAt.AddMinutes(3)),
        [SupervisorPid] = (LauncherShellPid, ChainStartedAt.AddMinutes(2)),
        [LauncherShellPid] = (RecycledShellPid, ChainStartedAt),
        // The live process now holding pid 700 started three hours after the child that still names it.
        [RecycledShellPid] = (1, ChainStartedAt.AddHours(3))
    };

    private static WorkerProcessJobs.ProcessAncestryLookup AncestryLookup(
        IReadOnlyDictionary<int, (int ParentProcessId, DateTime StartTimeUtc)> chain) =>
        (int processId, out WorkerProcessJobs.ProcessAncestryFacts facts) =>
        {
            if (!chain.TryGetValue(processId, out var entry))
            {
                facts = default;
                return false;
            }

            facts = new WorkerProcessJobs.ProcessAncestryFacts(entry.ParentProcessId, entry.StartTimeUtc);
            return true;
        };

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_ancestry_walk_refuses_recycled_dead_ancestor_pid")]
    public void WorkerProcessJobsAncestryWalkRefusesRecycledDeadAncestorPid()
    {
        var lookup = AncestryLookup(ProtectedChain);

        Assert.False(WorkerProcessJobs.IsProtectedProcessOrAncestor(
            RecycledShellPid,
            ProtectedDaemonPid,
            lookup));
        Assert.False(WorkerProcessJobs.IsDescendantOf(ProtectedDaemonPid, RecycledShellPid, lookup));
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_ancestry_walk_refuses_hop_through_an_exited_parent_pid")]
    public void WorkerProcessJobsAncestryWalkRefusesHopThroughAnExitedParentPid()
    {
        // Same chain, except pid 700 names no process at all: the hop cannot be verified either way.
        var chain = ProtectedChain
            .Where(entry => entry.Key != RecycledShellPid)
            .ToDictionary(entry => entry.Key, entry => entry.Value);

        Assert.False(WorkerProcessJobs.IsProtectedProcessOrAncestor(
            RecycledShellPid,
            ProtectedDaemonPid,
            AncestryLookup(chain)));
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_ancestry_walk_refuses_equal_start_time_parent")]
    public void WorkerProcessJobsAncestryWalkRefusesEqualStartTimeParent()
    {
        var chain = new Dictionary<int, (int ParentProcessId, DateTime StartTimeUtc)>
        {
            [ProtectedDaemonPid] = (SupervisorPid, ChainStartedAt),
            [SupervisorPid] = (1, ChainStartedAt)
        };

        Assert.False(WorkerProcessJobs.IsDescendantOf(
            ProtectedDaemonPid,
            SupervisorPid,
            AncestryLookup(chain)));
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_ancestry_walk_still_refuses_protected_pid_and_live_ancestors")]
    public void WorkerProcessJobsAncestryWalkStillRefusesProtectedPidAndLiveAncestors()
    {
        var lookup = AncestryLookup(ProtectedChain);

        Assert.True(WorkerProcessJobs.IsProtectedProcessOrAncestor(
            ProtectedDaemonPid,
            ProtectedDaemonPid,
            lookup));
        Assert.True(WorkerProcessJobs.IsProtectedProcessOrAncestor(
            SupervisorPid,
            ProtectedDaemonPid,
            lookup));
        Assert.True(WorkerProcessJobs.IsProtectedProcessOrAncestor(
            LauncherShellPid,
            ProtectedDaemonPid,
            lookup));
        Assert.True(WorkerProcessJobs.IsDescendantOf(ProtectedDaemonPid, LauncherShellPid, lookup));
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_ancestry_walk_accepts_a_real_live_ancestor")]
    public void WorkerProcessJobsAncestryWalkAcceptsARealLiveAncestor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var child = StartLongRunningShell();
        try
        {
            // The default lookup reads the live Toolhelp32 parent plus Process.StartTime, so this
            // process — a genuinely live ancestor of the child it just spawned — still classifies as one.
            Assert.True(WorkerProcessJobs.IsProtectedProcessOrAncestor(
                Environment.ProcessId,
                child.Id,
                WorkerProcessJobs.ReadProcessAncestryFactsForTests));
        }
        finally
        {
            try { child.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static Process StartLongRunningShell()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }.WithArguments(
            WorkerShell.BaseArguments().Concat([
                "Start-Sleep -Seconds 9999"
            ])))
            ?? throw new InvalidOperationException("Failed to start wrapper process.");

        if (SpawnProcessIdentityReader.TryReadForRegistration(process, out _))
        {
            return process;
        }

        try { process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
        throw new InvalidOperationException("Started wrapper process did not expose a durable identity within the registration window.");
    }

    private static ProcessStartInfo CreateIsolatedDotnetProbeStartInfo(string readyPath)
    {
        var assemblyDirectory = new DirectoryInfo(Path.GetDirectoryName(typeof(WorkerProcessJobsTests).Assembly.Location)!);
        var parentDirectory = assemblyDirectory.Parent;
        var probeAssemblyName = "Mcg.AgentOrchestrator.IsolatedDotnetProbe.dll";
        var candidates = new List<string>
        {
            Path.Combine(assemblyDirectory.FullName, probeAssemblyName)
        };
        if (parentDirectory?.Parent?.Parent is { } testProjectDirectory)
        {
            candidates.Add(Path.Combine(
                testProjectDirectory.FullName,
                "Fixtures",
                "IsolatedDotnetProbe",
                "bin",
                parentDirectory.Name,
                assemblyDirectory.Name,
                probeAssemblyName));
        }

        if (parentDirectory?.Parent is { } projectOutputRoot)
        {
            candidates.Add(Path.Combine(
                projectOutputRoot.FullName,
                "Mcg.AgentOrchestrator.IsolatedDotnetProbe",
                assemblyDirectory.Name,
                probeAssemblyName));
        }

        var probeAssembly = candidates.FirstOrDefault(File.Exists);
        Assert.False(
            string.IsNullOrWhiteSpace(probeAssembly),
            $"Missing isolated dotnet probe. Tried: {string.Join("; ", candidates)}");
        var startInfo = new ProcessStartInfo
        {
            FileName = InfrastructureTestSupport.ResolveDotnetHostPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(readyPath)!
        };
        startInfo.ArgumentList.Add(probeAssembly!);
        startInfo.ArgumentList.Add("spawn-descendant");
        startInfo.ArgumentList.Add(readyPath);
        return startInfo;
    }

    private static int WaitForPidFile(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path) &&
                    int.TryParse(File.ReadAllText(path).Trim(), out var pid) &&
                    pid > 0)
                {
                    return pid;
                }
            }
            catch (IOException)
            {
                // The writer can still hold the pid file open when it first appears; a sharing
                // violation here means "not ready yet", not failure — keep polling until the
                // deadline. (Killed a full acceptance-gate attempt as a flake on 2026-07-25.)
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException("Timed out waiting for grandchild pid file.");
    }

    private static bool WaitUntilNotRunning(int processId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsRunning(processId))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return !IsRunning(processId);
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_release_returns_job_accounting_counters")]
    public void WorkerProcessJobsReleaseReturnsJobAccountingCounters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Process? wrapper = null;
        try
        {
            wrapper = Process.Start(new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }.WithArguments(
                WorkerShell.BaseArguments().Concat([
                    "$bytes = New-Object byte[] 1048576; Start-Sleep -Milliseconds 250; [GC]::KeepAlive($bytes)"
                ])))
                ?? throw new InvalidOperationException("Failed to start wrapper process.");

            Assert.True(WorkerProcessJobs.TryRegister(wrapper));
            wrapper.WaitForExit(5000);

            WorkerProcessJobs.Release(wrapper.Id, out var accounting);

            Assert.NotNull(accounting);
            Assert.True(accounting.CpuMilliseconds >= 0);
            Assert.True(accounting.PeakMemoryBytes > 0);
            Assert.True(accounting.IoBytes >= 0);
        }
        finally
        {
            if (wrapper is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(wrapper.Id); } catch { }
                wrapper.Dispose();
            }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_try_kill_returns_job_accounting_counters")]
    public void WorkerProcessJobsTryKillReturnsJobAccountingCounters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Process? wrapper = null;
        try
        {
            wrapper = Process.Start(new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }.WithArguments(
                WorkerShell.BaseArguments().Concat([
                    "$bytes = New-Object byte[] 1048576; Start-Sleep -Seconds 30; [GC]::KeepAlive($bytes)"
                ])))
                ?? throw new InvalidOperationException("Failed to start wrapper process.");

            Assert.True(WorkerProcessJobs.TryRegister(wrapper));

            Assert.True(WorkerProcessJobs.TryKillOrFallback(wrapper.Id, out var accounting));

            Assert.NotNull(accounting);
            Assert.True(accounting.CpuMilliseconds >= 0);
            Assert.True(accounting.PeakMemoryBytes > 0);
            Assert.True(accounting.IoBytes >= 0);
            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            if (wrapper is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(wrapper.Id); } catch { }
                wrapper.Dispose();
            }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_source_routes_owned_group_close_through_accounting_helper")]
    public void WorkerProcessJobsSourceRoutesOwnedGroupCloseThroughAccountingHelper()
    {
        AssertOwnedGroupCloseRoutesThroughAccountingHelper();
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_production_callers_observe_registration_failure")]
    public void WorkerProcessJobsProductionCallersObserveRegistrationFailure()
    {
        AssertProductionCallersObserveRegistrationFailure();
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_test_sources_reject_negative_wait_assertions")]
    public void WorkerProcessJobsTestSourcesRejectNegativeWaitAssertions()
    {
        var repoRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var testRoot = Path.Combine(repoRoot, "tests");
        var separator = Path.DirectorySeparatorChar;
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)))
        {
            var source = File.ReadAllText(path);
            foreach (Match match in NegativeWaitAssertionPattern.Matches(source))
            {
                if (HasNegativeWaitAssertionExemption(source, match.Index))
                {
                    continue;
                }

                var lineNumber = source.Take(match.Index).Count(character => character == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(repoRoot, path)}:{lineNumber}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Negative wait assertions are forbidden because they cannot prove liveness. Offenders: " +
            string.Join(", ", offenders));
    }

    [Xunit.Theory(DisplayName = "WorkerProcessJobs_negative_wait_source_guard_recognizes_forbidden_members")]
    [Xunit.InlineData("WaitForExit")]
    [Xunit.InlineData("WaitForExitAsync")]
    [Xunit.InlineData("SpinUntil")]
    [Xunit.InlineData("Wait")]
    public void WorkerProcessJobsNegativeWaitSourceGuardRecognizesForbiddenMembers(string memberName)
    {
        var source = string.Concat("Assert", ".False(subject.", memberName, "(100));");

        Assert.True(NegativeWaitAssertionPattern.IsMatch(source));
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_negative_wait_source_guard_allows_state_assertions")]
    public void WorkerProcessJobsNegativeWaitSourceGuardAllowsStateAssertions()
    {
        var source = string.Concat("Assert", ".False(process.HasExited);");

        Assert.False(NegativeWaitAssertionPattern.IsMatch(source));
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_negative_wait_source_guard_allows_documented_exemption")]
    public void WorkerProcessJobsNegativeWaitSourceGuardAllowsDocumentedExemption()
    {
        var source = string.Concat(
            "// ",
            NegativeWaitAssertionExemption,
            " - deterministic single-threaded fallback\n",
            "Assert",
            ".False(subject.Wait(10));");
        var match = NegativeWaitAssertionPattern.Match(source);

        Assert.True(match.Success);
        Assert.True(HasNegativeWaitAssertionExemption(source, match.Index));
    }

    private static bool HasNegativeWaitAssertionExemption(string source, int assertionIndex)
    {
        var assertionLineStart = source.LastIndexOf('\n', Math.Max(0, assertionIndex - 1));
        if (assertionLineStart <= 0)
        {
            return false;
        }

        var previousLineStart = source.LastIndexOf('\n', assertionLineStart - 1) + 1;
        var previousLine = source.AsSpan(previousLineStart, assertionLineStart - previousLineStart).Trim();
        var exemptionPrefix = $"// {NegativeWaitAssertionExemption} - ";
        return previousLine.StartsWith(exemptionPrefix, StringComparison.Ordinal) &&
               previousLine.Length > exemptionPrefix.Length;
    }

    private static void AssertProductionCallersObserveRegistrationFailure(
        [CallerFilePath] string sourceFilePath = "")
    {
        var repoRoot = VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot)
            ? verifiedRoot
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", ".."));
        var checkedCallers = new Dictionary<string, string>
        {
            [Path.Combine("src", "Mcg.AgentOrchestrator.Execution", "Processes", "BackgroundDispatchRunner.cs")] =
                "if (!WorkerProcessJobs.TryRegister(",
            [Path.Combine("src", "Mcg.AgentOrchestrator.Infrastructure", "Workspaces", "LocalProcessVerifier.cs")] =
                "WorkerProcessJobs.StartRegisteredOwnedRedirectedOrThrow(",
            [Path.Combine("src", "Mcg.AgentOrchestrator.Infrastructure", "Workspaces", "AcceptanceCheckProcessInvoker.cs")] =
                "WorkerProcessJobs.StartRegisteredOwnedOrThrow(",
            [Path.Combine("src", "Mcg.AgentOrchestrator.App", "Orchestration", "PostLandingCanaryRunner.cs")] =
                "WorkerProcessJobs.StartRegisteredOrThrow("
        };

        foreach (var (relativePath, expectedCall) in checkedCallers)
        {
            var source = File.ReadAllText(Path.Combine(repoRoot, relativePath));
            Assert.Contains(expectedCall, source, StringComparison.Ordinal);
        }

        var acceptanceSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "Mcg.AgentOrchestrator.Infrastructure",
            "Workspaces",
            "AcceptanceCheckProcessInvoker.cs"));
        var registrationIndex = acceptanceSource.IndexOf(
            "process = StartAcceptanceProcess(",
            StringComparison.Ordinal);
        var ownedPidAssignmentIndex = acceptanceSource.IndexOf(
            "startedProcessId = process.Id;",
            registrationIndex,
            StringComparison.Ordinal);
        Assert.True(registrationIndex >= 0 && ownedPidAssignmentIndex > registrationIndex);
        var helperIndex = acceptanceSource.IndexOf(
            "private static RegisteredOwnedProcess StartAcceptanceProcess(",
            StringComparison.Ordinal);
        var atomicStartIndex = acceptanceSource.IndexOf(
            "return WorkerProcessJobs.StartRegisteredOwnedOrThrow(",
            helperIndex,
            StringComparison.Ordinal);
        var legacyStartIndex = acceptanceSource.IndexOf(
            "var legacyProcess = ProcessTreeGuiSuppression.Start(",
            helperIndex,
            StringComparison.Ordinal);
        var negativeControlIndex = acceptanceSource.IndexOf(
            "LegacyOwnedStartNegativeControlVariable",
            helperIndex,
            StringComparison.Ordinal);
        Assert.True(helperIndex >= 0 && atomicStartIndex > helperIndex);
        Assert.True(negativeControlIndex > helperIndex && legacyStartIndex > negativeControlIndex);
        Assert.Equal(
            legacyStartIndex + "var legacyProcess = ".Length,
            acceptanceSource.LastIndexOf(
                "ProcessTreeGuiSuppression.Start(",
                StringComparison.Ordinal));
    }

    private static void AssertOwnedGroupCloseRoutesThroughAccountingHelper(
        [CallerFilePath] string sourceFilePath = "")
    {
        var repoRoot = VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot)
            ? verifiedRoot
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", ".."));
        var productionRoot = Path.Combine(repoRoot, "src", "Mcg.AgentOrchestrator.Infrastructure");
        var offenders = Directory.EnumerateFiles(productionRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => new { path, line, lineNumber = index + 1 }))
            .Where(item =>
                item.path.EndsWith("OwnedProcessGroup.cs", StringComparison.OrdinalIgnoreCase) is false &&
                IsReadAccountingAndDisposeHelperLine(item.path, item.lineNumber) is false &&
                (item.line.Contains("workerGroup?.Dispose()", StringComparison.Ordinal) ||
                 item.line.Contains("workerGroup?.Kill()", StringComparison.Ordinal) ||
                 item.line.Contains("processGroup?.Dispose()", StringComparison.Ordinal) ||
                 item.line.Contains("processGroup?.Kill()", StringComparison.Ordinal) ||
                 item.line.Contains("group.Dispose()", StringComparison.Ordinal) ||
                 item.line.Contains("group.Kill()", StringComparison.Ordinal)) &&
                !item.line.Contains("ReadAccountingAndDispose", StringComparison.Ordinal))
            .Select(item => $"{Path.GetRelativePath(repoRoot, item.path)}:{item.lineNumber}:{item.line.Trim()}")
            .ToArray();

        Assert.True(offenders.Length == 0, string.Join(Environment.NewLine, offenders));
    }

    private static bool IsReadAccountingAndDisposeHelperLine(string path, int lineNumber)
    {
        if (!path.EndsWith(Path.Combine("Processes", "WorkerProcessJobs.cs"), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, line => line.Contains("internal static bool ReadAccountingAndDispose(", StringComparison.Ordinal));
        var end = Array.FindIndex(lines, line => line.Contains("internal static bool HasRegisteredJob", StringComparison.Ordinal));
        return start >= 0 && end > start && lineNumber > start && lineNumber <= end;
    }
}

internal static class ProcessStartInfoTestExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo startInfo, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
