namespace Mcg.AgentOrchestrator.Core;

public sealed record ReviewRetryRoute(
    AgentRole? TargetRole,
    bool EscalateToOperator,
    string Reason);

public static class ReviewFindingRouting
{
    public static ReviewRetryRoute Resolve(
        IReadOnlyList<ReviewFinding> openBlockingFindings,
        string blockerProse)
    {
        ArgumentNullException.ThrowIfNull(openBlockingFindings);
        blockerProse ??= string.Empty;

        if (openBlockingFindings.Any(finding =>
                finding.Category is FindingCategory.OperatorOwned or FindingCategory.SpecDefect))
        {
            return new ReviewRetryRoute(null, true, "typed operator-owned or spec-defect finding");
        }

        if (openBlockingFindings.Count > 0 &&
            openBlockingFindings.All(finding =>
                finding.Category is FindingCategory.TestEvidence or FindingCategory.TestCoverage))
        {
            return new ReviewRetryRoute(AgentRole.Tester, false, "all typed findings require test work");
        }

        if (openBlockingFindings.Any(finding =>
                finding.Category is FindingCategory.SpecCompliance
                    or FindingCategory.Correctness
                    or FindingCategory.CodeQuality))
        {
            return new ReviewRetryRoute(AgentRole.Developer, false, "typed finding requires source work");
        }

        if (IsOperatorOwnedReviewBlocker(blockerProse))
        {
            return new ReviewRetryRoute(null, true, "legacy prose identifies operator-owned evidence");
        }

        return new ReviewRetryRoute(
            InferReviewRetryTargetRole(blockerProse),
            false,
            "legacy prose fallback");
    }

    private static AgentRole InferReviewRetryTargetRole(string blocker)
    {
        var text = blocker.ToLowerInvariant();
        return text.Contains("tester", StringComparison.Ordinal) ||
            text.Contains("test-execution", StringComparison.Ordinal) ||
            text.Contains("test execution", StringComparison.Ordinal) ||
            text.Contains("test receipt", StringComparison.Ordinal) ||
            text.Contains("verification command", StringComparison.Ordinal)
            ? AgentRole.Tester
            : AgentRole.Developer;
    }

    private static bool IsOperatorOwnedReviewBlocker(string blocker)
    {
        var text = blocker.ToLowerInvariant();
        return text.Contains("operator-owned", StringComparison.Ordinal) ||
            text.Contains("operator owned", StringComparison.Ordinal) ||
            text.Contains("operator receipt", StringComparison.Ordinal) ||
            text.Contains("operator receipts", StringComparison.Ordinal) ||
            text.Contains("measurement mandate", StringComparison.Ordinal) ||
            text.Contains("measurement mandates", StringComparison.Ordinal) ||
            text.Contains("operator evidence", StringComparison.Ordinal) ||
            text.Contains("human_input", StringComparison.Ordinal) ||
            text.Contains("human input", StringComparison.Ordinal);
    }
}
