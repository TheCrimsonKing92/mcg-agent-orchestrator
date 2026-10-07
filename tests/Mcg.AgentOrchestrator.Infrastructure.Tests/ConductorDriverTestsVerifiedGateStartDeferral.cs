using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using static ConductorDriverTestsVerifiedAdmissionDecision;

// Every test owns its journal and uses injected effects; no processes or git are invoked.
public sealed class ConductorDriverTestsVerifiedGateStartDeferral
{
    [Fact]
    public void InfrastructureDeferralPreservesOriginalHoldAndRecordedFacts()
    {
        var exception = new AcceptanceInfrastructureDeferredException("probe-code", null, "tail");

        AssertGateHold(exception, 6, "infrastructure-deferred",
            $"Acceptance infrastructure deferred (probe-code); retry on next conduct tick. {exception.Message}",
            new()
            {
                ["infrastructureDeferredReasonCode"] = "probe-code",
                ["infrastructureDeferredMessage"] = exception.Message
            });
    }

    [Fact]
    public void BlockedLockPreservesOriginalHoldAndRecordedFacts()
    {
        var attribution = new BuildLockAttribution(
            @"C:\probe\locked.dll", [new BuildLockHolder(7, "devenv", null, false)], "test");
        var detail = $"path={attribution.Path}; holders: pid 7 devenv";

        AssertGateHold(new BuildLockBlockedException(attribution), 8, "build-lock-blocked",
            $"Build artifact lock blocked acceptance; retry on next conduct tick. {detail}",
            new() { ["buildLockBlockedDetail"] = detail });
    }

    [Fact]
    public void CancelledAttemptPreservesOriginalHoldAndRecordedFacts()
    {
        var exception = new AcceptanceAttemptCancelledException(
            AcceptanceAttemptCancellationDecision.Cancel(AcceptanceAttemptCancellationCause.StoppedDisposition));

        AssertGateHold(exception, 9, "attempt-cancelled",
            "Acceptance attempt stopped by cancellation probe (StoppedDisposition); retry when the goal is eligible.",
            new() { ["cancellationProbeCause"] = "StoppedDisposition" });
    }

    private static void AssertGateHold(
        Exception exception, int rung, string kind, string reason, Dictionary<string, string> details)
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var gateRuns = 0;
        var driver = MakeDriver(runAcceptanceSummary: _ =>
        {
            gateRuns++;
            throw exception;
        });

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);

        Assert.Equal(1, gateRuns);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(reason, held.Reason);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Null(held.StableIdentity);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        var decision = AssertDecisionAndPayload(held, rung, "gate-start-" + kind);
        AssertFact(decision, "goalStatus", "Verified");
        AssertFact(decision, "ownerReviewHold", "false");
        AssertFact(decision, "cohortAttribution", "false");
        AssertFact(decision, "apparatusHold", "false");
        AssertFact(decision, "evidenceLease", "true");
        AssertFact(decision, "allTasksPassed", "true");
        AssertFact(decision, "gateStartDeferral", kind);
        foreach (var name in new[]
        {
            "ownerReviewSha", "ownerReviewReason", "ownerReviewFingerprint", "cohortAttributionEvidence",
            "apparatusBranchSha", "apparatusMainSha", "apparatusCandidate", "evidenceLeaseReason",
            "infrastructureDeferredReasonCode", "infrastructureDeferredMessage", "buildSlotsBusyDetail",
            "buildLockBlockedDetail", "cancellationProbeCause"
        })
        {
            AssertFact(decision, name, details.GetValueOrDefault(name, ""));
        }
    }
}
