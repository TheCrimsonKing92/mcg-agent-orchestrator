using Mcg.AgentOrchestrator.Core;

public sealed class DeveloperCriteriaSelfCheckNoteTests
{
    private const string Present = "[{\"criterion_index\":0,\"status\":\"proven\",\"evidence\":\"Tests.One fails before change\"},{\"criterion_index\":1,\"status\":\"proven\",\"evidence\":\"Tests.Two fails before change\"},{\"criterion_index\":2,\"status\":\"unmet\",\"evidence\":\"Missing assertion\"}]";

    [Fact]
    public void PresentAndMissingReportsRecordIdenticalCompletedRounds()
    {
        var present = Record(Present);
        var missing = Record(null);
        Assert.Equal($"DEVELOPER_CRITERIA_SELF_CHECK task={present.Task.Id.Value} proven=2 not_owned=0 unmet=1 field=present", Note(present));
        Assert.Equal($"DEVELOPER_CRITERIA_SELF_CHECK task={missing.Task.Id.Value} proven=0 not_owned=0 unmet=0 field=missing", Note(missing));
        Assert.Equal(present.Goal.Timeline.Select(e => e.Kind), missing.Goal.Timeline.Select(e => e.Kind));
        Assert.Equal(present.Task.LastVerification!.ExitCode, missing.Task.LastVerification!.ExitCode);
        Assert.Equal(present.Task.LastVerification.CompletionVerdictVerifiedSuccess, missing.Task.LastVerification.CompletionVerdictVerifiedSuccess);
        Assert.Equal(present.Task.LastVerification.CompletionVerdictRule, missing.Task.LastVerification.CompletionVerdictRule);
    }

    [Fact]
    public void MalformedReportStillCompletesWithOneDiagnosticNote()
    {
        var round = Record("[{\"criterion_index\":-1}]");
        Assert.Equal($"DEVELOPER_CRITERIA_SELF_CHECK task={round.Task.Id.Value} proven=0 not_owned=0 unmet=0 field=malformed reason=criterion_index must be a non-negative integer.", Note(round));
        Assert.DoesNotContain('\n', Note(round));
    }

    private static string Note((Goal Goal, TaskSpec Task) round) =>
        Assert.Single(round.Goal.Timeline.Where(e => e.Kind == ProgressKind.TaskNote &&
            e.TaskId == round.Task.Id && e.Message.StartsWith("DEVELOPER_CRITERIA_SELF_CHECK ", StringComparison.Ordinal))).Message;

    private static (Goal Goal, TaskSpec Task) Record(string? field)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Record Developer self-check.");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        const string command = "codex exec prompt";
        var output = "WORKER_RESULT:\nfiles: src/Foo.cs\ncommands: none\ntests: deferred - DeveloperCriteriaSelfCheckNoteTests\ncommit: none\nblockers: none\nassigned_scope_complete: true\n" +
            (field is null ? string.Empty : $"criteria_self_check: {field}\n") +
            "model_fit: OpenAI/gpt-6.1-sol - adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT";
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", command, "C:\\repo", clock.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id, "29edee5c");
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, "ce5e35c1");
        var verification = new TaskVerificationRecord(command, "C:\\repo", 0, output, string.Empty, clock.UtcNow,
            WorkerResultPresent: true, HeartbeatStandardOutputBytes: output.Length);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(output, task.LastVerification!.StandardOutput);
        Assert.Equal(verification.Command, task.LastVerification.Command);
        Assert.Equal(verification.CompletedAt, task.LastVerification.CompletedAt);
        Assert.Equal(verification.StandardError, task.LastVerification.StandardError);
        Assert.Equal(verification.ExitCode, task.LastVerification.ExitCode);
        Assert.Single(task.VerificationHistory);
        Assert.DoesNotContain(goal.Timeline, e => e.Kind == ProgressKind.TaskRetried);
        Assert.Null(task.LastVerification.HumanInputQuestion);
        Assert.Empty(kernel.HumanInputRequests);
        return (goal, task);
    }
}
