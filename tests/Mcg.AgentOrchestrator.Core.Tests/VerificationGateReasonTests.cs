using Mcg.AgentOrchestrator.Core;

public sealed class VerificationGateReasonTests
{
    private const string DirtyGuardStderr =
        "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
        "branch=goal/abc12345; head=def1234; worktree=dirty; commits_after_dispatch=0; " +
        "status_short=M src/Foo.cs | M tests/FooTests.cs.";

    private const string TestPassStdout =
        "Test run successful. Passed: 5, Failed: 0, Skipped: 0.";

    private static TaskVerificationGate MakeGate(
        VerificationGateReason reason,
        VerificationGateStatus gateStatus,
        string message) =>
        new(TaskId.New(), AgentRole.Developer, "description", WorkTaskStatus.Completed, gateStatus, message, reason);

    [Xunit.Fact(DisplayName = "BuildVerificationSuggestedAction_Passed_returns_no_work_remains")]
    public void BuildVerificationSuggestedActionPassedReturnsNoWorkRemains()
    {
        var gate = MakeGate(VerificationGateReason.Passed, VerificationGateStatus.Passed, "Verified.");
        var action = AgentOrchestratorKernel.BuildVerificationSuggestedAction(gate);
        Assert.Equal("No verification work remains.", action);
    }

