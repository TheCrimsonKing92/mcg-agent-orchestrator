using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class TaskVerificationTests
{
    [Xunit.Fact(DisplayName = "Task_dispatch_snapshot_round_trips_review_retry_cap_receipt")]
    public void TaskDispatchSnapshotRoundTripsReviewRetryCapReceipt()
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Persist review cap context", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "reviewer",
            "review",
            "C:\\repo",
            DateTimeOffset.UtcNow,
            ReviewRetryCap: new ReviewRetryCapReceipt(5, 9)));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var receipt = Assert.IsType<ReviewRetryCapReceipt>(
            restored.GetGoal(goal.Id).FindTask(reviewer.Id).LastDispatch!.ReviewRetryCap);

        Assert.Equal(5, receipt.Round);
        Assert.Equal(9, receipt.StopRound);
        Assert.False(receipt.IsAtCap);
    }

    [Xunit.Fact]
    public void TaskDispatchSnapshotRoundTripsContextPackageReceiptAndTypedUsage()
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Persist context package receipt", [developer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var receipt = new WorkerContextPackageReceipt(
            "ctxpkg-v1-sha256:" + new string('a', 64),
            [new WorkerContextSectionReceipt("brief/current.md", 12, 12, new string('b', 64), ContextDeliveryMode.InlineFull, 1, [AgentRole.Developer])],
            ProviderUsageValue.Reported(123),
            ProviderUsageValue.Unknown("unsupported"),
            ProviderUsageValue.Reported(45),
            RenderedPromptBytes: 4_096,
            ReviewFindingProjectionMode: ReviewFindingHistoryProjectionMode.ContractRepair,
            UniqueReviewFindingRoundCount: 7,
            DuplicateReviewFindingRoundCount: 13,
            UniqueFindingEvidenceReceiptCount: 9,
            DuplicateFindingEvidenceReceiptCount: 21,
            ReviewFindingFallbackReason: "material-evidence-changed");
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "developer",
            "run",
            "C:\\repo",
            DateTimeOffset.UtcNow,
            ContextPackageReceipt: receipt));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restoredReceipt = restored.GetTask(goal.Id, developer.Id).LastDispatch!.ContextPackageReceipt!;

        Assert.Equal(receipt.SemanticPackageId, restoredReceipt.SemanticPackageId);
        Assert.Equal(123, restoredReceipt.InputTokens.Value);
        Assert.Equal(ProviderUsageState.Unknown, restoredReceipt.CachedInputTokens.State);
        Assert.Equal("unsupported", restoredReceipt.CachedInputTokens.UnknownReason);
        Assert.Equal(ContextDeliveryMode.InlineFull, Assert.Single(restoredReceipt.Sections).DeliveryMode);
        Assert.Equal(4_096, restoredReceipt.RenderedPromptBytes);
        Assert.Equal(ReviewFindingHistoryProjectionMode.ContractRepair, restoredReceipt.ReviewFindingProjectionMode);
        Assert.Equal(7, restoredReceipt.UniqueReviewFindingRoundCount);
        Assert.Equal(13, restoredReceipt.DuplicateReviewFindingRoundCount);
        Assert.Equal(9, restoredReceipt.UniqueFindingEvidenceReceiptCount);
        Assert.Equal(21, restoredReceipt.DuplicateFindingEvidenceReceiptCount);
        Assert.Equal("material-evidence-changed", restoredReceipt.ReviewFindingFallbackReason);
        var serializedReceipt = JsonSerializer.Serialize(restoredReceipt);
        Assert.DoesNotContain("prompt_body", serializedReceipt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("receipt_body", serializedReceipt, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void TaskDispatchSnapshot_RetryReceipts_RetainsEachAttempt()
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Retain attempt receipts", [developer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var firstAt = DateTimeOffset.Parse("2026-08-11T20:00:00Z");
        var secondRequestedAt = firstAt;
        var secondRecordedAt = firstAt.AddTicks(1);
        WorkerContextPackageReceipt Receipt(char id) => new(
            "ctxpkg-v1-sha256:" + new string(id, 64),
            [new WorkerContextSectionReceipt("brief/current.md", 12, 12, new string(id, 64), ContextDeliveryMode.InlineFull, 1, [AgentRole.Developer])],
            ProviderUsageValue.Unknown("not-yet-reported"),
            ProviderUsageValue.Unknown("not-yet-reported"),
            ProviderUsageValue.Unknown("not-yet-reported"));

        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "developer", "first", "C:\\repo", firstAt, ContextPackageReceipt: Receipt('a')));
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "developer", "second", "C:\\repo", secondRequestedAt, ContextPackageReceipt: Receipt('b')),
            allowPendingRecordedDispatchRefresh: true);

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot())
            .GetTask(goal.Id, developer.Id);

        Assert.Equal(2, restored.DispatchHistory.Count);
        Assert.Equal(firstAt, restored.DispatchHistory[0].DispatchedAt);
        Assert.EndsWith(new string('a', 64), restored.DispatchHistory[0].ContextPackageReceipt!.SemanticPackageId, StringComparison.Ordinal);
        Assert.Equal(secondRecordedAt, restored.DispatchHistory[1].DispatchedAt);
        Assert.EndsWith(new string('b', 64), restored.DispatchHistory[1].ContextPackageReceipt!.SemanticPackageId, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ContextPackageUsageUpdateTargetsOriginatingDispatchAttempt()
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Attribute usage by dispatch attempt", [developer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var firstAt = DateTimeOffset.Parse("2026-08-11T20:00:00Z");
        var secondAt = firstAt.AddMinutes(1);
        WorkerContextPackageReceipt Receipt(char id) => new(
            "ctxpkg-v1-sha256:" + new string(id, 64),
            [],
            ProviderUsageValue.Unknown("not-yet-reported"),
            ProviderUsageValue.Unknown("not-yet-reported"),
            ProviderUsageValue.Unknown("not-yet-reported"));
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "developer", "first", "C:\\repo", firstAt, ContextPackageReceipt: Receipt('a')));
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "developer", "second", "C:\\repo", secondAt, ContextPackageReceipt: Receipt('b')),
            allowPendingRecordedDispatchRefresh: true);

        kernel.RecordDispatchContextPackageReceipt(
            goal.Id,
            developer.Id,
            firstAt,
            Receipt('a').WithProviderUsage(new ProviderReportedUsage(4_000_000_000L, 17, 3_000_000_000L)));

        var task = kernel.GetTask(goal.Id, developer.Id);
        Assert.Equal(4_000_000_000L, task.DispatchHistory[0].ContextPackageReceipt!.InputTokens.Value);
        Assert.Equal(3_000_000_000L, task.DispatchHistory[0].ContextPackageReceipt!.CachedInputTokens.Value);
        Assert.Equal(ProviderUsageState.Unknown, task.DispatchHistory[1].ContextPackageReceipt!.InputTokens.State);
        Assert.Equal(secondAt, task.LastDispatch!.DispatchedAt);
    }

    [Xunit.Fact]
    public void TaskVerificationSnapshotRetainsCompleteAuthoritativeOutputBeyondPreview()
    {
        var full = "head-" + new string('x', VerificationTextBounds.MaxRetainedChars + 100) + "-tail";
        var record = new TaskVerificationRecord(
            "verify",
            "C:\\repo",
            0,
            full,
            string.Empty,
            DateTimeOffset.UtcNow,
            FullStandardOutput: full,
            FullStandardError: string.Empty);
        Assert.NotEqual(full, record.StandardOutput);
        Assert.Equal(full, record.AuthoritativeStandardOutput);

        var kernel = new AgentOrchestratorKernel();
        var tester = new TaskSpec(TaskId.New(), "Test", AgentRole.Tester);
        var goal = kernel.CreateGoal("Retain complete evidence", [tester]);
        kernel.RecordTaskVerification(goal.Id, tester.Id, record);

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        Assert.Equal(full, restored.GetTask(goal.Id, tester.Id).LastVerification!.AuthoritativeStandardOutput);
    }

    [Xunit.Fact]
    public void TaskExecutionSnapshotWithoutAuthorityDoesNotPromoteBoundedPreview()
    {
        var taskId = TaskId.New();
        var goalId = GoalId.New();
        var legacyExecution = new TaskExecutionSnapshot(
            "agent",
            "Developer",
            "OpenAI",
            "gpt",
            "bounded preview only",
            "stop",
            null,
            null,
            DateTimeOffset.Parse("2026-08-11T20:00:00Z"));
        var snapshot = new OrchestratorSnapshot(
            [new GoalSnapshot(
                goalId.Value,
                "Restore legacy execution",
                GoalStatus.Active,
                [new TaskSnapshot(
                    taskId.Value,
                    "Implement",
                    AgentRole.Developer,
                    WorkTaskStatus.Completed,
                    null,
                    legacyExecution,
                    null,
                    null,
                    null,
                    null)],
                [])],
            []);

        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot).GetTask(goalId, taskId).LastExecution!;

        Assert.Equal("bounded preview only", restored.Output);
        Assert.Null(restored.AuthoritativeOutput);
    }

    [Xunit.Fact]
    public void TaskVerificationSnapshotPreservesExplicitCompleteOutputAbsenceWithoutPromotingPreview()
    {
        var record = new TaskVerificationRecord(
            "verify",
            "C:\\repo",
            1,
            "bounded preview only",
            string.Empty,
            DateTimeOffset.UtcNow,
            FullStandardOutputUnavailableReason: "missing");
        var kernel = new AgentOrchestratorKernel();
        var tester = new TaskSpec(TaskId.New(), "Test", AgentRole.Tester);
        var goal = kernel.CreateGoal("Do not promote bounded previews", [tester]);
        kernel.RecordTaskVerification(goal.Id, tester.Id, record);

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot())
            .GetTask(goal.Id, tester.Id).LastVerification!;

        Assert.Equal("bounded preview only", restored.StandardOutput);
        Assert.Null(restored.AuthoritativeStandardOutput);
        Assert.Equal("missing", restored.FullStandardOutputUnavailableReason);
    }

    [Xunit.Fact]
    public void LegacySnapshotWithoutAuthorityMetadataDoesNotPromoteBoundedPreview()
    {
        var kernel = new AgentOrchestratorKernel();
        var tester = new TaskSpec(TaskId.New(), "Test", AgentRole.Tester);
        var goal = kernel.CreateGoal("Restore legacy bounded evidence", [tester]);
        kernel.RecordTaskVerification(goal.Id, tester.Id, new TaskVerificationRecord(
            "verify",
            "C:\\repo",
            0,
            "bounded preview only",
            string.Empty,
            DateTimeOffset.UtcNow,
            FullStandardOutput: "bounded preview only",
            FullStandardError: string.Empty));
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = Assert.Single(snapshot.Goals);
        var taskSnapshot = Assert.Single(goalSnapshot.Tasks);
        var legacyVerification = taskSnapshot.LastVerification! with
        {
            AuthoritativeStandardOutput = null,
            AuthoritativeStandardOutputUnavailableReason = null
        };
        var legacySnapshot = snapshot with
        {
            Goals =
            [
                goalSnapshot with
                {
                    Tasks =
                    [
                        taskSnapshot with
                        {
                            LastVerification = legacyVerification,
                            VerificationHistory = [legacyVerification]
                        }
                    ]
                }
            ]
        };

        var restored = AgentOrchestratorKernel.FromSnapshot(legacySnapshot)
            .GetTask(goal.Id, tester.Id).LastVerification!;

        Assert.Equal("bounded preview only", restored.StandardOutput);
        Assert.Null(restored.AuthoritativeStandardOutput);
        Assert.Equal(
            "legacy-snapshot-authoritative-output-unavailable",
            restored.FullStandardOutputUnavailableReason);
    }

    [Xunit.Fact(DisplayName = "PreReviewEvidenceReceipt_round_trips_through_snapshot")]
    public void PreReviewEvidenceReceiptRoundTripsThroughSnapshot()
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Persist pre-review evidence", [reviewer]);
        kernel.RecordPreReviewEvidence(
            goal.Id,
            reviewer.Id,
            new PreReviewEvidenceReceipt(
                goal.Id.Value,
                3,
                "candidate-sha",
                ["dotnet test --filter FocusedTests"],
                PreReviewEvidenceDisposition.Green,
                1,
                0,
                [new PreReviewEvidenceCheckReceipt("focused", "dotnet test --filter FocusedTests", true, 0, "receipt")],
                [],
                "mapped",
                "receipt\\result.trx",
                DateTimeOffset.UtcNow));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var receipt = restored.GetGoal(goal.Id).FindTask(reviewer.Id).PreReviewEvidenceReceipt;

        Assert.NotNull(receipt);
        Assert.Equal(3, receipt.ReviewerRound);
        Assert.Equal("candidate-sha", receipt.CandidateSha);
        Assert.Equal(PreReviewEvidenceDisposition.Green, receipt.Disposition);
        Assert.Equal("receipt\\result.trx", receipt.EvidencePointer);
        Assert.Single(restored.GetGoal(goal.Id).FindTask(reviewer.Id).PreReviewEvidenceHistory);
    }

    [Xunit.Fact(DisplayName = "PreReviewEvidenceReceipt_history_round_trips_current_candidate_constituents")]
    public void PreReviewEvidenceReceiptHistoryRoundTripsCurrentCandidateConstituents()
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Persist pre-review evidence constituents", [reviewer]);
        PreReviewEvidenceReceipt Create(string selection) => new(
            goal.Id.Value,
            1,
            "candidate-sha",
            [selection],
            PreReviewEvidenceDisposition.Green,
            1,
            0,
            [new PreReviewEvidenceCheckReceipt(selection, selection, true, 0)],
            [],
            "mapped",
            $"receipt\\{selection}.trx",
            DateTimeOffset.UtcNow);
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, Create("First"));
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, Create("Second"));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restoredReviewer = restored.GetGoal(goal.Id).FindTask(reviewer.Id);

        Assert.Equal(["First", "Second"], restoredReviewer.PreReviewEvidenceHistory
            .SelectMany(receipt => receipt.SelectedFocusedTests));
        Assert.Equal("Second", Assert.Single(restoredReviewer.PreReviewEvidenceReceipt!.SelectedFocusedTests));
    }

    [Xunit.Fact(DisplayName = "PreReviewEvidenceReceipt_is_structurally_idempotent_and_current_head_owned")]
    public void PreReviewEvidenceReceiptIsStructurallyIdempotentAndCurrentHeadOwned()
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Persist one current-head receipt", [reviewer]);
        var recordedAt = DateTimeOffset.UtcNow;
        var receiptNumber = 0;
        PreReviewEvidenceReceipt CreateReceipt() =>
            new(
                goal.Id.Value,
                1,
                "candidate-sha",
                ["dotnet test --filter FocusedTests"],
                PreReviewEvidenceDisposition.Green,
                1,
                0,
                [
                    new PreReviewEvidenceCheckReceipt(
                        "focused",
                        "dotnet test --filter FocusedTests",
                        true,
                        0,
                        "receipt",
                        ["receipt\\result.trx"])
                ],
                [],
                "mapped",
                "receipt\\result.trx",
                recordedAt.AddSeconds(receiptNumber++));

        var receipt = CreateReceipt();
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, receipt);
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, receipt);
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, receipt with { RecordedAt = receipt.RecordedAt.AddTicks(1) });

        Assert.Equal(2, goal.Timeline.Count(evt => evt.Kind == ProgressKind.PreReviewEvidenceRecorded));
        Assert.Equal(2, reviewer.PreReviewEvidenceAttemptCount);
        Assert.True(reviewer.PreReviewEvidenceReceipt!.MatchesCurrentCandidate(
            goal.Id.Value,
            "candidate-sha",
            ["dotnet test --filter FocusedTests"]));
        Assert.False(reviewer.PreReviewEvidenceReceipt.MatchesCurrentCandidate(
            goal.Id.Value,
            "stale-sha",
            ["dotnet test --filter FocusedTests"]));
    }

    [Xunit.Fact(DisplayName = "RecordTaskVerification_persists_result_and_timeline_event")]
    public void RecordTaskVerificationPersistsResultAndTimelineEvent()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Verify task behavior");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    var verification = new TaskVerificationRecord(
        "dotnet test",
        "C:\\repo",
        0,
        "Passed",
        string.Empty,
        clock.UtcNow);

    kernel.RecordTaskVerification(goal.Id, task.Id, verification);

    Assert.Equal(verification with { AcceptanceCriteriaVersionHash = "no-refined-spec" }, task.LastVerification);
    Assert.Equal(1, task.VerificationHistory.Count);
    Assert.Equal(verification with { AcceptanceCriteriaVersionHash = "no-refined-spec" }, task.VerificationHistory.Single());
    Assert.True(task.LastVerification!.Succeeded);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskVerificationRecorded &&
        evt.Message.Contains("passed", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "RecordTaskVerification_appends_history_and_updates_latest")]
    public void RecordTaskVerificationAppendsHistoryAndUpdatesLatest()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Retain verification history");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    var first = new TaskVerificationRecord("dotnet build", "C:\\repo", 1, string.Empty, "failed", clock.UtcNow);
    var second = new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", string.Empty, clock.UtcNow);

    kernel.RecordTaskVerification(goal.Id, task.Id, first);
    kernel.RecordTaskVerification(goal.Id, task.Id, second);

    Assert.Equal(second with { AcceptanceCriteriaVersionHash = "no-refined-spec" }, task.LastVerification);
    Assert.Equal(2, task.VerificationHistory.Count);
    Assert.Equal(first with { AcceptanceCriteriaVersionHash = "no-refined-spec" }, task.VerificationHistory[0]);
    Assert.Equal(second with { AcceptanceCriteriaVersionHash = "no-refined-spec" }, task.VerificationHistory[1]);
}
    [Xunit.Fact(DisplayName = "TaskVerificationRecord_bounds_large_path_output_at_creation")]
    public void TaskVerificationRecordBoundsLargePathOutputAtCreation()
{
    var largeOutput = new string('A', 2_000_000);
    var largeError = new string('E', 2_000_000);

    var verification = new TaskVerificationRecord(
        "worker",
        "C:\\repo",
        1,
        largeOutput,
        largeError,
        DateTimeOffset.UtcNow,
        StandardOutputPath: "C:\\logs\\worker.out.log",
        StandardErrorPath: "C:\\logs\\worker.err.log");

    AssertRetainedTextBounded(verification.StandardOutput);
    AssertRetainedTextBounded(verification.StandardError);
    Assert.Contains("full output at: C:\\logs\\worker.out.log", verification.StandardOutput, StringComparison.Ordinal);
    Assert.Contains("full output at: C:\\logs\\worker.err.log", verification.StandardError, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "TaskVerificationRecord_bounds_large_pathless_output_at_creation")]
    public void TaskVerificationRecordBoundsLargePathlessOutputAtCreation()
{
    var largeOutput = new string('B', 2_000_000);

    var verification = new TaskVerificationRecord(
        "manual",
        "C:\\repo",
        0,
        largeOutput,
        string.Empty,
        DateTimeOffset.UtcNow);

    AssertRetainedTextBounded(verification.StandardOutput);
    Assert.Contains("full output path not recorded", verification.StandardOutput, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "TaskVerificationRecord_rebounds_large_pathless_output_containing_truncation_marker")]
    public void TaskVerificationRecordReboundsLargePathlessOutputContainingTruncationMarker()
{
    var largeOutput =
        new string('H', 1_000_000) +
        "\n...[1,234 chars; full output at: C:\\logs\\old.out.log]...\n" +
        new string('T', 1_000_000);

    var verification = new TaskVerificationRecord(
        "manual",
        "C:\\repo",
        0,
        largeOutput,
        string.Empty,
        DateTimeOffset.UtcNow);

    AssertRetainedTextBounded(verification.StandardOutput);
    Assert.Contains("full output path not recorded", verification.StandardOutput, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "TaskExecutionSnapshot_bounds_large_output_at_construction")]
    public void TaskExecutionSnapshotBoundsLargeOutputAtConstruction()
{
    var snapshot = new TaskExecutionSnapshot(
        "agent",
        "Developer",
        "OpenAI",
        "gpt",
        new string('C', 2_000_000),
        "stop",
        null,
        null,
        DateTimeOffset.UtcNow);

    AssertRetainedTextBounded(snapshot.Output);
    Assert.Contains("full output path not recorded", snapshot.Output, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "RecordTaskVerification_caps_history_to_most_recent_entries")]
    public void RecordTaskVerificationCapsHistoryToMostRecentEntries()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Cap verification history");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);

    const int expectedHistoryLimit = 20;

    Assert.Equal(expectedHistoryLimit, TaskSpec.VerificationHistoryLimit);

    for (var index = 0; index < expectedHistoryLimit + 5; index++)
    {
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "verify",
            "C:\\repo",
            1,
            $"stdout-{index}",
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(index)));
    }

    Assert.Equal(expectedHistoryLimit, task.VerificationHistory.Count);
    Assert.Equal("stdout-5", task.VerificationHistory[0].StandardOutput);
    Assert.Equal("stdout-24", task.VerificationHistory[^1].StandardOutput);
    Assert.Equal("stdout-24", task.LastVerification!.StandardOutput);
}

    [Xunit.Fact(DisplayName = "TaskSpec_preserves_all_structured_inconclusive_attempts_beyond_normal_history_cap")]
    public void TaskSpecPreservesAllStructuredInconclusiveAttemptsBeyondNormalHistoryCap()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Preserve bounded inconclusive retry receipts");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var tester = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
        var attempts = TaskSpec.VerificationHistoryLimit + 5;

        for (var index = 0; index < attempts; index++)
        {
            tester.RecordVerification(new TaskVerificationRecord(
                "test",
                "C:\\repo",
                0,
                $"WORKER_RESULT:\ntests: inconclusive - attempt {index}; no TRX\nblockers: none\nEND_WORKER_RESULT",
                "",
                DateTimeOffset.UtcNow.AddMinutes(index),
                WorkerResultPresent: true));
        }

        Assert.Equal(attempts, tester.VerificationHistory.Count);
        Assert.All(tester.VerificationHistory, verification =>
            Assert.True(
                WorkerResultBlockers.TryGetTestsStatus(verification, out var status) &&
                status == WorkerResultBlockers.TestsStatus.Inconclusive));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restoredHistory = restored.GetTask(goal.Id, tester.Id).VerificationHistory;
        Assert.Equal(attempts, restoredHistory.Count);
        Assert.All(restoredHistory, verification =>
        {
            Assert.True(verification.WorkerResultPresent);
            Assert.True(
                WorkerResultBlockers.TryGetTestsStatus(verification, out var status) &&
                status == WorkerResultBlockers.TestsStatus.Inconclusive);
        });
    }

    [Xunit.Fact(DisplayName = "TaskSpec_excludes_durable_research_and_plan_receipts_from_history_trimming")]
    public void TaskSpecExcludesDurableResearchAndPlanReceiptsFromHistoryTrimming()
    {
        foreach (var role in new[] { AgentRole.Researcher, AgentRole.Planner })
        {
            var task = new TaskSpec(TaskId.New(), $"Produce {role} artifact.", role);
            var durable = new TaskVerificationRecord(
                "artifact",
                "C:\\repo",
                0,
                "complete artifact",
                string.Empty,
                DateTimeOffset.UtcNow,
                StandardOutputPath: $"C:\\logs\\{role}.out.log");
            task.RecordVerification(durable);

            for (var index = 0; index < TaskSpec.VerificationHistoryLimit + 5; index++)
            {
                task.RecordVerification(new TaskVerificationRecord(
                    "retry",
                    "C:\\repo",
                    1,
                    $"retry-noise-{index}",
                    string.Empty,
                    DateTimeOffset.UtcNow.AddMinutes(index + 1)));
            }

            Assert.Contains(durable, task.VerificationHistory);
            Assert.Equal(TaskSpec.VerificationHistoryLimit, task.VerificationHistory.Count);
            Assert.DoesNotContain(
                task.VerificationHistory,
                verification => verification.StandardOutput == "retry-noise-0");
        }
    }

    [Xunit.Fact(DisplayName = "TaskSpec_excludes_finding_evidence_receipts_from_history_trimming")]
    public void TaskSpecExcludesFindingEvidenceReceiptsFromHistoryTrimming()
    {
        var task = new TaskSpec(TaskId.New(), "Test the change.", AgentRole.Tester);
        var request = new FindingEvidenceRequest(
            [new FindingEvidenceSelection("Core.Tests", "TaskVerificationTests")]);
        var durable = new TaskVerificationRecord(
            "focused evidence",
            "C:\\repo",
            0,
            "focused evidence passed",
            "",
            DateTimeOffset.UtcNow,
            FindingEvidenceReceipts:
            [
                new FindingEvidenceReceipt(
                    "receipt-durable",
                    "abc1234",
                    request,
                    Accepted: true,
                    Passed: true,
                    "focused evidence passed")
            ]);
        task.RecordVerification(durable);

        for (var index = 0; index < TaskSpec.VerificationHistoryLimit + 5; index++)
        {
            task.RecordVerification(new TaskVerificationRecord(
                "retry",
                "C:\\repo",
                1,
                $"retry-noise-{index}",
                "",
                DateTimeOffset.UtcNow.AddMinutes(index + 1)));
        }

        Assert.Contains(durable, task.VerificationHistory);
        Assert.Equal(TaskSpec.VerificationHistoryLimit, task.VerificationHistory.Count);
        Assert.Same(
            durable.FindingEvidenceReceipts!.Single(),
            task.VerificationHistory.Single(verification => ReferenceEquals(verification, durable))
                .FindingEvidenceReceipts!.Single());
    }

    [Xunit.Fact(DisplayName = "FindingEvidenceReceipt_dual_arms_round_trip_through_snapshot")]
    public void FindingEvidenceReceiptDualArmsRoundTripThroughSnapshot()
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review dual-arm evidence.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Persist dual-arm evidence", [reviewer]);
        var request = new FindingEvidenceRequest(
            [new FindingEvidenceSelection("Core.Tests", "TaskVerificationTests")]);
        reviewer.RecordVerification(new TaskVerificationRecord(
            "focused evidence",
            "C:\\repo",
            0,
            "valid dual-arm evidence",
            "",
            DateTimeOffset.UtcNow,
            FindingEvidenceReceipts:
            [
                new FindingEvidenceReceipt(
                    "receipt-dual-arm",
                    "candidate-sha",
                    request,
                    Accepted: true,
                    Passed: true,
                    "valid evidence",
                    Arms:
                    [
                        new FindingEvidenceArmReceipt(
                            FindingEvidenceArm.Candidate,
                            "candidate-sha",
                            FindingEvidenceArmDisposition.Green,
                            true,
                            true,
                            "candidate passed",
                            ["candidate.trx"],
                            [],
                            ExecutedTestCount: 7,
                            TestResultPaths: ["candidate.trx"],
                            ReceiptArtifacts:
                            [
                                new AcceptanceCohortEvidenceArtifact(
                                    "trx",
                                    "candidate.trx",
                                    new string('a', 64),
                                    123)
                            ]),
                        new FindingEvidenceArmReceipt(
                            FindingEvidenceArm.Baseline,
                            "baseline-sha",
                            FindingEvidenceArmDisposition.Red,
                            true,
                            false,
                            "baseline failed",
                            ["baseline.trx"],
                            ["TaskVerificationTests.NegativeControl"])
                    ],
                    RequestDispositions:
                    [
                        new FindingEvidenceRequestDisposition(
                            "finding-a",
                            "Core.Tests:TaskVerificationTests",
                            "executed-batched",
                            "compatible-same-project"),
                        new FindingEvidenceRequestDisposition(
                            "finding-b",
                            "Infrastructure.Tests:ConductorDriverTests",
                            "superseded",
                            "superseded-by-actionable-red")
                    ],
                    FindingRoundFingerprint: "finding-round-contract-1",
                    ExecutionBasisIdentity: "focused-v1-sha256:contract")
            ]));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var receipt = Assert.Single(restored.GetGoal(goal.Id).FindTask(reviewer.Id)
            .LastVerification!.FindingEvidenceReceipts!);

        Assert.Equal("receipt-dual-arm", receipt.ReceiptId);
        Assert.Equal("finding-round-contract-1", receipt.FindingRoundFingerprint);
        Assert.Equal("focused-v1-sha256:contract", receipt.ExecutionBasisIdentity);
        Assert.Collection(
            receipt.Arms!,
            arm =>
            {
                Assert.Equal(FindingEvidenceArm.Candidate, arm.Arm);
                Assert.Equal(FindingEvidenceArmDisposition.Green, arm.Disposition);
                Assert.Equal(["candidate.trx"], arm.ReceiptPaths);
                Assert.Equal(7, arm.ExecutedTestCount);
                Assert.Equal(["candidate.trx"], arm.TestResultPaths);
                Assert.Equal(new string('a', 64), Assert.Single(arm.ReceiptArtifacts!).Sha256);
            },
            arm =>
            {
                Assert.Equal(FindingEvidenceArm.Baseline, arm.Arm);
                Assert.Equal(FindingEvidenceArmDisposition.Red, arm.Disposition);
                Assert.Equal(["TaskVerificationTests.NegativeControl"], arm.FailingTestIdentities);
            });
        Assert.Collection(
            receipt.RequestDispositions!,
            disposition =>
            {
                Assert.Equal("finding-a", disposition.FindingStableId);
                Assert.Equal("executed-batched", disposition.Disposition);
            },
            disposition =>
            {
                Assert.Equal("finding-b", disposition.FindingStableId);
                Assert.Equal("superseded-by-actionable-red", disposition.Reason);
            });
    }

    [Xunit.Fact]
    public void FindingEvidenceApparatusDispositionRoundTripsThroughWireConverters()
    {
        var armJson = JsonSerializer.Serialize(FindingEvidenceArmDisposition.ApparatusFailure);
        var outcomeJson = JsonSerializer.Serialize(FindingEvidenceOutcomeReason.ApparatusFailure);
        var refusalJson = JsonSerializer.Serialize(FindingEvidenceNotHonouredReason.SelectionApparatusFailure);
        var supersededJson = JsonSerializer.Serialize(FindingEvidenceNotHonouredReason.SupersededByActionableRed);

        Assert.Equal("\"apparatus-failure\"", armJson);
        Assert.Equal("\"apparatus-failure\"", outcomeJson);
        Assert.Equal("\"selection-apparatus-failure\"", refusalJson);
        Assert.Equal("\"superseded-by-actionable-red\"", supersededJson);
        Assert.Equal(
            FindingEvidenceArmDisposition.ApparatusFailure,
            JsonSerializer.Deserialize<FindingEvidenceArmDisposition>(armJson));
        Assert.Equal(
            FindingEvidenceOutcomeReason.ApparatusFailure,
            JsonSerializer.Deserialize<FindingEvidenceOutcomeReason>(outcomeJson));
        Assert.Equal(
            FindingEvidenceNotHonouredReason.SelectionApparatusFailure,
            JsonSerializer.Deserialize<FindingEvidenceNotHonouredReason>(refusalJson));
        Assert.Equal(
            FindingEvidenceNotHonouredReason.SupersededByActionableRed,
            JsonSerializer.Deserialize<FindingEvidenceNotHonouredReason>(supersededJson));
    }

    [Xunit.Fact(DisplayName = "RecordTaskVerification_completes_goal_only_when_all_gates_pass")]
    public void RecordTaskVerificationCompletesGoalOnlyWhenAllGatesPass()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Require verification gate",
        [new TaskSpec(TaskId.New(), "Only task", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, [DefaultAgents().First(agent => agent.Role == AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Implementation done.");

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(VerificationGateStatus.MissingVerification, kernel.BuildVerificationGate(goal.Id).Tasks.Single().GateStatus);

    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "dotnet test",
        "C:\\repo",
        0,
        "passed",
        string.Empty,
        clock.UtcNow));

    Assert.Equal(GoalStatus.Verified, goal.Status);
    Assert.True(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
}
    [Xunit.Fact(DisplayName = "Completion verdict does not pass an incomplete task gate")]
    public void CompletionVerdictDoesNotPassIncompleteTaskGate()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Keep stamped task incomplete",
        [new TaskSpec(TaskId.New(), "Incomplete task", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, [DefaultAgents().First(agent => agent.Role == AgentRole.Developer)]);
    var task = goal.Tasks.Single();
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "dotnet test", "C:\\repo", 1, "", "failed", DateTimeOffset.UtcNow));
    task.RecordCompletionVerdict(true, "verified-no-change-round");

    var gate = kernel.BuildVerificationGate(goal.Id).Tasks.Single();

    Assert.NotEqual(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(VerificationGateStatus.NotReady, gate.GateStatus);
}
    [Xunit.Fact(DisplayName = "RetryTask_reopens_task_and_preserves_verification_history")]
    public void RetryTaskReopensTaskAndPreservesVerificationHistory()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Retry failed verification");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Implementation done.");
    var failed = new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "", "failed", clock.UtcNow);
    kernel.RecordTaskVerification(goal.Id, task.Id, failed);

    var retried = kernel.RetryTask(goal.Id, task.Id, "Fix and rerun implementation.");
    var gate = kernel.BuildVerificationGate(goal.Id).Tasks.Single(item => item.TaskId == task.Id);
    var stage = kernel.BuildStageReadinessReport(goal.Id).Stages.Single(item => item.TaskId == task.Id);

    Assert.Equal(task, retried);
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal<TaskVerificationRecord?>(null, task.LastVerification);
    Assert.Equal(1, task.VerificationHistory.Count);
    Assert.Equal(failed with { AcceptanceCriteriaVersionHash = "no-refined-spec" }, task.VerificationHistory.Single());
    Assert.Equal(VerificationGateStatus.NotReady, gate.GateStatus);
    Assert.Equal(StageReadinessStatus.ReadyToRun, stage.StageStatus);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.Contains("Fix and rerun", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "RetryTask_requires_message_before_clearing_evidence")]
    public void RetryTaskRequiresMessageBeforeClearingEvidence()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Retry needs reason");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var verification = new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", clock.UtcNow);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Implementation done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, verification);

    var ex = Assert.ThrowsAny<ArgumentException>(() => kernel.RetryTask(goal.Id, task.Id, " "));

    Assert.Contains("Retry message cannot be empty", ex.Message, StringComparison.Ordinal);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(verification with { AcceptanceCriteriaVersionHash = "no-refined-spec" }, task.LastVerification);
    Assert.True(!goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried));
}
    [Xunit.Fact(DisplayName = "RetryTask_mechanical_round_marker_persists_and_clears_after_verification")]
    public void RetryTaskMechanicalRoundMarkerPersistsAndClearsAfterVerification()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Retry mechanical receipt");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var candidateSha = new string('a', 40);
    var location = new ReviewFindingLocation("src/One.cs", "One.Run");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "review",
        "C:\\repo",
        1,
        "malformed output",
        string.Empty,
        DateTimeOffset.UtcNow,
        ReviewFindingTouchedAnchors: [location],
        ReviewedCommit: candidateSha,
        MergedReviewFindings: [new ReviewFinding("stable-one", ReviewFindingState.Open, location, "finding")],
        ReviewFindingContractViolation: new ReviewFindingContractViolation("schema-invalid", "missing fields"),
        FindingEvidenceReceipts:
        [
            new FindingEvidenceReceipt(
                "receipt-one",
                candidateSha,
                new FindingEvidenceRequest([new FindingEvidenceSelection("tests/Tests.csproj", "Tests.One")]),
                true,
                true,
                "evidence")
        ],
        FullStandardOutput: "malformed output",
        FullStandardError: string.Empty));

    kernel.RetryTask(goal.Id, task.Id, "Rerun named commands and quote receipts.", retryRoundKind: RetryRoundKind.Mechanical);

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
    var restoredTask = restored.GetTask(goal.Id, task.Id);
    Assert.Equal(RetryRoundKind.Mechanical, restoredTask.PendingRetryRoundKind);
    Assert.Equal(candidateSha, restoredTask.PendingReviewFindingRepairCheckpoint!.CandidateSha);
    Assert.Single(restoredTask.PendingReviewFindingRepairCheckpoint.EvidenceContentHashes);
    Assert.Equal(["stable-one"], restoredTask.PendingReviewFindingRepairCheckpoint.StableFindingIds);

    restored.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", "worker run", "C:\\repo", DateTimeOffset.UtcNow));
    restored.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "worker run",
        "C:\\repo",
        0,
        "WORKER_RESULT:\ntests: pass - quoted receipts\nblockers: none\nEND_WORKER_RESULT",
        string.Empty,
        DateTimeOffset.UtcNow));

    Assert.Null(restored.GetTask(goal.Id, task.Id).PendingRetryRoundKind);
    Assert.Null(restored.GetTask(goal.Id, task.Id).PendingReviewFindingRepairCheckpoint);
}
    [Xunit.Fact(DisplayName = "RetryTask_rejects_running_or_waiting_tasks")]
    public void RetryTaskRejectsRunningOrWaitingTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Reject unsafe retry");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var running = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var waiting = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    kernel.ReportTaskProgress(goal.Id, running.Id, WorkTaskStatus.Running, "Running.");
    kernel.RequestHumanInput(goal.Id, waiting.Id, "Which command?");

    Assert.ThrowsAny<InvalidOperationException>(() => kernel.RetryTask(goal.Id, running.Id, "Retry."));
    Assert.ThrowsAny<InvalidOperationException>(() => kernel.RetryTask(goal.Id, waiting.Id, "Retry."));
}
    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_task_verification")]
    public void SnapshotRoundtripPreservesTaskVerification()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Persist verification");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);

    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "dotnet build",
        "C:\\repo",
        1,
        string.Empty,
        "Build failed",
        clock.UtcNow));
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "dotnet test",
        "C:\\repo",
        0,
        "Passed",
        string.Empty,
        clock.UtcNow));

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredTask = restored.GetTask(goal.Id, task.Id);

    Assert.Equal("dotnet test", restoredTask.LastVerification!.Command);
    Assert.Equal(0, restoredTask.LastVerification.ExitCode);
    Assert.True(restoredTask.LastVerification.Succeeded);
    Assert.Equal(2, restoredTask.VerificationHistory.Count);
    Assert.Equal("dotnet build", restoredTask.VerificationHistory[0].Command);
    Assert.Equal(1, restoredTask.VerificationHistory[0].ExitCode);
    Assert.Equal("Build failed", restoredTask.VerificationHistory[0].StandardError);
    Assert.False(restoredTask.VerificationHistory[0].Succeeded);
    Assert.Equal("dotnet test", restoredTask.VerificationHistory[1].Command);
}
    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_ModelFitNote_when_set")]
    public void SnapshotRoundtripPreservesModelFitNoteWhenSet()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Persist ModelFitNote");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);

        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "dotnet test",
            "C:\\repo",
            0,
            "Passed",
            string.Empty,
            clock.UtcNow,
            ModelFitNote: "claude-sonnet-4-6 - adequate - straightforward test task"));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        var restoredTask = restored.GetTask(goal.Id, task.Id);

        Assert.Equal("claude-sonnet-4-6 - adequate - straightforward test task", restoredTask.LastVerification!.ModelFitNote);
        Assert.Equal("claude-sonnet-4-6 - adequate - straightforward test task", restoredTask.VerificationHistory.Single().ModelFitNote);
    }

    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_loads_null_ModelFitNote_when_absent")]
    public void SnapshotRoundtripLoadsNullModelFitNoteWhenAbsent()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Persist without ModelFitNote");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);

        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "dotnet test",
            "C:\\repo",
            0,
            "Passed",
            string.Empty,
            clock.UtcNow));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        var restoredTask = restored.GetTask(goal.Id, task.Id);

        Assert.Equal<string?>(null, restoredTask.LastVerification!.ModelFitNote);
        Assert.Equal<string?>(null, restoredTask.VerificationHistory.Single().ModelFitNote);
    }

    [Xunit.Fact(DisplayName = "RecordVerification_populates_ModelFitNote_from_markdown_decorated_stdout_line")]
    public void RecordVerificationPopulatesModelFitNoteFromMarkdownDecoratedStdoutLine()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Populate ModelFitNote from stdout");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
        var stdout = "Tests passed.\n**Model fit:** Anthropic/claude-haiku-4-5 — adequate — file write — quick";

        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "dotnet test",
            "C:\\repo",
            0,
            stdout,
            string.Empty,
            clock.UtcNow));

        Assert.Equal(
            "Model fit: Anthropic/claude-haiku-4-5 - adequate - file write - quick",
            task.LastVerification!.ModelFitNote);
        Assert.Equal(
            "Model fit: Anthropic/claude-haiku-4-5 - adequate - file write - quick",
            task.VerificationHistory.Single().ModelFitNote);
    }

    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_retried_task_without_latest_verification")]
    public void SnapshotRoundtripPreservesRetriedTaskWithoutLatestVerification()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Persist retried task");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "dotnet test",
        "C:\\repo",
        1,
        string.Empty,
        "failed",
        clock.UtcNow));
    kernel.RetryTask(goal.Id, task.Id, "Retry after failed test.");

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredTask = restored.GetTask(goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Assigned, restoredTask.Status);
    Assert.Equal<TaskVerificationRecord?>(null, restoredTask.LastVerification);
    Assert.Equal(1, restoredTask.VerificationHistory.Count);
    Assert.Equal("dotnet test", restoredTask.VerificationHistory.Single().Command);
    Assert.Contains(restored.GetGoal(goal.Id).Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried);
}

    [Xunit.Fact(DisplayName = "TaskVerification_contract_violation_round_trips_through_snapshot")]
    public void TaskVerificationContractViolationRoundTripsThroughSnapshot()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Round-trip reviewer contract violation");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var reviewer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Reviewer);
        var violation = new ReviewFindingContractViolation(
            ReviewFindingConvergence.IdentityMovedViolationCode,
            "Finding moved.",
            "F-1",
            "F-1",
            new ReviewFindingLocation("src/A.cs", "A.Run"),
            new ReviewFindingLocation("src/B.cs", "B.Run"),
            [
                new ReviewFindingIdentityMismatch(
                    ReviewFindingConvergence.IdentityMovedViolationCode,
                    "Finding moved.",
                    "F-1",
                    "F-1",
                    new ReviewFindingLocation("src/A.cs", "A.Run"),
                    new ReviewFindingLocation("src/B.cs", "B.Run")),
                new ReviewFindingIdentityMismatch(
                    ReviewFindingConvergence.RecycledAnchorIdentityViolationCode,
                    "Anchor recycled.",
                    "F-2",
                    "F-NEW",
                    new ReviewFindingLocation("src/C.cs", "C.Run"),
                    new ReviewFindingLocation("src/C.cs", "C.Run"))
            ]);
        reviewer.RecordVerification(new TaskVerificationRecord(
            "review",
            "C:\\repo",
            1,
            "invalid round",
            string.Empty,
            DateTimeOffset.UtcNow,
            ReviewFindingContractViolation: violation));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restoredViolation = restored
            .GetTask(goal.Id, reviewer.Id)
            .LastVerification!
            .ReviewFindingContractViolation;

        Assert.Equal(violation, restoredViolation);
        var mismatches = Assert.IsAssignableFrom<IReadOnlyList<ReviewFindingIdentityMismatch>>(
            restoredViolation!.IdentityMismatches);
        Assert.Equal(2, mismatches.Count);
        Assert.Equal(["F-1", "F-2"], mismatches.Select(mismatch => mismatch.PriorStableId));
        Assert.Equal("F-NEW", mismatches[1].SubmittedStableId);
    }

