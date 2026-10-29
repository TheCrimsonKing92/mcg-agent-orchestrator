using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

public sealed class ReadinessWorklistTextViewCharacterizationTests
{
    [Fact]
    public void PrintEvidenceSummaryPreservesExactText()
    {
        var goal = CreateGoal();
        var summary = new GoalEvidenceSummary(
            GoalId: goal.Id,
            Objective: goal.Objective,
            Status: GoalStatus.Active,
            TotalTasks: 1,
            TasksWithExecution: 1,
            TasksWithDispatch: 1,
            TasksWithProcess: 1,
            RunningProcesses: 0,
            TasksWithVerification: 1,
            PassedVerifications: 0,
            FailedVerifications: 1,
            PendingHumanInputCount: 1,
            InputTokens: 120,
            OutputTokens: 40,
            PotentiallyPaidInputTokens: 100,
            PotentiallyPaidOutputTokens: 30,
            ModelUsage:
            [
                new ModelUsageSummary("OpenAI", "model-a", 1, 100, 30,
                    OutputTokenLimitHitCount: 1, MaxOutputTokens: 30, IsPotentiallyPaidProvider: true),
                new ModelUsageSummary("Local", "model-b", 1, null, null)
            ],
            DispatchModelUsage:
            [
                new DispatchModelSummary("OpenAI", "model-a", 2, TaskComplexity.Complex,
                    ReasoningEffort: "high", IsPotentiallyPaidProvider: true)
            ],
            ModelFit:
            [
                new ModelFitSummary("OpenAI", "model-a", 4, 1, 1, 1, 1, ["refactor", "tests"])
            ],
            Tasks:
            [
                new TaskEvidenceSummary(goal.Tasks[0].Id, AgentRole.Developer, goal.Tasks[0].Description,
                    WorkTaskStatus.Completed, TaskEvidenceKind.FailedVerification,
                    HasExecution: true, HasDispatch: true, HasProcess: true, IsProcessRunning: false,
                    HasVerification: true, LatestVerificationSucceeded: false, VerificationHistoryCount: 1,
                    PendingHumanInputCount: 1, Message: "Focused check failed")
            ]);

        // Expectations come from the pre-extraction format strings, including both blank lines.
        var expected = string.Join(Environment.NewLine,
            "",
            "Goal ddac2d5b evidence",
            "Objective: Extract readiness output",
            "Status: Active",
            "Tasks: 1",
            "Execution: 1; dispatch: 1; process: 1 (running 0)",
            "Tokens: 120 in / 40 out; potentially paid: 100 in / 30 out",
            "  model: OpenAI/model-a potentially paid: 1 run(s), 100 in / 30 out, cap hits 1 of 30",
            "  model: Local/model-b: 1 run(s), n/a in / n/a out",
            "  dispatch model: OpenAI/model-a Complex reasoning high potentially paid: 2 dispatch(es)",
            "  model fit: OpenAI/model-a: 4 notes; adequate 1, overkill 1, underpowered 1, unknown 1; shapes refactor, tests",
            "Verification: 1; passed=0; failed=1",
            "Pending human input: 1",
            "  1. [FailedVerification] Developer: Extract printers",
            "     Focused check failed",
            "",
            "");

        var actual = AsyncLocalConsoleRouter.Capture(() => ReadinessWorklistTextView.PrintEvidenceSummary(goal, summary));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PrintStageReadinessReportPreservesExactText()
    {
        var goal = CreateGoal();
        var report = new GoalStageReadinessReport(goal.Id, goal.Objective, GoalStatus.Active,
            TotalStages: 1, VerifiedStages: 0, OpenStages: 1, BlockedStages: 0, IsReadyForAcceptance: false,
            Stages:
            [
                new TaskStageReadiness(goal.Tasks[0].Id, AgentRole.Developer, goal.Tasks[0].Description,
                    WorkTaskStatus.Completed, IsAssigned: true, StageReadinessStatus.NeedsVerification,
                    TaskEvidenceKind.CompletedProcess, VerificationGateStatus.MissingVerification,
                    PendingHumanInputCount: 0, Message: "Implementation finished", SuggestedAction: "Verify the extraction")
            ]);
        var expected = string.Join(Environment.NewLine,
            "",
            "Goal ddac2d5b SDLC stages: not ready",
            "Objective: Extract readiness output",
            "Status: Active",
            "Stages: 1; verified=0; open=1; blocked=0",
            "  1. [NeedsVerification] Developer: Extract printers",
            "     task=Completed; assigned=True; evidence=CompletedProcess; gate=MissingVerification",
            "     Implementation finished",
            "     action: Verify the extraction",
            "     command: verify 1 <command> | verify-manual 1 passed <note>",
            "",
            "");

        var actual = AsyncLocalConsoleRouter.Capture(() => ReadinessWorklistTextView.PrintStageReadinessReport(goal, report));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PrintVerificationGatePreservesExactText()
    {
        var goal = CreateGoal();
        var gate = new GoalVerificationGate(goal.Id, goal.Objective, GoalStatus.Active, IsSatisfied: false,
            Tasks:
            [
                new TaskVerificationGate(goal.Tasks[0].Id, AgentRole.Developer, goal.Tasks[0].Description,
                    WorkTaskStatus.Completed, VerificationGateStatus.FailedVerification,
                    "Focused check failed", VerificationGateReason.VerificationFailed)
            ]);
        var expected = string.Join(Environment.NewLine,
            "",
            "Goal ddac2d5b verification gate: not passed",
            "Objective: Extract readiness output",
            "Status: Active",
            "  1. [FailedVerification] Developer: Extract printers",
            "     task status: Completed",
            "     Focused check failed",
            "",
            "");

        var actual = AsyncLocalConsoleRouter.Capture(() => ReadinessWorklistTextView.PrintVerificationGate(goal, gate));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PrintVerificationWorklistPreservesExactText()
    {
        var goal = CreateGoal();
        var worklist = new GoalVerificationWorklist(goal.Id, goal.Objective, GoalStatus.Active,
            IsSatisfied: false, OpenCount: 1,
            Items:
            [
                new TaskVerificationWorkItem(goal.Tasks[0].Id, AgentRole.Developer, goal.Tasks[0].Description,
                    WorkTaskStatus.Completed, VerificationGateStatus.MissingVerification,
                    "No verification recorded", "Run a focused check")
            ]);
        var expected = string.Join(Environment.NewLine,
            "",
            "Goal ddac2d5b verification worklist: 1 open",
            "Objective: Extract readiness output",
            "Status: Active",
            "  1. [MissingVerification] Developer: Extract printers",
            "     No verification recorded",
            "     action: Run a focused check",
            "     command: verify 1 <command> | verify-manual 1 passed <note>",
            "",
            "");

        var actual = AsyncLocalConsoleRouter.Capture(() => ReadinessWorklistTextView.PrintVerificationWorklist(goal, worklist));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PrintHumanInputWorklistPreservesExactText()
    {
        var goal = CreateGoal();
        var requestedAt = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var worklist = new GoalHumanInputWorklist(goal.Id, goal.Objective, GoalStatus.Active, OpenCount: 2,
            Items:
            [
                new HumanInputWorkItem(new HumanInputRequestId("request-task"), goal.Tasks[0].Id,
                    AgentRole.Developer, goal.Tasks[0].Description, WorkTaskStatus.WaitingForHuman,
                    "Which output should be kept?", requestedAt, HumanWaitKind.SpecClarification,
                    IsAutoDefaultable: false, IsDismissible: false, IsAnswerRequired: true,
                    IsExternallyBlocked: false, AgeSeconds: 90, ResumeCommand: "resume-extraction",
                    SuggestedAction: "Clarify the output", OccurrenceCount: 2),
                new HumanInputWorkItem(new HumanInputRequestId("request-goal"), null, null, null, null,
                    "Approve the extraction?", requestedAt, HumanWaitKind.OperatorApproval,
                    IsAutoDefaultable: true, IsDismissible: true, IsAnswerRequired: false,
                    IsExternallyBlocked: true, AgeSeconds: 30, ResumeCommand: "answer request- <answer>",
                    SuggestedAction: "Review the candidate")
            ]);
        var expected = string.Join(Environment.NewLine,
            "",
            "Goal ddac2d5b human input worklist: 2 open",
            "Objective: Extract readiness output",
            "Status: Active",
            "  request-task SpecClarification age=90s occurrences=2 task 1 [WaitingForHuman] Developer: Extract printers",
            "     flags: auto-defaultable=False; dismissible=False; answer-required=True; externally-blocked=False",
            "     question: Which output should be kept?",
            "     action: Clarify the output",
            "     command: resume-extraction",
            "  request-goal OperatorApproval age=30s occurrences=1 goal",
            "     flags: auto-defaultable=True; dismissible=True; answer-required=False; externally-blocked=True",
            "     question: Approve the extraction?",
            "     action: Review the candidate",
            "     command: answer request-goal <answer>",
            "",
            "");

        var actual = AsyncLocalConsoleRouter.Capture(() => ReadinessWorklistTextView.PrintHumanInputWorklist(goal, worklist));

        Assert.Equal(expected, actual);
    }

    private static Goal CreateGoal() => new(
        new GoalId("ddac2d5ba3b047528e74cb75fb319ba6"),
        "Extract readiness output",
        [new TaskSpec(new TaskId("task-1"), "Extract printers", AgentRole.Developer)]);
}
