using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
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
    string HeartbeatPath,
    GateLoadContext? LoadContext = null,
    AcceptanceGatePhaseBreakdown? PhaseBreakdown = null);

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
    private static readonly TimeSpan RunScopedHeartbeatFreshness = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static string GetStableSlotPath(int slotIndex) =>
        DotnetBuildEnvironmentManager.BuildSlotHeartbeatPath(slotIndex);

    public static string GetRunScopedStableSlotPath(int slotIndex, string runIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runIdentity);
        var stablePath = GetStableSlotPath(slotIndex);
        var directory = Path.GetDirectoryName(stablePath) ?? ".";
        var fileName = Path.GetFileNameWithoutExtension(stablePath);
        var extension = Path.GetExtension(stablePath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runIdentity)))[..12]
            .ToLowerInvariant();
        return Path.Combine(directory, $"{fileName}-{hash}{extension}");
    }

    public static string GetManualPath(string worktreePath) =>
        Path.Combine(worktreePath, ".orchestrator", FileName);

    public static IReadOnlyList<GateHeartbeatStatus> ReadStableSlots(DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        var statuses = new List<GateHeartbeatStatus>(DotnetBuildEnvironmentManager.StableSlotCount);
        for (var slot = 0; slot < DotnetBuildEnvironmentManager.StableSlotCount; slot++)
        {
            var runStatuses = EnumerateRunScopedStableSlotPaths(slot)
                .Select(path => Read(slot, path, now))
                .Where(status => IsLiveRunScopedHeartbeat(status, now))
                .ToArray();
            if (runStatuses.Length == 0)
            {
                statuses.Add(ReadStableSlot(slot, now));
                continue;
            }

            statuses.AddRange(runStatuses);
        }

        return statuses;
    }

    public static GateHeartbeatStatus ReadStableSlot(int slotIndex, DateTimeOffset? observedAt = null)
    {
        var path = GetStableSlotPath(slotIndex);
        var now = observedAt ?? DateTimeOffset.UtcNow;
        return Read(slotIndex, path, now);
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Heartbeats are observability artifacts; they must never decide acceptance.
        }
    }

    private static GateHeartbeatStatus Read(int slotIndex, string path, DateTimeOffset now)
    {
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

    private static bool IsLiveRunScopedHeartbeat(GateHeartbeatStatus status, DateTimeOffset now)
    {
        var snapshot = status.Snapshot;
        if (snapshot is not null &&
            string.Equals(snapshot.State, "running", StringComparison.OrdinalIgnoreCase) &&
            now - snapshot.LastObservedAt <= RunScopedHeartbeatFreshness &&
            IsProcessAlive(snapshot.ProcessId))
        {
            return true;
        }

        TryDelete(status.Path);
        return false;
    }

    private static bool IsProcessAlive(int? processId)
    {
        if (processId is not > 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> EnumerateRunScopedStableSlotPaths(int slotIndex)
    {
        var stablePath = GetStableSlotPath(slotIndex);
        var directory = Path.GetDirectoryName(stablePath) ?? ".";
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var pattern =
            $"{Path.GetFileNameWithoutExtension(stablePath)}-*{Path.GetExtension(stablePath)}";
        try
        {
            return Directory.EnumerateFiles(directory, pattern)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
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
