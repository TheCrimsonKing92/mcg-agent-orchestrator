namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteLaneNotExecutedVerdict(bool Accept, string? FirstUndeclared);

internal static class RemoteLaneNotExecutedDecision
{
    internal static RemoteLaneNotExecutedVerdict Decide(int? count,
        RemoteLaneNotExecutedIdentities results, IReadOnlySet<string> declared)
    {
        // A subset of the receipt cannot prove that every unexecuted test was declared.
        if (count is null or < 0 || results.Unreadable || results.Identities.Count < count)
            return new(false, "unidentified");
        var normalizedDeclarations = declared.Select(AcceptanceTrxTestIdentityResolver.NormalizeSelector)
            .ToHashSet(StringComparer.Ordinal);
        var undeclared = results.Identities.Order(StringComparer.Ordinal).FirstOrDefault(identity =>
            !normalizedDeclarations.Contains(AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity)));
        return new(undeclared is null, undeclared);
    }
}
