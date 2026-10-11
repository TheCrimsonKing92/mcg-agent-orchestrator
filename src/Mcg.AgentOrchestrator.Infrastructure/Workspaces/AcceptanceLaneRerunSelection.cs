using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A plan describes only selectors that the existing wildcard translation can safely carry.
internal static class AcceptanceLaneRerunSelection
{
    internal const int MaxNarrowedMethods = 25;
    internal const string NoIdentities = "no-identities";
    internal const string BlankIdentity = "blank-identity";
    internal const string UnknownIdentity = "unknown-identity";
    internal const string AssemblyCleanupIdentity = "assembly-cleanup-identity";
    internal const string NoClassForm = "no-class-form";
    internal const string UnsafeSelectorCharacter = "unsafe-selector-character";
    internal const string TooManyMethods = "too-many-methods";
    private const string AssemblyCleanupPrefix = "[Test Assembly Cleanup Failure";

    internal sealed record Plan(IReadOnlyList<FocusedEvidenceFilterToken> Tokens, string? FallbackReason)
    {
        internal bool IsNarrowed => FallbackReason is null;
        internal static Plan WholeLane(string reason) => new([], reason);
    }

    internal static Plan Create(IReadOnlyList<string> identities)
    {
        if (identities.Count == 0)
            return Plan.WholeLane(NoIdentities);

        var selectors = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            if (string.IsNullOrWhiteSpace(identity))
                return Plan.WholeLane(BlankIdentity);
            if (identity.Trim().Equals("unknown test", StringComparison.Ordinal))
                return Plan.WholeLane(UnknownIdentity);
            if (identity.StartsWith(AssemblyCleanupPrefix, StringComparison.Ordinal))
                return Plan.WholeLane(AssemblyCleanupIdentity);

            var selector = AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity);
            if (!selector.Contains('.') || selector.Split('.').Any(segment => segment.Length == 0))
                return Plan.WholeLane(NoClassForm);
            if (selector.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '+')))
                return Plan.WholeLane(UnsafeSelectorCharacter);
            selectors.Add(selector);
        }

        if (selectors.Count > MaxNarrowedMethods)
            return Plan.WholeLane(TooManyMethods);

        return new(selectors.Select(selector => new FocusedEvidenceFilterToken(
            selector, "FullyQualifiedName~" + selector, FocusedEvidenceTokenKind.Method,
            selector, selector[..selector.LastIndexOf('.')])).ToArray(), null);
    }
}
