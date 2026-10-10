using System.Diagnostics;
using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalRefinementClaimMissDiagnostic
{
    public static string Describe(IOrchestratorStateOutboxRepository repository, string messageId) =>
        Describe(repository, messageId, () => DateTimeOffset.UtcNow, CurrentProcessStartedAtUtc());

    internal static string Describe(
        IOrchestratorStateOutboxRepository repository,
        string messageId,
        Func<DateTimeOffset> utcNow,
        DateTimeOffset? processStartedAt)
    {
        var observedAt = utcNow();
        OrchestratorStateOutboxState? state;
        try
        {
            state = repository.GetOutboxStateAsync(messageId).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            return Format("unreadable", null, $"{exception.GetType().Name}: {exception.Message}",
                observedAt, processStartedAt);
        }

        var rowState = state?.Status switch
        {
            null => "absent",
            OrchestratorStateOutboxStatus.Pending => "pending",
            OrchestratorStateOutboxStatus.Processing => "processing",
            OrchestratorStateOutboxStatus.Failed => "failed",
            OrchestratorStateOutboxStatus.Quarantined => "quarantined",
            _ => throw new InvalidOperationException($"Unknown outbox row status: {state!.Status}")
        };
        return Format(rowState, state, state?.Detail, observedAt, processStartedAt);
    }

    private static string Format(
        string rowState,
        OrchestratorStateOutboxState? state,
        string? detail,
        DateTimeOffset observedAt,
        DateTimeOffset? processStartedAt)
    {
        var leaseAge = state?.ProcessingStartedAt is { } leaseStartedAt
            ? Math.Floor((observedAt - leaseStartedAt).TotalSeconds).ToString(CultureInfo.InvariantCulture)
            : "none";
        return $"row_state={rowState} row_created_at={Timestamp(state?.Message.CreatedAt)} " +
               $"lease_started_at={Timestamp(state?.ProcessingStartedAt)} lease_age_seconds={leaseAge} " +
               $"row_detail=\"{SingleLine(detail ?? "none")}\" observed_at={Timestamp(observedAt)} " +
               $"process_started_at={Timestamp(processStartedAt, "unknown")}";
    }

    private static string Timestamp(DateTimeOffset? value, string missing = "none") =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? missing;

    private static string SingleLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace('"', '\'');

    private static DateTimeOffset? CurrentProcessStartedAtUtc()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
