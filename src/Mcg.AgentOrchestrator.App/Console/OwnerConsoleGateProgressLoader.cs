using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGateProgressLoader(string logPath, string manifestPath, string durationPath, TimeProvider clock)
{
    internal OwnerConsoleGateProgress? Load(IReadOnlyDictionary<string, string> titles)
    {
        try
        {
            var events = ReadTail().Select(OwnerConsoleGateProgress.ParseEvent).OfType<OwnerConductEvent>().ToArray();
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var lanes = manifest.RootElement.GetProperty("engine").GetProperty("infrastructureTestLanes").EnumerateArray()
                .Select(lane => new OwnerConsoleGateManifestLane(lane.GetProperty("name").GetString()!,
                    lane.GetProperty("estimatedSerialSeconds").GetDouble())).ToArray();
            var history = new List<OwnerConsoleGateDuration>();
            if (File.Exists(durationPath))
                foreach (var line in SharedJsonlFile.ReadAllLines(durationPath))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        history.Add(new(root.GetProperty("laneName").GetString()!, root.GetProperty("durationSeconds").GetDouble(),
                            root.GetProperty("recordedAt").GetDateTimeOffset()));
                    }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
                }
            return OwnerConsoleGateProgress.Build(clock.GetUtcNow(), events, lanes, history,
                id => titles.FirstOrDefault(pair => pair.Key.StartsWith(id, StringComparison.Ordinal)).Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private IEnumerable<string> ReadTail()
    {
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var offset = Math.Max(0, length - 1_048_576);
        stream.Position = offset;
        var buffer = new byte[(int)(length - offset)];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0) break;
            read += count;
        }
        using var data = new MemoryStream(buffer, 0, read);
        using var reader = new StreamReader(data);
        if (offset > 0) reader.ReadLine();
        while (reader.ReadLine() is { } line) yield return line;
    }
}
