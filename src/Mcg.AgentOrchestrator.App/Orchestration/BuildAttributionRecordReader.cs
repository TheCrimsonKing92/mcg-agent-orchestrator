using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BuildAttributionRecordReader
{
    internal static BuildAttributionInput Read(DotnetBuildStorageRoot storageRoot,
        OrchestratorWorkspace workspace, DateTimeOffset since, DateTimeOffset until)
    {
        var records = new List<BuildAttributionJournalRecord>();
        var runs = new List<BuildAttributionRun>();
        var attempts = new List<BuildAttributionAttempt>();
        var unreadable = 0;
        var slots = Path.Combine(storageRoot.RootPath, "build-slots");
        foreach (var path in Files(slots, "*.jsonl"))
        {
            var name = Path.GetFileName(path);
            var holds = name == "build-slot-holds.jsonl" || name.StartsWith("build-slot-holds-", StringComparison.Ordinal) ||
                name.StartsWith("build-slot-holds.jsonl.pending-", StringComparison.Ordinal);
            var waits = name == "lease.journal.jsonl" || name.StartsWith("lease.journal-", StringComparison.Ordinal);
            if (!holds && !waits) continue;
            try
            {
                using var reader = Open(path);
                while (reader.ReadLine() is { } line)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        var eventName = Required(r, "event");
                        if (waits && eventName != "ACCEPTANCE_SLOT_PRIORITY_WAIT") continue;
                        var trigger = Optional(r, "triggerPath") ?? "";
                        if (trigger.Length > 0) _ = BuildAttributionReport.NormalizePath(trigger);
                        records.Add(new(eventName, Time(r, "recordedAt"), r.GetProperty("processId").GetInt32(),
                            Required(r, "leaseId"), Required(r, "decision"), Required(r, "reason"), trigger));
                    }
                    catch (Exception ex) when (Unreadable(ex)) { unreadable++; }
                }
            }
            catch (Exception ex) when (Unreadable(ex)) { unreadable++; }
        }
        foreach (var goal in Directories(Path.Combine(storageRoot.RootPath, "goals")))
        foreach (var artifacts in Directories(goal).Where(p => Path.GetFileName(p).StartsWith("artifacts", StringComparison.Ordinal)))
        foreach (var run in Directories(Path.Combine(artifacts, "worker-build-logs")))
        {
            var id = Path.GetFileName(run);
            if (!BuildAttributionReport.ShouldReadRun(run, id, records, since, until)) continue;
            List<string>? lines = [];
            foreach (var log in Files(run, "*.log"))
            {
                try
                {
                    using var reader = Open(log);
                    while (reader.ReadLine() is { } line) lines?.Add(line);
                }
                catch (Exception ex) when (Unreadable(ex)) { unreadable++; lines = null; }
            }
            runs.Add(new(run, Path.GetFileName(goal), id, lines));
        }
        foreach (var family in new[] { "pre-review-evidence-attempts", "acceptance-gate-attempts" })
        foreach (var goal in Directories(Path.Combine(workspace.RootDirectory, ".orchestrator", family)))
        foreach (var path in Files(goal, "*.attempt.json"))
        {
            try
            {
                using var reader = Open(path);
                using var doc = JsonDocument.Parse(reader.ReadToEnd());
                var r = doc.RootElement;
                var id = Path.GetFileName(path)[..^".attempt.json".Length];
                DateTimeOffset? completed = Optional(r, "completedAt") is null ? null : Time(r, "completedAt");
                string? kind = null, fault = null;
                var resultPath = Path.Combine(goal, id + ".result.json");
                if (File.Exists(resultPath))
                {
                    try
                    {
                        using var resultReader = Open(resultPath);
                        using var result = JsonDocument.Parse(resultReader.ReadToEnd());
                        kind = Optional(result.RootElement, "kind");
                        fault = Optional(result.RootElement, "faultKind");
                    }
                    catch (Exception ex) when (Unreadable(ex)) { unreadable++; }
                }
                attempts.Add(new(id, Optional(r, "branchHeadSha"), completed, kind, fault,
                    family == "pre-review-evidence-attempts"));
            }
            catch (Exception ex) when (Unreadable(ex)) { unreadable++; }
        }
        return new(records, runs, attempts, unreadable);

        string[] Files(string directory, string pattern)
        {
            try { return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern) : []; }
            catch (Exception ex) when (Unreadable(ex)) { unreadable++; return []; }
        }
        string[] Directories(string directory)
        {
            try { return Directory.Exists(directory) ? Directory.GetDirectories(directory) : []; }
            catch (Exception ex) when (Unreadable(ex)) { unreadable++; return []; }
        }
    }

    private static StreamReader Open(string path) => new(new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete));
    private static string Required(JsonElement r, string key) => r.GetProperty(key).GetString() ?? throw new JsonException(key);
    private static string? Optional(JsonElement r, string key) =>
        r.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
    private static DateTimeOffset Time(JsonElement r, string key) =>
        DateTimeOffset.TryParse(Required(r, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUniversalTime() : throw new JsonException(key);
    private static bool Unreadable(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or
        InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException;
}
