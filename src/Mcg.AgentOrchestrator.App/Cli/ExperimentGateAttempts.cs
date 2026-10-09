using Mcg.AgentOrchestrator.App.OwnerConsole;

namespace Mcg.AgentOrchestrator.App.Cli;

/// <summary>Reads completed fleet-wide gate attempts, retaining their first completion instant.</summary>
internal static class ExperimentGateAttempts
{
    internal static IReadOnlyCollection<DateTimeOffset> Read(string conductEventsLogPath)
    {
        var attempts = new Dictionary<AttemptIdentity, DateTimeOffset>();
        var fullPath = Path.GetFullPath(conductEventsLogPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        string[] rotated;
        try
        {
            rotated = Directory.GetFiles(directory,
                $"{Path.GetFileNameWithoutExtension(fullPath)}-*{Path.GetExtension(fullPath)}",
                SearchOption.TopDirectoryOnly);
        }
        catch (DirectoryNotFoundException) { return []; }

        foreach (var path in rotated.Prepend(fullPath))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    if (!ConductEventFileSource.TryParse(line, out var item)) continue;
                    var completed = item!.EventKind switch
                    {
                        "acceptance" => Token(item.Detail, "result=") is { } result &&
                            (result.Equals("passed", StringComparison.OrdinalIgnoreCase) ||
                             result.Equals("failed", StringComparison.OrdinalIgnoreCase)),
                        "acceptance-cohort" => item.Detail.StartsWith("ACCEPTANCE_COHORT_CHILD_COMPLETED ", StringComparison.Ordinal),
                        _ => false
                    };
                    if (!completed) continue;
                    // Lifecycle completions can be emitted again on later ticks with new detail and
                    // timestamps. Events without an attempt id use the entire parsed event instead.
                    var attemptId = Token(item.Detail, "attempt=");
                    var identity = !string.IsNullOrEmpty(attemptId)
                        ? new AttemptIdentity(item.EventKind, attemptId, null, null, null)
                        : new AttemptIdentity(item.EventKind, null, item.GoalId, item.Timestamp, item.Detail);
                    if (!attempts.TryGetValue(identity, out var previous) || item.Timestamp < previous)
                        attempts[identity] = item.Timestamp;
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return attempts.Values.ToArray();
    }

    internal static int Count(IReadOnlyCollection<DateTimeOffset> attempts, DateTimeOffset since, DateTimeOffset asOf) =>
        attempts.Count(at => at >= since && at <= asOf);

    private static string? Token(string detail, string prefix) => detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(token => token.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private readonly record struct AttemptIdentity(string Kind, string? AttemptId, string? GoalId,
        DateTimeOffset? Timestamp, string? Detail);
}
