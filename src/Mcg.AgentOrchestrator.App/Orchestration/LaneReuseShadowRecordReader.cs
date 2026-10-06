using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class LaneReuseShadowRecordReader
{
    internal static (IReadOnlyList<LaneReuseShadowRecord> Records, int UntimedRecords, int UnreadableRecords)
        Read(OrchestratorWorkspace workspace)
    {
        var root = Path.Combine(workspace.RootDirectory, ".orchestrator", "lane-reuse-shadow");
        var records = new List<LaneReuseShadowRecord>();
        var untimed = 0;
        var unreadable = 0;
        if (!Directory.Exists(root)) return (records, untimed, unreadable);
        foreach (var directory in Directory.EnumerateDirectories(root))
        foreach (var path in Directory.EnumerateFiles(directory).Where(p =>
            Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(stream);
                var record = document.RootElement;
                if (record.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a shadow record object.");
                var timestamp = Optional(record, "recorded_at");
                var lanes = ReadLanes(record);
                if (timestamp is null) { untimed++; continue; }
                if (timestamp.Value.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(timestamp.Value.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var at))
                    throw new JsonException("Invalid recorded_at.");
                records.Add(new(Path.GetFileName(directory), Path.GetFileNameWithoutExtension(path),
                    at.ToUniversalTime(), lanes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            {
                unreadable++;
            }
        }
        return (records, untimed, unreadable);
    }

    private static IReadOnlyList<LaneReuseShadowLaneRow> ReadLanes(JsonElement record)
    {
        var value = Optional(record, "lanes");
        if (value is null) return [];
        var lanes = new List<LaneReuseShadowLaneRow>();
        foreach (var row in value.Value.EnumerateArray())
        {
            var lane = row.GetProperty("lane").GetString() ?? throw new JsonException("Missing lane.");
            var decision = row.GetProperty("decision").GetString() ?? throw new JsonException("Missing decision.");
            var executed = row.GetProperty("executed").GetBoolean();
            var ruleV2 = Optional(row, "rule_v2");
            if (ruleV2 is not null && ruleV2.Value.ValueKind != JsonValueKind.Object)
                throw new JsonException("Invalid rule_v2.");
            lanes.Add(new(lane, decision, String(row, "reason"), executed,
                Optional(row, "duration_ms")?.GetInt64(), Boolean(row, "shadow_miss"),
                String(row, "miss_reason"), String(row, "reference_source"), Boolean(row, "flake_confirmed"),
                String(row, "failed_predicate"), Strings(row, "failing_classes"), String(row, "verdict"),
                ruleV2 is null ? null : String(ruleV2.Value, "decision"),
                ruleV2 is null ? null : String(ruleV2.Value, "reason")));
        }
        return lanes;
    }

    private static JsonElement? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string? String(JsonElement element, string name) => Optional(element, name)?.GetString();
    private static bool Boolean(JsonElement element, string name) => Optional(element, name)?.GetBoolean() ?? false;

    private static IReadOnlyList<string> Strings(JsonElement element, string name)
    {
        var value = Optional(element, name);
        return value is null ? [] : value.Value.EnumerateArray()
            .Select(v => v.GetString() ?? throw new JsonException("Invalid failing class.")).ToArray();
    }
}
