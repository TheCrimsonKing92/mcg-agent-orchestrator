using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record GateShardProcessFacts(int CurrentProcessId, Func<int, DateTimeOffset?> StartTimeForProcess)
{
    internal static GateShardProcessFacts System { get; } = new(Environment.ProcessId, pid =>
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    });
}

internal sealed class GateShardWaiterMarkers(string permitDirectory, GateShardProcessFacts processFacts)
{
    private readonly string _directory = Path.Combine(permitDirectory, "waiters");

    internal string Publish()
    {
        var startTime = processFacts.StartTimeForProcess(processFacts.CurrentProcessId)
            ?? throw new InvalidOperationException("Cannot identify the gate waiter process start time.");
        Directory.CreateDirectory(_directory);
        var marker = Path.Combine(_directory, $"gate-{processFacts.CurrentProcessId}-{Guid.NewGuid():N}.json");
        var temporary = marker + ".tmp";
        try
        {
            File.WriteAllText(temporary,
                $"{processFacts.CurrentProcessId.ToString(CultureInfo.InvariantCulture)}\n{startTime.ToUniversalTime():O}\n");
            File.Move(temporary, marker);
            return marker;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    internal bool Exists(string path) => File.Exists(path);

    internal bool HasLiveWaiter()
    {
        if (!Directory.Exists(_directory)) return false;
        var found = false;
        foreach (var path in Directory.EnumerateFiles(_directory, "gate-*.json"))
        {
            if (IsLive(path)) found = true;
            else TryDelete(path);
        }
        return found;
    }

    private bool IsLive(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var pidLine = reader.ReadLine();
            var startLine = reader.ReadLine();
            if (reader.ReadLine() is not null ||
                !int.TryParse(pidLine, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                !DateTimeOffset.TryParseExact(startLine, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var recordedStart))
                return false;
            return processFacts.StartTimeForProcess(pid) is { } actualStart && actualStart == recordedStart;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
