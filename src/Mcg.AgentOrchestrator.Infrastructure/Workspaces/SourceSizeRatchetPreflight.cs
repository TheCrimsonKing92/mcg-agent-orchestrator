using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class SourceSizeRatchetPreflight
{
    private static readonly Regex CeilingPattern = new(
        "new\\s+SourceSizeCeiling\\(\\s*\"(?<path>[^\"]+)\"\\s*,\\s*(?<ceiling>\\d+)\\s*\\)",
        RegexOptions.CultureInvariant);

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

        var violations = SourceSizeRatchet.Evaluate(worktreeRoot, ceilings, readLines);
        var blockingViolations = violations
            .Where(violation => violation.ActualLineCount is not null)
            .ToArray();
        return new SourceSizeRatchetPreflightResult(
            blockingViolations.Length > 0,
            string.Join(Environment.NewLine, blockingViolations.Select(violation => violation.Message)),
            violations);
    }
}

internal sealed record SourceSizeRatchetPreflightResult(
    bool HasBlockingViolation,
    string Message,
    IReadOnlyList<SourceSizeViolation> Violations)
{
    internal static SourceSizeRatchetPreflightResult Declined { get; } = new(false, string.Empty, []);
}
