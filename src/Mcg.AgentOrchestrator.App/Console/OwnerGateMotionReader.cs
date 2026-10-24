using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerGateMotionReader(string conductEventsLogPath, TimeProvider clock, OwnerConsoleJsonlTail? tail = null)
{
    private const int WindowBytes = 1_048_576;
    private readonly OwnerConsoleJsonlTail _tail = tail ?? new(conductEventsLogPath);

    internal string? Describe(string goalId)
    {
        try
        {
            var samples = _tail.Select("gate-motion", Parse, WindowBytes).OfType<OwnerConductEvent>().ToArray();
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
