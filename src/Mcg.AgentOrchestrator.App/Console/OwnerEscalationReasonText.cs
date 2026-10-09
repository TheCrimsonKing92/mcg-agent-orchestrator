using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Interpret conductor reasons only at the presentation boundary; retain the source in Detail.
internal static class OwnerEscalationReasonText
{
    private const RegexOptions Matching = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    internal static bool IsAuthorQuestion(string? reason) => Regex.IsMatch(Normalize(reason),
        @"^Resolve (?:the )?spec clarification(?: item)? before dispatching planner work\b", Matching);

    internal static string Plain(string? reason, string fallback, Func<string?, string, string> words,
        Func<string, string?>? roleOfTask = null)
    {
        var firstLine = OwnerHoldReason.Read(reason);
        if (string.IsNullOrWhiteSpace(firstLine)) return fallback;
        var normalized = Normalize(firstLine);
        // Literals originate in FailedGoalRecoveryPolicy and LandingRebasePolicy.
        var retry = Regex.Match(normalized,
            @"^Task (?<task>[a-f0-9]{8}) exhausted bounded real-failure retries(?: \((?<used>\d+)/(?<max>\d+)\))?", Matching);
        if (retry.Success)
        {
            var worker = roleOfTask?.Invoke(retry.Groups["task"].Value) ?? "the worker";
            var count = retry.Groups["used"].Success ?
                $" ({retry.Groups["used"].Value} of {retry.Groups["max"].Value})" : "";
            return worker + " failed and used up its retries" + count + ".";
        }
        // Match the encoded source, preserving real underscores and vocabulary inside paths.
        var rebase = Regex.Match(firstLine,
            @"^pre[-_ ](?:landing|merge)[_ ]+rebase[_ ]+conflict(?:[_ ]+with[_ ]+(?<branch>[^_ ()]+))?[_ ]*\((?<files>[^)]+)\)", Matching);
        if (rebase.Success)
        {
            var files = string.Join(", ", rebase.Groups["files"].Value.Split(',').Select(file => file.Trim(' ', '_')));
            var branch = rebase.Groups["branch"].Success ? rebase.Groups["branch"].Value : TrunkBranchName.Default;
            return $"conflicts with {branch} in {files}; needs a rebase.";
        }
        if (IsAuthorQuestion(firstLine))
        {
            var question = Clean(words(firstLine, fallback));
            question = Regex.Replace(question, @" for goal [a-f0-9]{8}\b", "", Matching);
            return "question for the Author: " + question.TrimEnd('.') + ".";
        }
        var plain = Clean(words(firstLine, fallback));
        return plain.Length == 0 ? fallback : plain.TrimEnd('.', '!', '?') + ".";
    }

    private static string Normalize(string? reason) => Regex.Replace(
        (OwnerHoldReason.Read(reason) ?? "").Replace('_', ' '), @"\s+", " ").Trim();

    private static string Clean(string text)
    {
        text = text.Replace('_', ' ');
        var marker = Regex.Match(text,
            @"codex\s+exec|workspace\s+rebase|attention\s+show|\bmcg(?:-orchestrator(?:\.cmd)?\b|\s|$)|`| \$ |command:", Matching);
        if (marker.Success) text = text[..marker.Index];
        text = Regex.Replace(text, @"\s+", " ").Trim();
        // Avoid leaving a partial 'Run ...' hint after a complete explanatory sentence.
        var sentenceEnd = text.IndexOf(". ", StringComparison.Ordinal);
        if (sentenceEnd >= 0) text = text[..(sentenceEnd + 1)];
        text = Regex.Replace(text, @"(?:;\s*)?\b(?:Run|use)\s*['""]?$", "", Matching);
        return text.TrimEnd(' ', ';', ':', ',', '\'', '"');
    }
}
