using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record GateLaneReceipt(
    string LaneName, int RunOrdinal, GateLoadSample WallMilliseconds,
    GateLoadSample CpuMilliseconds, GateLoadSample PeakMemoryBytes,
    string? AccountingSource, GateLoadSample HostAvailableMemoryMb)
{
    internal static GateLaneReceipt FromSummary(
        string laneName, int runOrdinal, TimeSpan wall, string? summary, GateLoadSample memory)
    {
        const string marker = "RESOURCE phase=gate";
        var start = summary?.LastIndexOf(marker, StringComparison.Ordinal) ?? -1;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (start >= 0)
        {
            var receipt = summary![(start + marker.Length)..].Split(';')[0];
            foreach (var token in receipt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = token.IndexOf('=');
                if (separator > 0)
                    fields[token[..separator]] = token[(separator + 1)..];
            }
        }

        fields.TryGetValue("accounting_source", out var source);
        GateLoadSample Read(string key, string invalidReason)
        {
            if (start < 0) return GateLoadSample.Unavailable("no-resource-receipt");
            if (source == "accounting-unavailable")
                return GateLoadSample.Unavailable("accounting-unavailable");
            return fields.TryGetValue(key, out var text) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
                    ? GateLoadSample.Available(value) : GateLoadSample.Unavailable(invalidReason);
        }

        return new(laneName, runOrdinal,
            wall < TimeSpan.Zero ? GateLoadSample.Unavailable("invalid-wall-ms") : GateLoadSample.Available(wall.TotalMilliseconds),
            Read("cpu_ms", "invalid-cpu-ms"), Read("peak_mem_bytes", "invalid-peak-mem-bytes"), source, memory);
    }
}
