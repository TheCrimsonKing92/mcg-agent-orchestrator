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
    public void RequiredCanonicalInputMutationAllowsPaidRetryAdmission()
    {
        var baseline = Input();
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

        foreach (var mutation in mutations)
        {
            var task = RetryingTask();
            var firstAttempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
            var secondAttempt = firstAttempt.AddMinutes(1);
            var original = RetryContextFingerprintBuilder.Build(baseline);
            task.RecordDispatch(Dispatch(firstAttempt, original));
            var first = RetryAdmissionPolicy.Evaluate(
                task, original, PaidRouteClassification.Paid, RetryCause.NewSourceFinding,
                firstAttempt, firstAttempt);
            task.RecordRetryAdmission(first.Receipt);
            task.MarkRetryAdmissionStarted(firstAttempt, firstAttempt.AddSeconds(1));
            var changed = RetryContextFingerprintBuilder.Build(mutation);
            task.RecordDispatch(Dispatch(secondAttempt, changed));

            var result = RetryAdmissionPolicy.Evaluate(
                task, changed, PaidRouteClassification.Paid, RetryCause.NewSourceFinding,
                secondAttempt, secondAttempt);

            Assert.Equal(RetryAdmissionDecision.Allowed, result.Decision);
        }
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
    public void ConcurrentContenderCannotResumeLiveReservation()
    {
        var task = RetryingTask();
        var attempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var fingerprint = RetryContextFingerprintBuilder.Build(Input());
        task.RecordDispatch(Dispatch(attempt, fingerprint));
        var first = RetryAdmissionPolicy.Evaluate(
            task, fingerprint, PaidRouteClassification.Paid, RetryCause.ProviderInterruption,
            attempt, attempt, reservationOwnerId: "owner-a", reservationLeaseExpiresAt: attempt.AddMinutes(1));
        task.RecordRetryAdmission(first.Receipt);

        var contender = RetryAdmissionPolicy.Evaluate(
            task, fingerprint, PaidRouteClassification.Paid, RetryCause.ProviderInterruption,
            attempt, attempt.AddSeconds(1), reservationOwnerId: "owner-b", reservationLeaseExpiresAt: attempt.AddMinutes(1));

        Assert.Equal(RetryAdmissionDecision.Prevented, contender.Decision);
        Assert.False(contender.AllowsProcessStart);
        Assert.Equal(RetryAdmissionRoute.ReservationLease, contender.Receipt.Route);
    }

    [Xunit.Fact]
    public void OwnedRetryAdmissionProcessRecordDoesNotBypassDurableStartConfirmation()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry implementation", AgentRole.Developer);
        var goal = kernel.CreateGoal("Preserve two-phase paid start", [task]);
        kernel.RetryTask(goal.Id, task.Id, "Repair the source finding.", RetryCause.NewSourceFinding);
        task.AssignTo(new AgentId("developer"));
        var at = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var fingerprint = RetryContextFingerprintBuilder.Build(Input());
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(at, fingerprint));
        kernel.RecordPreparedRetryAdmission(
            goal.Id,
            task.Id,
            fingerprint,
            PaidRouteClassification.Paid,
            at,
            reservationOwnerId: "owner-a",
            reservationLeaseExpiresAt: at.AddMinutes(1));

        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(123, "command", "worktree", "out", "err", "exit", at, null, null));

        Assert.Null(Assert.Single(task.RetryAdmissionHistory).WorkerStartedAt);
    }

    [Xunit.Fact]
    public void ExpiredReservationCanBeResumedByNewOwner()
    {
        var task = RetryingTask();
        var attempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var fingerprint = RetryContextFingerprintBuilder.Build(Input());
        task.RecordDispatch(Dispatch(attempt, fingerprint));
        var first = RetryAdmissionPolicy.Evaluate(
            task, fingerprint, PaidRouteClassification.Paid, RetryCause.ProviderInterruption,
            attempt, attempt, reservationOwnerId: "owner-a", reservationLeaseExpiresAt: attempt.AddMinutes(1));
        task.RecordRetryAdmission(first.Receipt);

        var recovery = RetryAdmissionPolicy.Evaluate(
            task, fingerprint, PaidRouteClassification.Paid, RetryCause.ProviderInterruption,
            attempt, attempt.AddMinutes(1), reservationOwnerId: "owner-b", reservationLeaseExpiresAt: attempt.AddMinutes(2),
            reservationRecoveryConfirmed: true);

        Assert.Equal(RetryAdmissionDecision.ResumedReservation, recovery.Decision);
        Assert.True(recovery.AllowsProcessStart);
        Assert.Equal("owner-b", recovery.Receipt.ReservationOwnerId);
    }

    [Xunit.Fact]
    public void ExpiredReservationWithoutRecoveryEvidenceRemainsPrevented()
    {
        var task = RetryingTask();
        var attempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var fingerprint = RetryContextFingerprintBuilder.Build(Input());
        task.RecordDispatch(Dispatch(attempt, fingerprint));
        var first = RetryAdmissionPolicy.Evaluate(
            task, fingerprint, PaidRouteClassification.Paid, RetryCause.ProviderInterruption,
            attempt, attempt, reservationOwnerId: "owner-a", reservationLeaseExpiresAt: attempt.AddMinutes(1));
        task.RecordRetryAdmission(first.Receipt);

        var contender = RetryAdmissionPolicy.Evaluate(
            task, fingerprint, PaidRouteClassification.Paid, RetryCause.ProviderInterruption,
            attempt, attempt.AddMinutes(2), reservationOwnerId: "owner-b", reservationLeaseExpiresAt: attempt.AddMinutes(3));

        Assert.Equal(RetryAdmissionDecision.Prevented, contender.Decision);
        Assert.Equal(RetryAdmissionRoute.ReservationLease, contender.Receipt.Route);
    }

    [Xunit.Fact]
    public void MissingRoleTargetEscalatesToHumanInsteadOfSilentlyHolding()
    {
        var kernel = new AgentOrchestratorKernel();
        var held = new TaskSpec(TaskId.New(), "Review retry", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Route missing evidence owner", [held]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(
            new AgentId("reviewer"),
            "Reviewer",
            AgentRole.Reviewer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey))]);
        var attempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var fingerprint = RetryContextFingerprintBuilder.Build(Input() with { Role = AgentRole.Reviewer });
        kernel.RecordTaskDispatch(goal.Id, held.Id, Dispatch(attempt, fingerprint));
        var receipt = new RetryAdmissionReceipt(
            "missing-target",
            RetryCause.NewTestFinding,
            fingerprint,
            RetryAdmissionDecision.Prevented,
            RetryAdmissionRoute.EvidenceLane,
            PaidRouteClassification.Paid,
            attempt,
            attempt);

        kernel.ApplyPreparedRetryAdmission(
            goal.Id,
            held.Id,
            new RetryAdmissionResult(RetryAdmissionDecision.Prevented, receipt));

        Assert.Equal(WorkTaskStatus.WaitingForHuman, held.Status);
        Assert.Equal(RetryAdmissionRoute.HumanClarification, held.RetryAdmissionHoldRoute);
        Assert.Single(kernel.HumanInputRequests, request => request.GoalId == goal.Id && !request.IsCompleted);
    }

    [Xunit.Fact]
    public void PreventedAdmissionDoesNotForceTaskFailure()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Preserve task state on retry prevention", [task]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey))]);
        var attempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var fingerprint = RetryContextFingerprintBuilder.Build(Input());
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(attempt, fingerprint));
        var receipt = new RetryAdmissionReceipt(
            "prevented",
            RetryCause.UnchangedContextRepeat,
            fingerprint,
            RetryAdmissionDecision.Prevented,
            RetryAdmissionRoute.AcceptanceRegate,
            PaidRouteClassification.Paid,
            attempt,
            attempt);

        kernel.ApplyPreparedRetryAdmission(
            goal.Id,
            task.Id,
            new RetryAdmissionResult(RetryAdmissionDecision.Prevented, receipt));

        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Assert.NotEqual(WorkTaskStatus.Failed, task.Status);
    }

    [Xunit.Fact]
    public void AcceptanceRegate_DoesNotRestoreVerificationOrCompletionState()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Review candidate", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Re-gate unchanged accepted candidate", [task]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(
            new AgentId("reviewer"),
            "Reviewer",
            AgentRole.Reviewer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey))]);
        var firstAttempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var secondAttempt = firstAttempt.AddMinutes(1);
        var fingerprint = RetryContextFingerprintBuilder.Build(Input() with { Role = AgentRole.Reviewer });
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(firstAttempt, fingerprint));
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord("command", "worktree", 0, "accepted", "", firstAttempt));
        kernel.RetryTask(
            goal.Id,
            task.Id,
            "Re-check unchanged accepted context.",
            invalidateDownstream: false,
            retryCause: RetryCause.UnchangedContextRepeat);
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(secondAttempt, fingerprint));
        var receipt = new RetryAdmissionReceipt(
            "acceptance-regate",
            RetryCause.UnchangedContextRepeat,
            fingerprint,
            RetryAdmissionDecision.Prevented,
            RetryAdmissionRoute.AcceptanceRegate,
            PaidRouteClassification.Paid,
            secondAttempt,
            secondAttempt,
            firstAttempt);
        var result = new RetryAdmissionResult(RetryAdmissionDecision.Prevented, receipt);

        kernel.ApplyPreparedRetryAdmission(goal.Id, task.Id, result);
        kernel.ApplyPreparedRetryAdmission(goal.Id, task.Id, result);

        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
        Assert.Equal(RetryAdmissionRoute.HumanClarification, task.RetryAdmissionHoldRoute);
        Assert.Null(task.LastVerification);
        Assert.Null(task.LastProcess);
        Assert.Equal(0, goal.OperatorAcceptanceRegateCount);
        Assert.Single(goal.Timeline.Where(item => item.Message.Contains("receipt=acceptance-regate", StringComparison.Ordinal)));
        Assert.Single(kernel.HumanInputRequests, request => request.GoalId == goal.Id && !request.IsCompleted);
    }

    [Xunit.Fact]
    public void AcceptanceRegate_MissingVerification_FailsClosedToHuman()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Review candidate", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Hold unsafe acceptance re-gate", [task]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(
            new AgentId("reviewer"),
            "Reviewer",
            AgentRole.Reviewer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey))]);
        var attempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var fingerprint = RetryContextFingerprintBuilder.Build(Input() with { Role = AgentRole.Reviewer });
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(attempt, fingerprint));
        var receipt = new RetryAdmissionReceipt(
            "acceptance-regate-missing-verification",
            RetryCause.UnchangedContextRepeat,
            fingerprint,
            RetryAdmissionDecision.Prevented,
            RetryAdmissionRoute.AcceptanceRegate,
            PaidRouteClassification.Paid,
            attempt,
            attempt);

        kernel.ApplyPreparedRetryAdmission(
            goal.Id,
            task.Id,
            new RetryAdmissionResult(RetryAdmissionDecision.Prevented, receipt));

        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
        Assert.Equal(RetryAdmissionRoute.HumanClarification, task.RetryAdmissionHoldRoute);
        Assert.Single(kernel.HumanInputRequests, request => request.GoalId == goal.Id && !request.IsCompleted);
        Assert.Equal(0, goal.OperatorAcceptanceRegateCount);
    }

    [Xunit.Fact]
    public void AcceptanceRegate_LatestVerificationFailed_DoesNotRestoreOlderSuccess()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Review candidate", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Do not suppress a later blocker", [task]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(
            new AgentId("reviewer"),
            "Reviewer",
            AgentRole.Reviewer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey))]);
        var firstAttempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var secondAttempt = firstAttempt.AddMinutes(1);
        var fingerprint = RetryContextFingerprintBuilder.Build(Input() with { Role = AgentRole.Reviewer });
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(firstAttempt, fingerprint));
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord("command", "worktree", 0, "accepted", "", firstAttempt));
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord("command", "worktree", 1, "", "new blocker", firstAttempt.AddSeconds(30)));
        kernel.RetryTask(
            goal.Id,
            task.Id,
            "Re-check context containing the blocker.",
            invalidateDownstream: false,
            retryCause: RetryCause.UnchangedContextRepeat);
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(secondAttempt, fingerprint));
        var receipt = new RetryAdmissionReceipt(
            "acceptance-regate-latest-failed",
            RetryCause.UnchangedContextRepeat,
            fingerprint,
            RetryAdmissionDecision.Prevented,
            RetryAdmissionRoute.AcceptanceRegate,
            PaidRouteClassification.Paid,
            secondAttempt,
            secondAttempt,
            firstAttempt);

        kernel.ApplyPreparedRetryAdmission(
            goal.Id,
            task.Id,
            new RetryAdmissionResult(RetryAdmissionDecision.Prevented, receipt));

        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
        Assert.Null(task.LastVerification);
        Assert.Single(kernel.HumanInputRequests, request => request.GoalId == goal.Id && !request.IsCompleted);
    }

    [Xunit.Fact]
    public void UpstreamRouteReopensCompletedImplementationTask()
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement correction", AgentRole.Developer);
        var reviewer = new TaskSpec(TaskId.New(), "Review correction", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Route held reviewer work upstream", [developer, reviewer]);
        kernel.ActivateGoal(goal.Id,
        [
            new AgentDefinition(new AgentId("developer"), "Developer", AgentRole.Developer,
                new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey)),
            new AgentDefinition(new AgentId("reviewer"), "Reviewer", AgentRole.Reviewer,
                new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]);
        developer.SetStatus(WorkTaskStatus.Completed);
        var attempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var fingerprint = RetryContextFingerprintBuilder.Build(Input() with { Role = AgentRole.Reviewer });
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, Dispatch(attempt, fingerprint));
        var receipt = new RetryAdmissionReceipt(
            "route-upstream",
            RetryCause.UnchangedContextRepeat,
            fingerprint,
            RetryAdmissionDecision.Prevented,
            RetryAdmissionRoute.UpstreamImplementation,
            PaidRouteClassification.Paid,
            attempt,
            attempt);

        kernel.ApplyPreparedRetryAdmission(
            goal.Id,
            reviewer.Id,
            new RetryAdmissionResult(RetryAdmissionDecision.Prevented, receipt));

        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Equal(RetryCause.UnchangedContextRepeat, developer.PendingRetryCause);
        Assert.Equal(WorkTaskStatus.Running, reviewer.Status);
        Assert.Equal(RetryAdmissionRoute.UpstreamImplementation, reviewer.RetryAdmissionHoldRoute);
    }

    [Xunit.Fact]
    public void CancelledRoleTargetEscalatesToHumanInsteadOfSilentlyHolding()
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement correction", AgentRole.Developer);
        var reviewer = new TaskSpec(TaskId.New(), "Review correction", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Escalate unavailable cancelled route", [developer, reviewer]);
        kernel.ActivateGoal(goal.Id,
        [
            new AgentDefinition(new AgentId("developer"), "Developer", AgentRole.Developer,
                new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey)),
            new AgentDefinition(new AgentId("reviewer"), "Reviewer", AgentRole.Reviewer,
                new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]);
        developer.SetStatus(WorkTaskStatus.Cancelled);
        var attempt = DateTimeOffset.Parse("2026-08-25T12:01:00Z");
        var fingerprint = RetryContextFingerprintBuilder.Build(Input() with { Role = AgentRole.Reviewer });
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, Dispatch(attempt, fingerprint));
        var receipt = new RetryAdmissionReceipt(
            "route-cancelled-upstream",
            RetryCause.UnchangedContextRepeat,
            fingerprint,
            RetryAdmissionDecision.Prevented,
            RetryAdmissionRoute.UpstreamImplementation,
            PaidRouteClassification.Paid,
            attempt,
            attempt);

        kernel.ApplyPreparedRetryAdmission(
            goal.Id,
            reviewer.Id,
            new RetryAdmissionResult(RetryAdmissionDecision.Prevented, receipt));

        Assert.Equal(WorkTaskStatus.Cancelled, developer.Status);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, reviewer.Status);
        Assert.Equal(RetryAdmissionRoute.HumanClarification, reviewer.RetryAdmissionHoldRoute);
        Assert.Single(kernel.HumanInputRequests, request => request.GoalId == goal.Id && !request.IsCompleted);
    }

    [Xunit.Fact]
    public void LatestOperatorRetryFeedbackChangesRetryContextFingerprint()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Fingerprint ordinary retry feedback", [task]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey))]);

        kernel.RetryTask(
            goal.Id, task.Id, "Inspect alpha evidence.", RetryCause.NewSourceFinding,
            invalidateDownstream: false);
        var alpha = RetryContextFingerprintFactory.Build(
            goal, task, "OpenAI", "test", PaidRouteClassification.Paid, null, null, null);
        kernel.RetryTask(
            goal.Id, task.Id, "Inspect beta evidence.", RetryCause.NewSourceFinding,
            invalidateDownstream: false);
        var beta = RetryContextFingerprintFactory.Build(
            goal, task, "OpenAI", "test", PaidRouteClassification.Paid, null, null, null);

        Assert.NotEqual(alpha, beta);
    }

    [Xunit.Fact]
    public void LatestOperatorRetryFeedbackRemainsFingerprintInputWithCriterionFeedback()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Fingerprint operator correction", [task]);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["Criterion evidence is unchanged."]);

        kernel.RetryTask(
            goal.Id, task.Id, "Inspect alpha evidence.", RetryCause.NewSourceFinding,
            invalidateDownstream: false);
        var alpha = RetryContextFingerprintFactory.Build(
            goal, task, "OpenAI", "test", PaidRouteClassification.Paid, null, null, null);
        kernel.RetryTask(
            goal.Id, task.Id, "Inspect beta evidence.", RetryCause.NewSourceFinding,
            invalidateDownstream: false);
        var beta = RetryContextFingerprintFactory.Build(
            goal, task, "OpenAI", "test", PaidRouteClassification.Paid, null, null, null);

        Assert.NotEqual(alpha, beta);
    }

    [Xunit.Fact]
    public void VolatileConductorCountersDoNotChangeRetryContextFingerprint()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Fingerprint actionable retry feedback", [task]);

        kernel.RetryTask(
            goal.Id,
            task.Id,
            "Auto-retry real worker failure for task aaaaaaaa (attempt 1/3); same evidence.",
            RetryCause.NewSourceFinding,
            invalidateDownstream: false);
        var first = RetryContextFingerprintFactory.Build(
            goal, task, "OpenAI", "test", PaidRouteClassification.Paid, null, null, null);
        kernel.RetryTask(
            goal.Id,
            task.Id,
            "Auto-retry real worker failure for task bbbbbbbb (attempt 2/3); same evidence.",
            RetryCause.NewSourceFinding,
            invalidateDownstream: false);
        var second = RetryContextFingerprintFactory.Build(
            goal, task, "OpenAI", "test", PaidRouteClassification.Paid, null, null, null);

        Assert.Equal(first, second);
    }

    [Xunit.Fact]
    public void VolatileConductorWrapperDoesNotChangeRetryContextFingerprint()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Fingerprint actionable recovery evidence", [task]);

        kernel.RetryTask(
            goal.Id,
            task.Id,
            "Auto-retry empty-output dispatch flake 1/3 in recovery cycle 1/2; task produced zero-byte stdout with exit 0",
            RetryCause.EnvironmentApparatusFailure,
            invalidateDownstream: false);
        var first = RetryContextFingerprintFactory.Build(
            goal, task, "OpenAI", "test", PaidRouteClassification.Paid, null, null, null);
        kernel.RetryTask(
            goal.Id,
            task.Id,
            "Auto-recover+re-admit empty-output dispatch flake cycle 2/2; task produced zero-byte stdout with exit 0",
            RetryCause.EnvironmentApparatusFailure,
            invalidateDownstream: false);
        var second = RetryContextFingerprintFactory.Build(
            goal, task, "OpenAI", "test", PaidRouteClassification.Paid, null, null, null);

        Assert.Equal(first, second);
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
