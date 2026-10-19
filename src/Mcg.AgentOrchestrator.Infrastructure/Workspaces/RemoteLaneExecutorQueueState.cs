using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteLaneExecutorQueueState(DateTimeOffset Utc, int Slots,
    IReadOnlyList<string> Running, IReadOnlyList<string> Queued)
{
    internal static bool TryParse(string text, out RemoteLaneExecutorQueueState? state)
    {
        state = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject()) fields[property.Name] = property.Value;
            if (!fields.TryGetValue("utc", out var utc) || utc.ValueKind != JsonValueKind.String ||
                !utc.TryGetDateTimeOffset(out var time) ||
                !fields.TryGetValue("slots", out var slots) || slots.ValueKind != JsonValueKind.Number ||
                !slots.TryGetInt32(out var count) || !ReadJobs("running", out var running) ||
                !ReadJobs("queued", out var queued)) return false;
            state = new(time, count, running, queued);
            return true;

            bool ReadJobs(string name, out IReadOnlyList<string> jobs)
            {
                jobs = [];
                if (!fields.TryGetValue(name, out var value)) return true;
                if (value.ValueKind != JsonValueKind.Array) return false;
                var names = new List<string>();
                foreach (var job in value.EnumerateArray())
                {
                    if (job.ValueKind != JsonValueKind.String) return false;
                    names.Add(job.GetString()!);
                }
                jobs = names.ToArray();
                return true;
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        { return false; }
    }

    internal bool IsBusy(DateTimeOffset now) => Slots >= 1 &&
        (now - Utc).Duration() <= TimeSpan.FromSeconds(120) && (long)Running.Count + Queued.Count >= Slots;
}
