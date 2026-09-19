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

        var preview = EvidenceRetentionPolicy.EvaluatePath(
            true,
            first,
            EvidenceOwnershipSource.Declared,
            protectedAttempt: false,
            referencedArtifact: false,
            countBound: false,
            aged: true,
            byteBoundEligible: false,
            firstRevision);
        var execution = EvidenceRetentionPolicy.EvaluatePath(
            true,
            first,
            EvidenceOwnershipSource.Declared,
            protectedAttempt: false,
            referencedArtifact: false,
            countBound: false,
            aged: true,
            byteBoundEligible: false,
            firstRevision);

        Assert.Equal(preview, execution);
        Assert.Equal(EvidenceEligibility.DeleteWhenSafe, execution.Disposition);
        Assert.NotEqual(firstRevision, secondRevision);
    }
}
