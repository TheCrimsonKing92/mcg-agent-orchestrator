using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: effects are synchronous recording delegates with no shared state.
public sealed class VerifiedGateStartExecutorTests
{
    [Fact]
    public void PreflightSummarySkipsAcceptanceAndPreservesInstance()
    {
        var summary = new AcceptanceVerificationSummary(false, []);
        var calls = new List<string>();

        var result = VerifiedGateStartExecutor.Execute(
            () => { calls.Add("preflight"); return summary; },
            () => { calls.Add("acceptance"); return new(true, []); });

        Assert.Equal(new[] { "preflight" }, calls);
        Assert.Same(summary, result.Summary);
        Assert.Equal(new VerifiedGateStartExecution(summary, null, null, null, null, null, null), result);
    }

    [Fact]
    public void NullPreflightRunsAcceptanceOnceAfterPreflightAndPreservesInstance()
    {
        var summary = new AcceptanceVerificationSummary(true, []);
        var calls = new List<string>();

        var result = VerifiedGateStartExecutor.Execute(
            () => { calls.Add("preflight"); return null; },
            () => { calls.Add("acceptance"); return summary; });

        Assert.Equal(new[] { "preflight", "acceptance" }, calls);
        Assert.Same(summary, result.Summary);
        Assert.Equal(new VerifiedGateStartExecution(summary, null, null, null, null, null, null), result);
    }

    [Fact]
    public void InfrastructureDeferralCarriesOnlyReasonCodeAndMessage()
    {
        var exception = new AcceptanceInfrastructureDeferredException("probe-code", null, "tail");

        var result = VerifiedGateStartExecutor.Execute(() => null, () => throw exception);

        Assert.Equal("infrastructure-deferred", result.DeferralKind);
        Assert.Equal("probe-code", result.InfrastructureDeferredReasonCode);
        Assert.Equal(exception.Message, result.InfrastructureDeferredMessage);
        Assert.Equal(new VerifiedGateStartExecution(
            null, VerifiedGateStartExecutor.InfrastructureDeferred, "probe-code", exception.Message, null, null, null), result);
    }

    [Fact]
    public void BusySlotsCarriesOnlyOriginalSlotsInstance()
    {
        var slots = new DotnetBuildLeaseAcquisition.SlotsBusy(
            "goal-slots-busy", [new DotnetBuildStableSlotWait(0, 12345)]);

        var result = VerifiedGateStartExecutor.Execute(() => null, () => throw new DotnetBuildSlotsBusyException(slots));

        Assert.Equal("build-slots-busy", result.DeferralKind);
        Assert.Same(slots, result.SlotsBusy);
        Assert.Equal(new VerifiedGateStartExecution(
            null, VerifiedGateStartExecutor.BuildSlotsBusy, null, null, slots, null, null), result);
    }

    [Fact]
    public void BlockedLockCarriesOnlyOriginalAttributionInstance()
    {
        var attribution = new BuildLockAttribution(
            @"C:\probe\locked.dll", [new BuildLockHolder(7, "devenv", null, false)], "test");

        var result = VerifiedGateStartExecutor.Execute(() => null, () => throw new BuildLockBlockedException(attribution));

        Assert.Equal("build-lock-blocked", result.DeferralKind);
        Assert.Same(attribution, result.BuildLockAttribution);
        Assert.Equal(new VerifiedGateStartExecution(
            null, VerifiedGateStartExecutor.BuildLockBlocked, null, null, null, attribution, null), result);
    }

    [Fact]
    public void CancelledAttemptCarriesOnlyCancellationCause()
    {
        var exception = new AcceptanceAttemptCancelledException(
            AcceptanceAttemptCancellationDecision.Cancel(AcceptanceAttemptCancellationCause.StoppedDisposition));

        var result = VerifiedGateStartExecutor.Execute(() => null, () => throw exception);

        Assert.Equal("attempt-cancelled", result.DeferralKind);
        Assert.Equal("StoppedDisposition", result.CancellationCause);
        Assert.Equal(new VerifiedGateStartExecution(
            null, VerifiedGateStartExecutor.AttemptCancelled, null, null, null, null, "StoppedDisposition"), result);
    }

    [Fact]
    public void PreflightBusySlotsSkipsAcceptanceAndCarriesOnlyOriginalSlotsInstance()
    {
        var slots = new DotnetBuildLeaseAcquisition.SlotsBusy("preflight", [new DotnetBuildStableSlotWait(1, 12345)]);
        var acceptanceCalls = 0;

        var result = VerifiedGateStartExecutor.Execute(
            () => throw new DotnetBuildSlotsBusyException(slots),
            () => { acceptanceCalls++; return new(true, []); });

        Assert.Equal(0, acceptanceCalls);
        Assert.Equal("build-slots-busy", result.DeferralKind);
        Assert.Same(slots, result.SlotsBusy);
        Assert.Equal(new VerifiedGateStartExecution(
            null, VerifiedGateStartExecutor.BuildSlotsBusy, null, null, slots, null, null), result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlainCancellationPropagatesSameInstance(bool fromPreflight)
    {
        var exception = new OperationCanceledException("plain cancellation");

        Assert.Same(exception, Assert.Throws<OperationCanceledException>(() =>
            VerifiedGateStartExecutor.Execute(
                () => fromPreflight ? throw exception : null,
                () => throw exception)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnlistedExceptionPropagatesSameInstance(bool fromPreflight)
    {
        var exception = new InvalidOperationException("unlisted failure");

        Assert.Same(exception, Assert.Throws<InvalidOperationException>(() =>
            VerifiedGateStartExecutor.Execute(
                () => fromPreflight ? throw exception : null,
                () => throw exception)));
    }

    [Theory]
    [InlineData(VerifiedGateStartExecutor.InfrastructureDeferred, "infrastructure-deferred", 6)]
    [InlineData(VerifiedGateStartExecutor.BuildSlotsBusy, "build-slots-busy", 7)]
    [InlineData(VerifiedGateStartExecutor.BuildLockBlocked, "build-lock-blocked", 8)]
    [InlineData(VerifiedGateStartExecutor.AttemptCancelled, "attempt-cancelled", 9)]
    public void DeferralConstantsMatchPolicyHolds(string kind, string expectedKind, int rung)
    {
        var facts = new VerifiedAdmissionFacts(GoalStatus.Verified)
        {
            GateStartDeferral = kind,
            InfrastructureDeferredReasonCode = kind == VerifiedGateStartExecutor.InfrastructureDeferred ? "probe-code" : null,
            InfrastructureDeferredMessage = kind == VerifiedGateStartExecutor.InfrastructureDeferred ? "message" : null,
            BuildSlotsBusyDetail = kind == VerifiedGateStartExecutor.BuildSlotsBusy ? "busy slots" : null,
            BuildLockBlockedDetail = kind == VerifiedGateStartExecutor.BuildLockBlocked ? "lock attribution" : null,
            CancellationProbeCause = kind == VerifiedGateStartExecutor.AttemptCancelled ? "StoppedDisposition" : null
        };

        var decision = VerifiedAdmissionPolicy.Evaluate(facts);

        Assert.Equal(expectedKind, kind);
        Assert.Equal(VerifiedAdmissionAction.Hold, decision.Action);
        Assert.Equal(rung, decision.DiscriminatingRung);
        Assert.Equal("gate-start-" + kind, decision.DiscriminatingEvidence);
        Assert.Equal("", decision.StableIdentity);
    }
}