    [Xunit.Fact(DisplayName = "BuildVerificationSuggestedAction_NotReady_returns_complete_task_action")]
    public void BuildVerificationSuggestedActionNotReadyReturnsCompleteTaskAction()
    {
        var gate = MakeGate(VerificationGateReason.NotReady, VerificationGateStatus.NotReady, "Task status is Assigned.");
        var action = AgentOrchestratorKernel.BuildVerificationSuggestedAction(gate);
        Assert.Contains("Complete the task", action, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BuildVerificationSuggestedAction_MissingVerification_returns_record_verification_action")]
    public void BuildVerificationSuggestedActionMissingVerificationReturnsRecordVerificationAction()
    {
        var gate = MakeGate(VerificationGateReason.MissingVerification, VerificationGateStatus.MissingVerification, "No verification.");
        var action = AgentOrchestratorKernel.BuildVerificationSuggestedAction(gate);
        Assert.Contains("Record verification", action, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BuildVerificationSuggestedAction_VerificationFailed_returns_inspect_action")]
    public void BuildVerificationSuggestedActionVerificationFailedReturnsInspectAction()
    {
        var gate = MakeGate(VerificationGateReason.VerificationFailed, VerificationGateStatus.FailedVerification, "exit 1: dotnet test");
        var action = AgentOrchestratorKernel.BuildVerificationSuggestedAction(gate);
        Assert.Contains("Inspect", action, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BuildVerificationSuggestedAction_OutputTokenLimit_returns_retry_narrow_scope_action")]
    public void BuildVerificationSuggestedActionOutputTokenLimitReturnsRetryNarrowScopeAction()
    {
        var gate = MakeGate(VerificationGateReason.OutputTokenLimit, VerificationGateStatus.FailedVerification,
            "Model output may be truncated at 8192 token(s); retry with narrower scope or stronger model before accepting this gate.");
        var action = AgentOrchestratorKernel.BuildVerificationSuggestedAction(gate);
        Assert.Contains("narrower scope", action, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BuildVerificationSuggestedAction_DirtyUsefulRecovery_returns_commit_changes_action")]
    public void BuildVerificationSuggestedActionDirtyUsefulRecoveryReturnsCommitChangesAction()
    {
        var gate = MakeGate(VerificationGateReason.DirtyUsefulRecovery, VerificationGateStatus.FailedVerification,
            "dirty-useful dispatch recovery needed: changed files [src/Foo.cs]; verification evidence: Test run successful.");
        var action = AgentOrchestratorKernel.BuildVerificationSuggestedAction(gate);
        Assert.Contains("commit the worker changes", action, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BuildVerificationSuggestedAction_DirtyUnverifiedRecovery_returns_run_verification_first_action")]
    public void BuildVerificationSuggestedActionDirtyUnverifiedRecoveryReturnsRunVerificationFirstAction()
    {
        var gate = MakeGate(VerificationGateReason.DirtyUnverifiedRecovery, VerificationGateStatus.FailedVerification,
            "dirty-unverified dispatch recovery needed: changed files [src/Foo.cs]; verification evidence: no verification evidence found.");
        var action = AgentOrchestratorKernel.BuildVerificationSuggestedAction(gate);
        Assert.Contains("run focused verification", action, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BuildVerificationGate_sets_DirtyUsefulRecovery_reason_for_dirty_useful_dispatch")]
    public void BuildVerificationGateSetsDirtyUsefulRecoveryReasonForDirtyUsefulDispatch()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Dirty useful gate", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, [DefaultAgents().First(a => a.Role == AgentRole.Developer)]);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec", "C:\\repo", 1, TestPassStdout, DirtyGuardStderr, clock.UtcNow));

        var gate = kernel.BuildVerificationGate(goal.Id).Tasks.Single();

        Assert.Equal(VerificationGateReason.DirtyUsefulRecovery, gate.Reason);
        Assert.Equal(VerificationGateStatus.FailedVerification, gate.GateStatus);
    }

    [Xunit.Fact(DisplayName = "BuildVerificationGate_sets_DirtyUnverifiedRecovery_reason_for_dirty_unverified_dispatch")]
    public void BuildVerificationGateSetsDirtyUnverifiedRecoveryReasonForDirtyUnverifiedDispatch()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Dirty unverified gate", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, [DefaultAgents().First(a => a.Role == AgentRole.Developer)]);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec", "C:\\repo", 1, "Wrote files.", DirtyGuardStderr, clock.UtcNow));

        var gate = kernel.BuildVerificationGate(goal.Id).Tasks.Single();

        Assert.Equal(VerificationGateReason.DirtyUnverifiedRecovery, gate.Reason);
    }

    [Xunit.Fact(DisplayName = "BuildVerificationGate_sets_OutputTokenLimit_reason_for_truncated_execution")]
    public async Task BuildVerificationGateSetsOutputTokenLimitReasonForTruncatedExecution()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Token limit gate", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var agents = DefaultAgents();
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        var provider = new FakeModelProvider("OpenAI", "Partial output...", stopReason: "length");
        var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);
        await runner.RunAsync(goal.Id, task.Id);

        var gate = kernel.BuildVerificationGate(goal.Id).Tasks.Single();

        Assert.Equal(VerificationGateReason.OutputTokenLimit, gate.Reason);
        Assert.Equal(VerificationGateStatus.FailedVerification, gate.GateStatus);
    }

    [Xunit.Fact(DisplayName = "BuildVerificationSuggestedAction_DirtyUsefulRecovery_routes_by_reason_not_message")]
    public void BuildVerificationSuggestedActionDirtyUsefulRecoveryRoutesByReasonNotMessage()
    {
        var gateWithStandardMessage = MakeGate(
            VerificationGateReason.DirtyUsefulRecovery,
            VerificationGateStatus.FailedVerification,
            "dirty-useful dispatch recovery needed: changed files [src/Foo.cs]; verification evidence: Passed: 5.");
        var gateWithModifiedMessage = MakeGate(
            VerificationGateReason.DirtyUsefulRecovery,
            VerificationGateStatus.FailedVerification,
            "This message no longer contains the old routing substring.");

        var actionStandard = AgentOrchestratorKernel.BuildVerificationSuggestedAction(gateWithStandardMessage);
        var actionModified = AgentOrchestratorKernel.BuildVerificationSuggestedAction(gateWithModifiedMessage);

        Assert.Equal(actionStandard, actionModified);
        Assert.Contains("commit the worker changes", actionModified, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BuildVerificationSuggestedAction_DirtyUnverifiedRecovery_routes_by_reason_not_message")]
    public void BuildVerificationSuggestedActionDirtyUnverifiedRecoveryRoutesByReasonNotMessage()
    {
        var gateWithStandardMessage = MakeGate(
            VerificationGateReason.DirtyUnverifiedRecovery,
            VerificationGateStatus.FailedVerification,
            "dirty-unverified dispatch recovery needed: changed files [src/Foo.cs]; verification evidence: no verification evidence found.");
        var gateWithModifiedMessage = MakeGate(
            VerificationGateReason.DirtyUnverifiedRecovery,
            VerificationGateStatus.FailedVerification,
            "This message no longer contains the old routing substring.");

        var actionStandard = AgentOrchestratorKernel.BuildVerificationSuggestedAction(gateWithStandardMessage);
        var actionModified = AgentOrchestratorKernel.BuildVerificationSuggestedAction(gateWithModifiedMessage);

        Assert.Equal(actionStandard, actionModified);
        Assert.Contains("run focused verification", actionModified, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BuildVerificationSuggestedAction_OutputTokenLimit_routes_by_reason_not_message")]
    public void BuildVerificationSuggestedActionOutputTokenLimitRoutesByReasonNotMessage()
    {
        var gateWithStandardMessage = MakeGate(
            VerificationGateReason.OutputTokenLimit,
            VerificationGateStatus.FailedVerification,
            "Model output may be truncated at 8192 token(s); retry with narrower scope or stronger model before accepting this gate.");
        var gateWithModifiedMessage = MakeGate(
            VerificationGateReason.OutputTokenLimit,
            VerificationGateStatus.FailedVerification,
            "This message no longer starts with the old routing prefix.");

        var actionStandard = AgentOrchestratorKernel.BuildVerificationSuggestedAction(gateWithStandardMessage);
        var actionModified = AgentOrchestratorKernel.BuildVerificationSuggestedAction(gateWithModifiedMessage);

        Assert.Equal(actionStandard, actionModified);
        Assert.Contains("narrower scope", actionModified, StringComparison.Ordinal);
    }
}
