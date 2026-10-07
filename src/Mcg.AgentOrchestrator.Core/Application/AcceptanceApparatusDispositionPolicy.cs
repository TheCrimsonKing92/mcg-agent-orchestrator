using System.Globalization;

namespace Mcg.AgentOrchestrator.Core;

public enum AcceptanceApparatusDispositionAction { Hold, Escalate }

public enum AcceptanceApparatusDisposition
{
    ExcludedUnattributableHold,
    ApparatusRedRegateHold,
    ApparatusRedBoundExhausted,
    WithinAttemptRerunRegateHold,
    WithinAttemptRerunBoundExhausted
}

/// <summary>Observed disposition inputs; absent heads denote inputs not read on this path.</summary>
public sealed record AcceptanceApparatusDispositionFacts(string GoalId, AcceptanceApparatusDisposition Disposition)
{
    public string? BranchHeadSha { get; init; }
    public string? MainHeadSha { get; init; }
    public string EvidenceKind { get; init; } = string.Empty;
    public int? RegateOrdinal { get; init; }
    public int? RegateCount { get; init; }
    public int? RegateCap { get; init; }
    private readonly string _testIdentities = string.Empty;
    public string TestIdentities
    {
        get => _testIdentities;
        init => _testIdentities = value is not null ? CanonicalTestIdentities(value.Split('\n'))
            : throw new InvalidOperationException("Acceptance apparatus disposition requires test identities.");
    }
    public string Reason { get; init; } = string.Empty;

    public static string CanonicalTestIdentities(IEnumerable<string> identities) =>
        string.Join("\n", identities.OrderBy(identity => identity, StringComparer.Ordinal));

    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts()
    {
        if (Disposition == AcceptanceApparatusDisposition.ApparatusRedBoundExhausted &&
            (BranchHeadSha is not null || MainHeadSha is not null))
            throw new InvalidOperationException("Apparatus RED bound facts cannot contain unobserved heads.");
        if ((Disposition == AcceptanceApparatusDisposition.ExcludedUnattributableHold &&
                (EvidenceKind != string.Empty || RegateOrdinal is not null || RegateCount is not null || RegateCap is not null)) ||
            (IsRegate(Disposition) && RegateCount is not null) ||
            (Disposition is AcceptanceApparatusDisposition.ApparatusRedBoundExhausted or
                AcceptanceApparatusDisposition.WithinAttemptRerunBoundExhausted && RegateOrdinal is not null))
            throw new InvalidOperationException("Acceptance apparatus facts contain fields outside the disposition schema.");
        var facts = new List<PolicyDecisionFact>
        {
            new("goalId", GoalId), new("disposition", DispositionName(Disposition))
        };
        if (Disposition != AcceptanceApparatusDisposition.ApparatusRedBoundExhausted)
        {
            if (BranchHeadSha is not null) facts.Add(new("branchHeadSha", BranchHeadSha));
            if (MainHeadSha is not null) facts.Add(new("mainHeadSha", MainHeadSha));
        }
        if (Disposition != AcceptanceApparatusDisposition.ExcludedUnattributableHold)
        {
            facts.Add(new("evidenceKind", EvidenceKind));
            if (IsRegate(Disposition)) facts.Add(new("regateOrdinal", FormatCount(RegateOrdinal)));
            else facts.Add(new("regateCount", FormatCount(RegateCount)));
            facts.Add(new("regateCap", FormatCount(RegateCap)));
        }
        facts.Add(new("testIdentities", TestIdentities));
        facts.Add(new("reason", Reason));
        return facts.AsReadOnly();
    }

    public static AcceptanceApparatusDispositionFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        if (facts is null)
            throw new InvalidOperationException("Acceptance apparatus disposition replay requires named facts.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (fact is null || fact.Name is null || fact.Value is null || !values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException("Invalid or duplicate acceptance apparatus disposition fact.");
        }

