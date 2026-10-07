using Mcg.AgentOrchestrator.Core;

public sealed class AcceptanceApparatusDispositionPolicyTests
{
    [Theory]
    [InlineData(AcceptanceApparatusDisposition.ExcludedUnattributableHold, 1, "", "excluded-outside-scope", "Hold")]
    [InlineData(AcceptanceApparatusDisposition.ApparatusRedRegateHold, 2, "cross-goal-flake", "cross-goal-flake", "Hold")]
    [InlineData(AcceptanceApparatusDisposition.ApparatusRedRegateHold, 2, "candidate-rerun-pass", "candidate-rerun-pass", "Hold")]
    [InlineData(AcceptanceApparatusDisposition.ApparatusRedRegateHold, 2, "candidate-rerun-not-executed", "candidate-rerun-not-executed", "Hold")]
    [InlineData(AcceptanceApparatusDisposition.ApparatusRedBoundExhausted, 3, "infrastructure-exception", "infrastructure-exception", "Escalate")]
    [InlineData(AcceptanceApparatusDisposition.WithinAttemptRerunRegateHold, 4, "within-attempt-rerun-pass", "within-attempt-rerun-pass", "Hold")]
    [InlineData(AcceptanceApparatusDisposition.WithinAttemptRerunBoundExhausted, 5, "within-attempt-rerun-pass", "within-attempt-rerun-pass", "Escalate")]
    public void EachDisposition_AttributesAndReplaysExactDecision(
        AcceptanceApparatusDisposition disposition, int rung, string evidenceKind, string evidence, string action)
    {
        var facts = Facts(disposition) with { EvidenceKind = evidenceKind };
        var expected = AcceptanceApparatusDispositionPolicy.Evaluate(facts).ToRecord();

        Assert.Equal("acceptance-apparatus-disposition", expected.Stage);
        Assert.Equal(action, expected.Action);
        Assert.Equal(rung, expected.Rung);
        Assert.Equal(evidence, expected.DiscriminatingEvidence);
        Assert.Equal("Exact reason.\r\nUnchanged details.", expected.Reason);
        Assert.Equal("A.Test\nZ.Test", expected.Facts.Single(fact => fact.Name == "testIdentities").Value);
        var restored = AcceptanceApparatusDispositionFacts.FromRecordedFacts(expected.Facts);
        Assert.Equal(facts, restored);
        Assert.Equal(facts, AcceptanceApparatusDispositionFacts.FromRecordedFacts(expected.Facts.Reverse().ToArray()));
        var actual = AcceptanceApparatusDispositionPolicy.Evaluate(restored).ToRecord();
        Assert.Equal(expected.Stage, actual.Stage);
        Assert.Equal(expected.Action, actual.Action);
        Assert.Equal(expected.Rung, actual.Rung);
        Assert.Equal(expected.DiscriminatingEvidence, actual.DiscriminatingEvidence);
        Assert.Equal(expected.Reason, actual.Reason);
        Assert.Equal(expected.Facts.ToArray(), actual.Facts.ToArray());
    }

