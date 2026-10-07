using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class SourceSizeRatchetPreflight
{
    internal const string CheckName = "source size ratchet preflight";

    private static readonly Regex CeilingPattern = new(
        "new\\s+SourceSizeCeiling\\(\\s*\"(?<path>[^\"]+)\"\\s*,\\s*(?<ceiling>\\d+)\\s*\\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex ClassCeilingPattern = new(
        "new\\s+SourceClassCeiling\\(\\s*\"(?<name>[^\"]+)\"\\s*,\\s*(?<total>\\d+)\\s*,\\s*(?<count>\\d+)\\s*\\)",
        RegexOptions.CultureInvariant);

    internal static IReadOnlyList<SourceClassCeiling> TryReadClassAuthority(string worktreeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeRoot);
        var authorityPath = Path.Combine(
            worktreeRoot,
            SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(authorityPath))
            {
                return [];
            }

            return ClassCeilingPattern.Matches(File.ReadAllText(authorityPath))
                .Select(match => new SourceClassCeiling(
                    match.Groups["name"].Value,
                    int.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture)))
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<SourceSizeCeiling>? TryReadAuthority(string worktreeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeRoot);
        var authorityPath = Path.Combine(
            worktreeRoot,
            SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(authorityPath))
            {
                return null;
            }

            var ceilings = CeilingPattern.Matches(File.ReadAllText(authorityPath))
                .Select(match => new SourceSizeCeiling(
                    match.Groups["path"].Value,
                    int.Parse(match.Groups["ceiling"].Value, CultureInfo.InvariantCulture)))
                .ToArray();
            return ceilings.Length == 0 ? null : ceilings;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return null;
        }
    }

    internal static SourceSizeRatchetPreflightResult Evaluate(
        string worktreeRoot,
        Func<string, IEnumerable<string>>? readLines = null)
    {
        var ceilings = TryReadAuthority(worktreeRoot);
        if (ceilings is null)
        {
            return SourceSizeRatchetPreflightResult.Declined;
        }

        var violations = SourceSizeRatchet.Evaluate(worktreeRoot, ceilings, readLines)
            .Concat(SourceSizeRatchet.EvaluateClasses(worktreeRoot, TryReadClassAuthority(worktreeRoot), readLines))
            .ToArray();
        var blockingViolations = violations
            .Where(violation => violation.ActualLineCount is not null)
            .ToArray();
        return new SourceSizeRatchetPreflightResult(
            blockingViolations.Length > 0,
            string.Join(Environment.NewLine, blockingViolations.Select(violation => violation.Message)),
            violations);
    }

    internal static IReadOnlyList<string> BlockingViolationMessages(
        SourceSizeRatchetPreflightResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Violations
            .Where(violation => violation.ActualLineCount is not null)
            .Select(violation => violation.Message)
            .ToArray();
    }
}

internal sealed record SourceSizeRatchetPreflightResult(
    bool HasBlockingViolation,
    string Message,
    IReadOnlyList<SourceSizeViolation> Violations)
{
    internal static SourceSizeRatchetPreflightResult Declined { get; } = new(false, string.Empty, []);
}
