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
            seam.WriteReady(record.ReadyPath,
                new ConductorSupervisorReadyRecord(record.Token, seam.Self, supervisorHandoff.OwnBuild));
            var deadline = _timeProvider.GetUtcNow() + supervisorHandoff.Timeout;
            while (!seam.IsOwner(seam.Self))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seam.IsAlive(record.Incumbent) || _timeProvider.GetUtcNow() >= deadline)
                    throw new InvalidOperationException("Supervisor lease was not transferred before the handoff deadline.");
                await _delay(supervisorHandoff.PollDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordSupervisorEvent("supervisor-handoff", "abandoned", "SUPERVISOR_HANDOFF_ABANDONED",
                new { reason = $"{ex.GetType().Name}:{ex.Message}", inboundPath });
            throw;
        }

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
        string? blockedActivationCommit, CancellationToken cancellationToken)
    {
        var target = _adoptedSupervisorTarget;
        _adoptedSupervisorTarget = null;
        if (supervisorHandoff is null || target is null) return false;

        var seam = supervisorHandoff.Seam;
        var targetBuild = new ConductorSupervisorBuildIdentity(target.CommitSha, target.StagedBuildId);
        var token = Guid.NewGuid().ToString("N");
        var readyPath = Path.Combine(outputDirectory, $"supervisor-ready-{token}.json");
        int launchedPid = 0;
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
                0, readyPath);
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
            launchedPid = seam.Launch(request).ProcessId;
            var deadline = _timeProvider.GetUtcNow() + supervisorHandoff.Timeout;
            ConductorSupervisorReadyRecord? ready;
            while ((ready = seam.ReadReady(readyPath)) is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seam.IsRunning(launchedPid))
                    throw new InvalidOperationException("successor-exited");
                if (_timeProvider.GetUtcNow() >= deadline)
                    throw new TimeoutException("readiness-timeout");
                await _delay(supervisorHandoff.PollDelay, cancellationToken).ConfigureAwait(false);
            }
            if (ready.Token != token || ready.Successor.ProcessId != launchedPid ||
                !SameRunDirectory(ready.Build.RunDirectory, target.StagedBuildId) ||
                !seam.IsAlive(ready.Successor))
                throw new InvalidDataException("Successor readiness identity did not match the launched build and pid.");

            seam.Transfer(seam.Self, ready.Successor, targetBuild);
            if (!seam.IsAlive(ready.Successor))
            {
                seam.Transfer(ready.Successor, seam.Self, supervisorHandoff.OwnBuild);
                throw new InvalidOperationException("successor-exited-after-transfer");
            }
            RecordSupervisorEvent("supervisor-handoff", "completed", "SUPERVISOR_HANDOFF",
                new { fromCommit = supervisorHandoff.OwnBuild.CommitSha,
                    fromRunDir = supervisorHandoff.OwnBuild.RunDirectory,
                    toCommit = target.CommitSha, toRunDir = target.StagedBuildId,
                    successorPid = launchedPid });
            _consecutiveSupervisorHandoffFailures = 0;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!seam.IsOwner(seam.Self))
                throw new InvalidOperationException("Supervisor lease left incumbent during failed handoff.", ex);
            if (launchedPid > 0)
            {
                try { seam.StopPending(launchedPid); }
                catch (Exception stopEx)
                {
                    RecordSupervisorEvent("supervisor-handoff", "failed", "SUPERVISOR_HANDOFF_FAILED",
                        new { reason = $"stop-pending-failed:{stopEx.Message}", launchedPid,
                            fromCommit = supervisorHandoff.OwnBuild.CommitSha,
                            toCommit = target.CommitSha });
                }
            }
            _consecutiveSupervisorHandoffFailures++;
            RecordSupervisorEvent("supervisor-handoff", "failed", "SUPERVISOR_HANDOFF_FAILED",
                new { reason = $"{ex.GetType().Name}:{ex.Message}",
                    fromCommit = supervisorHandoff.OwnBuild.CommitSha,
                    fromRunDir = supervisorHandoff.OwnBuild.RunDirectory,
                    toCommit = target.CommitSha, toRunDir = target.StagedBuildId,
                    consecutiveFailures = _consecutiveSupervisorHandoffFailures });
            if (_consecutiveSupervisorHandoffFailures == 3)
                RecordSupervisorEvent("supervisor-handoff", "escalated", "SUPERVISOR_HANDOFF_ESCALATED",
                    new { consecutiveFailures = _consecutiveSupervisorHandoffFailures,
                        fromCommit = supervisorHandoff.OwnBuild.CommitSha,
                        toCommit = target.CommitSha });
            return false;
        }
    }

    private void RecordSupervisorBuild(string reason)
    {
        if (supervisorHandoff is null) return;
        RecordSupervisorEvent("supervisor-build", "active", "SUPERVISOR_BUILD",
            new { reason, commitSha = supervisorHandoff.OwnBuild.CommitSha,
                runDir = supervisorHandoff.OwnBuild.RunDirectory,
                pid = supervisorHandoff.Seam.Self.ProcessId });
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
