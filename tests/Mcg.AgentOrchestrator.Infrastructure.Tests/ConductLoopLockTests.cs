using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class ConductLoopLockTests
{
    [Fact(DisplayName = "ConductLoopLock_no_existing_lock_acquires_and_orderly_dispose_removes_file")]
    public void NoExistingLockAcquiresAndOrderlyDisposeRemovesFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            var lockPath = Path.Combine(orchestratorDirectory, "conduct-loop.lock");

            using (ConductorLoopLease.Acquire(orchestratorDirectory))
            {
                Assert.True(File.Exists(lockPath));
                Assert.Equal(
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                    ReadLockLines(lockPath)[0]);
                Assert.True(DateTimeOffset.TryParseExact(
                    ReadLockLines(lockPath)[2],
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _));
            }

            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact(DisplayName = "ConductLoopLock_scope_unwind_removes_lock_after_exception")]
    public void ScopeUnwindRemovesLockAfterException()
    {
        var root = CreateTempDirectory();
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            var lockPath = Path.Combine(orchestratorDirectory, "conduct-loop.lock");

            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var lease = ConductorLoopLease.Acquire(orchestratorDirectory);
                throw new InvalidOperationException("loop failed");
            }));

            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact(DisplayName = "ConductLoopLock_dead_pid_is_logged_and_replaced")]
    public void DeadPidIsLoggedAndReplaced()
    {
        var root = CreateTempDirectory();
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(orchestratorDirectory);
            var lockPath = Path.Combine(orchestratorDirectory, "conduct-loop.lock");
            var writtenAt = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            var now = writtenAt.AddMinutes(90);
            File.WriteAllLines(
                lockPath,
                [
                    "424242",
                    writtenAt.ToString("O", CultureInfo.InvariantCulture),
                    writtenAt.AddHours(-1).ToString("O", CultureInfo.InvariantCulture)
                ]);

            var probe = new StubPidProbe(isRunning: false, isSameProcess: false);
            ConductorLoopLease? lease = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                lease = ConductorLoopLease.Acquire(
                    orchestratorDirectory,
                    probe,
                    () => now);
            });
            using (lease)
            {
                var lines = ReadLockLines(lockPath);
                Assert.Equal(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), lines[0]);
                Assert.Equal(now.ToString("O", CultureInfo.InvariantCulture), lines[1]);
            }

            const string expected =
                "conduct-loop.lock held by pid 424242 (not running), written 1h 30m ago — stale lock removed, proceeding";
            Assert.Contains(expected, output, StringComparison.Ordinal);
            Assert.Equal(0, probe.IdentityMatchCount);
            var eventLogPath = Path.Combine(
                orchestratorDirectory,
                "logs",
                ConductEventLogWriter.CurrentFileName);
            var staleTakeover = File.ReadAllLines(eventLogPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Single(record => record.EventKind == "conduct-lock-stale-takeover");
            Assert.Equal(expected, staleTakeover.Detail);
            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact(DisplayName = "ConductLoopLock_recycled_pid_is_logged_and_replaced")]
    public void RecycledPidIsLoggedAndReplaced()
    {
        var root = CreateTempDirectory();
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(orchestratorDirectory);
            var lockPath = Path.Combine(orchestratorDirectory, "conduct-loop.lock");
            var writtenAt = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            var recordedProcessStartedAt = writtenAt.AddHours(-2);
            File.WriteAllLines(
                lockPath,
                [
                    "31337",
                    writtenAt.ToString("O", CultureInfo.InvariantCulture),
                    recordedProcessStartedAt.ToString("O", CultureInfo.InvariantCulture)
                ]);
            var probe = new StubPidProbe(isRunning: true, isSameProcess: false);

            ConductorLoopLease? lease = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                lease = ConductorLoopLease.Acquire(
                    orchestratorDirectory,
                    probe,
                    () => writtenAt.AddMinutes(5));
            });
            lease!.Dispose();

            Assert.Contains(
                "conduct-loop.lock held by pid 31337 (dead-or-recycled), written 5m ago — stale lock removed, proceeding",
                output,
                StringComparison.Ordinal);
            Assert.Equal(1, probe.IdentityMatchCount);
            Assert.Equal(recordedProcessStartedAt, probe.LastExpectedStartTime);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact(DisplayName = "ConductLoopLock_live_pid_is_refused_with_owner_age_and_remedy")]
    public void LivePidIsRefusedWithOwnerAgeAndRemedy()
    {
        var root = CreateTempDirectory();
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(orchestratorDirectory);
            var lockPath = Path.Combine(orchestratorDirectory, "conduct-loop.lock");
            var writtenAt = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            File.WriteAllLines(
                lockPath,
                [
                    "31337",
                    writtenAt.ToString("O", CultureInfo.InvariantCulture),
                    writtenAt.AddHours(-1).ToString("O", CultureInfo.InvariantCulture)
                ]);

            var probe = new StubPidProbe(isRunning: true, isSameProcess: true);
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ConductorLoopLease.Acquire(
                    orchestratorDirectory,
                    probe,
                    () => writtenAt.AddMinutes(5)));

            Assert.Equal(
                "Refused: conduct-loop.lock held by pid 31337, running, written 5m ago — delete to proceed",
                exception.Message);
            Assert.True(File.Exists(lockPath));
            Assert.Equal(1, probe.IdentityMatchCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact(DisplayName = "ConductLoopLock_legacy_lock_degrades_to_pid_only_liveness")]
    public void LegacyLockDegradesToPidOnlyLiveness()
    {
        var root = CreateTempDirectory();
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(orchestratorDirectory);
            var lockPath = Path.Combine(orchestratorDirectory, "conduct-loop.lock");
            var writtenAt = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            File.WriteAllLines(
                lockPath,
                ["31337", writtenAt.ToString("O", CultureInfo.InvariantCulture)]);
            var probe = new StubPidProbe(isRunning: true, isSameProcess: false);

            Assert.Throws<InvalidOperationException>(() =>
                ConductorLoopLease.Acquire(orchestratorDirectory, probe, () => writtenAt));

            Assert.Equal(0, probe.IdentityMatchCount);
            Assert.True(File.Exists(lockPath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory(DisplayName = "ConductLoopLock_is_active_revalidates_recorded_identity")]
    [InlineData(true)]
    [InlineData(false)]
    public void IsActiveRevalidatesRecordedIdentity(bool isSameProcess)
    {
        var root = CreateTempDirectory();
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(orchestratorDirectory);
            var lockPath = Path.Combine(orchestratorDirectory, "conduct-loop.lock");
            var writtenAt = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            var recordedProcessStartedAt = writtenAt.AddHours(-1);
            File.WriteAllLines(
                lockPath,
                [
                    "31337",
                    writtenAt.ToString("O", CultureInfo.InvariantCulture),
                    recordedProcessStartedAt.ToString("O", CultureInfo.InvariantCulture)
                ]);
            var probe = new StubPidProbe(isRunning: true, isSameProcess);

            Assert.Equal(isSameProcess, ConductorLoopLease.IsActive(orchestratorDirectory, probe));
            Assert.Equal(1, probe.IdentityMatchCount);
            Assert.Equal(recordedProcessStartedAt, probe.LastExpectedStartTime);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory(DisplayName = "ConductLoopLock_named_orderly_exit_releases_lock")]
    [InlineData("all-done-or-escalated", "all-done-or-escalated")]
    [InlineData("max-duration-expiry", "max-duration")]
    [InlineData("stop-sentinel", "stop-file")]
    public void NamedOrderlyExitReleasesLock(string orderlyExit, string loggedReason)
    {
        var root = CreateTempDirectory();
        const string handoffRenewalsVariable = "MCG_ORCHESTRATOR_HANDOFF_RENEWALS";
        var previousHandoffRenewals = Environment.GetEnvironmentVariable(handoffRenewalsVariable);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var lockPath = Path.Combine(workspace.OrchestratorDirectory, "conduct-loop.lock");
            if (orderlyExit == "stop-sentinel")
                File.WriteAllText(Path.Combine(root, ConductorBatchLoop.StopFileName), "stop");
            if (orderlyExit == "max-duration-expiry")
            {
                Environment.SetEnvironmentVariable(
                    handoffRenewalsVariable,
                    ConductorLoopHandoff.DefaultMaxRenewalsWithoutLanding.ToString(CultureInfo.InvariantCulture));
            }

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var workerProfiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var args = orderlyExit == "max-duration-expiry"
                ? new[]
                {
                    "conduct",
                    "--loop",
                    "--max-duration",
                    "0"
                }
                : ["conduct", "--loop"];

            var output = AsyncLocalConsoleRouter.Capture(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    args,
                    CreateMigratedStateRepository(workspace.SqliteStatePath),
                    workspace,
                    ref agents,
                    providers,
                    ref workerProfiles,
                    ref currentGoal));

            Assert.Contains($"reason={loggedReason}", output, StringComparison.Ordinal);
            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(handoffRenewalsVariable, previousHandoffRenewals);
            TryDeleteDirectory(root);
        }
    }

    [Fact(DisplayName = "ConductLoopLock_release_and_reacquire_preserve_clean_state_transitions")]
    public void ReleaseAndReacquirePreserveCleanStateTransitions()
    {
        var root = CreateTempDirectory();
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            var lockPath = Path.Combine(orchestratorDirectory, "conduct-loop.lock");
            using var controller = ConductorLoopLeaseController.Acquire(orchestratorDirectory);
            Assert.True(File.Exists(lockPath));

            controller.Release();
            Assert.False(File.Exists(lockPath));

            controller.Reacquire();
            Assert.True(File.Exists(lockPath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private sealed class StubPidProbe(bool isRunning, bool isSameProcess = true) : IConductLockPidProbe
    {
        public int IdentityMatchCount { get; private set; }

        public DateTimeOffset? LastExpectedStartTime { get; private set; }

        public bool IsRunning(int processId) => isRunning;

        public bool IsSameProcess(int processId, DateTimeOffset processStartedAt)
        {
            IdentityMatchCount++;
            LastExpectedStartTime = processStartedAt;
            return isSameProcess;
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-conduct-loop-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string[] ReadLockLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
