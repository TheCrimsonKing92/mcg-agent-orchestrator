using Mcg.AgentOrchestrator.Core;

public sealed class DirtyDispatchRecoveryTests
{
    private const string DirtyGuardStderr =
        "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
        "branch=goal/abc12345; head=def1234; worktree=dirty; commits_after_dispatch=0; " +
        "status_short=M src/Foo.cs | M tests/FooTests.cs.";

    private const string TestPassStdout =
        "Test run successful. Passed: 5, Failed: 0, Skipped: 0.";

    [Xunit.Fact(DisplayName = "TryBuildDirtyDispatchRecovery_returns_dirty_useful_when_dirty_guard_with_verification_evidence")]
    public void TryBuildDirtyDispatchRecoveryReturnsDirtyUsefulWhenDirtyGuardWithVerificationEvidence()
    {
        var task = BuildFailedDeveloperTaskWithVerification(TestPassStdout, DirtyGuardStderr);

        var result = DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out var recovery);

        Assert.True(result);
        Assert.Equal("dirty-useful", recovery.Label);
        Assert.True(recovery.HasUsefulVerification);
        Assert.Contains(recovery.ChangedFiles, f => f.Contains("src/Foo.cs", StringComparison.Ordinal));
        Assert.Contains(recovery.ChangedFiles, f => f.Contains("tests/FooTests.cs", StringComparison.Ordinal));
        Assert.Contains(recovery.VerificationEvidence, ev => ev.Contains("Passed: 5", StringComparison.Ordinal));
        Assert.Equal("C:\\repo", recovery.WorkingDirectory);
    }

    [Xunit.Fact(DisplayName = "TryBuildDirtyDispatchRecovery_returns_dirty_unverified_when_dirty_guard_without_verification_evidence")]
    public void TryBuildDirtyDispatchRecoveryReturnsDirtyUnverifiedWhenDirtyGuardWithoutVerificationEvidence()
    {
        var noEvidenceStdout = "Done. Files written.";
        var task = BuildFailedDeveloperTaskWithVerification(noEvidenceStdout, DirtyGuardStderr);

        var result = DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out var recovery);

        Assert.True(result);
        Assert.Equal("dirty-unverified", recovery.Label);
        Assert.False(recovery.HasUsefulVerification);
        Assert.Equal(0, recovery.VerificationEvidence.Count);
        Assert.Contains(recovery.ChangedFiles, f => f.Contains("src/Foo.cs", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "TryBuildDirtyDispatchRecovery_returns_false_for_non_dirty_guard_failure")]
    public void TryBuildDirtyDispatchRecoveryReturnsFalseForNonDirtyGuardFailure()
    {
        var differentStderr = "Developer/Tester dispatch exited 0 but did not produce required file-change evidence. branch=goal/abc; head=def; worktree=clean; commits_after_dispatch=0.";
        var task = BuildFailedDeveloperTaskWithVerification(TestPassStdout, differentStderr);

        var result = DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out var recovery);

        Assert.False(result);
        Assert.Equal(DirtyDispatchRecovery.None, recovery);
    }

    [Xunit.Fact(DisplayName = "TryBuildDirtyDispatchRecovery_returns_false_for_non_developer_tester_role")]
    public void TryBuildDirtyDispatchRecoveryReturnsFalseForNonDeveloperTesterRole()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Non-developer dirty dispatch");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var plannerTask = goal.Tasks.First(t => t.RequiredRole == AgentRole.Planner);
        kernel.RecordTaskDispatch(goal.Id, plannerTask.Id, new TaskDispatchRecord("local", "plan", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, plannerTask.Id, new TaskVerificationRecord(
            "plan",
            "C:\\repo",
            1,
            string.Empty,
            DirtyGuardStderr,
            clock.UtcNow));

        var result = DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(plannerTask, out var recovery);

        Assert.False(result);
        Assert.Equal(DirtyDispatchRecovery.None, recovery);
    }

    [Xunit.Fact(DisplayName = "TryBuildDirtyDispatchRecovery_returns_false_when_no_failed_verification")]
    public void TryBuildDirtyDispatchRecoveryReturnsFalseWhenNoFailedVerification()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Clean dispatch with no failure");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);

        var result = DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out var recovery);

        Assert.False(result);
        Assert.Equal(DirtyDispatchRecovery.None, recovery);
    }

    [Xunit.Fact(DisplayName = "TryBuildDirtyDispatchRecovery_tester_role_is_also_eligible_for_dirty_recovery")]
    public void TryBuildDirtyDispatchRecoveryTesterRoleIsAlsoEligibleForDirtyRecovery()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Tester dirty dispatch");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var testerTask = goal.Tasks.First(t => t.RequiredRole == AgentRole.Tester);
        kernel.RecordTaskDispatch(goal.Id, testerTask.Id, new TaskDispatchRecord("local", "test", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, testerTask.Id, new TaskVerificationRecord(
            "test",
            "C:\\repo",
            1,
            TestPassStdout,
            DirtyGuardStderr,
            clock.UtcNow));

        var result = DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(testerTask, out var recovery);

        Assert.True(result);
        Assert.Equal("dirty-useful", recovery.Label);
    }

    [Xunit.Fact(DisplayName = "BuildNextActions_includes_dirty_useful_label_in_fix_verification_action")]
    public void BuildNextActionsIncludesDirtyUsefulLabelInFixVerificationAction()
    {
        var task = BuildFailedDeveloperTaskWithVerification(TestPassStdout, DirtyGuardStderr);
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Next actions dirty dispatch");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var devTask = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(goal.Id, devTask.Id, new TaskDispatchRecord("codex-cli", "codex exec", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, devTask.Id, new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            1,
            TestPassStdout,
            DirtyGuardStderr,
            clock.UtcNow));

        var actions = kernel.BuildNextActions(goal.Id);

        Assert.Contains(actions.Items, item =>
            item.Kind == NextActionKind.FixFailedVerification &&
            item.Message.Contains("dirty-useful", StringComparison.Ordinal) &&
            item.Message.Contains("src/Foo.cs", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BuildNextActions_includes_dirty_unverified_label_when_no_verification_evidence")]
    public void BuildNextActionsIncludesDirtyUnverifiedLabelWhenNoVerificationEvidence()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Next actions dirty unverified dispatch");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var devTask = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(goal.Id, devTask.Id, new TaskDispatchRecord("codex-cli", "codex exec", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, devTask.Id, new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            1,
            "Wrote files.",
            DirtyGuardStderr,
            clock.UtcNow));

        var actions = kernel.BuildNextActions(goal.Id);

        Assert.Contains(actions.Items, item =>
            item.Kind == NextActionKind.FixFailedVerification &&
            item.Message.Contains("dirty-unverified", StringComparison.Ordinal) &&
            item.Message.Contains("rerun focused tests", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "BuildGoalEvidenceSummary_includes_dirty_recovery_label_for_failed_dirty_dispatch")]
    public void BuildGoalEvidenceSummaryIncludesDirtyRecoveryLabelForFailedDirtyDispatch()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Evidence summary dirty dispatch");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var devTask = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(goal.Id, devTask.Id, new TaskDispatchRecord("codex-cli", "codex exec", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, devTask.Id, new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            1,
            TestPassStdout,
            DirtyGuardStderr,
            clock.UtcNow));

        var summary = kernel.BuildGoalEvidenceSummary(goal.Id);
        var taskSummary = summary.Tasks.First(t => t.TaskId == devTask.Id);

        Assert.Contains(taskSummary.Message, text => text.Contains("dirty-useful", StringComparison.Ordinal));
        Assert.Contains(taskSummary.Message, text => text.Contains("src/Foo.cs", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "TryBuildDirtyDispatchRecovery_returns_unavailable_changed_files_when_no_status_short_in_stderr")]
    public void TryBuildDirtyDispatchRecoveryReturnsUnavailableChangedFilesWhenNoStatusShortInStderr()
    {
        var stderrNoStatusShort =
            "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
            "branch=goal/abc; head=def; worktree=dirty; commits_after_dispatch=0.";
        var task = BuildFailedDeveloperTaskWithVerification(string.Empty, stderrNoStatusShort);

        var result = DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out var recovery);

        Assert.True(result);
        Assert.Equal("dirty-unverified", recovery.Label);
        Assert.Equal(1, recovery.ChangedFiles.Count);
        Assert.Equal("unavailable", recovery.ChangedFiles[0]);
    }

    private static TaskSpec BuildFailedDeveloperTaskWithVerification(string stdout, string stderr)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Dirty dispatch test goal");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            1,
            stdout,
            stderr,
            clock.UtcNow));
        return task;
    }
}
