using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorHostHealthMonitor(
    string ledgerPath, string statePath, ConductEventLogWriter writer, Action? afterEventAppend = null)
{
    internal const string EventKind = "host-health";
    internal const string StateFileName = "host-health-state.json";
    internal const string DegradedEvent = "HOST_HEALTH_DEGRADED";
    internal const string RecoveredEvent = "HOST_HEALTH_RECOVERED";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal void Evaluate()
    {
        try
        {
            // The persisted state and pending transition have one cross-process owner even during handoff.
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                Path.GetFullPath(statePath).ToUpperInvariant())));
            using var mutex = new Mutex(false, "McgHostHealth-" + key);
            try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
            try { EvaluateUnderLock(); }
            finally { mutex.ReleaseMutex(); }
        }
        catch
        {
            // Host-health observation is advisory; it cannot stop conductor work.
        }
    }

    private void EvaluateUnderLock()
    {
        var state = File.Exists(statePath)
            ? JsonSerializer.Deserialize<MonitorState>(File.ReadAllText(statePath), JsonOptions)
                ?? throw new InvalidDataException("Host-health state is empty.")
            : new MonitorState(false);
        if (state.Pending is { } pending)
        {
            if (!Publish(pending)) return;
            state = new MonitorState(pending.IsDegraded);
            Save(state);
        }

        // Missing/unreadable evidence is not proof of recovery.
        if (!File.Exists(ledgerPath)) return;
        var records = GateHostHealthLedger.ReadRecent(ledgerPath);
        if (records.Count == 0) return;
        var assessment = GateHostHealthEvaluator.Evaluate(records);
        if (assessment.IsDegraded == state.IsDegraded) return;
        var transition = new PendingTransition(Guid.NewGuid().ToString("N"), assessment.IsDegraded,
            FormatDetail(assessment), DateTimeOffset.UtcNow);
        Save(state with { Pending = transition });
        if (Publish(transition)) Save(new MonitorState(assessment.IsDegraded));
    }

    private bool Publish(PendingTransition transition)
    {
        if (!writer.AppendRequired(EventKind, null, transition.Detail, transition.ObservedAt,
            eventId: "host-health:" + transition.Id)) return false;
        afterEventAppend?.Invoke();
        return true;
    }

    private void Save(MonitorState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        var temporaryPath = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string FormatDetail(HostHealthAssessment assessment)
    {
        static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable";
        return $"{(assessment.IsDegraded ? DegradedEvent : RecoveredEvent)} " +
            $"launch_ms={Number(assessment.LatestLaunchMs)} baseline_ms={Number(assessment.BaselineMs)} " +
            $"ratio={Number(assessment.Ratio)} paged_pool_mb={Number(assessment.LatestPagedPoolMb)} " +
            $"consecutive={assessment.ConsecutiveCount}";
    }

    private sealed record MonitorState(bool IsDegraded, PendingTransition? Pending = null);
    private sealed record PendingTransition(string Id, bool IsDegraded, string Detail, DateTimeOffset ObservedAt);
}
