namespace Mcg.AgentOrchestrator.App.Orchestration;

// One run's unexpected starts; planned renewals and supervision transfers never enter this ledger.
internal sealed class ConductorSupervisorRestartLedger(TimeSpan window)
{
    private readonly Queue<Entry> _entries = [];

    internal int Count => _entries.Count;

    internal void Record(DateTimeOffset now, int attempt, int processId, int exitCode,
        string failure, string? stderrPath)
    {
        while (_entries.TryPeek(out var oldest) && now - oldest.At > window) _entries.Dequeue();
        _entries.Enqueue(new Entry(now, attempt, processId, exitCode, failure, stderrPath));
    }

    internal string Describe() => string.Join(" | ", _entries.Select(entry =>
        $"attempt={entry.Attempt} pid={entry.ProcessId} exit={entry.ExitCode} " +
        $"failure={entry.Failure} stderr={ReadLastLine(entry.StderrPath)}"));

    private static string ReadLastLine(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return "none";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? last = null;
            while (reader.ReadLine() is { } line)
                if (!string.IsNullOrWhiteSpace(line)) last = line;
            return last is null ? "none" : last[..Math.Min(last.Length, 200)];
        }
        catch (FileNotFoundException) { return "none"; }
        catch (DirectoryNotFoundException) { return "none"; }
        catch { return "unreadable"; }
    }

    private sealed record Entry(DateTimeOffset At, int Attempt, int ProcessId, int ExitCode,
        string Failure, string? StderrPath);
}
