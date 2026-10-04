using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAuthorFrozenFactRulingCheck
{
    internal static string? Evaluate(ConductorAuthorItem item, FrozenFactRuling ruling, string workingDirectory)
    {
        if (item.TargetKind != OperatorAnswerTargetKind.HumanInput)
            return "frozen-fact-ruling-unsupported-target";
        if (Empty(ruling.FrozenClasses)) return Incomplete("frozenClasses");
        if (ruling.AmendedFacts.Count == 0) return Incomplete("amendedFacts");
        foreach (var fact in ruling.AmendedFacts)
        {
            if (string.IsNullOrWhiteSpace(fact.Fact)) return Incomplete("amendedFacts.fact");
            if (string.IsNullOrWhiteSpace(fact.File)) return Incomplete("amendedFacts.file");
            if (string.IsNullOrWhiteSpace(fact.AllowedChange)) return Incomplete("amendedFacts.allowedChange");
        }
        if (string.IsNullOrWhiteSpace(ruling.Basis)) return Incomplete("basis");
        if (string.IsNullOrWhiteSpace(ruling.Unmodified)) return Incomplete("unmodified");
        if (string.IsNullOrWhiteSpace(ruling.DiffCheck)) return Incomplete("diffCheck");
        if (Empty(ruling.EvidenceReferences)) return Incomplete("evidenceReferences");
        foreach (var fact in ruling.AmendedFacts)
        {
            var separator = fact.Fact.LastIndexOf('.');
            if (separator <= 0 || separator == fact.Fact.Length - 1 ||
                !ruling.FrozenClasses.Contains(fact.Fact[..separator], StringComparer.Ordinal))
                return Incomplete("amendedFacts.class");
        }
        foreach (var fact in ruling.AmendedFacts)
            if (!fact.File.StartsWith("tests/", StringComparison.Ordinal) ||
                !fact.File.EndsWith(".cs", StringComparison.Ordinal) || !ExistsUnder(workingDirectory, fact.File))
                return Incomplete("amendedFacts.file");
        if (ruling.FrozenClasses.Any(name => !Regex.IsMatch(ruling.Unmodified,
                @"(?<![\w.])" + Regex.Escape(name) + @"(?![\w.])", RegexOptions.CultureInvariant)))
            return Incomplete("unmodified");
        if (!ruling.EvidenceReferences.Any(reference => reference.StartsWith("tests/", StringComparison.Ordinal)) ||
            !ruling.EvidenceReferences.Any(reference => reference.StartsWith("src/", StringComparison.Ordinal)))
            return Incomplete("evidenceReferences.coverage");
        foreach (var reference in ruling.EvidenceReferences)
        {
            var match = Regex.Match(reference, @"^(?<path>.+?):\d+(?:[-:]\d+)?$", RegexOptions.CultureInvariant);
            if (!match.Success || !ExistsUnder(workingDirectory, match.Groups["path"].Value))
                return Incomplete("evidenceReferences.form");
        }
        return null;
    }

    private static bool Empty(IReadOnlyList<string> values) =>
        values.Count == 0 || values.Any(string.IsNullOrWhiteSpace);
    private static string Incomplete(string field) => "frozen-fact-ruling-incomplete:" + field;

    private static bool ExistsUnder(string directory, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':')) return false;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, relative));
            return path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
