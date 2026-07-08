namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalEventsCommand
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    public static async Task RunAsync(
        string eventsDirectory,
        string goalPrefix,
        bool follow,
        TextWriter output,
        IEnumerable<string>? knownGoalIds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(goalPrefix))
        {
            throw new ArgumentException("goal-events requires <goal-prefix>.");
        }

        var path = ResolveEventFilePath(eventsDirectory, goalPrefix, knownGoalIds);
        Directory.CreateDirectory(eventsDirectory);

        var position = 0L;
        if (File.Exists(path))
        {
            position = await WriteAvailableLinesAsync(path, 0, output, cancellationToken);
        }

        if (!follow)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!File.Exists(path))
            {
                path = ResolveEventFilePath(eventsDirectory, goalPrefix, knownGoalIds);
            }

            if (File.Exists(path))
            {
                position = await WriteAvailableLinesAsync(path, position, output, cancellationToken);
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static string ResolveEventFilePath(
        string eventsDirectory,
        string goalPrefix,
        IEnumerable<string>? knownGoalIds)
    {
        var exact = Path.Combine(eventsDirectory, $"{goalPrefix}.jsonl");
        if (File.Exists(exact))
        {
            return exact;
        }

        var knownMatches = (knownGoalIds ?? [])
            .Where(goalId => goalId.StartsWith(goalPrefix, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (knownMatches.Length > 1)
        {
            throw new InvalidOperationException($"Goal prefix '{goalPrefix}' matches multiple known goals.");
        }

        if (knownMatches.Length == 1)
        {
            return Path.Combine(eventsDirectory, $"{knownMatches[0]}.jsonl");
        }

        if (!Directory.Exists(eventsDirectory))
        {
            return exact;
        }

        var matches = Directory.EnumerateFiles(eventsDirectory, $"{goalPrefix}*.jsonl")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidOperationException($"Goal prefix '{goalPrefix}' matches multiple event files.");
        }

        return matches.Length == 1 ? matches[0] : exact;
    }

    private static async Task<long> WriteAvailableLinesAsync(
        string path,
        long position,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (position > stream.Length)
        {
            position = 0;
        }

        stream.Seek(position, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            await output.WriteLineAsync(line.AsMemory(), cancellationToken);
            await output.FlushAsync(cancellationToken);
        }

        return stream.Position;
    }
}
