using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteLaneExecutorEntry(string Id, int LeaseSeconds,
    string? Transport = null, string? RunnerAlias = null, string? AdminAlias = null,
    string? RemoteRepository = null, string? RunRoot = null, int PollSeconds = 10, int Slots = 1);

internal sealed record RemoteFocusedEvidenceSettings(
    string Mode = "off", int SampleEvery = 4, int GraceSeconds = 300, string? FaultReason = null);

// Operator-owned, immutable for a single gate attempt. Invalid input always disables dispatch.
internal sealed record RemoteLaneExecutorConfiguration(
    IReadOnlyList<RemoteLaneExecutorEntry> Executors, IReadOnlyList<string> Lanes, string? DisabledReason)
{
    internal IReadOnlyList<string> MachineLocalResourceKeys { get; init; } = [];
    internal RemoteFocusedEvidenceSettings FocusedEvidence { get; init; } = new();
    internal bool AllInfrastructureLanes { get; init; }
    internal RemoteLaneExecutorConfiguration ResolveLanes(IEnumerable<string> infrastructureLaneNames) =>
        AllInfrastructureLanes ? this with { Lanes = infrastructureLaneNames.Distinct(StringComparer.Ordinal).ToArray() } : this;
    internal bool Enabled => DisabledReason is null;
    internal static string ResolveStorePath(string worktreePath) => Path.Combine(
        AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath), ".orchestrator", "remote-lane-executors.json");

    internal static RemoteLaneExecutorConfiguration Load(string path)
    {
        var parsed = Parse(path);
        return parsed.DisabledReason is null && (parsed.Executors.Count == 0 || parsed.Lanes.Count == 0)
            ? Disabled("empty") : parsed;
    }

    internal static IReadOnlyList<RemoteLaneExecutorEntry> LoadExecutors(string path) => Parse(path).Executors;

    internal static RemoteLaneExecutorConfiguration LoadForFocusedEvidence(string path) => Parse(path);

    private static RemoteLaneExecutorConfiguration Parse(string path)
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
                var slots = 1;
                if (executor.TryGetProperty("slots", out var slotCount))
                {
                    if (slotCount.ValueKind != JsonValueKind.Number || !slotCount.TryGetInt32(out slots) ||
                        slots is < 1 or > 16)
                        return Disabled("invalid");
                }
                if (!executor.TryGetProperty("transport", out var transport))
                {
                    entries.Add(new(id.GetString()!, seconds, Slots: slots));
                    continue;
                }
                if (transport.ValueKind != JsonValueKind.String || transport.GetString() != "ssh")
                    return Disabled("invalid");
                var runner = ReadString(executor, "runnerAlias");
                var admin = ReadString(executor, "adminAlias");
                var repository = ReadString(executor, "remoteRepository");
                var runRoot = executor.TryGetProperty("runRoot", out _) ? ReadString(executor, "runRoot") : "C:/mcg-executor";
                if (!Matches(runner, "^[A-Za-z0-9][A-Za-z0-9._-]*$") ||
                    !Matches(admin, "^[A-Za-z0-9][A-Za-z0-9._-]*$") ||
                    !Matches(repository, "^[A-Za-z]:/[A-Za-z0-9._/-]+$") ||
                    !Matches(runRoot, "^[A-Za-z]:/[A-Za-z0-9._/-]+$"))
                    return Disabled("invalid");
                var pollSeconds = 10;
                if (executor.TryGetProperty("pollSeconds", out var poll))
                {
                    if (poll.ValueKind != JsonValueKind.Number || !poll.TryGetInt32(out var value))
                        return Disabled("invalid");
                    if (value > 0) pollSeconds = value;
                }
                entries.Add(new(id.GetString()!, seconds, "ssh", runner, admin, repository, runRoot, pollSeconds, slots));
            }
            var names = new List<string>();
            foreach (var lane in lanes.EnumerateArray())
            {
                if (lane.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(lane.GetString()))
                    return Disabled("invalid");
                names.Add(lane.GetString()!);
            }
            var allInfrastructureLanes = names.Contains("*", StringComparer.Ordinal);
            if (allInfrastructureLanes && names.Count != 1) return Disabled("invalid");
            var keys = new List<string>();
            if (root.TryGetProperty("machineLocalResourceKeys", out var keyArray))
            {
                if (keyArray.ValueKind != JsonValueKind.Array) return Disabled("invalid");
                foreach (var key in keyArray.EnumerateArray())
                {
                    if (key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString()))
                        return Disabled("invalid");
                    keys.Add(key.GetString()!);
                }
            }
            return new(entries.ToArray(), names.ToArray(), null)
            {
                MachineLocalResourceKeys = keys.ToArray(), AllInfrastructureLanes = allInfrastructureLanes,
                FocusedEvidence = ParseFocusedEvidence(root)
            };        }
        catch (FileNotFoundException) { return Disabled("missing"); }
        catch (DirectoryNotFoundException) { return Disabled("missing"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return Disabled("invalid"); }
    }
    private static RemoteFocusedEvidenceSettings ParseFocusedEvidence(JsonElement root)
    {
        if (!root.TryGetProperty("focusedEvidence", out var block)) return new();
        var mode = "off";
        var sampleEvery = 4;
        var graceSeconds = 300;
        if (block.ValueKind != JsonValueKind.Object ||
            (block.TryGetProperty("mode", out var m) &&
                (m.ValueKind != JsonValueKind.String || (mode = m.GetString()!) is not ("off" or "shadow"))) ||
            (block.TryGetProperty("sampleEvery", out var s) &&
                (s.ValueKind != JsonValueKind.Number || !s.TryGetInt32(out sampleEvery) || sampleEvery < 1)) ||
            (block.TryGetProperty("graceSeconds", out var g) &&
                (g.ValueKind != JsonValueKind.Number || !g.TryGetInt32(out graceSeconds) || graceSeconds < 0)))
            return new(FaultReason: "invalid");
        return new(mode, sampleEvery, graceSeconds);
    }
    private static RemoteLaneExecutorConfiguration Disabled(string reason) => new([], [], reason);
    private static string? ReadString(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Matches(string? value, string pattern) =>
        value is not null && Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant) && !value.Contains('\n');
}
