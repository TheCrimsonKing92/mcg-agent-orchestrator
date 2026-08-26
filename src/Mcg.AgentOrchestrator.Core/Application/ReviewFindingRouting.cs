namespace Mcg.AgentOrchestrator.Core;

public sealed record ReviewRetryRoute(
    AgentRole? TargetRole,
    bool EscalateToOperator,
    string Reason);

public sealed record ReviewFindingOwnerProjection(
    ReviewFinding Finding,
    AgentRole? TargetRole,
    bool EscalateToOperator,
    bool AcceptanceOwned,
    string Reason);

public static class ReviewFindingRouting
{
    public static ReviewRetryRoute Resolve(
        IReadOnlyList<ReviewFinding> openBlockingFindings,
        string blockerProse)
    {
        ArgumentNullException.ThrowIfNull(openBlockingFindings);
        blockerProse ??= string.Empty;

        if (openBlockingFindings.Count > 0 &&
            openBlockingFindings.All(finding => finding.Category == FindingCategory.Unspecified))
        {
            if (IsOperatorOwnedReviewBlocker(blockerProse))
            {
                return new ReviewRetryRoute(null, true, "legacy prose identifies operator-owned evidence");
            }

            return new ReviewRetryRoute(
                InferReviewRetryTargetRole(blockerProse),
                false,
                "legacy prose fallback");
        }

        var projections = Project(openBlockingFindings, blockerProse);
        if (projections.Any(projection => projection.TargetRole == AgentRole.Developer))
        {
            return new ReviewRetryRoute(AgentRole.Developer, false, "writable source/test-code finding requires Developer");
        }

        if (projections.Any(projection => projection.EscalateToOperator))
        {
            return new ReviewRetryRoute(null, true, "operator/specification-owned finding requires operator decision");
        }

        if (projections.Any(projection => projection.TargetRole == AgentRole.Tester))
        {
            return new ReviewRetryRoute(AgentRole.Tester, false, "typed finding requires evidence work");
        }

        if (projections.Any(projection => projection.AcceptanceOwned))
        {
            return new ReviewRetryRoute(null, true, "acceptance-owned finding requires acceptance execution");
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

    public static IReadOnlyList<ReviewFindingOwnerProjection> Project(
        IReadOnlyList<ReviewFinding> findings,
        string blockerProse = "")
    {
        ArgumentNullException.ThrowIfNull(findings);
        blockerProse ??= string.Empty;
        return findings.Select(finding => Project(finding, blockerProse)).ToArray();
    }

    private static ReviewFindingOwnerProjection Project(ReviewFinding finding, string blockerProse) =>
        finding.Category switch
        {
            FindingCategory.OperatorOwned or FindingCategory.SpecDefect =>
                new(finding, null, true, false, "typed operator/specification ownership"),
            FindingCategory.AcceptanceOwned =>
                new(finding, null, false, true, "typed acceptance ownership"),
            FindingCategory.TestEvidence =>
                new(finding, AgentRole.Tester, false, false, "evidence execution without source change"),
            FindingCategory.SpecCompliance or
            FindingCategory.Correctness or
            FindingCategory.TestCoverage or
            FindingCategory.CodeQuality =>
                new(finding, AgentRole.Developer, false, false, "writable source/test-code repair"),
            _ when IsOperatorOwnedReviewBlocker(blockerProse) =>
                new(finding, null, true, false, "legacy prose identifies operator ownership"),
            _ => new(finding, AgentRole.Developer, false, false, "fail-safe unspecified source ownership")
        };

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
