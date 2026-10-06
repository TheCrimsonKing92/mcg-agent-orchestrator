using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorJudgePanelTriggerSources
{
    private IEnumerable<string> NewLines(string path, PanelSourceState state, HashSet<string> observed)
    {
        if (!File.Exists(path)) yield break;
        var source = Path.GetFullPath(path);
        observed.Add(source);
        var info = new FileInfo(path);
        state.Cursors.TryGetValue(source, out var old);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Snapshot metadata before reading, including for a first-seen file.
        info.Refresh();
        var length = info.Length;
        var writeTicks = info.LastWriteTimeUtc.Ticks;
        var creationTicks = info.CreationTimeUtc.Ticks;
        var identity = FileIdentity(stream, info);
        if (old is not null && old.Length == length && old.WriteTicks == writeTicks &&
            old.CreationTicks == creationTicks && old.Identity == identity) yield break;
        var offset = old is not null && stream.Length >= old.Length && old.Identity == identity &&
            old.CreationTicks == creationTicks &&
            Fingerprint(stream, old.Offset) == old.Fingerprint ? old.Offset : 0;
        stream.Position = offset;
        var buffer = new byte[64 * 1024];
        using var pending = new MemoryStream();
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != '\n') continue;
                pending.Write(buffer, start, index - start + 1);
                var text = Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length);
                if (offset == 0) text = text.TrimStart('\uFEFF');
                offset += pending.Length;
                pending.SetLength(0);
                start = index + 1;
                // Match ReadLine's CR/LF handling inside the newline-terminated range.
                using var lines = new StringReader(text);
                while (lines.ReadLine() is { } line)
                {
                    Reads?.EventLine();
                    Reads?.ParsedLine();
                    yield return line;
                }
            }
            pending.Write(buffer, start, read - start);
        }
        // Observe a torn record, but never parse or consume it. A later append retries it.
        if (pending.Length > 0) Reads?.EventLine();
        // Bytes appended after EOF have not been observed and must remain discoverable.
        state.Cursors[source] = new(offset, offset + pending.Length, writeTicks,
            creationTicks, Fingerprint(stream, offset), identity);
    }

    private static string Fingerprint(FileStream stream, long offset)
    {
        // Check both the prefix and the consumed boundary, bounded independently of history.
        var bytes = new byte[(int)Math.Min(offset, 4096)];
        stream.Position = 0;
        stream.ReadExactly(bytes);
        var first = Convert.ToHexString(SHA256.HashData(bytes));
        stream.Position = Math.Max(0, offset - bytes.Length);
        stream.ReadExactly(bytes);
        return first + Convert.ToHexString(SHA256.HashData(bytes));
    }
}
