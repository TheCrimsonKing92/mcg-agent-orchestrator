using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BoardFillPreflightChecks
{
    internal static IReadOnlyList<AuthorBriefDraftCheck> Run(string markdown)
    {
        var findings = BriefLint.Lint(markdown);
        var blocking = findings.Where(finding => finding.Severity is
            BriefLintSeverity.BlocksDispatch or BriefLintSeverity.BlocksCliStart).Select(finding => finding.Kind).Distinct().ToArray();
        var noisy = Regex.Matches(markdown, @"[\w.\-/\\*]+", RegexOptions.CultureInvariant)
            .Select(match => match.Value).Where(token => token.Contains('/') || token.Contains('\\'))
            .Where(token => token.Split('/', '\\').Any(segment =>
                segment.TrimStart('.') is var folder &&
                new[] { "scratch", "artifacts", "bin", "obj" }.Contains(folder, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        AuthorBriefDraftCheck Finding(string kind) => new("preflight:" + kind,
            !findings.Any(finding => finding.Kind == kind),
            string.Join("; ", findings.Where(finding => finding.Kind == kind).Select(finding => finding.Message)));
        return
        [
            new("preflight:brief-lint-blocking", blocking.Length == 0, string.Join(", ", blocking)),
            Finding("inline-heading-split"),
            new("preflight:build-output-path", noisy.Length == 0, string.Join(", ", noisy)),
            Finding("missing-test-removal-bullet")
        ];
    }
}