private static void AssertRetainedTextBounded(string text)
{
    Assert.True(text.Length <= VerificationTextBounds.MaxRetainedChars, $"Expected retained text <= {VerificationTextBounds.MaxRetainedChars} chars, actual {text.Length}.");
    Assert.Contains("chars;", text, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "RetryTask_invalidates_downstream_verification_current_state_but_preserves_history")]
    public void RetryTaskInvalidatesDownstreamVerificationCurrentStateButPreservesHistory()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Verify fix", AgentRole.Tester);
    var reviewer = new TaskSpec(TaskId.New(), "Review fix", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Retry cascades stale evidence", [developer, tester, reviewer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    CompleteWithVerification(kernel, goal, developer, "developer ok", clock);
    CompleteWithVerification(kernel, goal, tester, "tester ok", clock);
    CompleteWithVerification(kernel, goal, reviewer, "reviewer ok", clock);
    Assert.Equal(GoalStatus.Verified, goal.Status);

    kernel.RetryTask(goal.Id, developer.Id, "Developer output changed; downstream evidence is stale.");

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
    Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
    Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
    Assert.Null(developer.LastVerification);
    Assert.Null(tester.LastVerification);
    Assert.Null(reviewer.LastVerification);
    Assert.Single(tester.VerificationHistory);
    Assert.Single(reviewer.VerificationHistory);
    Assert.Equal(VerificationGateStatus.NotReady, kernel.BuildVerificationGate(goal.Id).Tasks.Single(item => item.TaskId == tester.Id).GateStatus);
    Assert.Equal(VerificationGateStatus.NotReady, kernel.BuildVerificationGate(goal.Id).Tasks.Single(item => item.TaskId == reviewer.Id).GateStatus);
}

    [Xunit.Fact(DisplayName = "RetryTask_developer_retry_preserves_completed_research_and_plan")]
    public void RetryTaskDeveloperRetryPreservesCompletedResearchAndPlan()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var researcher = new TaskSpec(TaskId.New(), "Research source.", AgentRole.Researcher);
        var planner = new TaskSpec(TaskId.New(), "Plan from research.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement plan.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Reuse upstream artifacts on Developer retry.", [researcher, planner, developer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        CompleteWithVerification(kernel, goal, researcher, "research artifact", clock);
        CompleteWithVerification(kernel, goal, planner, "plan artifact", clock);
        CompleteWithVerification(kernel, goal, developer, "implementation", clock);
        var researchVerification = researcher.LastVerification;
        var plannerVerification = planner.LastVerification;

        kernel.RetryTask(goal.Id, developer.Id, "Retry implementation without replanning.");

        Assert.Equal(WorkTaskStatus.Completed, researcher.Status);
        Assert.Equal(WorkTaskStatus.Completed, planner.Status);
        Assert.Equal(researchVerification, researcher.LastVerification);
        Assert.Equal(plannerVerification, planner.LastVerification);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
    }

private static void CompleteWithVerification(
    AgentOrchestratorKernel kernel,
    Goal goal,
    TaskSpec task,
    string stdout,
    FakeClock clock)
{
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, $"{task.RequiredRole} done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        $"{task.RequiredRole} verify",
        "C:\\repo",
        0,
        stdout,
        string.Empty,
        clock.UtcNow));
    clock.Advance();
}
}
