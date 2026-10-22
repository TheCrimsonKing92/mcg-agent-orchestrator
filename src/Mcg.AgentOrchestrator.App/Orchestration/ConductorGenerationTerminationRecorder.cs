using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorGenerationTerminationRecorder
{
    internal static void Record(IRunEventStore store, TimeProvider time,
        ConductorSupervisorProcessResult result, string cause, string? reason, string commitSha)
    {
        if (!result.ForcedStop || !result.TerminationConfirmed || result.ProcessId <= 0) return;
        var terminatedAt = time.GetUtcNow().ToUniversalTime();
        var startedAt = result.ProcessStartedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var endedAt = terminatedAt.ToString("O", CultureInfo.InvariantCulture);
        var detail = $"CONDUCTOR_GENERATION_TERMINATED pid={result.ProcessId} startedAt={startedAt ?? "unknown"} " +
            $"terminatedAt={endedAt} cause={cause} reason={reason ?? "none"} commit={commitSha}";
        try
        {
            store.AppendAsync(new RunEventAppend(RunEventTypes.ConductorSupervision, null,
                "generation-terminated", "forced-stop", detail, JsonSerializer.Serialize(new
                {
                    processId = result.ProcessId,
                    processStartedAt = startedAt,
                    terminatedAt = endedAt,
                    cause,
                    reason,
                    commitSha
                }), terminatedAt)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conduct supervisor] Could not record generation termination: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
