using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class VerificationOwnershipBriefSettlement
{
    internal const string Label = "Verification ownership:";

    private static readonly Regex ExecutionQuestion = new(
        @"\bwho\b[^?]{0,80}\b(?:execut\w*|runs?)\b[^?]{0,80}\b(?:verification|tests?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool TrySettle(
        string? forkKind, string? topicKey, string question, string objective, out string paragraph)
    {
        paragraph = string.Empty;
        if (!string.Equals(forkKind?.Trim(), "ownership-lifecycle", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (topicKey?.Contains("verification", StringComparison.OrdinalIgnoreCase) != true &&
            !ExecutionQuestion.IsMatch(question))
        {
            return false;
        }

        var lines = objective.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r').Trim();
            if (!line.StartsWith(Label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var paragraphLines = new List<string> { line };
            for (index++; index < lines.Length; index++)
            {
                line = lines[index].TrimEnd('\r').Trim();
                if (line.Length == 0 || Regex.IsMatch(line, @"^#{1,6}(?:\s|$)", RegexOptions.CultureInvariant))
                {
                    break;
                }

                paragraphLines.Add(line);
            }

            var candidate = string.Join(" ", paragraphLines);
            if (string.IsNullOrWhiteSpace(candidate[Label.Length..]))
            {
                return false;
            }

            paragraph = candidate;
            return true;
        }

        return false;
    }
}
