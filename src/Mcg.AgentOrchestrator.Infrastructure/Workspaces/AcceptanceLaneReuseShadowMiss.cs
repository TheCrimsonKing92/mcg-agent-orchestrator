namespace Mcg.AgentOrchestrator.Infrastructure;

// Observational comparisons only; none of these results authorize cache reuse.
internal static class AcceptanceLaneReuseShadowMiss
{
    internal sealed record ReferenceRow(string GoalId, string AttemptId, string Lane, string PartitionId,
        bool Executed, string? Verdict);
    internal sealed record Result(bool MissEvaluated, string? ReferenceVerdict, string? ReferenceSource,
        bool ShadowMiss, string? MissReason);

    internal static (string Verdict, string Source) ResolveReference(
        IEnumerable<ReferenceRow> rows, string lane, string partitionId)
    {
        var matches = rows.Where(row => row.Lane == lane && row.PartitionId == partitionId && row.Executed)
            .OrderBy(row => row.GoalId, StringComparer.Ordinal)
            .ThenBy(row => row.AttemptId, StringComparer.Ordinal).ToArray();
        var reference = matches.FirstOrDefault(row => row.Verdict == "GREEN") ??
            matches.FirstOrDefault(row => row.Verdict == "RED");
        return reference is null ? ("GREEN", "landing-rule") :
            (reference.Verdict!, $"recorded:{reference.GoalId}/{reference.AttemptId}");
    }

    internal static Result Evaluate(string decision, bool executed, string? verdict,
        (string Verdict, string Source) reference)
    {
        if (decision != "would-reuse" || !executed || verdict is null)
            return new(false, null, null, false, null);
        if (verdict is not ("GREEN" or "RED") || reference.Verdict is not ("GREEN" or "RED"))
            throw new ArgumentException("A shadow comparison requires GREEN or RED verdicts.");
        var miss = verdict != reference.Verdict;
        return new(true, reference.Verdict, reference.Source, miss,
            miss ? verdict == "RED" ? "red-against-green" : "green-against-red" : null);
    }

    internal static string[] ReduceFailingClasses(IReadOnlyList<string>? identities) =>
        (identities ?? []).Select(ReduceToClass).OfType<string>()
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    // Keep the reduction rule aligned with AcceptanceTestReuseShadow.ReduceToClass.
    private static string? ReduceToClass(string identity)
    {
        var normalized = AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity);
        var methodStart = normalized.LastIndexOf('.');
        if (methodStart < 0) return null;
        var type = normalized[..methodStart];
        var nestedStart = type.IndexOf('+');
        if (nestedStart >= 0) type = type[..nestedStart];
        var name = type[(type.LastIndexOf('.') + 1)..];
        var arityStart = name.IndexOf('`');
        return arityStart < 0 ? name : name[..arityStart];
    }
}
