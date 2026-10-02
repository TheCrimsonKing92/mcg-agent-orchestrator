using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DeveloperResumedDeferredNoChangeQualifierTests
{
    internal static readonly string Candidate = new('b', 40);
    internal const string Rationale = "NO_CHANGE: the candidate already contains the approved implementation.";
    internal const string Output = Rationale + "\nWORKER_RESULT:\nfiles: none\ncommands: none\n" +
        "tests: deferred - ClassA\ncommit: none\nblockers: none\nassigned_scope_complete: true\n" +
        "model_fit: test/model - adequate - fixture\nskills: none\nconfidence: high\nEND_WORKER_RESULT";

    [Xunit.Theory]
    [Xunit.InlineData(HumanWaitKind.SpecClarification)]
    [Xunit.InlineData(HumanWaitKind.OperatorApproval)]
    [Xunit.InlineData(HumanWaitKind.RecoveryChoice)]
    public void AnswerAfterCommittingDispatchQualifies(HumanWaitKind kind)
    {
        var (goal, task) = Scenario(kind: kind);

        Xunit.Assert.True(Qualify(goal, task, out var outcome));
        Xunit.Assert.Equal(task.LastDispatch!.BaseCommit, outcome.CandidateSha);
        Xunit.Assert.Equal(Candidate, outcome.CandidateSha);
        Xunit.Assert.Equal(["ClassA"], outcome.TestClasses);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [Xunit.InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void PreviousDispatchWithoutNewCommitDoesNotQualify(string? resultCommit)
    {
        var (goal, task) = Scenario(resultCommit: resultCommit);

        Xunit.Assert.False(Qualify(goal, task, out _));
    }

    [Xunit.Theory]
    [Xunit.InlineData(-1)]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(2)]
    [Xunit.InlineData(3)]
    public void AnswerOutsideExclusiveDispatchWindowDoesNotQualify(int answerOffset)
    {
        var (goal, task) = Scenario(answerOffset: answerOffset);

        Xunit.Assert.False(Qualify(goal, task, out _));
    }

    [Xunit.Fact]
    public void OlderCommitDoesNotExcuseImmediatelyPreviousNoChangeDispatch()
    {
        var (goal, task) = Scenario(repeatResume: true);

        Xunit.Assert.False(Qualify(goal, task, out _));
    }

    [Xunit.Theory]
    [Xunit.InlineData("dirty")]
    [Xunit.InlineData("new-commit")]
    [Xunit.InlineData("wrong-head")]
    [Xunit.InlineData("no-rationale")]
    [Xunit.InlineData("blocker")]
    [Xunit.InlineData("no-classes")]
    public void ResumptionPreservesOtherQualificationRequirements(string missing)
    {
        var (goal, task) = Scenario();
        var output = missing switch
        {
            "no-rationale" => Output.Replace(Rationale + "\n", string.Empty, StringComparison.Ordinal),
            "blocker" => Output.Replace("blockers: none", "blockers: source work remains", StringComparison.Ordinal),
            "no-classes" => Output.Replace("deferred - ClassA", "deferred - conductor will verify", StringComparison.Ordinal),
            _ => Output
        };

        Xunit.Assert.False(Qualify(goal, task, out _, output,
            clean: missing != "dirty", relevantCommit: missing == "new-commit",
            head: missing == "wrong-head" ? new string('c', 40) : Candidate));
    }

    internal static (Goal Goal, TaskSpec Task) Scenario(
        string? resultCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        int answerOffset = 1, HumanWaitKind kind = HumanWaitKind.SpecClarification,
        bool repeatResume = false)
    {
        var start = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock { UtcNow = start };
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Review the approved implementation.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Resumed deferred no-change", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", start, BaseCommit: new string('a', 40)));
        if (resultCommit is not null) kernel.RecordDispatchResultCommit(goal.Id, task.Id, resultCommit);
        CompleteProcess();
        AnswerAt(start.AddMinutes(answerOffset));
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", start.AddMinutes(2), BaseCommit: Candidate));
        if (repeatResume)
        {
            kernel.RecordDispatchResultCommit(goal.Id, task.Id, Candidate);
            CompleteProcess();
            AnswerAt(start.AddMinutes(3));
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "codex-cli", "codex exec", "C:\\repo", start.AddMinutes(4), BaseCommit: Candidate));
        }

        Xunit.Assert.Null(task.LatestRetryAt);
        Xunit.Assert.Equal(0, task.CriterionRetryCount);
        Xunit.Assert.Empty(task.CriterionRetryFeedback);
        Xunit.Assert.Contains(goal.Timeline, item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.HumanInputReceived);
        return (goal, task);

        void CompleteProcess() => kernel.RecordTaskProcessRefreshed(goal.Id, task.Id,
            new TaskProcessRecord(123, "codex exec", "C:\\repo", "out", "err", "exit",
                task.LastDispatch!.DispatchedAt, task.LastDispatch.DispatchedAt, 0), null);

        void AnswerAt(DateTimeOffset time)
        {
            clock.UtcNow = time;
            var request = kernel.RequestHumanInput(goal.Id, task.Id, $"Confirm the implementation at {time:O}.", kind);
            kernel.SubmitHumanInput(request.Id, "The implementation meets the approved boundary.");
            Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        }
    }

    internal static bool Qualify(Goal goal, TaskSpec task, out DeferredNoChangeOutcome outcome,
        string output = Output, bool clean = true, bool relevantCommit = false, string? head = null)
    {
        var catalog = WorkerProviderCatalog.Default();
        var classifier = new WorkerDispatchCompletionClassifier(
            dispatch => catalog.ResolveProfile(dispatch.WorkerName), new FixedClock(),
            _ => false, _ => throw new FileNotFoundException());
        return DeveloperDeferredNoChangeQualifier.TryQualify(goal, task, head ?? Candidate,
            clean, relevantCommit, output, string.Empty, classifier, out outcome);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
