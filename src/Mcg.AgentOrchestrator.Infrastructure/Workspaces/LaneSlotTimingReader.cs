using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns discovery and best-effort reading of the per-invocation slot timing sidecar.
internal static class LaneSlotTimingReader
{
    internal const string VariableName = "MCG_TEST_SLOT_TIMING_FILE";
    internal sealed record LaneSlotTotals(double WaitMs, double HeldMs, int SlotCount);
    internal sealed record Reading(
        IReadOnlyDictionary<string, Dictionary<string, double>> WaitsByFile,
        LaneSlotTotals? Totals, string? UnavailableReason)
    {
        internal double WaitFor(string trxPath, string identity) =>
            WaitsByFile.TryGetValue(PathFor(trxPath), out var waits) ? waits.GetValueOrDefault(identity) : 0;
    }

    internal static string PathFor(string trxPath) => Path.ChangeExtension(Path.GetFullPath(trxPath), ".slot-timing.jsonl");

    internal static string? PathForArguments(IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var directory = AcceptanceLaneResultsDirectory.FromArguments(arguments);
        if (string.IsNullOrWhiteSpace(directory)) return null;
        string? name = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals("--report-trx-filename", StringComparison.OrdinalIgnoreCase)
                && index + 1 < arguments.Count)
                name = arguments[index + 1];
            if ((arguments[index].Equals("--logger", StringComparison.OrdinalIgnoreCase)
                 || arguments[index].Equals("-l", StringComparison.OrdinalIgnoreCase))
                && index + 1 < arguments.Count)
            {
                var parts = arguments[index + 1].Split(';');
                if (!parts[0].Equals("trx", StringComparison.OrdinalIgnoreCase)) continue;
                name = parts.Skip(1).FirstOrDefault(p => p.StartsWith("LogFileName=", StringComparison.OrdinalIgnoreCase))?[12..];
            }
        }
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var trxPath = Path.Combine(directory, name);
            return PathFor(workingDirectory is null ? trxPath : Path.GetFullPath(trxPath, workingDirectory));
        }
        catch (Exception error) { Diagnose(directory, "destination", error); return null; }
    }

    internal static double Subtract(double trxMs, double waitMs) => Math.Max(0, trxMs - waitMs);

    internal static Reading Read(IReadOnlyList<string>? trxPaths)
    {
        var waitsByFile = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<string>();
        var counts = new SortedSet<int>();
        double totalWait = 0, totalHeld = 0;
        foreach (var trxPath in trxPaths ?? [])
        {
            string path;
            try { path = PathFor(trxPath); }
            catch (Exception error) { diagnostics.Add($"slot-timing-path:{trxPath}:{error.GetType().Name}"); continue; }
            if (waitsByFile.ContainsKey(path)) continue;
            var waits = new Dictionary<string, double>(StringComparer.Ordinal);
            waitsByFile[path] = waits;
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception error)
            {
                diagnostics.Add($"slot-timing-unreadable:{path}:{error.GetType().Name}:{error.Message}");
                continue;
            }
            var skipped = 0;
            foreach (var line in lines)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var row = document.RootElement;
                    if (row.ValueKind != JsonValueKind.Object
                        || !row.TryGetProperty("test_name", out var name) || name.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(name.GetString())
                        || !Number(row, "wait_ms", out var wait) || !Number(row, "held_ms", out var held)
                        || !PositiveInteger(row, "slot_count", out var count)
                        || !PositiveInteger(row, "process_id", out _))
                    { skipped++; continue; }
                    var identity = AcceptanceTrxTestIdentityResolver.NormalizeSelector(name.GetString()!);
                    var accumulated = waits.GetValueOrDefault(identity) + wait;
                    if (!double.IsFinite(accumulated) || !double.IsFinite(totalWait + wait) || !double.IsFinite(totalHeld + held))
                    { skipped++; continue; }
                    waits[identity] = accumulated;
                    totalWait += wait;
                    totalHeld += held;
                    counts.Add(count);
                }
                catch (JsonException) { skipped++; }
            }
            if (skipped > 0) diagnostics.Add($"slot-timing-skipped:{path}:{skipped} invalid lines");
        }
        if (counts.Count > 1)
            diagnostics.Add($"slot-timing-inconsistent-counts:{string.Join(",", counts)}");
        return new(waitsByFile, counts.Count == 0 ? null : new(totalWait, totalHeld, counts.Max),
            diagnostics.Count == 0 ? null : string.Join("; ", diagnostics));
    }

    private static bool Number(JsonElement row, string key, out double value)
    {
        value = 0;
        return row.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number
            && field.TryGetDouble(out value) && double.IsFinite(value) && value >= 0;
    }

    private static bool PositiveInteger(JsonElement row, string key, out int value)
    {
        value = 0;
        return row.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number
            && field.TryGetInt32(out value) && value > 0;
    }

    internal static void Reset(string trxPath)
    {
        try { File.Delete(PathFor(trxPath)); }
        catch (Exception error) { Diagnose(trxPath, "reset", error); }
    }

    internal static void CopyBeside(string sourceTrx, string destinationTrx)
    {
        try
        {
            var source = PathFor(sourceTrx);
            var destination = PathFor(destinationTrx);
            if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) return;
            File.Delete(destination);
            if (File.Exists(source)) File.Copy(source, destination, overwrite: true);
        }
        catch (Exception error) { Diagnose(sourceTrx, "copy", error); }
    }

    private static void Diagnose(string path, string operation, Exception error)
    {
        try { Console.Error.WriteLine($"Slot timing {operation} '{path}': {error.GetType().Name}: {error.Message}"); }
        catch (Exception) { /* Timing artifacts must not fail a lane. */ }
    }
}