        string Read(string name) => values.TryGetValue(name, out var value) ? value
            : throw new InvalidOperationException($"Missing acceptance apparatus disposition fact '{name}'.");
        int ReadCount(string name) => int.TryParse(Read(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) &&
            Read(name) == count.ToString(CultureInfo.InvariantCulture)
            ? count : throw new InvalidOperationException($"Invalid acceptance apparatus disposition count '{name}'.");
        var disposition = Read("disposition") switch
        {
            "excluded-unattributable-hold" => AcceptanceApparatusDisposition.ExcludedUnattributableHold,
            "apparatus-red-regate-hold" => AcceptanceApparatusDisposition.ApparatusRedRegateHold,
            "apparatus-red-bound-exhausted" => AcceptanceApparatusDisposition.ApparatusRedBoundExhausted,
            "within-attempt-rerun-regate-hold" => AcceptanceApparatusDisposition.WithinAttemptRerunRegateHold,
            "within-attempt-rerun-bound-exhausted" => AcceptanceApparatusDisposition.WithinAttemptRerunBoundExhausted,
            var name => throw new InvalidOperationException($"Invalid acceptance apparatus disposition '{name}'.")
        };
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "goalId", "disposition", "testIdentities", "reason" };
        if (disposition != AcceptanceApparatusDisposition.ApparatusRedBoundExhausted)
        {
            allowed.Add("branchHeadSha");
            allowed.Add("mainHeadSha");
        }
        var excluded = disposition == AcceptanceApparatusDisposition.ExcludedUnattributableHold;
        if (!excluded)
        {
            allowed.Add("evidenceKind");
            allowed.Add(IsRegate(disposition) ? "regateOrdinal" : "regateCount");
            allowed.Add("regateCap");
        }
        if (values.Keys.Any(name => !allowed.Contains(name)))
            throw new InvalidOperationException("Unknown acceptance apparatus disposition fact.");
        var identities = Read("testIdentities");
        if (identities != CanonicalTestIdentities(identities.Split('\n')))
            throw new InvalidOperationException("Recorded acceptance apparatus test identities must be ordinal-sorted.");

        return new(Read("goalId"), disposition)
        {
            BranchHeadSha = values.GetValueOrDefault("branchHeadSha"),
            MainHeadSha = values.GetValueOrDefault("mainHeadSha"),
            EvidenceKind = excluded ? string.Empty : Read("evidenceKind"),
            RegateOrdinal = !excluded && IsRegate(disposition) ? ReadCount("regateOrdinal") : null,
            RegateCount = !excluded && !IsRegate(disposition) ? ReadCount("regateCount") : null,
            RegateCap = excluded ? null : ReadCount("regateCap"),
            TestIdentities = identities,
            Reason = Read("reason")
        };
    }

    private static bool IsRegate(AcceptanceApparatusDisposition disposition) =>
        disposition is AcceptanceApparatusDisposition.ApparatusRedRegateHold or AcceptanceApparatusDisposition.WithinAttemptRerunRegateHold;

    private static string FormatCount(int? count) => count?.ToString(CultureInfo.InvariantCulture)
        ?? throw new InvalidOperationException("Missing acceptance apparatus disposition count.");

    private static string DispositionName(AcceptanceApparatusDisposition disposition) => disposition switch
    {
        AcceptanceApparatusDisposition.ExcludedUnattributableHold => "excluded-unattributable-hold",
        AcceptanceApparatusDisposition.ApparatusRedRegateHold => "apparatus-red-regate-hold",
        AcceptanceApparatusDisposition.ApparatusRedBoundExhausted => "apparatus-red-bound-exhausted",
        AcceptanceApparatusDisposition.WithinAttemptRerunRegateHold => "within-attempt-rerun-regate-hold",
        AcceptanceApparatusDisposition.WithinAttemptRerunBoundExhausted => "within-attempt-rerun-bound-exhausted",
        _ => throw new InvalidOperationException($"Invalid acceptance apparatus disposition '{disposition}'.")
    };
}

