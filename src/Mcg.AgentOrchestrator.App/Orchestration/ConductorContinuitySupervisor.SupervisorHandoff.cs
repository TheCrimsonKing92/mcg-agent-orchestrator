using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorContinuitySupervisor
{
    private ConductorActivationBuild? _adoptedSupervisorTarget;
    private int _consecutiveSupervisorHandoffFailures;

    private async Task<ConductorSupervisorHandoffRecord?> BeginSupervisionAsync(
        CancellationToken cancellationToken)
    {
        if (supervisorHandoff is null) return null;
        var seam = supervisorHandoff.Seam;
        var inboundPath = seam.IncomingRecordPath;
        if (string.IsNullOrWhiteSpace(inboundPath))
        {
            seam.Acquire(supervisorHandoff.OwnBuild);
            RecordSupervisorBuild("start");
            return null;
        }

        ConductorSupervisorHandoffRecord record;
        try
        {
            record = seam.ReadRecord(inboundPath);
            if (!SameRunDirectory(record.AdoptedBuild.RunDirectory, supervisorHandoff.OwnBuild.RunDirectory))
                throw new InvalidDataException("Inbound handoff targets a different run directory.");
            if (string.IsNullOrWhiteSpace(record.Token) ||
                record.AdoptedBuild.CommitSha != supervisorHandoff.OwnBuild.CommitSha ||
                !seam.IsAlive(record.Incumbent) || !seam.IsOwner(record.Incumbent))
                throw new InvalidDataException("Inbound handoff does not identify the current incumbent and build.");
            cancellationToken.ThrowIfCancellationRequested();
            seam.WriteReady(record.ReadyPath,
                new ConductorSupervisorReadyRecord(record.Token, seam.Self, supervisorHandoff.OwnBuild));
            var deadline = _timeProvider.GetUtcNow() + supervisorHandoff.Timeout;
            while (!seam.IsOwner(seam.Self))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seam.IsAlive(record.Incumbent))
                    throw new InvalidOperationException($"incumbent-exited pid={record.Incumbent.ProcessId}");
                if (_timeProvider.GetUtcNow() >= deadline)
                    throw new TimeoutException($"deadline timeoutSeconds={supervisorHandoff.Timeout.TotalSeconds}");
                await _delay(supervisorHandoff.PollDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordSupervisorEvent("supervisor-handoff", "abandoned", "SUPERVISOR_HANDOFF_ABANDONED",
                new { reason = $"{ex.GetType().Name}:{ex.Message}", inboundPath });
            throw;
        }

        RebindProtectedIdentity(seam.Self);
        RecordSupervisorBuild("handoff");
        return record;
    }

    private void NoteAdoptedBuild(ConductorActivationBuild build)
    {
        if (supervisorHandoff is null) return;
        _adoptedSupervisorTarget = SameRunDirectory(build.StagedBuildId, supervisorHandoff.OwnBuild.RunDirectory)
            ? null : build;
    }

    private async Task<bool> TryHandOffSupervisionAsync(
        IReadOnlyList<string> args, string workingDirectory, string outputDirectory,
        ConductorPreparedSuccessor? pendingSuccessor, int renewalsWithoutProgress,
        string? blockedActivationCommit, CancellationToken cancellationToken,
        ConductorSupervisorActiveChild? activeChild = null)
    {
        var target = _adoptedSupervisorTarget;
        _adoptedSupervisorTarget = null;
        if (supervisorHandoff is null || target is null) return false;

        var seam = supervisorHandoff.Seam;
        var targetBuild = new ConductorSupervisorBuildIdentity(target.CommitSha, target.StagedBuildId);
        var token = Guid.NewGuid().ToString("N");
        var readyPath = Path.Combine(outputDirectory, $"supervisor-ready-{token}.json");
        int launchedPid = 0;
        ConductorSupervisorProcessIdentity? launchedIdentity = null;
        var phase = "record";
        try
        {
            var appDll = target.CommandPrefix is { Count: > 1 }
                ? target.CommandPrefix[1]
                : Path.Combine(target.StagedBuildId, "Mcg.AgentOrchestrator.App.dll");
            var pendingSnapshot = pendingSuccessor is null ? null : new ConductorSupervisorPendingSnapshot(
                pendingSuccessor.RunDirectory, pendingSuccessor.AppDllPath,
                pendingSuccessor.RepositoryHead, pendingSuccessor.StagedSourceCommit,
                pendingSuccessor.SelfCheckDetail);
            var record = new ConductorSupervisorHandoffRecord(token, seam.Self, supervisorHandoff.OwnBuild,
                new ConductorSupervisorBuildSnapshot(target.CommitSha, target.StagedBuildId, appDll),
                pendingSnapshot, renewalsWithoutProgress, blockedActivationCommit,
                0, readyPath, activeChild);
            var recordPath = seam.WriteRecord(record);
            var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            var request = new ConductLoopLaunchRequest("supervisor", args,
                Path.Combine(outputDirectory, $"supervisor-handoff-{stamp}-{token}.out.log"),
                Path.Combine(outputDirectory, $"supervisor-handoff-{stamp}-{token}.err.log"),
                workingDirectory, renewalsWithoutProgress,
                CommandPrefix: [_dotnetPath, appDll],
                AdditionalEnvironment: new Dictionary<string, string>
                {
                    [SystemConductorSupervisorHandoffSeam.InboundEnvironmentVariable] = recordPath
                });
            phase = "launch";
            var launched = seam.Launch(request);
            launchedPid = launched.ProcessId;
            launchedIdentity = launched.SuccessorIdentity;
            RequireSupervisorHandoff(launched.StartedAt is not null,
                new InvalidDataException("Launched supervisor identity has no start time."));
            phase = "readiness";
            var deadline = _timeProvider.GetUtcNow() + supervisorHandoff.Timeout;
            ConductorSupervisorReadyRecord? ready;
            while ((ready = seam.ReadReady(readyPath)) is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireSupervisorHandoff(seam.IsAlive(launchedIdentity),
                    new InvalidOperationException("successor-exited"));
                RequireSupervisorHandoff(_timeProvider.GetUtcNow() < deadline,
                    new TimeoutException("readiness-timeout"));
                await _delay(supervisorHandoff.PollDelay, cancellationToken).ConfigureAwait(false);
            }
            phase = "identity";
            RequireSupervisorHandoff(ready.Token == token && ready.Successor == launchedIdentity &&
                ready.Build.CommitSha == targetBuild.CommitSha &&
                SameRunDirectory(ready.Build.RunDirectory, target.StagedBuildId) &&
                seam.IsAlive(ready.Successor),
                new InvalidDataException("Successor readiness identity did not match the launched build and pid."));

            cancellationToken.ThrowIfCancellationRequested();
            phase = "transfer";
            seam.Transfer(seam.Self, launchedIdentity, targetBuild);
            phase = "post-transfer";
            if (!seam.IsAlive(ready.Successor))
            {
                seam.Transfer(ready.Successor, seam.Self, supervisorHandoff.OwnBuild);
                RequireSupervisorHandoff(false, new InvalidOperationException("successor-exited-after-transfer"));
            }
            RecordSupervisorEvent("supervisor-handoff", "completed", "SUPERVISOR_HANDOFF",
                new { fromCommit = supervisorHandoff.OwnBuild.CommitSha,
                    fromRunDir = supervisorHandoff.OwnBuild.RunDirectory,
                    toCommit = target.CommitSha, toRunDir = target.StagedBuildId,
                    successorPid = launchedPid, successorStartedAt = launchedIdentity.StartedAt,
                    childPid = activeChild?.Process.ProcessId,
                    childStartedAt = activeChild?.Process.StartedAt,
                    phase, leaseOwner = "successor" });
            _consecutiveSupervisorHandoffFailures = 0;
            return true;
        }
        catch (Exception ex)
        {
            var ownsSelf = SafelyCheck(() => seam.IsOwner(seam.Self));
            if (!ownsSelf && launchedIdentity is not null &&
                SafelyCheck(() => seam.IsOwner(launchedIdentity) && seam.IsAlive(launchedIdentity)))
            {
                RecordSupervisorEvent("supervisor-handoff", "completed", "SUPERVISOR_HANDOFF",
                    new { fromCommit = supervisorHandoff.OwnBuild.CommitSha,
                        fromRunDir = supervisorHandoff.OwnBuild.RunDirectory,
                        toCommit = target.CommitSha, toRunDir = target.StagedBuildId,
                        successorPid = launchedPid, successorStartedAt = launchedIdentity.StartedAt,
                        childPid = activeChild?.Process.ProcessId, childStartedAt = activeChild?.Process.StartedAt,
                        phase, leaseOwner = "successor", recoveredFrom = ex.GetType().Name });
                _consecutiveSupervisorHandoffFailures = 0;
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                return true;
            }
            var leaseOwner = ownsSelf ? "self" : "unknown";
            if (launchedIdentity is not null)
            {
                try { seam.StopPending(launchedIdentity); }
                catch (Exception stopEx)
                {
                    RecordSupervisorEvent("supervisor-handoff", "failed", "SUPERVISOR_HANDOFF_FAILED",
                        new { reason = $"stop-pending-failed:{stopEx.Message}", launchedPid,
                            fromCommit = supervisorHandoff.OwnBuild.CommitSha,
                            toCommit = target.CommitSha, phase, exceptionType = ex.GetType().Name,
                            message = ex.Message, hresult = ex.HResult, leaseOwner });
                }
            }
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw;
            _consecutiveSupervisorHandoffFailures++;
            RecordSupervisorEvent("supervisor-handoff", "failed", "SUPERVISOR_HANDOFF_FAILED",
                new { reason = $"{ex.GetType().Name}:{ex.Message}",
                    fromCommit = supervisorHandoff.OwnBuild.CommitSha,
                    fromRunDir = supervisorHandoff.OwnBuild.RunDirectory,
                    toCommit = target.CommitSha, toRunDir = target.StagedBuildId,
                    consecutiveFailures = _consecutiveSupervisorHandoffFailures,
                    phase, exceptionType = ex.GetType().Name, message = ex.Message,
                    hresult = ex.HResult, leaseOwner });
            if (_consecutiveSupervisorHandoffFailures == 3)
                RecordSupervisorEvent("supervisor-handoff", "escalated", "SUPERVISOR_HANDOFF_ESCALATED",
                    new { consecutiveFailures = _consecutiveSupervisorHandoffFailures,
                        fromCommit = supervisorHandoff.OwnBuild.CommitSha,
                        toCommit = target.CommitSha });
            return false;
        }
    }

    private static void RequireSupervisorHandoff(bool condition, Exception error)
    {
        if (!condition) throw error;
    }

    private static bool SafelyCheck(Func<bool> check)
    {
        try { return check(); }
        catch { return false; }
    }

    private void RecordSupervisorBuild(string reason)
    {
        if (supervisorHandoff is null) return;
        var job = ProcessJobTrapProbe.ReadForSupervisor();
        RecordSupervisorEvent("supervisor-build", "active", "SUPERVISOR_BUILD",
            new { reason, commitSha = supervisorHandoff.OwnBuild.CommitSha,
                runDir = supervisorHandoff.OwnBuild.RunDirectory,
                pid = supervisorHandoff.Seam.Self.ProcessId,
                jobMembership = ProcessJobTrapProbe.FormatMembership(job),
                innermostJobLimitFlags = job.LimitFlags is { } flags ? ProcessJobTrapProbe.FormatFlags(flags) : null });
    }

    private void RecordSupervisorHandoffPreparationFailure(ConductorActivationBuild target, Exception error)
    {
        if (supervisorHandoff is null) return;
        _adoptedSupervisorTarget = null;
        _consecutiveSupervisorHandoffFailures++;
        RecordSupervisorEvent("supervisor-handoff", "failed", "SUPERVISOR_HANDOFF_FAILED",
            new { reason = $"child-identity:{error.GetType().Name}:{error.Message}",
                fromCommit = supervisorHandoff.OwnBuild.CommitSha,
                fromRunDir = supervisorHandoff.OwnBuild.RunDirectory,
                toCommit = target.CommitSha, toRunDir = target.StagedBuildId,
                consecutiveFailures = _consecutiveSupervisorHandoffFailures });
        if (_consecutiveSupervisorHandoffFailures == 3)
            RecordSupervisorEvent("supervisor-handoff", "escalated", "SUPERVISOR_HANDOFF_ESCALATED",
                new { consecutiveFailures = _consecutiveSupervisorHandoffFailures,
                    fromCommit = supervisorHandoff.OwnBuild.CommitSha,
                    toCommit = target.CommitSha });
    }

    private void RecordSupervisorEvent(string operation, string status, string eventName, object payload)
    {
        var now = _timeProvider.GetUtcNow();
        var json = JsonSerializer.Serialize(payload);
        var detail = $"{eventName} {json}";
        try { appendConductEvent?.Invoke(operation, null, detail); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conduct supervisor] Could not append {eventName}: {ex.Message}");
        }
        try
        {
            eventStore.AppendAsync(new RunEventAppend(RunEventTypes.ConductorSupervision,
                null, operation, status, detail, json, now)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conduct supervisor] Could not record {eventName}: {ex.Message}");
        }
    }

    private static bool SameRunDirectory(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
