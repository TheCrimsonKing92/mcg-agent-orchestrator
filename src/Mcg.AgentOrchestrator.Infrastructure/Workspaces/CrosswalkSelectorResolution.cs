using Mcg.AgentOrchestrator.Core;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Resolves explicit selectors against discovery and preserves each unmatched selector as a refusal.</summary>
internal sealed class CrosswalkSelectorResolution
{
    internal required string[] Selected { get; init; }
    internal required string[] Refused { get; init; }
    internal required string[] DiscoveredFiles { get; init; }

    internal static CrosswalkSelectorResolution Resolve(IReadOnlyList<string> selectors, IReadOnlyList<string> discoveredIds)
    {
        var normalized = selectors.Select(CrosswalkFocusedEvidence.NormalizeSelector).Distinct(StringComparer.Ordinal).ToArray();
        var files = discoveredIds.Select(CrosswalkFocusedEvidence.FileSelector).Distinct(StringComparer.Ordinal).ToArray();
        // Discovery owns the file vocabulary; a dotted suite such as PZISCollectionUtils.Set is still a suite.
        var extensions = files.Select(Path.GetExtension).Where(extension => extension.Length > 0).ToHashSet(StringComparer.Ordinal);
        var suiteVariants = normalized.ToDictionary(selector => selector, SuiteVariants, StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<string>();
        foreach (var id in discoveredIds)
        {
            var include = false;
            foreach (var selector in normalized)
            {
                if (!Matches(selector, id, extensions, suiteVariants[selector])) continue;
                matched.Add(selector);
                include = true;
            }
            if (include) selected.Add(id);
        }
        return new CrosswalkSelectorResolution
        {
            Selected = selected.Distinct(StringComparer.Ordinal).ToArray(),
            Refused = normalized.Where(selector => !matched.Contains(selector)).ToArray(),
            DiscoveredFiles = files.Order(StringComparer.Ordinal).Take(10).ToArray()
        };
    }

    private static bool Matches(string selector, string id, IReadOnlySet<string> extensions, IReadOnlyList<string> variants)
    {
        var normalizedId = CrosswalkFocusedEvidence.NormalizeSelector(id);
        if (selector.Contains("::", StringComparison.Ordinal))
            return string.Equals(selector, normalizedId, StringComparison.Ordinal);
        // Normalization intentionally preserves the existing backslash-as-file-path convention.
        if (selector.Contains('/') || extensions.Contains(Path.GetExtension(selector)))
            return string.Equals(selector, CrosswalkFocusedEvidence.FileSelector(id), StringComparison.Ordinal);
        // Preserve file equality even for extensionless, root-level discovered files.
        if (string.Equals(selector, CrosswalkFocusedEvidence.FileSelector(id), StringComparison.Ordinal)) return true;
        return MatchesSuite(selector, id, extensions, variants);
    }

    internal static bool MatchesSuite(
        string selector, string id, IReadOnlySet<string> extensions, IReadOnlyList<string>? variants = null)
    {
        selector = CrosswalkFocusedEvidence.NormalizeSelector(selector);
        if (selector.Contains("::", StringComparison.Ordinal) || selector.Contains('/') ||
            extensions.Contains(Path.GetExtension(selector)) ||
            string.Equals(selector, CrosswalkFocusedEvidence.FileSelector(id), StringComparison.Ordinal)) return false;
        var normalizedId = CrosswalkFocusedEvidence.NormalizeSelector(id);
        var separator = normalizedId.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0) return false;
        var name = normalizedId[(separator + 2)..];
        return (variants ?? SuiteVariants(selector)).Any(variant =>
            string.Equals(variant, name, StringComparison.Ordinal) || name.StartsWith(variant + " ", StringComparison.Ordinal));
    }

    // Callers matching multiple rows prepare the bounded variants once per selector.
    internal static string[] SuiteVariants(string selector)
    {
        selector = CrosswalkFocusedEvidence.NormalizeSelector(selector);
        var dots = new List<int>();
        for (var index = 1; index < selector.Length - 1; index++)
        {
            if (selector[index] != '.' || !IsIdentifier(selector[index - 1]) || !IsIdentifier(selector[index + 1])) continue;
            dots.Add(index);
            if (dots.Count > 8) return [selector];
        }

        var variants = new string[1 << dots.Count];
        variants[0] = selector;
        for (var mask = 1; mask < variants.Length; mask++)
        {
            var characters = selector.ToCharArray();
            for (var bit = 0; bit < dots.Count; bit++)
                if ((mask & (1 << bit)) != 0) characters[dots[bit]] = ' ';
            variants[mask] = new string(characters);
        }
        return variants;
    }

    private static bool IsIdentifier(char character) => char.IsLetterOrDigit(character) || character == '_';

    internal static string RefusalDetail(string selector, IReadOnlyList<string> files) =>
        $"selector matched no discovered test: {selector}; discovered test files: {string.Join(", ", files)}";

    internal static bool TryBuildRejection(
        string request, IReadOnlyList<Check> checks, IReadOnlyList<AcceptanceCheckResult> results,
        out FocusedEvidenceRejection rejection)
    {
        rejection = null!;
        if (checks.Count == 0 || checks.Any(check => !CrosswalkFocusedEvidence.IsEvidenceCheck(check) || check.Arguments.Count == 0) ||
            results.Count != checks.Count) return false;
        var details = new List<string>();
        foreach (var check in checks)
        {
            var result = results.SingleOrDefault(result => result.Name == check.Name);
            if (result is null || result.Passed || result.DiscoveredTestCount is null || result.ExecutedTestCount != 0 ||
                result.NormalizedTestResultPaths is { Count: > 0 } || result.CrosswalkDiagnostics is not { Count: > 0 } diagnostics ||
                diagnostics.Count != check.Arguments.Count || diagnostics.Any(d => d.Category != CrosswalkDiagnosticCategory.InvalidSelection))
                return false;
            foreach (var selector in check.Arguments)
            {
                var prefix = RefusalDetail(selector, []);
                var diagnostic = diagnostics.SingleOrDefault(d => d.Detail.StartsWith(prefix, StringComparison.Ordinal));
                if (diagnostic is null) return false;
                details.Add(diagnostic.Detail);
            }
        }
        rejection = new(FocusedEvidenceRejectionCode.UnresolvableSelection, request, string.Join("; ", details));
        return true;
    }
}
