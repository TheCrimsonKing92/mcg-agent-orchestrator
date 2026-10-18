using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteShadowAgreementTests
{
    private const string Miss = "DotnetBaseBuildCacheMissDetailTests.BuildPhase_MissReasonIsOneJournalCompatibleField";
    private const string Runner = "DotnetBuildEnvironmentManagerTestsLocalHostOnlyFocusedRunner.FocusedRunner_Pass_ExecutesUnderLeaseAndWritesReceipt";
    private const string Lock = "DotnetBuildEnvironmentManagerTestsLocalHostOnlyLockAttribution.LockAttributionRestartManagerNamesFileHolder";

    public static IEnumerable<object?[]> Decisions()
    {
        var passed = new AcceptanceCheckResult("focused", true, 0, null);
        var failed = new AcceptanceCheckResult("focused", false, 1, null,
            FailingTestIdentities: [Miss], CompletionDecision: new(false, "failing-trx", false, 1, null, null, "Failed"));
        yield return ["TrxIncomplete", new[] { Miss }, null, "incomparable", "remote-trx-incomplete", null];
        yield return ["Accepted", null, null, "incomparable", "local-unavailable", null];
        yield return ["RemoteRed", new[] { Miss }, failed with
        {
            CompletionDecision = new(false, "timed-out", true, 1, null, null, "Failed"),
            FailureClassification = "build-phase-timeout"
        }, "incomparable", "local-timed-out", null];
        yield return ["RemoteRed", new[] { Miss }, failed with
        {
            FailingTestIdentities = null,
            CompletionDecision = new(false, "timed-out", true, 1, null, null, "Failed")
        }, "incomparable", "local-timed-out", null];
        yield return ["Accepted", null, failed with
        {
            FailureClassification = "build-phase-timeout", FailingTestIdentities = null
        }, "incomparable", "local-apparatus", null];
        yield return ["Accepted", null, failed with { FailingTestIdentities = null },
            "incomparable", "local-no-failing-identities", null];
        yield return ["RemoteRed", null, failed with { FailingTestIdentities = [] },
            "incomparable", "local-no-failing-identities", null];
        yield return ["RemoteRed", null, passed, "incomparable", "remote-no-failing-identities", null];
        yield return ["RemoteRed", Array.Empty<string>(), passed, "incomparable", "remote-no-failing-identities", null];
        yield return ["Accepted", null, passed, "agree", "both-passed", true];
        yield return ["RemoteRed", new[] { Miss }, passed, "disagree", "verdict-differs", false];
        yield return ["Accepted", null, failed, "disagree", "verdict-differs", false];
        yield return ["RemoteRed", new[] { Miss, Runner, Miss }, failed with
        {
            FailingTestIdentities = [Runner, Miss, Runner]
        }, "agree", "same-failing-set", true];
        yield return ["RemoteRed", new[] { Miss, Runner, Lock }, failed,
            "disagree", "failing-set-differs", false];
        yield return ["RemoteRed", new[] { "alphatests.Failure" }, failed with
        {
            FailingTestIdentities = ["AlphaTests.Failure"]
        }, "disagree", "failing-set-differs", false];
        yield return ["RemoteRed", new[] { "AlphaTests.Failure " }, failed with
        {
            FailingTestIdentities = ["AlphaTests.Failure"]
        }, "disagree", "failing-set-differs", false];
    }

    [Theory]
    [MemberData(nameof(Decisions))]
    public void Decide_FixedInputs_UsesFirstMatchingRule(string outcome, string[]? remote,
        AcceptanceCheckResult? local, string agreement, string reason, bool? agree)
    {
        var decision = RemoteShadowAgreement.Decide(Enum.Parse<RemoteLaneOutcomeCode>(outcome), remote, local);
        Assert.Equal(agreement, decision.Agreement);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(agree, decision.Agree);
    }

    [Theory]
    [InlineData("NotEligibleExclusiveResource", "remote-not-eligible-exclusive-resource")]
    [InlineData("ShadowSkippedNoExecutor", "remote-shadow-skipped-no-executor")]
    public void Decide_UnusableRemote_DerivesLedgerOutcomeName(string outcome, string reason)
    {
        var decision = RemoteShadowAgreement.Decide(Enum.Parse<RemoteLaneOutcomeCode>(outcome), [Miss],
            new("focused", true, 0, null));
        Assert.Equal("incomparable", decision.Agreement);
        Assert.Equal(reason, decision.Reason);
        Assert.Null(decision.Agree);
    }
}
