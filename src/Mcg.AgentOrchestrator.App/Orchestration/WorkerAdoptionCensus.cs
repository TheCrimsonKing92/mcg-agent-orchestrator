using System.Diagnostics;
using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record WorkerAdoptionCensusRow(
    string OwnerId,
    int WorkerProcessId,
    DateTimeOffset WorkerStartedAt,
    SpawnRegistryLifecycle Lifecycle,
    int? OwnerProcessId,
    DateTimeOffset? OwnerStartedAt,
    SpawnOwnerLiveness OwnerLiveness,
    string Verdict,
    string Evidence);

internal static class WorkerAdoptionCensus
{
    private static readonly string[] Verdicts =
        ["current-generation", "worker-gone", "identity-unproven", "inheritable", "orphaned", "owner-held"];

    public static string Classify(
        SpawnRegistryLifecycle lifecycle,
        SpawnTrackedProcessStatus worker,
        SpawnOwnerLiveness owner,
        bool ownedByCurrentProcess)
    {
        if (ownedByCurrentProcess) return "current-generation";
        if (worker == SpawnTrackedProcessStatus.DeadOrRecycled) return "worker-gone";
        if (worker == SpawnTrackedProcessStatus.Unknown) return "identity-unproven";
        if (lifecycle != SpawnRegistryLifecycle.Owned && owner != SpawnOwnerLiveness.Live) return "inheritable";
        if (lifecycle == SpawnRegistryLifecycle.Owned && owner == SpawnOwnerLiveness.DeadOrRecycled) return "orphaned";
        return "owner-held";
    }

    public static IReadOnlyList<WorkerAdoptionCensusRow> Read(
        IReadOnlyList<SpawnRegistryEntry> entries,
        Func<SpawnRegistryEntry, (SpawnTrackedProcessStatus Status, string Evidence)> evaluateWorker,
        Func<SpawnRegistryEntry, (SpawnOwnerLiveness Liveness, string Evidence)> evaluateOwner,
        SpawnProcessIdentity current)
    {
        var rows = new List<WorkerAdoptionCensusRow>(entries.Count);
        foreach (var entry in entries)
        {
            var worker = evaluateWorker(entry);
            var owner = evaluateOwner(entry);
            var ownedByCurrentProcess = entry.OwnerProcessId == current.ProcessId &&
                                        entry.OwnerProcessStartedAt == current.StartedAt;
            rows.Add(new WorkerAdoptionCensusRow(
                entry.OwnerId, entry.ProcessId, entry.ProcessStartedAt, entry.Lifecycle,
                entry.OwnerProcessId, entry.OwnerProcessStartedAt, owner.Liveness,
                Classify(entry.Lifecycle, worker.Status, owner.Liveness, ownedByCurrentProcess),
                $"worker: {worker.Evidence}; owner: {owner.Evidence}"));
        }
        return rows;
    }

    public static Func<IReadOnlyList<WorkerAdoptionCensusRow>> CreateDefault(string stateDatabasePath) => () =>
    {
        using var currentProcess = Process.GetCurrentProcess();
        if (!SpawnProcessIdentityReader.TryRead(currentProcess, out var current))
            throw new InvalidOperationException("Current process identity could not be read.");

        return Read(new SpawnRegistry(stateDatabasePath).ListActive(), EvaluateWorker,
            entry =>
            {
                var liveness = SpawnProcessIdentityReader.EvaluateOwner(entry, out var evidence);
                return (liveness, evidence);
            }, current);
    };

    private static (SpawnTrackedProcessStatus Status, string Evidence) EvaluateWorker(SpawnRegistryEntry entry)
    {
        Process? process = null;
        try
        {
            var status = SpawnProcessIdentityReader.EvaluateTrackedProcess(entry, out process, out var evidence);
            return (status, evidence);
        }
        finally
        {
            process?.Dispose();
        }
    }

    public static string FormatRowDetail(WorkerAdoptionCensusRow row) =>
        $"owner={row.OwnerId} workerPid={row.WorkerProcessId.ToString(CultureInfo.InvariantCulture)} " +
        $"workerStartedAt={row.WorkerStartedAt.ToString("O", CultureInfo.InvariantCulture)} lifecycle={row.Lifecycle} " +
        $"ownerPid={row.OwnerProcessId?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
        $"ownerStartedAt={row.OwnerStartedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "none"} " +
        $"ownerLiveness={row.OwnerLiveness} evidence={row.Evidence}";

    public static string FormatSummaryDetail(IReadOnlyList<WorkerAdoptionCensusRow> rows) =>
        $"workers={rows.Count.ToString(CultureInfo.InvariantCulture)} " +
        string.Join(" ", Verdicts.Select(verdict =>
            $"{verdict}={rows.Count(row => row.Verdict == verdict).ToString(CultureInfo.InvariantCulture)}"));
}
