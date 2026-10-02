using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ApparatusRedFailureSummary
{
    private const int MaxEntries = 5;
    private const int MaxLineLength = 200;
    private const string Unavailable = "failure message unavailable";

    internal static string Format(
        IReadOnlyList<string> testIdentities,
        IReadOnlyList<ApparatusRedFailingTest> failingTests)
    {
        var entries = new List<string>();
        foreach (var identity in testIdentities.Take(MaxEntries))
        {
            var failure = failingTests.FirstOrDefault(test =>
                string.Equals(test.TestIdentity, identity, StringComparison.Ordinal));
            var line = FirstFailureLine(failure?.FailureMessage);
            var singleLineIdentity = identity.Replace('\r', ' ').Replace('\n', ' ');
            entries.Add($"{singleLineIdentity}: {line}");
        }

        if (testIdentities.Count > MaxEntries)
        {
            entries.Add($"(+{testIdentities.Count - MaxEntries} more)");
        }

        return string.Join("; ", entries);
    }

    private static string FirstFailureLine(string? message)
    {
        var line = message?.Split(['\r', '\n'])
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        if (line is null)
        {
            return Unavailable;
        }

        line = Regex.Replace(line, @"\s+", " ").Trim();
        return line.Length > MaxLineLength ? line[..MaxLineLength] + "..." : line;
    }
}
