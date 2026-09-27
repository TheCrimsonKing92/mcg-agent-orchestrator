using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed class ConductorDriverTestsIncompleteScopeRetryContext
{
    internal const string CandidateSha = "abc1234";
    internal const string TestIdentity = "GateReadyCandidateProjectorTests.DefaultHarnessProducesSerializedResourceKey";
    internal const string ClosingAccount = "I preserved the unresolved dispatch failure for conductor evidence.";
    internal const string ResultBlock = "WORKER_RESULT:\nfiles: src/Feature.cs\ntests: deferred - FeatureTests\n" +
        "blockers: exact-blocker - failing candidate fixture\nassigned_scope_complete: false\nEND_WORKER_RESULT";

    [Fact]
    public void IncompleteScopeRetryCarriesPreviousAccountIntoRecordedFeedbackAndNextBrief()
    {
        var result = RunRecovery(ClosingAccount + "\n" + ResultBlock);

        Assert.Equal("Failed command: test.exe", result.Feedback[0]);
        Assert.Equal("Failure evidence: Developer declared the assigned implementation scope incomplete.", result.Feedback[1]);
        Assert.StartsWith("Previous round's account:", result.Feedback[2], StringComparison.Ordinal);
        foreach (var line in new[] { "files: src/Feature.cs", "tests: deferred - FeatureTests",
            "blockers: exact-blocker - failing candidate fixture", "assigned_scope_complete: false", ClosingAccount })
        {
            Assert.Contains(line, result.Feedback[2], StringComparison.Ordinal);
            Assert.Contains(line, result.BriefSection, StringComparison.Ordinal);
        }
        Assert.Equal(3, result.Feedback.Count);
        Assert.Equal(1, result.Starts);
    }

    [Fact]
    public void NewestRedReceiptOnCurrentCandidateAddsFailureDetail()
    {
        var root = CreateTempDirectory();
        try
        {
            var trxPath = Path.Combine(root, "candidate.trx");
            ConductorDriverTestsActionableRedFailureDetail.WriteFailureTrx(trxPath, TestIdentity);
            var result = RunRecovery(ClosingAccount + "\n" + ResultBlock,
                receipts: [
                    (Receipt("newest", CandidateSha, trxPath), new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero)),
                    (Receipt("earlier", CandidateSha, trxPath), new DateTimeOffset(2026, 9, 27, 11, 0, 0, TimeSpan.Zero)),
                    (Receipt("other-sha", "old-sha", trxPath), new DateTimeOffset(2026, 9, 27, 13, 0, 0, TimeSpan.Zero))
                ]);

            Assert.Equal(4, result.Feedback.Count);
            Assert.StartsWith("Candidate failure detail:", result.Feedback[3], StringComparison.Ordinal);
            Assert.Contains("Candidate failure detail (receipt newest):", result.Feedback[3], StringComparison.Ordinal);
            Assert.Contains("[FAIL] " + TestIdentity + " (Failed)", result.Feedback[3], StringComparison.Ordinal);
            Assert.Contains("fatal: synthetic too big", result.Feedback[3], StringComparison.Ordinal);
            Assert.Contains("fatal: synthetic too big", result.BriefSection, StringComparison.Ordinal);
            Assert.DoesNotContain("receipt earlier", result.Feedback[3], StringComparison.Ordinal);
            Assert.DoesNotContain("receipt other-sha", result.Feedback[3], StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void UnreadableCandidateTrxOmitsDetailEntry()
    {
        var result = RunRecovery(ClosingAccount + "\n" + ResultBlock,
            receipts: [(Receipt("missing", CandidateSha, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".trx")),
                new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero))]);

        Assert.Equal(3, result.Feedback.Count);
    }

    [Fact]
    public void EqualTimestampReceiptsUseOrdinalIdAndIgnoreBaselineRed()
    {
        var root = CreateTempDirectory();
        try
        {
            var trxPath = Path.Combine(root, "candidate.trx");
            ConductorDriverTestsActionableRedFailureDetail.WriteFailureTrx(trxPath, TestIdentity);
            var sameTime = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
            var result = RunRecovery(ClosingAccount + "\n" + ResultBlock,
                receipts: [
                    (Receipt("beta", CandidateSha, trxPath), sameTime),
                    (Receipt("alpha", CandidateSha, trxPath), sameTime),
                    (Receipt("baseline-only", CandidateSha, trxPath, FindingEvidenceArm.Baseline),
                        sameTime.AddMinutes(1))
                ]);

            Assert.Equal(4, result.Feedback.Count);
            Assert.Contains("Candidate failure detail (receipt alpha):", result.Feedback[3], StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static FindingEvidenceReceipt Receipt(
        string id, string sha, string trxPath, FindingEvidenceArm arm = FindingEvidenceArm.Candidate) =>
        new(id, sha, new FindingEvidenceRequest([]), Accepted: true, Passed: false, Summary: "candidate RED",
            Arms: [new FindingEvidenceArmReceipt(arm, sha,
                FindingEvidenceArmDisposition.Red, Accepted: true, Passed: false, Summary: "candidate RED",
                FailingTestIdentities: [TestIdentity], TestResultPaths: [trxPath])]);

    internal static RecoveryResult RunRecovery(
        string output,
        IReadOnlyList<(FindingEvidenceReceipt Receipt, DateTimeOffset CompletedAt)>? receipts = null)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var evidenceTasks = goal.Tasks.Where(task => task.Id != developer.Id).ToArray();
        Assert.True(receipts is null || receipts.Count <= evidenceTasks.Length);
        for (var index = 0; index < evidenceTasks.Length; index++)
        {
            var task = evidenceTasks[index];
            if (receipts is null || index >= receipts.Count)
                PassVerification(kernel, goal, task);
            else
            {
                var (receipt, completedAt) = receipts[index];
                DispatchTask(kernel, goal, task);
                kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                    "test.exe", "C:\\tmp", 0, "ok", "", completedAt,
                    FindingEvidenceReceipts: [receipt]));
            }
        }

        DispatchTask(kernel, goal, developer);
        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
            "test.exe", "C:\\tmp", 1, output, "", new DateTimeOffset(2026, 9, 27, 14, 0, 0, TimeSpan.Zero),
            WorkerResultPresent: true, AssignedScopeComplete: false));
        Assert.Equal(WorkTaskStatus.Failed, developer.Status);
        Assert.Contains("rule=incomplete-scope-declaration",
            DispatchFailureClassifier.Classify(developer, developer.LastVerification!).ClassifierReceipt,
            StringComparison.Ordinal);

        TaskId? retriedTask = null;
        RetryCause? retryCause = null;
        string? retryMessage = null;
        var starts = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                retriedTask = taskId;
                retryCause = cause;
                retryMessage = message;
                return kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); });
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var feedback = developer.CriterionRetryFeedback.ToArray();
        var brief = kernel.BuildTaskBriefSource(goal.Id, developer.Id)
            .ProjectLegacyMarkedTextV1(emitTypedSourceBoundaries: false).Content;
        const string heading = "## Unmet acceptance criteria from the prior attempt - fix these:";
        var sectionStart = brief.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(sectionStart >= 0, "The next Developer brief must contain retry feedback.");
        var sectionEnd = brief.IndexOf("\n## ", sectionStart + heading.Length, StringComparison.Ordinal);
        var section = brief[sectionStart..(sectionEnd < 0 ? brief.Length : sectionEnd)];
        return new RecoveryResult(goal, developer, feedback, section, retriedTask, retryCause, retryMessage, starts);
    }

    internal sealed record RecoveryResult(
        Goal Goal, TaskSpec Developer, IReadOnlyList<string> Feedback, string BriefSection,
        TaskId? RetriedTask, RetryCause? RetryCause, string? RetryMessage, int Starts);
}
