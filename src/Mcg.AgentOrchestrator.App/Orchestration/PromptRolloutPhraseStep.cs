using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IPromptRolloutGitContent
{
    string Read(string repositoryRoot, params string[] arguments);
}

internal sealed class PromptRolloutGitContent : IPromptRolloutGitContent
{
    public string Read(string repositoryRoot, params string[] arguments)
    {
        var result = GitCli.Run(repositoryRoot, arguments);
        if (!result.Succeeded || result.DrainTimedOut)
            throw new IOException($"Prompt rollout git read failed ({result.ExitCode}): {result.Error}");
        return result.Output;
    }
}

// Reads immutable commit content only; never consults or changes the working tree.
internal static class PromptRolloutPhraseStep
{
    internal const string RoleRequirementsPath = "src/Mcg.AgentOrchestrator.Core/Application/SdlcRolePromptRequirements.cs";
    internal const string OutputDirectivesPath = "src/Mcg.AgentOrchestrator.Core/AgentOutputDirectives.cs";
    private static readonly Regex Backticks = new("`(?<phrase>[^`\\r\\n]+)`");
    private static readonly Regex EscapedQuotes = new(
        """
        \\"(?<phrase>.*?)\\"
        """);
    private static readonly Regex Quotes = new(
        """
        "(?<phrase>(?:\\.|[^"\\])*)"
        """);
    private static readonly Regex Identifiers = new("[A-Za-z0-9]+(?:[_:\\-][A-Za-z0-9]+)+");

    internal static bool IsPromptPath(string path)
    {
        path = path.Replace('\\', '/');
        if (path.Equals(RoleRequirementsPath, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(OutputDirectivesPath, StringComparison.OrdinalIgnoreCase)) return true;
        var parts = path.Split('/');
        return parts.Length == 4 && parts[0].Equals(".agents", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("skills", StringComparison.OrdinalIgnoreCase) && parts[2].Length > 0 &&
            parts[3].Equals("SKILL.md", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> FindNovelPhrases(
        string repositoryRoot, string landingSha, IReadOnlyList<string> changedFiles,
        IPromptRolloutGitContent? git = null)
    {
        var paths = changedFiles.Select(path => path.Replace('\\', '/')).Where(IsPromptPath)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0 || string.IsNullOrWhiteSpace(landingSha)) return [];
        git ??= new PromptRolloutGitContent();
        var parent = git.Read(repositoryRoot, "rev-parse", "--verify", landingSha + "^1").Trim();
        var baseline = git.Read(repositoryRoot, "ls-tree", "-r", "--name-only", parent)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsPromptPath)
            .Select(path => git.Read(repositoryRoot, "show", $"{parent}:{path}")).ToArray();
        var diff = git.Read(repositoryRoot,
            ["diff", "--no-color", "--no-ext-diff", "--no-textconv", "--unified=0", parent, landingSha, "--", .. paths]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return diff.Split('\n').Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            .SelectMany(line => Extract(line[1..]))
            .Where(phrase => seen.Add(phrase) && !baseline.Any(text =>
                text.Contains(phrase, StringComparison.Ordinal) || Unescape(text).Contains(phrase, StringComparison.Ordinal)))
            .Take(32).ToArray();
    }

    private static IEnumerable<string> Extract(string line)
    {
        var spans = new[] { Backticks, EscapedQuotes, Quotes }.SelectMany(regex => regex.Matches(line)
            .Select(match => (match.Index, Phrase: Unescape(match.Groups["phrase"].Value))));
        var identifiers = Identifiers.Matches(line).Where(match => match.Length >= 5)
            .Select(match => (match.Index, Phrase: match.Value));
        return spans.Concat(identifiers).OrderBy(item => item.Index)
            .Select(item => item.Phrase).Where(phrase => !string.IsNullOrWhiteSpace(phrase));
    }

    private static string Unescape(string text) => text.Replace("\\\"", "\"", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);
}
