using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ConductorGenerationTermination(int ProcessId, DateTimeOffset? ProcessStartedAt,
    DateTimeOffset TerminatedAt, string Cause, string? Reason);

public sealed class ConductorGenerationInterruptionEvidence
{
    private readonly Func<IReadOnlyList<ConductorGenerationTermination>> _readTerminations;
    private readonly Func<string, int, DateTimeOffset, SpawnProcessIdentity?> _readWorkerOwner;
    private readonly Func<TaskProcessRecord, DispatchHeartbeatStatus> _readHeartbeat;

    public ConductorGenerationInterruptionEvidence(IClock clock,
        Func<IReadOnlyList<ConductorGenerationTermination>>? readTerminations = null,
        Func<string, int, DateTimeOffset, SpawnProcessIdentity?>? readWorkerOwner = null,
        Func<TaskProcessRecord, DispatchHeartbeatStatus>? readHeartbeat = null)
    {
        _readTerminations = readTerminations ?? WorkerProcessJobs.ReadConductorGenerationTerminations;
        _readWorkerOwner = readWorkerOwner ?? WorkerProcessJobs.TryReadOwnerProcessIdentity;
        _readHeartbeat = readHeartbeat ?? (process => ProcessLogReader.ReadHeartbeat(process, clock.UtcNow));
    }

    public bool TryProve(GoalId goalId, TaskSpec task, TaskProcessRecord process, out string receipt)
    {
        receipt = string.Empty;
        try
        {
            var owner = _readWorkerOwner($"{goalId.Value}:{task.Id.Value}", process.ProcessId, process.StartedAt);
            if (owner is null) return false;
            var termination = _readTerminations()
                .Where(item => item.ProcessId == owner.ProcessId &&
                    (item.ProcessStartedAt is null || (item.ProcessStartedAt.Value - owner.StartedAt).Duration() <= TimeSpan.FromSeconds(1)) &&
                    item.TerminatedAt > process.StartedAt && item.TerminatedAt > owner.StartedAt)
                .OrderBy(item => item.TerminatedAt).FirstOrDefault();
            if (termination is null) return false;
            var heartbeat = _readHeartbeat(process);
            if (!heartbeat.IsAvailable || heartbeat.LastObservedAt is not { } observed ||
                observed < termination.TerminatedAt.AddMinutes(-2) || observed > termination.TerminatedAt.AddSeconds(30)) return false;
            receipt = $"CONDUCTOR_GENERATION_INTERRUPTED owner_pid={owner.ProcessId} owner_started_at={Utc(owner.StartedAt)} " +
                $"terminated_at={Utc(termination.TerminatedAt)} cause={termination.Cause} heartbeat_at={Utc(observed)} " +
                $"dispatch_started_at={Utc(process.StartedAt)}";
            return true;
        }
        catch { return false; }
    }

    internal static IReadOnlyList<ConductorGenerationTermination> ReadTerminations(string? registryDbPath)
    {
        try
        {
            if (registryDbPath is null) return [];
            var path = Path.Combine(Path.GetDirectoryName(registryDbPath)!, "run-events.db");
            if (!File.Exists(path)) return [];
            var store = new SqliteRunEventStore(path, ensureSchema: false);
            var terminations = new List<ConductorGenerationTermination>();
            long sequence = 0;
            while (true)
            {
                var page = store.ReadByTypeSinceAsync(RunEventTypes.ConductorSupervision,
                    "generation-terminated", afterSequence: sequence).GetAwaiter().GetResult();
                if (page.Count == 0) return terminations;
                foreach (var item in page)
                {
                    using var document = JsonDocument.Parse(item.PayloadJson!);
                    var payload = document.RootElement;
                    terminations.Add(new(payload.GetProperty("processId").GetInt32(),
                        payload.GetProperty("processStartedAt").ValueKind == JsonValueKind.Null ? null : payload.GetProperty("processStartedAt").GetDateTimeOffset(),
                        payload.GetProperty("terminatedAt").GetDateTimeOffset(), payload.GetProperty("cause").GetString()!,
                        payload.GetProperty("reason").GetString()));
                }
                sequence = page[^1].Sequence;
            }
        }
        catch { return []; }
    }

    internal DispatchRefreshOutcome PreserveInterruptedWork(GoalId goalId, TaskSpec task, TaskProcessRecord process,
        Action<string> writeExit, Func<DispatchRecoveryDecision, string, DispatchRefreshOutcome> complete)
    {
        var decision = new DispatchRecoveryDecision(DispatchRecoveryAction.PreserveInterruptedWork,
            DispatchRecoveryPolicy.ToActionName(DispatchRecoveryAction.PreserveInterruptedWork), process.ExitCodePath,
            "process disappeared with dirty worktree evidence", "interrupted worker evidence requires operator verification");
        writeExit("process missing with dirty worktree evidence");
        var diagnostic = DispatchProcessRecoveryService.BuildRecoveryDiagnostic(decision);
        var proof = TryProve(goalId, task, process, out var receipt)
            ? DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.ConductorGenerationInterrupted) + Environment.NewLine + receipt
            : null;
        var outcome = complete(decision, proof is null ? diagnostic : diagnostic + Environment.NewLine + proof);
        if (proof is not null && outcome.Verification is { } verification)
        {
            outcome = outcome with { Verification = verification with
            {
                AuthoritativeStandardError = (verification.AuthoritativeStandardError ?? verification.StandardError) + Environment.NewLine + proof
            } };
        }
        return outcome.AutoRequeueDisposition is not null ? outcome : outcome with
        {
            AutoRequeueDisposition = new DispatchAutoRequeueDisposition("InterruptedDispatchWorkPreserved", diagnostic, ShouldRequeue: false)
        };
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
