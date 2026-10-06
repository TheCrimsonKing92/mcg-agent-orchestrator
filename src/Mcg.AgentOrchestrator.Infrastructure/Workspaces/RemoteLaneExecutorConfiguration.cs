using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteLaneExecutorEntry(string Id, int LeaseSeconds);

// Operator-owned, immutable for a single gate attempt. Invalid input always disables dispatch.
internal sealed record RemoteLaneExecutorConfiguration(
    IReadOnlyList<RemoteLaneExecutorEntry> Executors, IReadOnlyList<string> Lanes, string? DisabledReason)
{
    internal bool Enabled => DisabledReason is null;
    internal static string ResolveStorePath(string worktreePath) => Path.Combine(
        AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath), ".orchestrator", "remote-lane-executors.json");

    internal static RemoteLaneExecutorConfiguration Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("executors", out var executors) || executors.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("lanes", out var lanes) || lanes.ValueKind != JsonValueKind.Array)
                return Disabled("invalid");
            var entries = new List<RemoteLaneExecutorEntry>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var executor in executors.EnumerateArray())
            {
                if (executor.ValueKind != JsonValueKind.Object ||
                    !executor.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(id.GetString()) || !ids.Add(id.GetString()!))
                    return Disabled("invalid");
                var seconds = 60;
                if (executor.TryGetProperty("leaseSeconds", out var lease))
                {
                    if (lease.ValueKind != JsonValueKind.Number || !lease.TryGetInt32(out var value))
                        return Disabled("invalid");
                    if (value > 0) seconds = value;
                }
                entries.Add(new(id.GetString()!, seconds));
            }
            var names = new List<string>();
            foreach (var lane in lanes.EnumerateArray())
            {
                if (lane.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(lane.GetString()))
                    return Disabled("invalid");
                names.Add(lane.GetString()!);
            }
            return entries.Count == 0 || names.Count == 0 ? Disabled("empty") : new(entries.ToArray(), names.ToArray(), null);
        }
        catch (FileNotFoundException) { return Disabled("missing"); }
        catch (DirectoryNotFoundException) { return Disabled("missing"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return Disabled("invalid"); }
    }
    private static RemoteLaneExecutorConfiguration Disabled(string reason) => new([], [], reason);
}
