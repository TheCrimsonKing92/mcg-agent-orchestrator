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
}
