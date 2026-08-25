using Mcg.AgentOrchestrator.Core;

public sealed class RetryAdmissionTests
{
    [Xunit.Fact]
    public void RequiredTaxonomyIsPresent()
    {
        var expected = new[]
        {
            nameof(RetryCause.Unknown),
            nameof(RetryCause.NewSourceFinding),
            nameof(RetryCause.NewTestFinding),
            nameof(RetryCause.CriterionEvidenceOwnerMismatch),
            nameof(RetryCause.EnvironmentApparatusFailure),
            nameof(RetryCause.ContractClarification),
            nameof(RetryCause.MainDriftConflict),
            nameof(RetryCause.ProviderInterruption),
            nameof(RetryCause.UnchangedContextRepeat)
        };

        Assert.Equal(expected, Enum.GetNames<RetryCause>());
    }

    [Xunit.Fact]
    public void RequiredInputMutationChangesFingerprint()
    {
        var baseline = Input();
        var expected = RetryContextFingerprintBuilder.Build(baseline);
        var mutations = new RetryContextFingerprintInput[]
        {
            baseline with { GoalId = "goal-b" },
            baseline with { TaskId = "task-b" },
            baseline with { Role = AgentRole.Tester },
            baseline with { ProviderName = "provider-b" },
            baseline with { ModelName = "model-b" },
            baseline with { PaidRoute = PaidRouteClassification.NonPaid },
            baseline with { ReviewedCandidateSha = "b" },
            baseline with { EffectiveCriteriaHash = "criteria-b" },
            baseline with { OpenFindingIdentities = ["finding-b"] },
            baseline with { EvidenceReceiptIdentities = ["receipt-b"] },
            baseline with { RetryFeedback = ["feedback-b"] },
            baseline with { AuthoritativeDecisions = ["decision-b"] },
            baseline with { BaseIdentity = "base-b" },
            baseline with { MainIdentity = "main-b" }
        };

        Assert.All(mutations, mutation =>
            Assert.NotEqual(expected.Value, RetryContextFingerprintBuilder.Build(mutation).Value));
    }

    [Xunit.Fact]
    public void IdenticalPaidRetryIsPrevented()
    {
        var task = RetryingTask();
        var firstAttempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var secondAttempt = firstAttempt.AddMinutes(1);
        var fingerprint = RetryContextFingerprintBuilder.Build(Input());
        task.RecordDispatch(Dispatch(firstAttempt, fingerprint));
        var allowed = RetryAdmissionPolicy.Evaluate(
            task, fingerprint, PaidRouteClassification.Paid, RetryCause.NewSourceFinding,
            firstAttempt, firstAttempt);
        task.RecordRetryAdmission(allowed.Receipt);
        task.MarkRetryAdmissionStarted(firstAttempt, firstAttempt.AddSeconds(1));
        task.RecordDispatch(Dispatch(secondAttempt, fingerprint));

        var prevented = RetryAdmissionPolicy.Evaluate(
            task, fingerprint, PaidRouteClassification.Paid, RetryCause.NewSourceFinding,
            secondAttempt, secondAttempt);

        Assert.Equal(RetryAdmissionDecision.Prevented, prevented.Decision);
        Assert.Equal(RetryCause.UnchangedContextRepeat, prevented.Receipt.Cause);
        Assert.Equal(RetryAdmissionRoute.UpstreamImplementation, prevented.Receipt.Route);
    }

    [Xunit.Fact]
    public void ChangedCandidateAllowsPaidRetry()
    {
        var task = RetryingTask();
        var firstAttempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var secondAttempt = firstAttempt.AddMinutes(1);
        var original = RetryContextFingerprintBuilder.Build(Input());
        task.RecordDispatch(Dispatch(firstAttempt, original));
        var first = RetryAdmissionPolicy.Evaluate(
            task, original, PaidRouteClassification.Paid, RetryCause.NewSourceFinding,
            firstAttempt, firstAttempt);
        task.RecordRetryAdmission(first.Receipt);
        task.MarkRetryAdmissionStarted(firstAttempt, firstAttempt.AddSeconds(1));
        var changed = RetryContextFingerprintBuilder.Build(Input() with { ReviewedCandidateSha = "b" });
        task.RecordDispatch(Dispatch(secondAttempt, changed));

        var result = RetryAdmissionPolicy.Evaluate(
            task, changed, PaidRouteClassification.Paid, RetryCause.NewSourceFinding,
            secondAttempt, secondAttempt);

        Assert.Equal(RetryAdmissionDecision.Allowed, result.Decision);
    }

    [Xunit.Fact]
    public void UnstartedReservationIsResumed()
    {
        var task = RetryingTask();
        var attempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var fingerprint = RetryContextFingerprintBuilder.Build(Input());
        task.RecordDispatch(Dispatch(attempt, fingerprint));
        var first = RetryAdmissionPolicy.Evaluate(
            task, fingerprint, PaidRouteClassification.Paid, RetryCause.ProviderInterruption,
            attempt, attempt);
        task.RecordRetryAdmission(first.Receipt);

        var resumed = RetryAdmissionPolicy.Evaluate(
            task, fingerprint, PaidRouteClassification.Paid, RetryCause.ProviderInterruption,
            attempt, attempt.AddSeconds(1));

        Assert.Equal(RetryAdmissionDecision.ResumedReservation, resumed.Decision);
        Assert.True(resumed.AllowsProcessStart);
    }

    private static TaskSpec RetryingTask()
    {
        var task = new TaskSpec(TaskId.New(), "Retry work", AgentRole.Developer);
        task.RecordRetry(DateTimeOffset.Parse("2026-08-25T12:00:00Z"), retryCause: RetryCause.NewSourceFinding);
        return task;
    }

    private static TaskDispatchRecord Dispatch(
        DateTimeOffset at,
        RetryContextFingerprint fingerprint) =>
        new("worker", "command", "worktree", at,
            RetryContextFingerprint: fingerprint,
            PaidRoute: PaidRouteClassification.Paid);

    private static RetryContextFingerprintInput Input() => new(
        "goal-a",
        "task-a",
        AgentRole.Developer,
        "provider-a",
        "model-a",
        PaidRouteClassification.Paid,
        "a",
        "criteria-a",
        ["finding-a"],
        ["receipt-a"],
        ["feedback-a\r\nline"],
        ["decision-a"],
        "base-a",
        "main-a");
}
