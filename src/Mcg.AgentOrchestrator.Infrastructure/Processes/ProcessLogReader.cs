using Mcg.AgentOrchestrator.Core;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum ProcessLogStream
{
    All,
    Stdout,
    Stderr,
    Exit
}

public sealed record ProcessLogSnapshot(
    string StandardOutputPath,
    string StandardOutput,
    string StandardErrorPath,
    string StandardError,
    string ExitCodePath,
    string ExitCode,
    DispatchHeartbeatStatus Heartbeat);

public sealed record DispatchHeartbeatStatus(
    string Path,
    bool IsAvailable,
    string? UnavailableReason,
    int ProcessId,
    int? ChildProcessId,
    IReadOnlyList<int> OwnedProcessIds,
    string State,
    DateTimeOffset? LastObservedAt,
    DateTimeOffset? LastProgressAt,
    TimeSpan? HeartbeatAge,
    TimeSpan? IdleDuration,
    long StandardOutputBytes,
    long StandardErrorBytes,
    long OwnedCpuMs = 0L,
    string? ProviderSessionId = null,
    string? WorktreeHeadSha = null,
    string? DirtyStateHash = null)
{
    internal IReadOnlyList<SpawnProcessIdentity> OwnedProcessIdentities { get; init; } = [];
}

public sealed partial class ProcessLogReader
{
    public static ProcessLogSnapshot Read(TaskProcessRecord process)
    {
        return new ProcessLogSnapshot(
            process.StandardOutputPath,
            ReadIfExists(process.StandardOutputPath),
            process.StandardErrorPath,
            ReadIfExists(process.StandardErrorPath),
            process.ExitCodePath,
            ReadIfExists(process.ExitCodePath),
            ReadHeartbeat(process));
    }

    public static DispatchHeartbeatStatus ReadHeartbeat(TaskProcessRecord process, DateTimeOffset? now = null)
    {
        var path = BackgroundDispatchRunner.GetHeartbeatPath(process);
        if (!File.Exists(path))
        {
            return Unavailable(path, "missing");
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!TryGetDateTimeOffset(root, "lastObservedAt", out var lastObservedAt) ||
                !TryGetDateTimeOffset(root, "lastProgressAt", out var lastProgressAt))
            {
                return Unavailable(path, "invalid");
            }

            var observedAt = now ?? DateTimeOffset.UtcNow;
            return new DispatchHeartbeatStatus(
                path,
                true,
                null,
                GetInt32(root, "pid"),
                GetNullableInt32(root, "childPid"),
                GetInt32Array(root, "ownedPids"),
                GetString(root, "state"),
                lastObservedAt,
                lastProgressAt,
                observedAt - lastObservedAt,
                observedAt - lastProgressAt,
                GetInt64(root, "stdoutBytes"),
                GetInt64(root, "stderrBytes"),
                GetInt64(root, "ownedCpuMs"),
                GetNullableString(root, "providerSessionId"),
                GetNullableString(root, "worktreeHeadSha"),
                GetNullableString(root, "dirtyStateHash"))
            {
                OwnedProcessIdentities = DispatchProcessIdentityEvidence.Read(root)
            };
        }
        catch (IOException)
        {
            return Unavailable(path, "unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable(path, "unreadable");
        }
        catch (JsonException)
        {
            return Unavailable(path, "invalid");
        }
    }

    private static string ReadIfExists(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static DispatchHeartbeatStatus Unavailable(string path, string reason)
    {
        return new DispatchHeartbeatStatus(path, false, reason, 0, null, [], "unknown", null, null, null, null, 0, 0);
    }

    private static bool TryGetDateTimeOffset(JsonElement root, string propertyName, out DateTimeOffset value)
    {
        value = default;
        return root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(property.GetString(), out value);
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? "unknown"
            : "unknown";
    }

    private static string? GetNullableString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;
    }

    private static int GetInt32(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static int? GetNullableInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.TryGetInt32(out var value) ? value : null;
    }

    private static long GetInt64(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : 0;
    }

    private static IReadOnlyList<int> GetInt32Array(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<int>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.TryGetInt32(out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }
}
