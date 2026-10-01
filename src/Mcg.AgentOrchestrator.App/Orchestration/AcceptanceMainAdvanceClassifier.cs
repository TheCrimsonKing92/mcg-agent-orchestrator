using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal abstract record AcceptanceMainAdvanceDisposition
{
    internal sealed record Unchanged : AcceptanceMainAdvanceDisposition;

    internal sealed record Disjoint(
        IReadOnlyList<string> LandedPaths,
        IReadOnlyList<string> VerifiedPaths) : AcceptanceMainAdvanceDisposition;

    internal sealed record Overlap(
        string LandedPath,
        string VerifiedPath,
        string Kind) : AcceptanceMainAdvanceDisposition;

    internal sealed record Unknown(string Reason) : AcceptanceMainAdvanceDisposition;

    internal string FormatReceipt()
    {
        const int maxPaths = 8;
        const int maxLength = 1024;
        var receipt = this switch
        {
            Unchanged => "ACCEPTANCE_MAIN_ADVANCE disposition=unchanged",
            Disjoint disjoint =>
                $"ACCEPTANCE_MAIN_ADVANCE disposition=disjoint rule=path-level-independence " +
                $"landed={FormatPaths(disjoint.LandedPaths, maxPaths)} " +
                $"verified={FormatPaths(disjoint.VerifiedPaths, maxPaths)}",
            Overlap overlap =>
                $"ACCEPTANCE_MAIN_ADVANCE disposition=overlap kind={overlap.Kind} " +
                $"landed={overlap.LandedPath} verified={overlap.VerifiedPath}",
            Unknown unknown => $"ACCEPTANCE_MAIN_ADVANCE disposition=unknown reason={unknown.Reason}",
            _ => "ACCEPTANCE_MAIN_ADVANCE disposition=unknown reason=unsupported-disposition"
        };

        return receipt.Length <= maxLength ? receipt : receipt[..maxLength];
    }

    private static string FormatPaths(IReadOnlyList<string> paths, int maxPaths)
    {
        var rendered = string.Join(',', paths.Take(maxPaths));
        return paths.Count <= maxPaths ? rendered : $"{rendered},...(+{paths.Count - maxPaths})";
    }
}

internal static class AcceptanceMainAdvanceClassifier
{
    internal static AcceptanceMainAdvanceDisposition Classify(
        string verifiedMainSha,
        string currentMainSha,
        IReadOnlyList<string> expectedScopePaths,
        IReadOnlyList<string> verifiedDeltaPaths,
        IReadOnlyList<string> postRebaseDeltaPaths,
        IReadOnlyList<string> landedPaths)
    {
        if (string.Equals(verifiedMainSha, currentMainSha, StringComparison.OrdinalIgnoreCase))
        {
            return new AcceptanceMainAdvanceDisposition.Unchanged();
        }

        var expected = Normalize(expectedScopePaths);
        var verified = Normalize(verifiedDeltaPaths);
        var postRebase = Normalize(postRebaseDeltaPaths);
        var landed = Normalize(landedPaths);
        if (expected.Count == 0 || verified.Count == 0)
        {
            return new AcceptanceMainAdvanceDisposition.Unknown("empty-verified-scope");
        }

        if (landed.Count == 0)
        {
            return new AcceptanceMainAdvanceDisposition.Unknown("empty-main-advance-delta");
        }

        if (!SetEquals(expected, verified))
        {
            return new AcceptanceMainAdvanceDisposition.Unknown("recorded-scope-does-not-match-verified-delta");
        }

        if (!SetEquals(verified, postRebase))
        {
            return new AcceptanceMainAdvanceDisposition.Unknown("candidate-scope-drift-after-rebase");
        }

        foreach (var verifiedPath in verified)
        {
            var ownership = RepositoryOwnershipMap.Classify(verifiedPath);
            if (!IsSafeForPathLevelCarry(ownership.Area))
            {
                return new AcceptanceMainAdvanceDisposition.Overlap(
                    landed[0],
                    verifiedPath,
                    ownership.Area.ToString());
            }
        }

        foreach (var landedPath in landed)
        {
            var ownership = RepositoryOwnershipMap.Classify(landedPath);
            if (!IsSafeForPathLevelCarry(ownership.Area))
            {
                return new AcceptanceMainAdvanceDisposition.Overlap(
                    landedPath,
                    "repository-wide",
                    ownership.Area.ToString());
            }

            foreach (var verifiedPath in verified)
            {
                var overlap = RepositoryPathOverlap.Classify(landedPath, verifiedPath);
                if (overlap != PathOverlapKind.None)
                {
                    return new AcceptanceMainAdvanceDisposition.Overlap(
                        landedPath,
                        verifiedPath,
                        overlap.ToString());
                }
            }
        }

        // This is deliberately only path-level independence. Unknown inputs and scope drift fail closed.
        return new AcceptanceMainAdvanceDisposition.Disjoint(landed, verified);
    }

    // These areas are repository-local path scopes. Every other current or future area fails closed.
    private static bool IsSafeForPathLevelCarry(RepositoryOwnershipArea area) =>
        area is RepositoryOwnershipArea.Source or
            RepositoryOwnershipArea.Test or
            RepositoryOwnershipArea.Documentation;

    private static IReadOnlyList<string> Normalize(IEnumerable<string> paths) =>
        paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(RepositoryPathOverlap.Normalize)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool SetEquals(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count &&
        left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
}
