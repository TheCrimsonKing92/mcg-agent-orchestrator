using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WorkspaceProbeFaultClassifier
{
    internal static WorkspaceProbeFaultKind Classify(string? detail)
    {
        const string Probe = "probe='git rev-parse --is-inside-work-tree'";
        var normalized = detail?.Replace('_', ' ');
        if (normalized is null || !normalized.Contains("Goal workspaces require", StringComparison.Ordinal))
            return WorkspaceProbeFaultKind.None;
        var probeIndex = normalized.IndexOf(Probe, StringComparison.Ordinal);
        if (probeIndex < 0)
            return WorkspaceProbeFaultKind.None;

        var diagnostic = normalized[(probeIndex + Probe.Length)..];
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var consumed = 0;
        // Anchor each field to the preceding one; quoted output may itself contain semicolons.
        foreach (Match field in Regex.Matches(diagnostic, @"\G; *(?<name>[A-Za-z]+)=(?<value>'[^']*'|[^;']*)"))
        {
            consumed += field.Length;
            if (!fields.TryAdd(field.Groups["name"].Value, field.Groups["value"].Value))
                return WorkspaceProbeFaultKind.Definitive;
        }

        if (consumed != diagnostic.Length || fields.Count != 7 ||
            !fields.TryGetValue("exit", out var exit) || exit != "-1" ||
            !fields.TryGetValue("processStarted", out var started) || started != "True" ||
            !fields.TryGetValue("drainTimedOut", out var drain) || drain is not ("True" or "False") ||
            !fields.TryGetValue("stdoutBytes", out var bytes) || bytes != "0" ||
            !fields.TryGetValue("stdout", out var stdout) || stdout.Length < 2 || stdout[0] != '\'' || stdout[^1] != '\'' ||
            !fields.TryGetValue("stderr", out var stderr) || stderr.Length < 2 || stderr[0] != '\'' || stderr[^1] != '\'' ||
            !stderr[1..^1].Contains("timed out after", StringComparison.Ordinal) ||
            !fields.TryGetValue("directoryExists", out var exists) || exists != "True")
            return WorkspaceProbeFaultKind.Definitive;

        return WorkspaceProbeFaultKind.HostContentionTimeout;
    }

    internal static TimeSpan BackoffAfter(int transientFailureCount) => transientFailureCount switch
    {
        1 => TimeSpan.FromSeconds(120),
        2 => TimeSpan.FromSeconds(300),
        _ => throw new ArgumentOutOfRangeException(nameof(transientFailureCount))
    };

    internal static DateTimeOffset NotBefore(DateTimeOffset completedAt, int transientFailureCount) =>
        completedAt + BackoffAfter(transientFailureCount);
}