    [Theory]
    [InlineData(AcceptanceApparatusDisposition.ExcludedUnattributableHold)]
    [InlineData(AcceptanceApparatusDisposition.ApparatusRedRegateHold)]
    [InlineData(AcceptanceApparatusDisposition.ApparatusRedBoundExhausted)]
    [InlineData(AcceptanceApparatusDisposition.WithinAttemptRerunRegateHold)]
    [InlineData(AcceptanceApparatusDisposition.WithinAttemptRerunBoundExhausted)]
    public void Replay_RejectsEachMissingRequiredUnknownDuplicateAndNullFact(AcceptanceApparatusDisposition disposition)
    {
        var facts = Facts(disposition).ToRecordedFacts();
        foreach (var fact in facts)
        {
            if (fact.Name is not ("branchHeadSha" or "mainHeadSha"))
                Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionFacts.FromRecordedFacts(
                    facts.Where(item => item.Name != fact.Name).ToArray()));
            Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionFacts.FromRecordedFacts(
                facts.Select(item => item.Name == fact.Name ? item with { Value = null! } : item).ToArray()));
        }
        Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionFacts.FromRecordedFacts(
            facts.Append(new("unknown", "value")).ToArray()));
        Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionFacts.FromRecordedFacts(
            facts.Append(facts[0]).ToArray()));
        Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionFacts.FromRecordedFacts(
            facts.Select(fact => fact.Name == "disposition" ? fact with { Value = "unknown" } : fact).ToArray()));
        Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionFacts.FromRecordedFacts(
            facts.Append(new(null!, "value")).ToArray()));
        Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionFacts.FromRecordedFacts(
            facts.Append(null!).ToArray()));
    }

    [Theory]
    [InlineData(AcceptanceApparatusDisposition.ApparatusRedRegateHold, "regateOrdinal")]
    [InlineData(AcceptanceApparatusDisposition.ApparatusRedRegateHold, "regateCap")]
    [InlineData(AcceptanceApparatusDisposition.ApparatusRedBoundExhausted, "regateCount")]
    [InlineData(AcceptanceApparatusDisposition.WithinAttemptRerunRegateHold, "regateOrdinal")]
    [InlineData(AcceptanceApparatusDisposition.WithinAttemptRerunBoundExhausted, "regateCount")]
    public void Replay_RejectsNonIntegerCount(AcceptanceApparatusDisposition disposition, string name) =>
        Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionFacts.FromRecordedFacts(
            Facts(disposition).ToRecordedFacts().Select(fact => fact.Name == name ? fact with { Value = "invalid" } : fact).ToArray()));

    [Theory]
    [InlineData(AcceptanceApparatusDisposition.ExcludedUnattributableHold)]
    [InlineData(AcceptanceApparatusDisposition.ApparatusRedRegateHold)]
    [InlineData(AcceptanceApparatusDisposition.WithinAttemptRerunRegateHold)]
    [InlineData(AcceptanceApparatusDisposition.WithinAttemptRerunBoundExhausted)]
    public void OptionalHeadsAndEmptyIdentities_ReplayWithoutInventingFacts(AcceptanceApparatusDisposition disposition)
    {
        var facts = Facts(disposition) with { BranchHeadSha = null, MainHeadSha = null, TestIdentities = "" };
        var recorded = facts.ToRecordedFacts();
        Assert.DoesNotContain(recorded, fact => fact.Name is "branchHeadSha" or "mainHeadSha");
        Assert.Equal("", recorded.Single(fact => fact.Name == "testIdentities").Value);
        Assert.Equal(facts, AcceptanceApparatusDispositionFacts.FromRecordedFacts(recorded));
    }

    [Fact]
    public void Replay_RejectsNonCanonicalTestIdentitiesInsteadOfNormalizingEvidence()
    {
        var recorded = Facts(AcceptanceApparatusDisposition.ApparatusRedRegateHold).ToRecordedFacts();
        Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionFacts.FromRecordedFacts(
            recorded.Select(fact => fact.Name == "testIdentities" ? fact with { Value = "Z.Test\nA.Test" } : fact).ToArray()));
        Assert.Throws<InvalidOperationException>(() => Facts(AcceptanceApparatusDisposition.ApparatusRedRegateHold)
            with { TestIdentities = null! });
    }

    [Theory]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData(" 1 ")]
    public void Replay_RejectsNonCanonicalCountInsteadOfChangingRecordedFacts(string value)
    {
        var recorded = Facts(AcceptanceApparatusDisposition.ApparatusRedRegateHold).ToRecordedFacts();
        Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionFacts.FromRecordedFacts(
            recorded.Select(fact => fact.Name == "regateOrdinal" ? fact with { Value = value } : fact).ToArray()));
    }

    [Theory]
    [InlineData(AcceptanceApparatusDisposition.ExcludedUnattributableHold, "evidenceKind")]
    [InlineData(AcceptanceApparatusDisposition.ExcludedUnattributableHold, "regateOrdinal")]
    [InlineData(AcceptanceApparatusDisposition.ExcludedUnattributableHold, "regateCount")]
    [InlineData(AcceptanceApparatusDisposition.ExcludedUnattributableHold, "regateCap")]
    [InlineData(AcceptanceApparatusDisposition.ApparatusRedRegateHold, "regateCount")]
    [InlineData(AcceptanceApparatusDisposition.WithinAttemptRerunRegateHold, "regateCount")]
    [InlineData(AcceptanceApparatusDisposition.ApparatusRedBoundExhausted, "regateOrdinal")]
    [InlineData(AcceptanceApparatusDisposition.WithinAttemptRerunBoundExhausted, "regateOrdinal")]
    public void Record_RejectsFieldsOutsideSchemaInsteadOfDiscardingThem(
        AcceptanceApparatusDisposition disposition, string field)
    {
        var facts = Facts(disposition);
        var invalid = field switch
        {
            "evidenceKind" => facts with { EvidenceKind = "unexpected" },
            "regateOrdinal" => facts with { RegateOrdinal = 1 },
            "regateCount" => facts with { RegateCount = 1 },
            "regateCap" => facts with { RegateCap = 1 },
            _ => throw new InvalidOperationException("Unknown test field.")
        };
        Assert.Throws<InvalidOperationException>(() => invalid.ToRecordedFacts());
    }

    [Theory]
    [InlineData("branch", null)]
    [InlineData(null, "main")]
    public void Record_RejectsHeadsOutsideBoundSchemaInsteadOfDiscardingThem(string? branch, string? main) =>
        Assert.Throws<InvalidOperationException>(() => (Facts(AcceptanceApparatusDisposition.ApparatusRedBoundExhausted)
            with { BranchHeadSha = branch, MainHeadSha = main }).ToRecordedFacts());

    [Fact]
    public void Replay_RejectsFactsOutsideDispositionSchemaAndNullInput()
    {
        var bound = Facts(AcceptanceApparatusDisposition.ApparatusRedBoundExhausted).ToRecordedFacts();
        Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionFacts.FromRecordedFacts(
            bound.Append(new("branchHeadSha", "branch")).ToArray()));
        var excluded = Facts(AcceptanceApparatusDisposition.ExcludedUnattributableHold).ToRecordedFacts();
        Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionFacts.FromRecordedFacts(
            excluded.Append(new("regateCount", "1")).ToArray()));
        Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionFacts.FromRecordedFacts(null!));
        Assert.Throws<ArgumentNullException>(() => AcceptanceApparatusDispositionPolicy.Evaluate(null!));
        Assert.Throws<InvalidOperationException>(() => AcceptanceApparatusDispositionPolicy.Evaluate(
            new("goal", (AcceptanceApparatusDisposition)99)));
    }

    private static AcceptanceApparatusDispositionFacts Facts(AcceptanceApparatusDisposition disposition)
    {
        var excluded = disposition == AcceptanceApparatusDisposition.ExcludedUnattributableHold;
        var regate = disposition is AcceptanceApparatusDisposition.ApparatusRedRegateHold or AcceptanceApparatusDisposition.WithinAttemptRerunRegateHold;
        return new("goal-123", disposition)
        {
            BranchHeadSha = disposition == AcceptanceApparatusDisposition.ApparatusRedBoundExhausted ? null : "branch",
            MainHeadSha = disposition == AcceptanceApparatusDisposition.ApparatusRedBoundExhausted ? null : "main",
            EvidenceKind = excluded ? "" : "observed-evidence-kind",
            RegateOrdinal = regate ? 1 : null,
            RegateCount = !excluded && !regate ? 2 : null,
            RegateCap = excluded ? null : 2,
            TestIdentities = "Z.Test\nA.Test",
            Reason = "Exact reason.\r\nUnchanged details."
        };
    }
}
