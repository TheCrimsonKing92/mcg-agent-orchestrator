using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record AcceptanceGateProgress(
    string? GoalId,
    string Phase,
    string CurrentTarget,
    int? SlotIndex,
    int? ProcessId,
    int? ChildProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset LastObservedAt,
    DateTimeOffset LastProgressAt,
    TimeSpan Elapsed,
    long OutputBytes,
    string HeartbeatPath);

public sealed record GateHeartbeatSnapshot(
    string? GoalId,
    string Phase,
    string CurrentTarget,
    int? SlotIndex,
    int? ProcessId,
    int? ChildPid,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset LastObservedAt,
    DateTimeOffset LastProgressAt,
    long StdoutBytes,
    long StderrBytes,
    long OutputBytes,
    string? CommandLine = null,
    int? ExitCode = null,
    string? StdoutPath = null,
    string? StderrPath = null);

public sealed record GateHeartbeatStatus(
    int SlotIndex,
    string Path,
    bool IsAvailable,
    string? UnavailableReason,
    GateHeartbeatSnapshot? Snapshot,
    TimeSpan? HeartbeatAge,
    TimeSpan? IdleDuration);

public static class GateHeartbeatArtifacts
{
    public const string FileName = "gate-heartbeat.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static string GetStableSlotPath(int slotIndex) =>
        Path.Combine(DotnetBuildEnvironmentManager.StableSlotArtifactsPath(slotIndex), FileName);

    public static string GetManualPath(string worktreePath) =>
        Path.Combine(worktreePath, ".orchestrator", FileName);

    public static IReadOnlyList<GateHeartbeatStatus> ReadStableSlots(DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        var statuses = new List<GateHeartbeatStatus>(DotnetBuildEnvironmentManager.StableSlotCount);
        for (var slot = 0; slot < DotnetBuildEnvironmentManager.StableSlotCount; slot++)
        {
            statuses.Add(ReadStableSlot(slot, now));
        }

        return statuses;
    }

    public static GateHeartbeatStatus ReadStableSlot(int slotIndex, DateTimeOffset? observedAt = null)
    {
        var path = GetStableSlotPath(slotIndex);
        var now = observedAt ?? DateTimeOffset.UtcNow;
        if (!File.Exists(path))
        {
            return new GateHeartbeatStatus(slotIndex, path, false, "missing", null, null, null);
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<GateHeartbeatSnapshot>(File.ReadAllText(path), JsonOptions);
            if (snapshot is null)
            {
                return new GateHeartbeatStatus(slotIndex, path, false, "invalid", null, null, null);
            }

            return new GateHeartbeatStatus(
                slotIndex,
                path,
                true,
                null,
                snapshot,
                Positive(now - snapshot.LastObservedAt),
                Positive(now - snapshot.LastProgressAt));
        }
        catch
        {
            return new GateHeartbeatStatus(slotIndex, path, false, "invalid", null, null, null);
        }
    }

    public static void Write(string path, GateHeartbeatSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    public static void TryWrite(string path, GateHeartbeatSnapshot snapshot)
    {
        try
        {
            Write(path, snapshot);
        }
        catch
        {
            // Heartbeats are observability artifacts; they must never decide acceptance.
        }
    }

    private static TimeSpan Positive(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
