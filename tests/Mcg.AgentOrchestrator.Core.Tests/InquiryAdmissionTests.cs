namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class InquiryAdmissionTests
{
    private static readonly DateTimeOffset DispatchedAt = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-20T11:00:00Z");

    [Xunit.Fact(DisplayName = "InquiryResumeAdmission_admits_when_all_checks_pass")]
    public void InquiryResumeAdmissionAdmitsWhenAllChecksPass()
    {
        var fixture = CreateFixture();

        var decision = InquiryResumeAdmission.Evaluate(
            fixture.Goal,
            fixture.Task,
            fixture.Dispatch,
            fixture.Context);

        Xunit.Assert.True(decision.AllowsResume);
        Xunit.Assert.Equal("resume-admitted", decision.Outcome);
        Xunit.Assert.All(decision.Checks, check => Xunit.Assert.True(check.Passed, check.Kind.ToString()));
    }

    [Xunit.Fact(DisplayName = "InquiryResumeAdmission_rejects_each_failed_check_with_receipt_reason")]
    public void InquiryResumeAdmissionRejectsEachFailedCheckWithReceiptReason()
    {
        AssertRejects(InquiryAdmissionCheckKind.SameGoal, context: c => c with { RequestedGoalId = new GoalId("other-goal") });
        AssertRejects(InquiryAdmissionCheckKind.SameTask, context: c => c with { RequestedTaskId = new TaskId("other-task") });
        AssertRejects(InquiryAdmissionCheckKind.SameRole, context: c => c with { RequestedRole = AgentRole.Tester });
        AssertRejects(InquiryAdmissionCheckKind.SameProvider, context: c => c with { RequestedProviderKind = ProviderKind.AnthropicClaudeCli });
        AssertRejects(InquiryAdmissionCheckKind.SameWorktree, context: c => c with { RequestedWorktree = "C:\\other" });
        AssertRejects(InquiryAdmissionCheckKind.SessionPresent, dispatch: d => d with { ProviderSessionId = null });
        AssertRejects(InquiryAdmissionCheckKind.SessionNotRetired, dispatch: d => d with { ProviderSessionRetiredAt = Now });
        AssertRejects(InquiryAdmissionCheckKind.SpawnHeadAncestor, context: c => c with { CapturedHeadIsAncestorOfCurrentHead = false });
        AssertRejects(InquiryAdmissionCheckKind.NoCriteriaCorrectionSinceCapture, context: c => c with { LatestCriteriaCorrectionAt = DispatchedAt.AddMinutes(1) });
        AssertRejects(InquiryAdmissionCheckKind.NoIntegrationChangeSinceCapture, context: c => c with { LatestIntegrationChangeAt = DispatchedAt.AddMinutes(1) });
        AssertRejects(InquiryAdmissionCheckKind.SessionNotConsumedByNonForkedInquiry, context: c => c with { SessionConsumedByNonForkedInquiry = true });
        AssertRejects(InquiryAdmissionCheckKind.SessionAge, context: c => c with { Now = DispatchedAt.AddDays(8) });
        AssertRejects(InquiryAdmissionCheckKind.SessionTurnCount, context: c => c with { SessionTurnCount = 9 });
    }

    private static void AssertRejects(
        InquiryAdmissionCheckKind expectedKind,
        Func<TaskDispatchRecord, TaskDispatchRecord>? dispatch = null,
        Func<InquiryAdmissionContext, InquiryAdmissionContext>? context = null)
    {
        var fixture = CreateFixture();
        var decision = InquiryResumeAdmission.Evaluate(
            fixture.Goal,
            fixture.Task,
            dispatch?.Invoke(fixture.Dispatch) ?? fixture.Dispatch,
            context?.Invoke(fixture.Context) ?? fixture.Context);

        Xunit.Assert.False(decision.AllowsResume);
        Xunit.Assert.Equal("fresh-exec-fallback", decision.Outcome);
        var failed = Xunit.Assert.Single(decision.FailedChecks.Where(check => check.Kind == expectedKind));
        Xunit.Assert.Equal(InquiryAdmissionCheckStatus.Failed, failed.Status);
        Xunit.Assert.Contains(expectedKind.ToString(), decision.Reason, StringComparison.Ordinal);
    }

    private static (Goal Goal, TaskSpec Task, TaskDispatchRecord Dispatch, InquiryAdmissionContext Context) CreateFixture()
    {
        var kernel = new AgentOrchestratorKernel();
        var taskSpec = new TaskSpec(new TaskId("task-1"), "Implement inquiry.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Inquiry goal", [taskSpec]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var dispatch = new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            "C:\\repo",
            DispatchedAt,
            WorkerProviderKind: ProviderKind.OpenAICodexCli,
            ProviderSessionId: "session-12345678",
            WorktreeHeadSha: "abc123");
        var context = new InquiryAdmissionContext(
            goal.Id,
            task.Id,
            task.RequiredRole,
            ProviderKind.OpenAICodexCli,
            "C:\\repo",
            "def456",
            CapturedHeadIsAncestorOfCurrentHead: true,
            Now,
            LatestCriteriaCorrectionAt: null,
            LatestIntegrationChangeAt: null,
            SessionConsumedByNonForkedInquiry: false,
            SessionTurnCount: 1);
        return (goal, task, dispatch, context);
    }
}
