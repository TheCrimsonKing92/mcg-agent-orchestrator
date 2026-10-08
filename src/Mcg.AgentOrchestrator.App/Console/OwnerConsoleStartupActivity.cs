using Mcg.AgentOrchestrator.App.Orchestration;
using System.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleStartupActivity
{
    internal static bool IsOperatorEvent(OwnerConductEvent item) =>
        OwnerConsoleActivityPresentation.Classify(item) is not null;

    internal static IReadOnlyList<OwnerConductEvent> ReadRecent(string path, CancellationToken token = default)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Snapshot the length: a writer cannot extend this one-shot startup scan indefinitely.
            var remaining = stream.Length;
            using var line = new MemoryStream();
            var buffer = new byte[8192];
            var recent = new PriorityQueue<OwnerConductEvent, (DateTimeOffset Timestamp, long Sequence)>();
            long sequence = 0;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) break;
                for (var index = 0; index < read; index++)
                {
                    if (buffer[index] == (byte)'\n') { ParseLine(); line.SetLength(0); line.Position = 0; }
                    else line.WriteByte(buffer[index]);
                }
                remaining -= read;
            }
            if (line.Length > 0) ParseLine();
            var result = new List<OwnerConductEvent>();
            while (recent.TryDequeue(out var item, out _)) result.Add(item);
            return result;

            void ParseLine()
            {
                var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r').TrimStart('\uFEFF');
                if (!ConductEventFileSource.TryParse(text, out var item) || !IsOperatorEvent(item!)) return;
                recent.Enqueue(item!, (item!.Timestamp, sequence++));
                if (recent.Count > OwnerConsoleViewModelBuilder.MaxActivityItems) recent.Dequeue();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static void Append(List<OwnerConductEvent> recent, OwnerConductEvent item)
    {
        // The parsed record has no id/offset. Value equality identifies startup/live overlap.
        if (!IsOperatorEvent(item) || recent.Contains(item)) return;
        recent.Add(item);
        if (recent.Count > OwnerConsoleViewModelBuilder.MaxActivityItems)
        {
            var oldest = recent.Min(value => value.Timestamp);
            recent.RemoveAt(recent.FindIndex(value => value.Timestamp == oldest));
        }
    }
}
