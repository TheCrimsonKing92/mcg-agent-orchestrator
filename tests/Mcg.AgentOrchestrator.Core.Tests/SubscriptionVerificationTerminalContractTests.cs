using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using Xunit;

// Pure recorded-evidence fixtures: no provider invocation, filesystem or process environment.
public sealed class SubscriptionVerificationTerminalContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeReviewerFindingsUseCompleteCapturedTerminalBlock(bool corruptJson)
    {
        var mutationText = new string('x', VerificationTextBounds.MaxRetainedChars + 1024);
        var findings = JsonSerializer.Serialize(new[] { new
        {
            stable_id = "large-mutation", state = "open", severity = "blocking", category = "test-evidence",
            location = new { file = "src/Example.cs", region = "Example" }, description = "Execute the requested control.",
            evidence_request = new
            {
                selections = new[] { new { test_project = "Core", test_class = "ExampleTests" } },
                negative_control = "revert-src",
                mutation = new { path = "src/Example.cs", old_text = mutationText, new_text = "previous" }
            }
        } });
        var parsed = ReviewFindingConvergence.ParseJson(findings, "[]");
        Assert.Null(parsed.FatalDiagnostic);
        Assert.Empty(parsed.AdvisoryRejections);
        Assert.Single(parsed.Round.Findings);
        if (corruptJson)
            findings = findings[..^1];
        var output = Contract(AgentRole.Reviewer).Replace("findings: []", "findings: " + findings);
        var (_, _, task, at) = Dispatch(AgentRole.Reviewer);
        var evidence = Evidence(task, at, output);
        Assert.True(evidence.StandardOutput.Length < output.Length);
        Assert.Equal(corruptJson ? VerificationTerminalContractStatus.Malformed : VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessSubscriptionVerification(task, evidence).Status);
    }

    [Fact]
    public void TerminalContractInStderrCannotSupplyMissingStdoutContract()
    {
        var (_, _, task, at) = Dispatch(AgentRole.Reviewer);
        var evidence = Evidence(task, at, "Review still in progress.") with
        {
            StandardError = Contract(AgentRole.Reviewer),
            AuthoritativeStandardError = Contract(AgentRole.Reviewer)
        };
        Assert.Equal(VerificationTerminalContractStatus.Missing,
            WorkerResultBlockers.AssessSubscriptionVerification(task, evidence).Status);
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void IntroductionCannotBorrowCommitPassReceiptOrDeferral(AgentRole role)
    {
        var (_, _, task, at) = Dispatch(role);
        foreach (var output in new[] { "I will inspect the source.", "Passed: 4; Failed: 0", "tests: deferred - acceptance", "commit: abc123" })
        {
            var verification = Evidence(task, at, output) with { HasCommittedChanges = true, WorkerResultPresent = true };
            var outcome = DispatchFailureClassifier.Classify(task, verification, workerResultPresent: true, hasCommittedChanges: true);
            Assert.Equal(DispatchOutcomeKind.VerificationIncomplete, outcome.Kind);
            Assert.Contains("terminal_contract=Missing", outcome.EvidenceSummary);
            Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        }
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void FinalUnfinishedBlockCannotBorrowEarlierContract(AgentRole role)
    {
        var (_, _, task, at) = Dispatch(role);
        var output = Contract(role) + "\nWORKER_RESULT:\nfiles: none\n";
        var assessment = WorkerResultBlockers.AssessSubscriptionVerification(task, Evidence(task, at, output));
        Assert.Equal(VerificationTerminalContractStatus.Malformed, assessment.Status);
    }

    [Theory]
    [InlineData("findings: []", "findings: invalid")]
    [InlineData("criteria_verdicts: []", "criteria_verdicts: invalid")]
    [InlineData("verdict: pass", "verdict: unknown")]
    [InlineData("verdict: pass", "")]
    [InlineData("confidence: high", "confidence:")]
    public void MalformedReviewerFieldsCannotBePromoted(string original, string replacement)
    {
        var (_, _, task, at) = Dispatch(AgentRole.Reviewer);
        var record = Evidence(task, at, Contract(AgentRole.Reviewer).Replace(original, replacement)) with { HasCommittedChanges = true };
        Assert.Equal(VerificationTerminalContractStatus.Malformed,
            WorkerResultBlockers.AssessSubscriptionVerification(task, record).Status);
        Assert.Equal(DispatchOutcomeKind.VerificationIncomplete, DispatchFailureClassifier.Classify(task, record).Kind);
    }

    [Fact]
    public void UnavailableAuthorityCannotBorrowRetainedContract()
    {
        var (_, _, task, at) = Dispatch(AgentRole.Reviewer);
        var record = Evidence(task, at, Contract(AgentRole.Reviewer)) with
        {
            AuthoritativeStandardOutput = null, FullStandardOutputUnavailableReason = "unreadable"
        };
        Assert.Equal(VerificationTerminalContractStatus.Unreadable,
            WorkerResultBlockers.AssessSubscriptionVerification(task, record).Status);
        Assert.Equal(DispatchOutcomeKind.VerificationIncomplete, DispatchFailureClassifier.Classify(task, record).Kind);
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void CapturedContractSurvivesSnapshotWithoutLosingSourceAndAttempt(AgentRole role)
    {
        var (kernel, goal, task, at) = Dispatch(role);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Evidence(task, at, Contract(role)));
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.True(task.LastVerification!.CompletionVerdictVerifiedSuccess);
        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Assert.Equal(VerificationEvidenceSource.Dispatch, restoredTask.LastVerification!.EvidenceSource);
        Assert.Equal(at, restoredTask.LastVerification.DispatchAttemptAt);
        Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessSubscriptionVerification(restoredTask, restoredTask.LastVerification).Status);
        Assert.True(restored.BuildVerificationGate(goal.Id).IsSatisfied);
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void ContractRepairStopsAfterTwoSameRoleRetries(AgentRole role)
    {
        var (kernel, goal, task, at) = Dispatch(role);
        var assigned = task.AssignedAgentId;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                at = at.AddMinutes(1);
                kernel.RecordTaskDispatch(goal.Id, task.Id, DispatchRecord(at));
                kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Fixture attempt started.");
            }
            var output = attempt == 1 ? Contract(role).Replace("END_WORKER_RESULT", "") : "Introduction only.";
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Evidence(task, at, output));
            Assert.Equal(assigned, task.AssignedAgentId);
            Assert.DoesNotContain(task.VerificationHistory, record => record.CompletionVerdictVerifiedSuccess);
            Assert.False(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
            Assert.Equal(attempt < 2 ? WorkTaskStatus.Assigned : WorkTaskStatus.Failed, task.Status);
        }
        Assert.Equal(2, goal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskRetried));
        Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskFailed && evt.Message.Contains("operator action required"));
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void OldAttemptCannotAcquireCurrentCandidateOrVerdict(AgentRole role)
    {
        var (kernel, goal, task, at) = Dispatch(role);
        var old = Evidence(task, at, Contract(role));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Cancelled, "Fixture first attempt cancelled.");
        kernel.RetryTask(goal.Id, task.Id, "New attempt.");
        var next = at.AddMinutes(1);
        kernel.RecordTaskDispatch(goal.Id, task.Id, DispatchRecord(next));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Fixture retry started.");
        Assert.Equal(VerificationTerminalContractStatus.Stale,
            WorkerResultBlockers.AssessSubscriptionVerification(task, old with { CompletedAt = next.AddMinutes(1) }).Status);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, old with { CompletedAt = next.AddMinutes(1) });
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Null(task.LastVerification);
        Assert.Empty(task.VerificationHistory);
        Assert.Equal(next, task.LastDispatch!.DispatchedAt);
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void CopiedCandidateCommitDoesNotReplaceWorkerTerminalEvidence(AgentRole role)
    {
        var (kernel, goal, task, at) = Dispatch(role);
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, "abc123");
        var record = Evidence(task, at, "Passed: 5; Failed: 0") with { WorkerResultPresent = false };
        Assert.Equal(DispatchOutcomeKind.VerificationIncomplete, DispatchFailureClassifier.Classify(task, record).Kind);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, record);
        Assert.NotEqual(WorkTaskStatus.Completed, task.Status);
        Assert.DoesNotContain(task.VerificationHistory, item => item.CompletionVerdictVerifiedSuccess);
    }

    [Fact]
    public void ProviderFailedTurnInStderrOutranksEarlierCompletedTurn()
    {
        var (_, _, task, at) = Dispatch(AgentRole.Reviewer);
        var record = Evidence(task, at, "{\"type\":\"turn.completed\"}\n" + Contract(AgentRole.Reviewer)) with
        {
            StandardError = "{\"type\":\"turn.failed\",\"error\":{\"message\":\"permission cancelled\"}}",
            AuthoritativeStandardError = "{\"type\":\"turn.failed\",\"error\":{\"message\":\"permission cancelled\"}}"
        };
        Assert.Equal(DispatchOutcomeKind.ProviderInterruption, DispatchFailureClassifier.Classify(task, record).Kind);
    }

    [Theory]
    [InlineData("inconclusive - receipt unavailable", DispatchOutcomeKind.VerificationInconclusive)]
    [InlineData("not-run - acceptance owns execution", DispatchOutcomeKind.VerificationInconclusive)]
    [InlineData("pass - gate-owned.trx; Passed: 1", DispatchOutcomeKind.VerifiedSuccess)]
    [InlineData("pass — gate-owned.trx; Passed: 1", DispatchOutcomeKind.VerifiedSuccess)]
    [InlineData("pass – gate-owned.trx; Passed: 1", DispatchOutcomeKind.VerifiedSuccess)]
    [InlineData("pass: gate-owned.trx; Passed: 1", DispatchOutcomeKind.VerifiedSuccess)]
    public void TesterStatusKeepsItsMeaningEvenWithCommit(string tests, DispatchOutcomeKind expected)
    {
        var (_, _, task, at) = Dispatch(AgentRole.Tester);
        var record = Evidence(task, at, Contract(AgentRole.Tester, tests)) with { HasCommittedChanges = true };
        Assert.Equal(expected, DispatchFailureClassifier.Classify(task, record).Kind);
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("pass — ")]
    [InlineData("pass – none")]
    [InlineData("pass: <evidence>")]
    public void TesterPassWithoutEvidenceIsMalformed(string tests)
    {
        var (_, _, task, at) = Dispatch(AgentRole.Tester);
        Assert.Equal(VerificationTerminalContractStatus.Malformed,
            WorkerResultBlockers.AssessSubscriptionVerification(task, Evidence(task, at, Contract(AgentRole.Tester, tests))).Status);
    }

    [Fact]
    public void ValidTesterBlockerRemainsAReportedFailure()
    {
        var (kernel, goal, task, at) = Dispatch(AgentRole.Tester);
        var output = Contract(AgentRole.Tester, "fail - gate-owned.trx; Failed: 1")
            .Replace("blockers: none", "blockers: exact-blocker - fixture failure needs operator evidence");
        var record = Evidence(task, at, output);
        Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessSubscriptionVerification(task, record).Status);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, record);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(DispatchOutcomeKind.UnknownFailure, DispatchFailureClassifier.Classify(task, task.LastVerification!).Kind);
        Assert.Equal(0, goal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskRetried));
    }

    [Fact]
    public void MalformedReportCannotManufactureTesterBlockerAtGateConsumption()
    {
        var (kernel, goal, task, at) = Dispatch(AgentRole.Tester);
        var output = Contract(AgentRole.Tester).Replace("confidence: high", "confidence:")
            .Replace("blockers: none", "blockers: exact-blocker - implementation finding in malformed report");
        task.RecordVerification(Evidence(task, at, output) with { CompletionVerdictVerifiedSuccess = true });
        task.SetStatus(WorkTaskStatus.Completed);
        var gate = Assert.Single(kernel.BuildVerificationGate(goal.Id).Tasks);
        Assert.Equal(VerificationGateReason.VerificationFailed, gate.Reason);
        Assert.Contains("terminal contract is incomplete", gate.Message);
        Assert.NotEqual(VerificationGateReason.TesterWorkerResultBlocker, gate.Reason);
    }

    [Fact]
    public void ValidNonPassingReviewerContractCannotPromoteACommit()
    {
        var (_, _, task, at) = Dispatch(AgentRole.Reviewer);
        var record = Evidence(task, at, Contract(AgentRole.Reviewer).Replace("verdict: pass", "verdict: needs-work")) with
            { HasCommittedChanges = true };
        Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessSubscriptionVerification(task, record).Status);
        Assert.NotEqual(DispatchOutcomeKind.VerifiedSuccess, DispatchFailureClassifier.Classify(task, record).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewerUnmetCriterionUsesAuthoritativeWaiver(bool waived)
    {
        var (kernel, goal, task, at) = Dispatch(AgentRole.Reviewer);
        const string criterion = "The guard is implemented.";
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Review the guard", [criterion], VerificationClass.TestVerifiable, [], []));
        if (waived)
            kernel.WaiveAcceptanceCriterion(goal.Id, criterion, "Operator accepts the gap.", "operator");
        var output = Contract(AgentRole.Reviewer).Replace("criteria_verdicts: []",
            """criteria_verdicts: [{"criterion_index":0,"verdict":"not-met","evidence":"Guard is absent."}]""");
        var record = Evidence(task, at, output);
        Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessSubscriptionVerification(task, record).Status);

        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, record);

        Assert.Equal(waived ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, task.Status);
        Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.TaskRetried);
        Assert.DoesNotContain(goal.Timeline, item => item.Message.Contains("terminal contract repair"));
        if (waived)
            Assert.True(task.LastVerification!.CompletionVerdictVerifiedSuccess);
        else
        {
            Assert.False(task.LastVerification!.CompletionVerdictVerifiedSuccess);
            Assert.False(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
            Assert.Contains(goal.Timeline, item => item.Kind == ProgressKind.TaskFailed &&
                item.Message.Contains("criteria attestation rejected"));
        }
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void NonSubscriptionDispatchRetainsItsExistingContract(AgentRole role)
    {
        var (kernel, goal, task, at) = Dispatch(role, subscription: false);
        var record = new TaskVerificationRecord("api review", @"C:\repo", 0, "Passed: 1", "", at.AddSeconds(1));
        Assert.Equal(VerificationTerminalContractStatus.NotApplicable,
            WorkerResultBlockers.AssessSubscriptionVerification(task, record).Status);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, record);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
    }

    [Theory]
    [InlineData(AgentRole.Reviewer, false)]
    [InlineData(AgentRole.Reviewer, true)]
    [InlineData(AgentRole.Tester, false)]
    [InlineData(AgentRole.Tester, true)]
    public void InheritedCandidateReuseRequiresValidOriginalSubscriptionContract(AgentRole role, bool valid)
    {
        var (_, goal, task, at) = Dispatch(role);
        var identity = new CandidateIdentity("patch", "basis", "manifest");
        var record = Evidence(task, at, valid ? Contract(role) : "Incomplete report.") with
        {
            CandidateIdentity = identity, AcceptanceCriteriaVersionHash = EffectiveAcceptanceCriteriaVersion.ComputeForGoal(goal),
            CompletionVerdictVerifiedSuccess = true
        };
        task.RecordVerification(record);
        task.RecordRetry(at.AddMinutes(1), RetryCause.UnchangedContextRepeat, inherited: true);
        task.ClearLatestVerification();
        task.ClearLastDispatch();
        task.SetStatus(WorkTaskStatus.Assigned);
        Assert.Equal(valid, UnchangedCandidateRule.EvaluateReinstatement(goal, task, identity) is not null);
    }

    [Fact]
    public void ExplicitCancellationOutranksExitZeroAndContractAndSurvivesSnapshot()
    {
        var (kernel, goal, task, at) = Dispatch(AgentRole.Reviewer);
        var record = Evidence(task, at, Contract(AgentRole.Reviewer)) with { StandardError = "ERROR: permission_cancelled", AuthoritativeStandardError = "ERROR: permission_cancelled" };
        Assert.Equal(DispatchOutcomeKind.ProviderInterruption, DispatchFailureClassifier.Classify(task, record).Kind);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, record);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        var restoredTask = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot()).GetTask(goal.Id, task.Id);
        var history = Assert.Single(restoredTask.VerificationHistory);
        Assert.Contains("permission_cancelled", history.AuthoritativeStandardError!);
        Assert.False(history.CompletionVerdictVerifiedSuccess);
    }

    [Theory]
    [InlineData("The fixture expects ERROR: permission_cancelled.")]
    [InlineData("Assert.Equal(\"permission_cancelled\", reason)")]
    [InlineData("\"ERROR: permission_cancelled\"")]
    [InlineData("```\nERROR: permission_cancelled\n```")]
    [InlineData("WORKER_RESULT:\nERROR: permission_cancelled\nEND_WORKER_RESULT")]
    public void QuotedCancellationDoesNotManufactureProviderFailure(string stderr)
    {
        var (_, _, task, at) = Dispatch(AgentRole.Reviewer);
        var record = Evidence(task, at, Contract(AgentRole.Reviewer)) with { StandardError = stderr, AuthoritativeStandardError = stderr };
        Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, DispatchFailureClassifier.Classify(task, record).Kind);
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void ManualVerificationAfterSubscriptionAttemptRemainsManual(AgentRole role)
    {
        var (kernel, goal, task, at) = Dispatch(role);
        // The same command text cannot make this explicit recording API a worker completion.
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(task.LastDispatch!.Command,
            task.LastDispatch.WorkingDirectory, 0, "Operator verified.", "", at.AddMinutes(1)));
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        Assert.Equal(VerificationEvidenceSource.Manual, restored.GetTask(goal.Id, task.Id).LastVerification!.EvidenceSource);
        Assert.True(restored.BuildVerificationGate(goal.Id).IsSatisfied);
    }

    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Researcher)]
    [InlineData(AgentRole.Ideation)]
    [InlineData(AgentRole.Developer)]
    public void OtherRolesDoNotAcquireVerificationContractRequirements(AgentRole role)
    {
        var (_, _, task, at) = Dispatch(role);
        Assert.Equal(VerificationTerminalContractStatus.NotApplicable,
            WorkerResultBlockers.AssessSubscriptionVerification(task, Evidence(task, at, "Existing role-specific output.")).Status);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, DateTimeOffset At) Dispatch(AgentRole role, bool subscription = true)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Require terminal evidence", [new TaskSpec(TaskId.New(), "Verify candidate", role)]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(AgentId.New(), role.ToString(), role,
            new ModelProfile("Test", "fixture", ModelCapability.Text, SubscriptionMode.ApiKey))]);
        var task = Assert.Single(goal.Tasks);
        kernel.RecordTaskDispatch(goal.Id, task.Id, subscription ? DispatchRecord(clock.UtcNow) :
            new TaskDispatchRecord("api-worker", "api review", @"C:\repo", clock.UtcNow, WorkerProviderKind: ProviderKind.HermesAcp));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Fixture attempt started.");
        return (kernel, goal, task, clock.UtcNow);
    }

    private static TaskDispatchRecord DispatchRecord(DateTimeOffset at) =>
        new("grok-cli", "grok review", @"C:\repo", at, WorkerProviderKind: ProviderKind.XaiGrokCli);

    private static TaskVerificationRecord Evidence(TaskSpec task, DateTimeOffset at, string output) =>
        new(task.LastDispatch!.Command, task.LastDispatch.WorkingDirectory, 0, output, "", at.AddSeconds(1),
            WorkerResultPresent: true, DispatchStartedAt: at, EvidenceSource: VerificationEvidenceSource.Dispatch, DispatchAttemptAt: at);

    private static string Contract(AgentRole role, string? tests = null) =>
        "WORKER_RESULT:\nfiles: none\ncommands: inspected gate receipt\ntests: " +
        (tests ?? (role == AgentRole.Tester ? "pass - gate-owned.trx; Passed: 1" : "deferred - AcceptanceTests")) +
        "\ncommit: none\nblockers: none\nmodel_fit: fixture\nskills: none\nconfidence: high\n" +
        (role == AgentRole.Reviewer ? "findings: []\ntouched_anchors: []\ncriteria_verdicts: []\nverdict: pass\n" : "") +
        "END_WORKER_RESULT";
}