public sealed record AcceptanceApparatusDispositionDecision(
    AcceptanceApparatusDispositionAction Action,
    int DiscriminatingRung,
    string DiscriminatingEvidence,
    string Reason,
    AcceptanceApparatusDispositionFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(
        AcceptanceApparatusDispositionPolicy.StageName, Action.ToString(), DiscriminatingRung,
        DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

/// <summary>Attributes acceptance apparatus dispositions without owning their effects or rendering their reasons.</summary>
public static class AcceptanceApparatusDispositionPolicy
{
    public const string StageName = "acceptance-apparatus-disposition";

    public static AcceptanceApparatusDispositionDecision ExcludedHold(
        string goalId, string reason, string? branchHeadSha, string? mainHeadSha, IEnumerable<string> identities) =>
        Evaluate(new(goalId, AcceptanceApparatusDisposition.ExcludedUnattributableHold)
        {
            Reason = reason, BranchHeadSha = branchHeadSha, MainHeadSha = mainHeadSha,
            TestIdentities = AcceptanceApparatusDispositionFacts.CanonicalTestIdentities(identities)
        });

    public static AcceptanceApparatusDispositionDecision RegateHold(
        string goalId, AcceptanceApparatusDisposition disposition, string reason,
        string? branchHeadSha, string? mainHeadSha, string evidenceKind, int ordinal, int cap,
        IEnumerable<string> identities)
    {
        if (disposition is not (AcceptanceApparatusDisposition.ApparatusRedRegateHold or
            AcceptanceApparatusDisposition.WithinAttemptRerunRegateHold))
            throw new InvalidOperationException("A re-gate hold requires a re-gate disposition.");
        return Evaluate(new(goalId, disposition)
        {
            Reason = reason, BranchHeadSha = branchHeadSha, MainHeadSha = mainHeadSha,
            EvidenceKind = evidenceKind, RegateOrdinal = ordinal, RegateCap = cap,
            TestIdentities = AcceptanceApparatusDispositionFacts.CanonicalTestIdentities(identities)
        });
    }

    public static AcceptanceApparatusDispositionDecision BoundExhausted(
        string goalId, AcceptanceApparatusDisposition disposition, string reason,
        string evidenceKind, int count, int cap, IEnumerable<string> identities,
        string? branchHeadSha = null, string? mainHeadSha = null)
    {
        if (disposition is not (AcceptanceApparatusDisposition.ApparatusRedBoundExhausted or
            AcceptanceApparatusDisposition.WithinAttemptRerunBoundExhausted))
            throw new InvalidOperationException("A bound escalation requires a bound-exhausted disposition.");
        return Evaluate(new(goalId, disposition)
        {
            Reason = reason, BranchHeadSha = branchHeadSha, MainHeadSha = mainHeadSha,
            EvidenceKind = evidenceKind, RegateCount = count, RegateCap = cap,
            TestIdentities = AcceptanceApparatusDispositionFacts.CanonicalTestIdentities(identities)
        });
    }

    public static AcceptanceApparatusDispositionDecision Evaluate(AcceptanceApparatusDispositionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var (action, rung, evidence) = facts.Disposition switch
        {
            AcceptanceApparatusDisposition.ExcludedUnattributableHold => (AcceptanceApparatusDispositionAction.Hold, 1, "excluded-outside-scope"),
            AcceptanceApparatusDisposition.ApparatusRedRegateHold => (AcceptanceApparatusDispositionAction.Hold, 2, facts.EvidenceKind),
            AcceptanceApparatusDisposition.ApparatusRedBoundExhausted => (AcceptanceApparatusDispositionAction.Escalate, 3, facts.EvidenceKind),
            AcceptanceApparatusDisposition.WithinAttemptRerunRegateHold => (AcceptanceApparatusDispositionAction.Hold, 4, facts.EvidenceKind),
            AcceptanceApparatusDisposition.WithinAttemptRerunBoundExhausted => (AcceptanceApparatusDispositionAction.Escalate, 5, facts.EvidenceKind),
            _ => throw new InvalidOperationException($"Invalid acceptance apparatus disposition '{facts.Disposition}'.")
        };
        return new(action, rung, evidence, facts.Reason, facts);
    }
}
