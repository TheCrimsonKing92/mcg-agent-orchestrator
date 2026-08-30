using System.Reflection;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DispatchProcessRecoveryServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-22T12:00:00Z");
    private static readonly DateTimeOffset IdentityStartedAt = DateTimeOffset.Parse("2026-08-22T11:00:00Z");

    [Xunit.Fact]
    public void DiagnosticReconciliationCreatesOneBoundedCommandLineRead()
    {
        var process = ProcessRecord(40, Now.AddMinutes(-1));
        var task = new TaskSpec(TaskId.New(), "Inspect recovery.", AgentRole.Researcher);
        var recordProcess = typeof(TaskSpec).GetMethod("RecordProcess", BindingFlags.Instance | BindingFlags.NonPublic);
        Xunit.Assert.NotNull(recordProcess);
        recordProcess!.Invoke(task, [process]);
        var commandLineReads = 0;
        var writer = new CaptureDiagnosticWriter();
        var service = CreateService(
            new Dictionary<string, string>(StringComparer.Ordinal),
            liveProcessIds: new HashSet<int> { process.ProcessId },
            diagnosticWriter: writer,
            readCommandLines: processIds =>
            {
                commandLineReads++;
                Xunit.Assert.Equal([process.ProcessId], processIds);
                return new Dictionary<int, string> { [process.ProcessId] = process.Command };
            });

        service.TryWriteDiagnosticRecord(
            GoalId.New(),
            task.Id,
            task,
            process,
            exitCode: 0,
            standardOutput: string.Empty,
            standardError: string.Empty);

        Xunit.Assert.Equal(1, commandLineReads);
        Xunit.Assert.NotNull(writer.Record);
    }

    [Xunit.Fact]
    public void ValidExitArtifactReturnsCompletedVerdictWithoutRealFilesOrProcesses()
    {
        var process = ProcessRecord(41, Now.AddMinutes(-5));
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [process.ExitCodePath] = "in-memory exit artifact"
        };
        var service = CreateService(
            artifacts,
            readExitArtifact: _ => new ExitCodeReadResult(
                ExitCodeReadKind.Valid,
                0,
                "origin=Native exit_code=0 reason=completed",
                DispatchExitArtifactOrigin.Native,
                "completed"));

        var verdict = Classify(service, process);

        Xunit.Assert.Equal(DispatchProcessVerdictKind.CompletedFromExitFile, verdict.Kind);
        Xunit.Assert.Equal(0, verdict.ExitCode);
        Xunit.Assert.Equal(
            "Dispatch recovery policy action='mark-stale' evidence='memory/job.exit.txt' reason='test recovery decision'.",
            verdict.Diagnostic);
    }

    [Xunit.Fact]
    public void ValidExitArtifactWithOnlyIdentityLessHeartbeatPidReturnsCompletedVerdict()
    {
        var process = ProcessRecord(410, Now.AddMinutes(-5));
        var unrelatedProcessId = 999;
        var heartbeatPath = DispatchProcessRecoveryService.GetHeartbeatPath(process);
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [process.ExitCodePath] = "in-memory native exit artifact",
            [heartbeatPath] = HeartbeatJson(
                unrelatedProcessId,
                childProcessId: unrelatedProcessId,
                lastObservedAt: Now,
                lastProgressAt: Now,
                ownedCpuMs: 10,
                stdoutBytes: 100,
                stderrBytes: 0,
                includeIdentity: false)
        };
        var killedProcessIds = new List<int>();
        var service = CreateService(
            artifacts,
            liveProcessIds: new HashSet<int> { unrelatedProcessId },
            killedProcessIds: killedProcessIds,
            readExitArtifact: _ => new ExitCodeReadResult(
                ExitCodeReadKind.Valid,
                0,
                "origin=Native exit_code=0 reason=completed",
                DispatchExitArtifactOrigin.Native,
                "completed"));

        var verdict = Classify(service, process);

        Xunit.Assert.Equal(DispatchProcessVerdictKind.CompletedFromExitFile, verdict.Kind);
        Xunit.Assert.Equal(0, verdict.ExitCode);
        Xunit.Assert.Empty(killedProcessIds);
    }

    [Xunit.Fact]
    public void LiveTrackedProcessWithoutHeartbeatIdentityStillHoldsNonterminalRecovery()
    {
        var process = ProcessRecord(411, Now.AddMinutes(-5));
        var liveProcessIds = new HashSet<int> { process.ProcessId };
        var withoutExit = CreateService(new Dictionary<string, string>(StringComparer.Ordinal), liveProcessIds);

        Xunit.Assert.True(withoutExit.AnyTrackedProcessStillRunning(process));
    }

    [Xunit.Fact]
    public void LiveTrackedProcessWithUnreadableHeartbeatIdentityDoesNotReapNonterminalDispatch()
    {
        var process = ProcessRecord(413, Now.AddMinutes(-5));
        var heartbeatPath = DispatchProcessRecoveryService.GetHeartbeatPath(process);
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [heartbeatPath] = HeartbeatJson(
                process.ProcessId,
                childProcessId: process.ProcessId,
                lastObservedAt: Now,
                lastProgressAt: Now,
                ownedCpuMs: 10,
                stdoutBytes: 100,
                stderrBytes: 0,
                includeIdentity: false)
        };
        var killedProcessIds = new List<int>();
        var service = CreateService(
            artifacts,
            liveProcessIds: new HashSet<int> { process.ProcessId },
            killedProcessIds: killedProcessIds);

        var verdict = Classify(service, process);

        Xunit.Assert.Equal(DispatchProcessVerdictKind.Live, verdict.Kind);
        Xunit.Assert.Empty(killedProcessIds);
    }

    [Xunit.Fact]
    public void ReapingTrackedProcessNeverTargetsHeartbeatOnlyPid()
    {
        var process = ProcessRecord(412, Now.AddMinutes(-5));
        const int unrelatedHeartbeatPid = 999;
        var heartbeatPath = DispatchProcessRecoveryService.GetHeartbeatPath(process);
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [heartbeatPath] = HeartbeatJson(
                unrelatedHeartbeatPid,
                childProcessId: unrelatedHeartbeatPid,
                lastObservedAt: Now,
                lastProgressAt: Now,
                ownedCpuMs: 10,
                stdoutBytes: 100,
                stderrBytes: 0,
                includeIdentity: false)
        };
        var killedProcessIds = new List<int>();
        var service = CreateService(artifacts, killedProcessIds: killedProcessIds);

        service.ReapTrackedProcessJobs(process, waitForExit: false);

        Xunit.Assert.DoesNotContain(unrelatedHeartbeatPid, killedProcessIds);
    }

    [Xunit.Fact]
    public void StalledHeartbeatWithExitedChildReturnsHungWrapperVerdict()
    {
        var process = ProcessRecord(42, Now.AddMinutes(-10));
        var heartbeatPath = DispatchProcessRecoveryService.GetHeartbeatPath(process);
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [heartbeatPath] = HeartbeatJson(
                process.ProcessId,
                childProcessId: null,
                lastObservedAt: Now.AddMinutes(-3),
                lastProgressAt: Now.AddMinutes(-3),
                ownedCpuMs: 2000,
                stdoutBytes: 12,
                stderrBytes: 0)
        };
        var service = CreateService(
            artifacts,
            liveProcessIds: new HashSet<int> { process.ProcessId });

        var verdict = Classify(service, process);

        Xunit.Assert.Equal(DispatchProcessVerdictKind.HungWrapper, verdict.Kind);
        Xunit.Assert.Equal(
            "Background dispatch wrapper appears hung with stalled heartbeat for 00:03:00; no exit file was written. " +
            "Marking dispatch based on role completion evidence.",
            verdict.Diagnostic);
    }

    [Xunit.Fact]
    public void NoToolLaunchSignalsPastTimeoutReturnsSuspectedHangVerdict()
    {
        var process = ProcessRecord(43, Now.AddMinutes(-5));
        var heartbeatPath = DispatchProcessRecoveryService.GetHeartbeatPath(process);
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [heartbeatPath] = HeartbeatJson(
                process.ProcessId,
                childProcessId: null,
                lastObservedAt: Now,
                lastProgressAt: Now,
                ownedCpuMs: 0,
                stdoutBytes: 0,
                stderrBytes: 0)
        };
        var service = CreateService(
            artifacts,
            liveProcessIds: new HashSet<int> { process.ProcessId });

        var verdict = Classify(service, process);

        Xunit.Assert.Equal(DispatchProcessVerdictKind.SuspectedHang, verdict.Kind);
        Xunit.Assert.Equal(DispatchRecoveryAction.Reap, verdict.HangRecoveryAction);
        Xunit.Assert.Equal(
            "Background dispatch never launched its tool process: childPid=null, ownedCpuMs=0, " +
            "stdout_bytes=0, stderr_bytes=0, alive_for=00:05:00, startup_hang_timeout=00:02:00. " +
            "No child process, startup CPU burst, or output since start; process tree killed and dispatch marked failed.",
            verdict.Diagnostic);
    }

    [Xunit.Fact]
    public void DeadProcessWithoutExitArtifactReturnsStaleVerdictAndUsesInjectedKillSeam()
    {
        var process = ProcessRecord(44, Now.AddMinutes(-20));
        var killedProcessIds = new List<int>();
        var service = CreateService(
            new Dictionary<string, string>(StringComparer.Ordinal),
            killedProcessIds: killedProcessIds);

        var verdict = Classify(service, process);

        Xunit.Assert.Equal(DispatchProcessVerdictKind.ProcessGone, verdict.Kind);
        Xunit.Assert.Equal(DispatchRecoveryAction.MarkStale, verdict.RecoveryDecision.Action);
        Xunit.Assert.Equal(new[] { process.ProcessId }, killedProcessIds);
    }

    private static DispatchProcessRefreshVerdict Classify(
        DispatchProcessRecoveryService service,
        TaskProcessRecord process)
    {
        var task = new TaskSpec(TaskId.New(), "Inspect recovery.", AgentRole.Researcher);
        return service.ClassifyRefresh(
            task,
            GoalId.New(),
            process,
            staleRetryBudgetRemaining: 0,
            usesCodexExitFileBehavior: false,
            requiresFileChangeEvidence: false,
            inspectWorktree: () => throw new InvalidOperationException("read-only recovery must not inspect a worktree"),
            hasCodexFinalOutput: () => throw new InvalidOperationException("non-codex recovery must not read worker output"),
            hasWorktreeProgress: () => throw new InvalidOperationException("read-only recovery must not inspect worktree progress"),
            heartbeatObserved: _ => { });
    }

    private static DispatchProcessRecoveryService CreateService(
        IReadOnlyDictionary<string, string> artifacts,
        IReadOnlySet<int>? liveProcessIds = null,
        List<int>? killedProcessIds = null,
        Func<string, ExitCodeReadResult>? readExitArtifact = null,
        IDispatchDiagnosticWriter? diagnosticWriter = null,
        Func<IEnumerable<int>, IReadOnlyDictionary<int, string>>? readCommandLines = null)
    {
        liveProcessIds ??= new HashSet<int>();
        killedProcessIds ??= [];
        return new DispatchProcessRecoveryService(
            clock: new TestClock(Now),
            postOutputIdleTimeout: TimeSpan.FromMinutes(2),
            progressStallTimeout: TimeSpan.FromMinutes(15),
            startupHangTimeout: TimeSpan.FromMinutes(2),
            isStillRunning: liveProcessIds.Contains,
            tryKillOwnedProcess: processId =>
            {
                killedProcessIds.Add(processId);
                return true;
            },
            fileExists: artifacts.ContainsKey,
            openArtifactReadStream: path => new MemoryStream(Encoding.UTF8.GetBytes(artifacts[path])),
            getLastWriteTimeUtc: _ => Now.AddMinutes(-10),
            getFileLength: path => Encoding.UTF8.GetByteCount(artifacts[path]),
            getPeakMemoryBytes: _ => null,
            readExitArtifact: readExitArtifact ?? (_ =>
                new ExitCodeReadResult(ExitCodeReadKind.Missing, null, "file-missing")),
            writeExitArtifact: (_, _, _) => throw new InvalidOperationException("classification must not write an exit artifact"),
            evaluateRecovery: (_, _, _, _) => RecoveryDecision(),
            diagnosticWriter: diagnosticWriter ?? new FileDiagnosticWriter(),
            readProcessIdentity: processId => new SpawnProcessIdentity(processId, IdentityStartedAt, @"C:\workers\worker.exe"),
            readCommandLines: readCommandLines);
    }

    private sealed class CaptureDiagnosticWriter : IDispatchDiagnosticWriter
    {
        internal DispatchDiagnosticRecord? Record { get; private set; }

        public void WriteRecord(DispatchDiagnosticRecord record) => Record = record;
    }

    private static DispatchRecoveryDecision RecoveryDecision() =>
        new(
            DispatchRecoveryAction.MarkStale,
            DispatchRecoveryPolicy.ToActionName(DispatchRecoveryAction.MarkStale),
            "memory/job.exit.txt",
            "test recovery decision");

    private static TaskProcessRecord ProcessRecord(int processId, DateTimeOffset startedAt) =>
        new(
            processId,
            "worker command",
            "memory",
            "memory/job.stdout.log",
            "memory/job.stderr.log",
            "memory/job.exit.txt",
            startedAt,
            null,
            null);

    private static string HeartbeatJson(
        int processId,
        int? childProcessId,
        DateTimeOffset lastObservedAt,
        DateTimeOffset lastProgressAt,
        long ownedCpuMs,
        long stdoutBytes,
        long stderrBytes,
        bool includeIdentity = true) =>
        $$"""
        {
          "pid": {{processId}},
          "childPid": {{(childProcessId is null ? "null" : childProcessId.Value.ToString())}},
          "state": "running",
          "lastObservedAt": "{{lastObservedAt:O}}",
          "lastProgressAt": "{{lastProgressAt:O}}",
          "stdoutBytes": {{stdoutBytes}},
          "stderrBytes": {{stderrBytes}},
          "ownedCpuMs": {{ownedCpuMs}},
          "ownedPids": [{{processId}}],
          "ownedProcessIdentities": {{(includeIdentity
              ? $"[{{\"processId\":{processId},\"startedAt\":\"{IdentityStartedAt:O}\",\"imagePath\":\"C:\\\\workers\\\\worker.exe\"}}]"
              : "[]")}}
        }
        """;

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
