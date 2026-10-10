using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerGateMotionReader(string conductEventsLogPath, TimeProvider clock)
{
    private const int WindowBytes = 1_048_576;

    internal string? Describe(string goalId)
    {
        try
        {
            using var stream = new FileStream(conductEventsLogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var offset = Math.Max(0, length - WindowBytes);
            stream.Position = offset;
            // Snapshot the byte bound so concurrent appends cannot extend this read.
            var window = new byte[(int)(length - offset)];
            var read = 0;
            while (read < window.Length)
            {
                var count = stream.Read(window, read, window.Length - read);
                if (count == 0) break;
                read += count;
            }
            using var data = new MemoryStream(window, 0, read);
            using var reader = new StreamReader(data);
            if (offset > 0) reader.ReadLine();
            var samples = new List<OwnerConductEvent>();
            while (reader.ReadLine() is { } line)
                if (Parse(line) is { } sample) samples.Add(sample);
            return OwnerGateMotion.Describe(goalId, clock.GetUtcNow(), samples);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static OwnerConductEvent? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("eventKind").GetString() != "gate-progress") return null;
            return new(root.GetProperty("timestamp").GetDateTimeOffset(), "gate-progress",
                root.GetProperty("goalId").GetString(), root.GetProperty("detail").GetString() ?? string.Empty);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
