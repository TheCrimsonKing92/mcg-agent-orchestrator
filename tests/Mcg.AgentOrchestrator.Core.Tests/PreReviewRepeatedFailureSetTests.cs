using Mcg.AgentOrchestrator.Core;

public sealed class PreReviewRepeatedFailureSetTests
{
    [Fact]
    public void EqualSetsAcrossNewCandidatesCountAndThirdRoundHolds()
    {
        var history = new[]
        {
            Receipt("sha-1", ["Example.A", "Example.B"]),
            Receipt("sha-2", ["Example.B", "Example.A"]),
            Receipt("sha-3", ["Example.A", "Example.B"])
        };

        var second = PreReviewRepeatedFailureSet.Evaluate(history[..2]);
        var third = PreReviewRepeatedFailureSet.Evaluate(history);

        Assert.Equal(2, second.ConsecutiveRounds);
        Assert.True(second.HasRepeat);
        Assert.False(second.HoldRequired);
        Assert.Equal(3, third.ConsecutiveRounds);
        Assert.True(third.HoldRequired);
        Assert.Equal(["Example.A", "Example.B"], third.RepeatedTests);
    }

    [Fact]
    public void ChangedIdentityAndPassingRoundResetCount()
    {
        var changed = PreReviewRepeatedFailureSet.Evaluate([
            Receipt("sha-1", ["Example.A", "Example.B"]),
            Receipt("sha-2", ["Example.A", "Example.C"])]);
        var passed = PreReviewRepeatedFailureSet.Evaluate([
            Receipt("sha-1", ["Example.A"]),
            Receipt("sha-2", [], PreReviewEvidenceDisposition.Green)]);

        Assert.Equal(1, changed.ConsecutiveRounds);
        Assert.False(changed.HasRepeat);
        Assert.Equal(0, passed.ConsecutiveRounds);
    }

    [Fact]
    public void SameCandidateDoesNotAddADeveloperRound()
    {
        var summary = PreReviewRepeatedFailureSet.Evaluate([
            Receipt("sha-1", ["Example.A"]),
            Receipt("sha-1", ["Example.A"])]);

        Assert.Equal(1, summary.ConsecutiveRounds);
    }

    [Fact]
    public void InterleavedSameCandidateEndsConsecutiveRun()
    {
        var latestRepeated = PreReviewRepeatedFailureSet.Evaluate([
            Receipt("sha-1", ["Example.A"]),
            Receipt("sha-2", ["Example.A"]),
            Receipt("sha-3", ["Example.A"]),
            Receipt("sha-3", ["Example.A"])]);
        var olderRepeated = PreReviewRepeatedFailureSet.Evaluate([
            Receipt("sha-1", ["Example.A"]),
            Receipt("sha-2", ["Example.A"]),
            Receipt("sha-2", ["Example.A"]),
            Receipt("sha-3", ["Example.A"])]);

        Assert.Equal(1, latestRepeated.ConsecutiveRounds);
        Assert.False(latestRepeated.HoldRequired);
        Assert.Equal(2, olderRepeated.ConsecutiveRounds);
        Assert.False(olderRepeated.HoldRequired);
    }

    private static PreReviewEvidenceReceipt Receipt(
        string sha, string[] identities, PreReviewEvidenceDisposition disposition = PreReviewEvidenceDisposition.Red) =>
        new("goal", 1, sha, [], disposition, 0, 1, [], identities, "mapped", null, DateTimeOffset.UtcNow);
}
