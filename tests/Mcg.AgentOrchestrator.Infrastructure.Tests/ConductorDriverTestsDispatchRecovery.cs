using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Xml.Linq;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsDispatchRecovery
{
    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_operator_evidence_blocker_escalates_without_retry")]
    public void ConductorDriverReviewerOperatorEvidenceBlockerEscalatesWithoutRetry()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "Operator receipt required for measurement mandate before acceptance.");
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.Contains("operator-owned evidence", escalation);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_provider_failure_does_not_auto_review_retry")]
    public void ConductorDriverReviewerProviderFailureDoesNotAutoReviewRetry()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        DispatchTask(kernel, goal, reviewer, "review");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            "ERROR: provider authentication failed before WORKER_RESULT",
            "",
            DateTimeOffset.UtcNow,
            ProviderFailureKind: ProviderFailureKind.RateLimit));
        var retried = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_short_silent_launch_failure_auto_recovers_without_review_round")]
    public void ConductorDriverSilentLaunchFailureAutoRecoversWithoutReviewRound()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        // The root exited non-zero but both redirected streams stayed empty: this is a launch failure,
        // not a worker verdict, so the conductor should re-admit it instead of escalating.
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "",
                DateTimeOffset.UtcNow,
                DispatchStartedAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.EmptyOutputRetryCount);
        Assert.Equal(0, task.CriterionRetryCount);

        var retried = false;
        var escalated = false;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; retryMessage = msg; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(retried);
        Assert.False(escalated);
        Assert.Contains("zero bytes on both streams with root exit 1", retryMessage!);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(0, task.CriterionRetryCount);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_sandbox_1312_launch_failure_uses_bounded_retry_without_commit_language")]
    public void ConductorDriverSandbox1312LaunchFailureUsesBoundedRetryWithoutCommitLanguage()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "worker output before sandbox command launch failed",
                "CreateProcessAsUserW 1312: A specified logon session does not exist.",
                DateTimeOffset.UtcNow,
                ProviderFailureKind: ProviderFailureKind.Sandbox1312));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.EmptyOutputRetryCount);

        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) =>
            {
                retryMessage = msg;
                return kernel.RetryTask(gid, tid, msg);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Contains("sandbox command-launch failure", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("sandbox logon session failed", retryMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("commit", retryMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_sandbox_preflight_failure_auto_retries_on_shared_dispatch_flake_budget")]
    public void ConductorDriverSandboxPreflightFailureAutoRetriesOnSharedDispatchFlakeBudget()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        // The worker never launched (sandbox launch-preflight failure): an intermittent sandbox-prep hiccup
        // that a fresh dispatch usually clears, so the conductor auto-retries on the shared dispatch-flake
        // budget instead of escalating the whole goal to the operator on a single flake.
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "CreateProcessAsUser failed during Low Integrity preflight",
                DateTimeOffset.UtcNow));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.EmptyOutputRetryCount);

        var retried = false;
        var escalated = false;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; retryMessage = msg; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(retried);
        Assert.False(escalated);
        Assert.Contains("sandbox-preflight dispatch flake", retryMessage!);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_scripting_failure_after_launch_preflight_retries_immediately_on_criterion_budget")]
    public void ConductorDriverScriptingFailureAfterLaunchPreflightRetriesImmediatelyOnCriterionBudget()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        const string failedCommand = "powershell.exe -Command & { while ($true) { Write-Output 'broken } }";
        DispatchTask(kernel, goal, task, failedCommand);
        const string stderr =
            "{\"event\":\"sandbox-prep\",\"phase\":\"launch-preflight\",\"elapsedMs\":200}\n" +
            "src/ProviderParser.cs:77: text.Contains(\"usage limit\", StringComparison.OrdinalIgnoreCase)\n" +
            "Worker inspected the classifier and prepared a focused change.\n" +
            "powershell.exe: ParserError: The string is missing the terminator: '.";
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                failedCommand,
                "C:\\tmp",
                1,
                string.Empty,
                stderr,
                DateTimeOffset.UtcNow,
                ProviderFailureKind: ProviderFailureKind.RateLimit));

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Null(task.SubscriptionRetryAfter);
        Assert.False(DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(task));

        var retried = false;
        var dispatched = false;
        var retryMessage = string.Empty;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                retryMessage = msg;
                return kernel.RetryTask(gid, tid, msg);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(retried);
        Assert.True(dispatched);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.Equal(1, goal.AutomaticAcceptanceRetryCount);
        Assert.Contains(failedCommand, retryMessage, StringComparison.Ordinal);
        Assert.Contains("ParserError", retryMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("subscription", retryMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_scripting_failure_escalates_after_criterion_budget_exhausted")]
    public void ConductorDriverScriptingFailureEscalatesAfterCriterionBudgetExhausted()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["first bounded retry"]);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["second bounded retry"]);
        const string failedCommand = "powershell.exe -Command broken";
        DispatchTask(kernel, goal, task, failedCommand);
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                failedCommand,
                "C:\\tmp",
                1,
                string.Empty,
                "powershell.exe: ParserError: Unexpected token '}' in expression.",
                DateTimeOffset.UtcNow));

        var retried = false;
        var dispatched = false;
        var escalationMessage = string.Empty;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalationMessage = message; },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.False(dispatched);
        Assert.Contains("exhausted bounded real-failure retries (2/2)", escalationMessage, StringComparison.Ordinal);
        Assert.Contains("ParserError", escalationMessage, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_sandbox_preflight_budget_exhaustion_escalates_after_bounded_retries")]
    public void ConductorDriverSandboxPreflightBudgetExhaustionEscalatesAfterBoundedRetries()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        const string preflightStderr = "CreateProcessAsUser failed during Low Integrity preflight";
        for (var i = 0; i < 2; i++)
        {
            DispatchTask(kernel, goal, task);
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
                new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "", preflightStderr, DateTimeOffset.UtcNow));
            if (i == 0)
            {
                kernel.RetryTask(goal.Id, task.Id, "previous preflight retry");
            }
        }
        Assert.Equal(2, task.EmptyOutputRetryCount);

        var policy = ConductorAutonomyPolicy.Permissive with
        {
            MaxEmptyOutputDispatchRetries = 2,
            MaxEmptyOutputAutoRecoverCycles = 1
        };
        var escalated = false;
        var retried = false;
        string? escalationMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalated = true; escalationMessage = message; });

        // count=2, maxAttempts=2 (2*1): 2 > 2 is false, so it still auto-retries.
        var result = driver.AdvanceOnce(goal, policy);
        Assert.True(retried);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);

        // The next preflight failure pushes the count past the budget -> escalate for operator action.
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "", preflightStderr, DateTimeOffset.UtcNow));
        Assert.Equal(3, task.EmptyOutputRetryCount);

        result = driver.AdvanceOnce(goal, policy);
        Assert.True(escalated);
        Assert.Contains("exhausted sandbox-preflight dispatch recovery", escalationMessage);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_stale_dispatch_recovery_retries_without_empty_output_budget")]
    public void ConductorDriverStaleDispatchRecoveryRetriesWithoutEmptyOutputBudget()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "Dispatch recovery policy action='mark-stale' evidence='heartbeat-absent' reason='no live process, exit-absent, stale retry budget remaining=1'.",
                DateTimeOffset.UtcNow));
        Assert.Equal(0, task.EmptyOutputRetryCount);

        var retried = false;
        var dispatched = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(retried);
        Assert.True(dispatched);
        Assert.Equal(0, task.EmptyOutputRetryCount);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_stale_dispatch_budget_exhausted_escalates_without_retry")]
    public void ConductorDriverStaleDispatchBudgetExhaustedEscalatesWithoutRetry()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "Dispatch recovery policy action='budget-exhausted' evidence='heartbeat-absent' reason='no live process, exit-absent, heartbeat absent' blocker='stale-dispatch retry budget exhausted'.",
                DateTimeOffset.UtcNow));
        Assert.Equal(0, task.EmptyOutputRetryCount);

        var retried = false;
        var dispatched = false;
        string? escalationMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalationMessage = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.False(dispatched);
        Assert.Equal(0, task.EmptyOutputRetryCount);
        Assert.Contains("stale-dispatch retry budget exhausted", escalationMessage);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_nonempty_stdout_failure_resets_empty_output_count_and_escalates")]
    public void ConductorDriverNonemptyStdoutFailureResetsEmptyOutputCountAndEscalates()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "",
                DateTimeOffset.UtcNow,
                DispatchStartedAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)));
        kernel.RetryTask(goal.Id, task.Id, "retry transient empty output");
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "x", "", DateTimeOffset.UtcNow));

        Assert.Equal(0, task.EmptyOutputRetryCount);
        var retried = false;
        var escalated = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_output_budget_exhaustion_auto_recovers_before_operator_escalation")]
    public void ConductorDriverEmptyOutputBudgetExhaustionAutoRecoversBeforeOperatorEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        for (var i = 0; i < 2; i++)
        {
            DispatchTask(kernel, goal, task);
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
                new TaskVerificationRecord(
                    "test.exe",
                    "C:\\tmp",
                    1,
                    "",
                    "",
                    DateTimeOffset.UtcNow,
                    DispatchStartedAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)));
            if (i == 0)
            {
                kernel.RetryTask(goal.Id, task.Id, "previous empty output retry");
            }
        }

        var policy = ConductorAutonomyPolicy.Permissive with
        {
            MaxEmptyOutputDispatchRetries = 2,
            MaxEmptyOutputAutoRecoverCycles = 1
        };
        var retryMessage = string.Empty;
        var escalated = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retryMessage = msg; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, policy);

        Assert.False(escalated);
        Assert.Contains("Auto-recover+re-admit", retryMessage);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);

        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "",
                DateTimeOffset.UtcNow,
                DispatchStartedAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)));

        result = driver.AdvanceOnce(goal, policy);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_output_backoff_uses_configured_delay")]
    public void ConductorDriverEmptyOutputBackoffUsesConfiguredDelay()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        for (var i = 0; i < 2; i++)
        {
            DispatchTask(kernel, goal, task);
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
                new TaskVerificationRecord(
                    "test.exe",
                    "C:\\tmp",
                    1,
                    "",
                    "",
                    DateTimeOffset.UtcNow,
                    DispatchStartedAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)));
            if (i == 0)
            {
                kernel.RetryTask(goal.Id, task.Id, "previous empty output retry");
            }
        }

        var delays = new List<TimeSpan>();
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => kernel.RetryTask(gid, tid, msg),
            emptyOutputBackoffDelay: delays.Add);
        var policy = ConductorAutonomyPolicy.Permissive with
        {
            EmptyOutputRetryInitialDelaySeconds = 1,
            EmptyOutputRetryBackoffMultiplier = 2,
            EmptyOutputRetryMaxDelaySeconds = 3
        };

        driver.AdvanceOnce(goal, policy);

        Assert.Single(delays);
        Assert.Equal(TimeSpan.FromSeconds(2), delays[0]);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Blocked_facts_escalates")]
    public void ConductorDriverBlockedFactsEscalates()
    {
        var (_, goal) = SimpleGoal();

        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(IsBlocked: true));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Blocked, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
    }

}
