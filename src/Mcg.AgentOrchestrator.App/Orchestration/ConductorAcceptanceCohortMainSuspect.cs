using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Shared untouched test sources are evidence to suspect main, not to blame either candidate.
internal static class ConductorAcceptanceCohortMainSuspect
{
    internal static readonly string FailureToken = System.Text.Json.JsonNamingPolicy.KebabCaseLower
        .ConvertName(PostLandingCanaryFailureReason.MainSuspect.ToString());

    internal static IReadOnlyList<string>? TryDecide(
        ConductorAcceptanceCohortIdentityAttribution classified,
        IReadOnlyList<AcceptanceCohortMemberBinding> bindings,
        string workspacePath,
        Func<string, IReadOnlyList<string>> resolveSources)
    {
        if (classified.Outcome != AcceptanceCohortAttributionOutcome.BothMembersFailed ||
            classified.AttributedMembers.Count != 2 || bindings.Count != 2)
            return null;

        var first = classified.AttributedMembers[0].ReproducedFailingTests;
        var second = classified.AttributedMembers[1].ReproducedFailingTests;
        if (first.Count == 0 || !first.ToHashSet(StringComparer.Ordinal).SetEquals(second))
            return null;

        var isUntouched = CreateUntouchedSourcePredicate(bindings.SelectMany(member => member.LandingPaths),
            workspacePath, resolveSources);
        foreach (var test in first)
            if (!isUntouched(test)) return null;
        return first.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<string> KeepUntouched(IEnumerable<string> tests,
        IEnumerable<string> candidateChangedPaths, string workspacePath,
        Func<string, IReadOnlyList<string>> resolveSources)
        => tests.Where(CreateUntouchedSourcePredicate(candidateChangedPaths, workspacePath, resolveSources))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static Func<string, bool> CreateUntouchedSourcePredicate(
        IEnumerable<string> candidateChangedPaths, string workspacePath,
        Func<string, IReadOnlyList<string>> resolveSources)
    {
        var changedPaths = candidateChangedPaths
            .Select(path => NormalizePath(workspacePath, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourcesByClass = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        return test =>
        {
            var className = AcceptanceTestSourceResolver.ExtractClassName(test);
            if (string.IsNullOrWhiteSpace(className)) return false;
            if (!sourcesByClass.TryGetValue(className, out var sources))
            {
                sources = resolveSources(test);
                sourcesByClass.Add(className, sources);
            }
            return sources.Count > 0 && !sources.Any(path => changedPaths.Contains(NormalizePath(workspacePath, path)));
        };
    }

    internal static string EventId(string sha, string cohortId) =>
        $"post-landing-canary:{sha}:{FailureToken}:{cohortId}";

    internal static string SoloEventId(string sha, string fingerprint) =>
        $"post-landing-canary:{sha}:{FailureToken}:solo:{fingerprint}";

    internal static string FormatTests(IReadOnlyList<string> tests) =>
        string.Join(",", tests.Take(10).Select(test =>
            string.Concat(test.Select(character => char.IsWhiteSpace(character) ? '_' : character)))) +
        (tests.Count > 10 ? $" more={tests.Count - 10}" : string.Empty);

    private static string NormalizePath(string workspacePath, string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized))
            normalized = Path.GetRelativePath(workspacePath, normalized).Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }
}
