using Mcg.AgentOrchestrator.Core;

public sealed class AssignedScopeCompleteTests
{
    [Xunit.Fact]
    public void DeveloperBriefRequestsAssignedScopeCompletionWithoutMakingItRequired()
    {
        var lines = AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Developer);

        Xunit.Assert.Contains(lines, line => line.StartsWith("assigned_scope_complete:", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain("assigned_scope_complete", AgentOutputDirectives.WorkerResultFieldNames);
    }

    [Xunit.Theory]
    [Xunit.InlineData("true", true)]
    [Xunit.InlineData("FALSE", false)]
    public void ReaderReadsSingleBooleanValue(string value, bool expected)
    {
        var output = $"WORKER_RESULT:{Environment.NewLine}assigned_scope_complete: {value}{Environment.NewLine}END_WORKER_RESULT";

        var found = WorkerResultBlockers.TryGetAssignedScopeComplete(output, out var actual, out var diagnostic);

        Xunit.Assert.True(found);
        Xunit.Assert.Equal(expected, actual);
        Xunit.Assert.Null(diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("maybe")]
    [Xunit.InlineData("true\nassigned_scope_complete: false")]
    public void ReaderRejectsMalformedOrConflictingValue(string value)
    {
        var output = $"WORKER_RESULT:{Environment.NewLine}assigned_scope_complete: {value}{Environment.NewLine}END_WORKER_RESULT";

        var found = WorkerResultBlockers.TryGetAssignedScopeComplete(output, out _, out var diagnostic);

        Xunit.Assert.False(found);
        Xunit.Assert.NotNull(diagnostic);
        Xunit.Assert.True(WorkerResultBlockers.TryFindMalformedEvidenceBoundOutcome(output, out _));
    }

    [Xunit.Fact]
    public void SnapshotRoundTripPreservesObservedValue()
    {
        var task = new TaskSpec(TaskId.New(), "Implement assigned scope", AgentRole.Developer);
        task.RecordVerification(new TaskVerificationRecord(
            "cmd", "C:\\repo", 0, "output", string.Empty, DateTimeOffset.UtcNow,
            AssignedScopeComplete: false));

        var restored = TaskSpec.FromSnapshot(task.ToSnapshot());

        Xunit.Assert.False(restored.LastVerification!.AssignedScopeComplete);
    }

    [Xunit.Fact]
    public void SameRoundEnrichmentPreservesObservedValueForReloadAndClassification()
    {
        var completedAt = DateTimeOffset.UtcNow;
        var task = new TaskSpec(TaskId.New(), "Implement assigned scope", AgentRole.Developer);
        var initial = new TaskVerificationRecord(
            "cmd", "C:\\repo", 0, "WORKER_RESULT:", string.Empty, completedAt,
            WorkerResultPresent: true);
        var enriched = new TaskVerificationRecord(
            "cmd", "C:\\repo", 0, "WORKER_RESULT:", string.Empty, completedAt,
            WorkerResultPresent: true,
            AssignedScopeComplete: false);
        task.RecordVerification(initial.MergeSameRoundEnrichment(enriched));

        var restored = TaskSpec.FromSnapshot(task.ToSnapshot());
        var outcome = DispatchFailureClassifier.Classify(restored, restored.LastVerification!);

        Xunit.Assert.False(restored.LastVerification!.AssignedScopeComplete);
        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Contains("rule=incomplete-scope-declaration", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ClassifierUsesPersistedObservationRatherThanReparsingOutput()
    {
        var task = new TaskSpec(TaskId.New(), "Implement assigned scope", AgentRole.Developer);
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: src/Feature.cs",
            "commands: focused verification",
            "tests: pass - focused verification passed",
            "commit: abc1234",
            "blockers: none",
            "model_fit: fixture/model - adequate - scope test",
            "skills: none",
            "confidence: high",
            "assigned_scope_complete: false",
            "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "test.exe", "C:\\repo", 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            AssignedScopeComplete: null);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.DoesNotContain("incomplete-scope-declaration", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData(true)]
    public void CompleteOrAbsentScopePreservesOperatorOwnedDeferredEvidence(bool? assignedScopeComplete)
    {
        var task = new TaskSpec(TaskId.New(), "Implement assigned scope", AgentRole.Developer);
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "tests: deferred - acceptance gate owns the full suite",
            "blockers: none",
            "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "test.exe", "C:\\repo", 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            AssignedScopeComplete: assignedScopeComplete);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.DoesNotContain("incomplete-scope-declaration", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void UnavailableFullOutputDoesNotPromoteAPreviewScopeDeclaration()
    {
        var task = new TaskSpec(TaskId.New(), "Implement assigned scope", AgentRole.Developer);
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: src/Feature.cs",
            "tests: pass - focused verification passed",
            "blockers: none",
            "assigned_scope_complete: false",
            "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "test.exe", "C:\\repo", 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            FullStandardOutputUnavailableReason: "full stdout was unavailable",
            AssignedScopeComplete: null);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Null(verification.AuthoritativeStandardOutput);
        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.DoesNotContain("incomplete-scope-declaration", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, true, "pass - focused verification passed", "incomplete-scope-declaration")]
    [Xunit.InlineData(true, false, "pass - focused verification passed", "incomplete-scope-declaration")]
    [Xunit.InlineData(false, false, "pass - focused verification passed", "incomplete-scope-declaration")]
    public void ExplicitIncompleteDeveloperScopeCannotReachAnySuccessPath(
        bool hasCommittedChanges,
        bool workerResultPresent,
        string tests,
        string expectedRule)
    {
        var task = new TaskSpec(TaskId.New(), "Implement assigned scope", AgentRole.Developer);
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: src/Feature.cs",
            "commands: focused verification",
            $"tests: {tests}",
            "commit: abc1234",
            "blockers: none",
            "model_fit: fixture/model - adequate - scope test",
            "skills: none",
            "confidence: high",
            "assigned_scope_complete: false",
            "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "test.exe", "C:\\repo", 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: workerResultPresent,
            HasCommittedChanges: hasCommittedChanges,
            AssignedScopeComplete: false);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.NotEqual(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains($"rule={expectedRule}", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void FailingTestsTakePrecedenceOverAnIncompleteDeveloperDeclaration()
    {
        var task = new TaskSpec(TaskId.New(), "Implement assigned scope", AgentRole.Developer);
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: src/Feature.cs",
            "commands: focused verification",
            "tests: fail - deterministic failure",
            "commit: abc1234",
            "blockers: none",
            "model_fit: fixture/model - adequate - scope test",
            "skills: none",
            "confidence: high",
            "assigned_scope_complete: false",
            "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "test.exe", "C:\\repo", 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            AssignedScopeComplete: false);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Contains("rule=succeeded-worker-result-failing-tests", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("incomplete-scope-declaration", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ScopeCompleteCannotOverrideTesterFailure()
    {
        var task = new TaskSpec(TaskId.New(), "Verify candidate", AgentRole.Tester);
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: focused verification",
            "tests: fail - deterministic failure",
            "commit: none",
            "blockers: none",
            "model_fit: fixture/model - adequate - tester control",
            "skills: none",
            "confidence: high",
            "assigned_scope_complete: true",
            "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "test.exe", "C:\\repo", 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            AssignedScopeComplete: true);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Contains("rule=succeeded-worker-result-failing-tests", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }
}
