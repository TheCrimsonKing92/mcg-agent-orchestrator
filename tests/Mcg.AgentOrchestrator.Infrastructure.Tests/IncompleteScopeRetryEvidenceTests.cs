using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: exercises only pure projections over in-memory strings and records.
public sealed class IncompleteScopeRetryEvidenceTests
{
    private const string Label = "Previous round's account:";
    private const string ResultBlock = "WORKER_RESULT:\nfiles: src/Feature.cs\ntests: deferred\n" +
        "blockers: none\nassigned_scope_complete: false\nEND_WORKER_RESULT";
    private static readonly DateTimeOffset CompletedAt = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Account_FortyProseLines_KeepsOrderedFieldsAndLastTwentyFiveLines()
    {
        var lines = Enumerable.Range(0, 40).Select(index => $"account line {index:D2}").ToArray();
        var output = string.Join("\n", lines) + "\n" + ResultBlock;

        var account = IncompleteScopeRetryEvidence.BuildPreviousRoundAccount(output);

        var expected = string.Join(Environment.NewLine,
            new[] { Label, "files: src/Feature.cs", "tests: deferred", "blockers: none",
                "assigned_scope_complete: false" }.Concat(lines.Skip(15)));
        Assert.Equal(expected, account);
    }

    [Theory]
    [InlineData("closing prose without a result")]
    [InlineData(null)]
    public void Account_MissingMarker_ReturnsUnavailable(string? output)
    {
        Assert.Equal(Label + Environment.NewLine + "previous round account unavailable",
            IncompleteScopeRetryEvidence.BuildPreviousRoundAccount(output));
    }

