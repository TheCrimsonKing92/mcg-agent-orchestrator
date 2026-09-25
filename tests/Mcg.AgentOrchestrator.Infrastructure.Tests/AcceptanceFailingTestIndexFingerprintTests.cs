using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class AcceptanceFailingTestIndexFingerprintTests
{
    [Fact]
    public void VolatilePathsDurationsIdsAndTimestampsNormalize()
    {
        var first = @"failure at C:\Temp\run-one\out.log after 1532ms id 84b2f846-839d-4fd9-a6b7-1e30df8ddf2a on 2026-09-24T12:00:00Z";
        var second = @"failure at D:\Temp\run-two\out.log after 2710ms id 3746ba61-a65a-4218-b230-76790801790b on 2026-09-25T13:00:00Z";

        Assert.Equal(AcceptanceFailingTestIndex.ComputeMessageFingerprint(first),
            AcceptanceFailingTestIndex.ComputeMessageFingerprint(second));
        Assert.Equal("failure at <path> after <dur> id <id> on <time>",
            AcceptanceFailingTestIndex.NormalizeFailureMessage(first));
    }

    [Fact]
    public void CountsAndIdentifierLikeTokensRemainDistinct()
    {
        Assert.NotEqual(
            AcceptanceFailingTestIndex.ComputeMessageFingerprint("expected 2, actual 3"),
            AcceptanceFailingTestIndex.ComputeMessageFingerprint("expected 2, actual 4"));
        Assert.NotEqual(
            AcceptanceFailingTestIndex.ComputeMessageFingerprint("offending class X"),
            AcceptanceFailingTestIndex.ComputeMessageFingerprint("offending class Y"));
    }

    [Fact]
    public void HexRunIdsNormalizeWithoutCollapsingBareNumbers()
    {
        var first = "run 0123456789abcdef0123456789abcdef01234567 expected 2";
        var second = "run fedcba9876543210fedcba9876543210fedcba98 expected 2";

        Assert.Equal(AcceptanceFailingTestIndex.ComputeMessageFingerprint(first),
            AcceptanceFailingTestIndex.ComputeMessageFingerprint(second));
        Assert.Equal("run <id> expected 2", AcceptanceFailingTestIndex.NormalizeFailureMessage(first));
    }

    [Fact]
    public void MissingMessagesAndLegacyRecordsCannotCrossMatch()
    {
        Assert.Null(AcceptanceFailingTestIndex.ComputeMessageFingerprint(null));
        Assert.Null(AcceptanceFailingTestIndex.ComputeMessageFingerprint(" \t "));
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z", null);
        var legacy = new AcceptanceFailingTestIndexRecord(
            AcceptanceFailingTestIndex.ContractVersion,
            AcceptanceFailingTestIndexKinds.GateFailure,
            "goal-a", now.AddHours(-1), TestIdentity: "Sample.Tests.Guard.Fails");
        var currentFingerprint = AcceptanceFailingTestIndex.ComputeMessageFingerprint("failure");

        Assert.False(AcceptanceFailingTestIndex.HasCrossGoalOccurrence(
            [legacy], "goal-b", "Sample.Tests.Guard.Fails", currentFingerprint, now, TimeSpan.FromHours(72)));
        Assert.False(AcceptanceFailingTestIndex.HasCrossGoalOccurrence(
            [legacy with { MessageFingerprint = currentFingerprint }],
            "goal-b", "Sample.Tests.Guard.Fails", null, now, TimeSpan.FromHours(72)));
    }

    [Fact]
    public void JsonlRoundTripPreservesFingerprintAndReadsLegacyLine()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z", null);
            var index = new AcceptanceFailingTestIndex(Path.Combine(root, AcceptanceFailingTestIndex.FileName));
            var fingerprint = AcceptanceFailingTestIndex.ComputeMessageFingerprint("failure");
            index.Append([
                new AcceptanceFailingTestIndexRecord(
                    AcceptanceFailingTestIndex.ContractVersion,
                    AcceptanceFailingTestIndexKinds.GateFailure,
                    "goal-a", now, TestIdentity: "Sample.Tests.Guard.Fails", MessageFingerprint: fingerprint)
            ], now);
            File.AppendAllText(index.Path,
                "{\"contractVersion\":1,\"kind\":\"gate-failure\",\"goalId\":\"goal-b\",\"recordedAt\":\"2026-09-25T12:00:00Z\",\"testIdentity\":\"Sample.Tests.Guard.Fails\"}" + Environment.NewLine);

            var records = index.Read();

            Assert.Equal(2, records.Count);
            Assert.Equal(fingerprint, records[0].MessageFingerprint);
            Assert.Null(records[1].MessageFingerprint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
