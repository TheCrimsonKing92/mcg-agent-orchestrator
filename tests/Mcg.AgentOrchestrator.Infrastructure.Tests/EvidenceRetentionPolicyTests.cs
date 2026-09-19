using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class EvidenceRetentionPolicyTests
{
    [Xunit.Fact]
    public void ProtectedAttempts_OrdinalPrecedesMutableTimestamp()
    {
        var attempts = new[]
        {
            new RetentionAttemptIdentity("older-failure", 4, DateTimeOffset.Parse("2026-08-20T12:00:00Z"), Failed: true, Reconciled: true),
            new RetentionAttemptIdentity("final-pass", 5, DateTimeOffset.Parse("2026-08-19T12:00:00Z"), Failed: false, Reconciled: true),
            new RetentionAttemptIdentity("early-pass", 3, DateTimeOffset.Parse("2026-08-21T12:00:00Z"), Failed: false, Reconciled: true)
        };

        var protectedIds = EvidenceRetentionPolicy.ProtectedAttemptIds(attempts);

        Assert.Equal(2, protectedIds.Count);
        Assert.Contains("older-failure", protectedIds);
        Assert.Contains("final-pass", protectedIds);
        Assert.DoesNotContain("early-pass", protectedIds);
    }

    [Xunit.Fact]
    public void ProtectedAttempts_EqualIdentityUsesAttemptIdTieBreak()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        var attempts = new[]
        {
            new RetentionAttemptIdentity("attempt-a", 7, startedAt, Failed: false, Reconciled: true),
            new RetentionAttemptIdentity("attempt-b", 7, startedAt, Failed: false, Reconciled: true)
        };

        var protectedIds = EvidenceRetentionPolicy.ProtectedAttemptIds(attempts);

        Assert.Equal(["attempt-b"], protectedIds);
    }

    [Xunit.Fact]
    public void CountBound_NeverEvictsProtectedLastFailure()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        var attempts = Enumerable.Range(1, 22)
            .Select(ordinal => new RetentionAttemptIdentity(
                $"attempt-{ordinal:D2}",
                ordinal,
                startedAt.AddMinutes(ordinal),
                Failed: ordinal == 1,
                Reconciled: true))
            .ToArray();
        var protectedIds = EvidenceRetentionPolicy.ProtectedAttemptIds(attempts);

        var expired = EvidenceRetentionPolicy.AttemptIdsPastCountBound(attempts, 20, protectedIds);

        Assert.Single(expired);
        Assert.Contains("attempt-02", expired);
        Assert.DoesNotContain("attempt-01", expired);
        Assert.DoesNotContain("attempt-22", expired);
    }

    [Xunit.Fact]
    public void ResolveOwner_DeclaredIdentityPrecedesFilenameInference()
    {
        var path = Path.GetFullPath(Path.Combine("retention-policy", "final.out.log"));
        var attempts = new[]
        {
            new RetentionAttemptIdentity("old", 1, DateTimeOffset.Parse("2026-08-20T12:00:00Z"), false, true, [path]),
            new RetentionAttemptIdentity("final", 2, DateTimeOffset.Parse("2026-08-21T12:00:00Z"), false, true, [Path.ChangeExtension(path, ".metadata.json")])
        };

        var owner = EvidenceRetentionPolicy.ResolveOwner(attempts, path);

        Assert.Equal("old", owner.Attempt?.AttemptId);
        Assert.Equal(EvidenceOwnershipSource.Declared, owner.Source);
        Assert.False(owner.Ambiguous);
    }

    [Xunit.Fact]
    public void ResolveOwner_LegacyMetadataUsesConservativeNameInference()
    {
        var attempt = new RetentionAttemptIdentity(
            "legacy",
            1,
            DateTimeOffset.Parse("2026-08-20T12:00:00Z"),
            false,
            true);

        var owner = EvidenceRetentionPolicy.ResolveOwner([attempt], Path.GetFullPath("legacy.out.log"));

        Assert.Equal(attempt, owner.Attempt);
        Assert.Equal(EvidenceOwnershipSource.InferredFromName, owner.Source);
    }

    [Xunit.Fact]
    public void DeclaredPathsAndNameInferenceAgreeOnLegacyFixtures()
    {
        var root = Path.GetFullPath("retention-shadow-fixtures");
        var paths = new[]
        {
            Path.Combine(root, "legacy.out.log"),
            Path.Combine(root, "legacy.err.log"),
            Path.Combine(root, "legacy.exit.txt"),
            Path.Combine(root, "legacy.heartbeat.json"),
            Path.Combine(root, "legacy.result.json"),
            Path.Combine(root, "legacy.attempt.json"),
            Path.Combine(root, "legacy.trx"),
            Path.Combine(root, "legacy.receipts", "receipt.json")
        };
        var legacy = new RetentionAttemptIdentity(
            "legacy",
            1,
            DateTimeOffset.Parse("2026-08-20T12:00:00Z"),
            false,
            true);

        foreach (var path in paths)
        {
            var declared = legacy with { DeclaredPaths = [path] };

            Assert.True(EvidenceRetentionPolicy.OwnsPath(legacy, path, out var inferredSource));
            Assert.True(EvidenceRetentionPolicy.OwnsPath(declared, path, out var declaredSource));
            Assert.Equal(EvidenceOwnershipSource.InferredFromName, inferredSource);
            Assert.Equal(EvidenceOwnershipSource.Declared, declaredSource);
        }
    }

    [Xunit.Fact]
    public void Eligibility_SameFactsShareRevisionAndOwnerChangeRevisesIt()
    {
        var path = Path.GetFullPath("attempt.out.log");
        var first = new RetentionAttemptIdentity(
            "attempt",
            1,
            DateTimeOffset.Parse("2026-08-20T12:00:00Z"),
            false,
            true,
            [path]);
        var firstRevision = EvidenceRetentionPolicy.ComputeFactRevision([first], [path]);
        var secondRevision = EvidenceRetentionPolicy.ComputeFactRevision([first with { Ordinal = 2 }], [path]);

        var facts = new EvidenceRetentionFacts(
            TerminalGoal: true,
            first,
            EvidenceOwnershipSource.Declared,
            ProtectedAttempt: false,
            ReferencedArtifact: false,
            CountBound: false,
            Aged: true,
            ByteBoundEligible: false,
            firstRevision);
        var eligibility = EvidenceRetentionPolicy.EvaluatePath(facts);

        Assert.Equal(EvidenceEligibility.DeleteWhenSafe, eligibility.Disposition);
        Assert.Equal(firstRevision, eligibility.FactRevision);
        Assert.NotEqual(firstRevision, secondRevision);
    }
}
