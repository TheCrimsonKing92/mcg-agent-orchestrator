using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorHostHealthMonitor(
    string ledgerPath, string statePath, ConductEventLogWriter writer, Action? afterEventAppend = null,
    ConductorHostHealthSignals? signals = null)
{
    internal const string EventKind = "host-health";
    internal const string StateFileName = "host-health-state.json";
    internal const string DegradedEvent = "HOST_HEALTH_DEGRADED";
    internal const string RecoveredEvent = "HOST_HEALTH_RECOVERED";
    internal const string ForegroundLockArmedEvent = "HOST_HEALTH_FOREGROUND_LOCK_ARMED";
    internal const string ForegroundLockDisarmedEvent = "HOST_HEALTH_FOREGROUND_LOCK_DISARMED";
    internal const string PagedPoolHighEvent = "HOST_HEALTH_PAGED_POOL_HIGH";
    internal const string PagedPoolNormalEvent = "HOST_HEALTH_PAGED_POOL_NORMAL";
    internal const string WorkerSandboxOffEvent = "HOST_HEALTH_WORKER_SANDBOX_OFF";
    internal const string WorkerSandboxOnEvent = "HOST_HEALTH_WORKER_SANDBOX_ON";
    internal const string RepositoryLowWritableEvent = "HOST_HEALTH_REPOSITORY_LOW_WRITABLE";
    internal const string RepositoryLowWritableClearedEvent = "HOST_HEALTH_REPOSITORY_LOW_WRITABLE_CLEARED";
    private const string ForegroundLockRemedy =
        ".orchestrator/operator-tools/Set-ForegroundLockTimeout.ps1 (run from a focused console)";
    private const int RequiredBuild = 26200;
    private const double PagedPoolHighWaterMb = 8192;
    private const int PagedPoolWindow = 5;
    private const double PagedPoolRiseMb = 1024;
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
            var committed = Commit(state, pending);
            if (!Publish(pending)) return;
            state = committed;
            Save(state);
        }

        // Missing/unreadable evidence is not proof of recovery.
        var records = File.Exists(ledgerPath) ? GateHostHealthLedger.ReadRecent(ledgerPath) : [];
        if (records.Count > 0)
        {
            var assessment = GateHostHealthEvaluator.Evaluate(records);
            if (assessment.IsDegraded != state.IsDegraded &&
                !Transition(ref state, "latency", assessment.IsDegraded, FormatDetail(assessment))) return;
        }

        // Legacy callers remain latency-only; the conductor opts into the additional observations.
        if (signals is null) return;
        if (!EvaluatePagedPool(ref state, records)) return;
        if (!EvaluateForegroundLock(ref state)) return;
        if (!EvaluateWorkerSandbox(ref state)) return;
        EvaluateRepositoryIntegrity(ref state);
    }

    private bool EvaluateForegroundLock(ref MonitorState state)
    {
        var reading = signals!.ForegroundLock.Read();
        if (!reading.IsAvailable || reading.Build != RequiredBuild) return true;
        var armed = reading.TimeoutMs != 0;
        if (armed == state.ForegroundLockArmed) return true;
        var detail = armed
            ? FormattableString.Invariant($"{ForegroundLockArmedEvent} timeout_ms={reading.TimeoutMs} build={reading.Build} remedy={ForegroundLockRemedy}")
            : FormattableString.Invariant($"{ForegroundLockDisarmedEvent} timeout_ms=0 build={reading.Build}");
        return Transition(ref state, "foreground-lock", armed, detail);
    }

    private bool EvaluateWorkerSandbox(ref MonitorState state)
    {
        if (signals!.WorkerSandbox is not { } reader) return true;
        var reading = reader.Read();
        if (!reading.IsAvailable) return true;
        var off = reading.Enabled == false;
        if (off == state.WorkerSandboxOff) return true;
        var detail = off
            ? $"{WorkerSandboxOffEvent} variable={WorkerSandboxOptions.EnabledVariable} value={reading.RawValue ?? "unset"} " +
                "remedy=set MCG_WORKER_SANDBOX=1 at User scope, then restart the conductor"
            : WorkerSandboxOnEvent;
        return Transition(ref state, "worker-sandbox", off, detail);
    }

    private bool EvaluateRepositoryIntegrity(ref MonitorState state)
    {
        if (signals!.RepositoryIntegrity is not { } probe) return true;
        var reading = probe.Read();
        if (!reading.IsAvailable) return true;
        var lowWritable = reading.LowPaths!.Count > 0;
        if (lowWritable == state.RepositoryLowWritable) return true;
        var detail = lowWritable
            ? $"{RepositoryLowWritableEvent} paths={string.Join(",", reading.LowPaths)} " +
                "remedy=report-only; relabeling needs owner approval"
            : RepositoryLowWritableClearedEvent;
        return Transition(ref state, "repository-integrity", lowWritable, detail);
    }

    private bool EvaluatePagedPool(ref MonitorState state, IReadOnlyList<HostHealthLedgerRecord> records)
    {
        static bool HasCache(HostHealthLedgerRecord record) =>
            record.FileCachePagedPoolMb is { } cache && double.IsFinite(cache) && cache >= 0;
        static double? Residual(HostHealthLedgerRecord record) =>
            record.PagedPoolMb is { } total && HasCache(record)
                ? Math.Max(0, total - record.FileCachePagedPoolMb!.Value) : record.PagedPoolMb;

        if (records.Count == 0 || records[^1].PagedPoolMb is not { } total) return true;
        var latestRecord = records[^1];
        var latest = Residual(latestRecord)!.Value;
        var window = records.TakeLast(PagedPoolWindow).Select(Residual).ToArray();
        var rise = latest - window[0];
        var monotonic = window.Length == PagedPoolWindow && window.All(value => value.HasValue);
        for (var index = 1; monotonic && index < window.Length; index++)
            monotonic = window[index] >= window[index - 1];
        var high = latest >= PagedPoolHighWaterMb || monotonic && rise >= PagedPoolRiseMb;
        if (high == state.PagedPoolHigh) return true;
        var cacheDetail = HasCache(latestRecord) ? $" file_cache_mb={Number(latestRecord.FileCachePagedPoolMb)}" : "";
        var detail = high
            ? $"{PagedPoolHighEvent} paged_pool_mb={Number(total)} rise_mb={Number(rise)} records={window.Length}" +
                cacheDetail + (HasCache(latestRecord) ? $" residual_mb={Number(latest)}" : "") +
                $" remedy={ForegroundLockRemedy}; leaked pool is reclaimed only by a reboot"
            : $"{PagedPoolNormalEvent} paged_pool_mb={Number(total)}" + cacheDetail;
        return Transition(ref state, "paged-pool", high, detail);
    }

    private bool Transition(ref MonitorState state, string condition, bool active, string detail)
    {
        var transition = new PendingTransition(Guid.NewGuid().ToString("N"), active, detail,
            signals?.Clock.GetUtcNow() ?? DateTimeOffset.UtcNow, condition);
        Save(state with { Pending = transition });
        if (!Publish(transition)) return false;
        state = Commit(state, transition);
        Save(state);
        return true;
    }

    private static MonitorState Commit(MonitorState state, PendingTransition transition) => transition.Condition switch
    {
        "latency" => state with { IsDegraded = transition.IsDegraded, Pending = null },
        "foreground-lock" => state with { ForegroundLockArmed = transition.IsDegraded, Pending = null },
        "paged-pool" => state with { PagedPoolHigh = transition.IsDegraded, Pending = null },
        "worker-sandbox" => state with { WorkerSandboxOff = transition.IsDegraded, Pending = null },
        "repository-integrity" => state with { RepositoryLowWritable = transition.IsDegraded, Pending = null },
        _ => throw new InvalidDataException($"Unknown host-health condition: {transition.Condition}")
    };

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

    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable";

    private sealed record MonitorState(bool IsDegraded, PendingTransition? Pending = null,
        bool ForegroundLockArmed = false, bool PagedPoolHigh = false,
        bool WorkerSandboxOff = false, bool RepositoryLowWritable = false);
    // IsDegraded retains the legacy JSON field name and holds the target state of the named condition.
    private sealed record PendingTransition(string Id, bool IsDegraded, string Detail, DateTimeOffset ObservedAt,
        string Condition = "latency");
}

internal sealed record ConductorHostHealthSignals(IForegroundLockReader ForegroundLock, TimeProvider Clock,
    IWorkerSandboxReader? WorkerSandbox = null, IRepositoryIntegrityProbe? RepositoryIntegrity = null);