    [Fact]
    public void Account_TwoBlocks_KeepsOnlyLastFieldsAndInterveningProse()
    {
        var output = "earlier prose\nWORKER_RESULT:\nfiles: old.cs\nEND_WORKER_RESULT\n" +
            "final account\nWORKER_RESULT:\nfiles: current.cs\ntests: deferred\nEND_WORKER_RESULT";

        var account = IncompleteScopeRetryEvidence.BuildPreviousRoundAccount(output);

        Assert.Equal(string.Join(Environment.NewLine,
            Label, "files: current.cs", "tests: deferred", string.Empty, "final account"), account);
        Assert.DoesNotContain("files: old.cs", account, StringComparison.Ordinal);
        Assert.DoesNotContain("earlier prose", account, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_ThreeThousandCharacters_KeepsLastTwentyFiveHundred()
    {
        var prose = string.Concat(Enumerable.Range(0, 300).Select(index => $"{index:D9}|"));

        var account = IncompleteScopeRetryEvidence.BuildPreviousRoundAccount(
            prose + "\nWORKER_RESULT:\nEND_WORKER_RESULT");

        Assert.Equal(3000, prose.Length);
        Assert.Equal(Label + Environment.NewLine + prose[500..], account);
    }

    [Fact]
    public void Account_CrLfAndLf_ProduceIdenticalAccounts()
    {
        var lf = "first line\nsecond line\n" + ResultBlock;
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.Equal(IncompleteScopeRetryEvidence.BuildPreviousRoundAccount(lf),
            IncompleteScopeRetryEvidence.BuildPreviousRoundAccount(crlf));
    }

    [Fact]
    public void Account_IndentedFields_KeepsKnownFieldsInCanonicalOrder()
    {
        var output = "WORKER_RESULT:\nother: drop me\nassigned_scope_complete: false\n" +
            "blockers: none\ntests: deferred\n  - files: current.cs\nEND_WORKER_RESULT";

        var account = IncompleteScopeRetryEvidence.BuildPreviousRoundAccount(output);

        Assert.Equal(string.Join(Environment.NewLine, Label, "  - files: current.cs",
            "tests: deferred", "blockers: none", "assigned_scope_complete: false"), account);
        Assert.DoesNotContain("other:", account, StringComparison.Ordinal);
    }

    [Fact]
    public void Selector_DifferentCompletionTimes_ReturnsNewestMatchingPayload()
    {
        var older = Receipt("alpha", "abc1234", "older.trx", "Older.Test");
        var newer = Receipt("beta", "abc1234", "newer.trx", "Newer.Test");
        var wrongSha = Receipt("wrong", "other-sha", "wrong.trx", "Wrong.Test");
        TaskVerificationRecord[] verifications = [
            Verification(older, CompletedAt),
            Verification(newer, CompletedAt.AddMinutes(1)),
            Verification(wrongSha, CompletedAt.AddMinutes(2))
        ];

        var selected = IncompleteScopeRetryEvidence.SelectNewestCandidateRed(verifications, "abc1234");

        Assert.True(selected.HasValue);
        Assert.Equal("beta", selected.Value.ReceiptId);
        Assert.Equal(new[] { "Newer.Test" }, selected.Value.FailingTestIdentities);
        Assert.Equal(new[] { "newer.trx" }, selected.Value.TestResultPaths);
    }

    [Fact]
    public void Selector_EqualCompletionTimes_UsesOrdinalReceiptId()
    {
        TaskVerificationRecord[] verifications = [
            Verification(Receipt("beta", "abc1234", "beta.trx", "Beta.Test"), CompletedAt),
            Verification(Receipt("alpha", "abc1234", "alpha.trx", "Alpha.Test"), CompletedAt)
        ];

        var selected = IncompleteScopeRetryEvidence.SelectNewestCandidateRed(verifications, "abc1234");

        Assert.True(selected.HasValue);
        Assert.Equal("alpha", selected.Value.ReceiptId);
        Assert.Equal(new[] { "Alpha.Test" }, selected.Value.FailingTestIdentities);
        Assert.Equal(new[] { "alpha.trx" }, selected.Value.TestResultPaths);
    }

    [Fact]
    public void Selector_MixedCaseSha_MatchesCandidate()
    {
        var verification = Verification(Receipt("matching", "AbC1234", "candidate.trx", "Test"), CompletedAt);

        var selected = IncompleteScopeRetryEvidence.SelectNewestCandidateRed([verification], "aBc1234");

        Assert.True(selected.HasValue);
        Assert.Equal("matching", selected.Value.ReceiptId);
    }

    [Theory]
    [InlineData(FindingEvidenceArm.Baseline, "abc1234", FindingEvidenceArmDisposition.Red)]
    [InlineData(FindingEvidenceArm.Candidate, "other-sha", FindingEvidenceArmDisposition.Red)]
    [InlineData(FindingEvidenceArm.Candidate, "abc1234", FindingEvidenceArmDisposition.Green)]
    public void Selector_IneligibleReceipt_ReturnsNull(
        FindingEvidenceArm arm, string sha, FindingEvidenceArmDisposition disposition)
    {
        var receipt = Receipt("ineligible", sha, "opaque.trx", "Test", arm);
        receipt = receipt with { Arms = [receipt.Arms![0] with { Disposition = disposition }] };

        Assert.Null(IncompleteScopeRetryEvidence.SelectNewestCandidateRed(
            [Verification(receipt, CompletedAt)], "abc1234"));
    }

    [Fact]
    public void Selector_EmptyVerificationList_ReturnsNull()
    {
        Assert.Null(IncompleteScopeRetryEvidence.SelectNewestCandidateRed([], "abc1234"));
    }

    [Fact]
    public void Selector_NullPayload_KeepsEmptyIdentitiesAndNullPaths()
    {
        var receipt = Receipt("matching", "abc1234", "unused.trx", "Unused.Test");
        receipt = receipt with { Arms = [receipt.Arms![0] with {
            FailingTestIdentities = null, TestResultPaths = null }] };

        var selected = IncompleteScopeRetryEvidence.SelectNewestCandidateRed(
            [Verification(receipt, CompletedAt)], "abc1234");

        Assert.True(selected.HasValue);
        Assert.Empty(selected.Value.FailingTestIdentities);
        Assert.Null(selected.Value.TestResultPaths);
    }

    private static FindingEvidenceReceipt Receipt(string id, string sha, string path, string identity,
        FindingEvidenceArm arm = FindingEvidenceArm.Candidate)
    {
        var receipt = ConductorDriverTestsIncompleteScopeRetryContext.Receipt(id, sha, path, arm);
        return receipt with { Arms = [receipt.Arms![0] with { FailingTestIdentities = [identity] }] };
    }

    private static TaskVerificationRecord Verification(FindingEvidenceReceipt receipt, DateTimeOffset completedAt) =>
        new("test.exe", "C:\\tmp", 0, "ok", "", completedAt, FindingEvidenceReceipts: [receipt]);
}
