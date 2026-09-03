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
}
