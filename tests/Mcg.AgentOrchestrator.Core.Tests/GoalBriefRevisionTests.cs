using Mcg.AgentOrchestrator.Core;
using System.Text.Json;

public sealed class GoalBriefRevisionTests
{
    [Xunit.Fact]
    public void ReviseGoalBriefPreservesIdentityHistoryAnswersAndCompletedTaskSnapshot()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var completedTask = new TaskSpec(TaskId.New(), "Complete before revision", AgentRole.Developer);
        var pendingTask = new TaskSpec(TaskId.New(), "Dispatch after revision", AgentRole.Tester);
        var goal = kernel.CreateGoal("Original brief", [completedTask, pendingTask]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var clarification = kernel.RequestHumanInput(goal.Id, null, "Which contract applies?");
        kernel.SubmitHumanInput(clarification.Id, "Keep the stable contract");
        kernel.RecordTaskDispatch(
            goal.Id,
            completedTask.Id,
            new TaskDispatchRecord("worker", "worker run", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            completedTask.Id,
            new TaskVerificationRecord(
                "worker run",
                "C:\\repo",
                0,
                "WORKER_RESULT:\nfiles: src/feature.cs\ncommands: build\ntests: pass - focused\ncommit: abc123\nblockers: none\nEND_WORKER_RESULT",
                string.Empty,
                clock.UtcNow,
                WorkerResultPresent: true,
                HasCommittedChanges: true));
        var completedBefore = JsonSerializer.Serialize(
            kernel.ExportGoalSnapshot(goal.Id).Tasks.Single(task => task.Id == completedTask.Id.Value));
        var timelineBefore = goal.Timeline.ToArray();

        clock.Advance();
        var result = kernel.ReviseGoalBrief(
            goal.Id,
            "Narrow revised brief",
            "Cut deferred scope",
            [new GoalBriefAnswerSupersession(clarification.Id, "Use the narrowed contract")]);

        Assert.Equal(goal.Id, result.GoalId);
        Assert.Equal("Narrow revised brief", goal.Objective);
        Assert.Equal([pendingTask.Id], result.NotYetStartedTaskIds);
        Assert.Equal([completedTask.Id], result.CompletedTaskIds);
        Assert.Equal(timelineBefore, goal.Timeline.Take(timelineBefore.Length));
        Assert.Equal(ProgressKind.GoalBriefRevised, goal.Timeline[^1].Kind);
        Assert.Contains("Cut deferred scope", goal.Timeline[^1].Message, StringComparison.Ordinal);
        Assert.Equal("Use the narrowed contract", clarification.Answer);
        Assert.Collection(
            clarification.AnswerHistory,
            original =>
            {
                Assert.Equal("Keep the stable contract", original.Text);
                Assert.Equal(1, original.BriefVersion);
                Assert.True(original.IsRetracted);
            },
            replacement =>
            {
                Assert.Equal("Use the narrowed contract", replacement.Text);
                Assert.Equal(2, replacement.BriefVersion);
                Assert.False(replacement.IsRetracted);
            });
        Assert.Equal(
            completedBefore,
            JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id).Tasks.Single(task => task.Id == completedTask.Id.Value)));

        Assert.Collection(
            goal.BriefVersions,
            first =>
            {
                Assert.Equal(1, first.Version);
                Assert.Equal("Original brief", first.Text);
                Assert.Equal(2, first.SupersededByVersion);
                Assert.True(first.IsSuperseded);
            },
            second =>
            {
                Assert.Equal(2, second.Version);
                Assert.Equal("Narrow revised brief", second.Text);
                Assert.Equal("Cut deferred scope", second.Reason);
                Assert.True(second.IsAuthoritative);
            });

        var pendingBrief = kernel.BuildTaskBrief(goal.Id, pendingTask.Id);
        Assert.Contains("Narrow revised brief", pendingBrief.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Original brief", pendingBrief.Content, StringComparison.Ordinal);
        Assert.Contains("Use the narrowed contract", pendingBrief.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Keep the stable contract", pendingBrief.Content, StringComparison.Ordinal);
        Assert.Contains("answered under brief v2; current brief v2", pendingBrief.Content, StringComparison.Ordinal);
        kernel.RecordTaskDispatch(
            goal.Id,
            pendingTask.Id,
            new TaskDispatchRecord("worker", "worker run revised", "C:\\repo", clock.UtcNow));
        Assert.Equal(2, pendingTask.LastDispatch!.BriefVersion);
        Assert.Equal("Narrow revised brief", pendingTask.LastDispatch.BriefSnapshot);
    }

    [Xunit.Fact]
    public void ReviseGoalBriefChainsVersionsAndRoundTripsSnapshot()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("v1 brief", [new TaskSpec(TaskId.New(), "work", AgentRole.Developer)]);

        clock.Advance();
        kernel.ReviseGoalBrief(goal.Id, "v2 brief");
        clock.Advance();
        kernel.ReviseGoalBrief(goal.Id, "  v3 brief\r\n", "second narrowing");
        var restoredKernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        var restored = restoredKernel.GetGoal(goal.Id);

        Assert.Equal([1, 2, 3], restored.BriefVersions.Select(version => version.Version));
        Assert.Equal([2, 3, null], restored.BriefVersions.Select(version => version.SupersededByVersion));
        Assert.Equal("v3 brief", restored.AuthoritativeBrief.Text);
        Assert.Single(restored.BriefVersions.Where(version => version.IsAuthoritative));
    }

    [Xunit.Fact]
    public void ReviseGoalBriefRejectsTerminalAndIdenticalBriefsWithoutMutation()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Same\nbrief", [new TaskSpec(TaskId.New(), "work", AgentRole.Developer)]);
        var before = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id));

        var noChange = Assert.Throws<GoalBriefRevisionNoChangeException>(
            () => kernel.ReviseGoalBrief(goal.Id, "Same\r\nbrief"));
        Assert.Contains("v1", noChange.Message, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id)));

        kernel.CancelGoal(goal.Id, "terminal test");
        var terminalBefore = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id));
        Assert.Throws<GoalBriefRevisionNotAllowedException>(() => kernel.ReviseGoalBrief(goal.Id, "Different brief"));
        Assert.Equal(terminalBefore, JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id)));
        Assert.Throws<KeyNotFoundException>(
            () => kernel.ReviseGoalBrief(new GoalId(Guid.NewGuid().ToString("n")), "Different brief"));
    }

    [Xunit.Fact]
    public void ReviseGoalBriefDoesNotInvokeOrDispatchAnyRole()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Original brief", [
            new TaskSpec(TaskId.New(), "pending developer work", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "pending tester work", AgentRole.Tester)
        ]);
        var taskRecordsBefore = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id).Tasks);
        var timelineCountBefore = goal.Timeline.Count;

        kernel.ReviseGoalBrief(goal.Id, "Revised brief");

        Assert.Equal(taskRecordsBefore, JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id).Tasks));
        var revisionEvents = goal.Timeline.Skip(timelineCountBefore).ToArray();
        Assert.Single(revisionEvents);
        Assert.Equal(ProgressKind.GoalBriefRevised, revisionEvents[0].Kind);
        Assert.DoesNotContain(revisionEvents, progress => progress.TaskId is not null);
        Assert.DoesNotContain(revisionEvents, progress => progress.Kind == ProgressKind.TaskDispatchRecorded);
        Assert.All(goal.Tasks, task =>
        {
            Assert.Null(task.LastDispatch);
            Assert.Null(task.LastExecution);
            Assert.Equal(WorkTaskStatus.Pending, task.Status);
        });
    }

    [Xunit.Fact]
    public void InvalidSupersessionLeavesGoalAndAnswersUnchanged()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Original brief");
        var clarification = kernel.RequestHumanInput(goal.Id, null, "Which contract?");
        kernel.SubmitHumanInput(clarification.Id, "Original answer");
        var before = JsonSerializer.Serialize(kernel.ExportSnapshot());

        Assert.Throws<KeyNotFoundException>(() => kernel.ReviseGoalBrief(
            goal.Id,
            "Revised brief",
            answerSupersessions:
            [
                new GoalBriefAnswerSupersession(clarification.Id, "Replacement answer"),
                new GoalBriefAnswerSupersession(
                    new HumanInputRequestId(Guid.NewGuid().ToString("n")),
                    "Missing answer")
            ]));

        Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportSnapshot()));
    }

    [Xunit.Fact]
    public void ExplicitReRefinementSupersedesPriorOutputAndRoundTripsHistory()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "consume authoritative refinement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Original brief", [task]);
        var original = new RefinedSpec(
            "Original role output",
            ["Original finding"],
            VerificationClass.TestVerifiable,
            [],
            []);
        kernel.RecordGoalRefinement(goal.Id, original);
        clock.Advance();
        kernel.ReviseGoalBrief(goal.Id, "Revised brief");
        clock.Advance();
        var replacement = new RefinedSpec(
            "Replacement role output",
            ["Replacement finding"],
            VerificationClass.TestVerifiable,
            [],
            []);

        kernel.RecordGoalRefinement(goal.Id, replacement);
        var restoredKernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        var restored = restoredKernel.GetGoal(goal.Id);

        Assert.Collection(
            restored.RefinedSpecVersions,
            first =>
            {
                Assert.Equal("Original role output", first.Spec.BehavioralContract);
                Assert.Equal(["Original finding"], first.Spec.AcceptanceCriteria);
                Assert.Equal(2, first.SupersededByVersion);
                Assert.True(first.IsSuperseded);
            },
            second =>
            {
                Assert.Equal("Replacement role output", second.Spec.BehavioralContract);
                Assert.Equal(["Replacement finding"], second.Spec.AcceptanceCriteria);
                Assert.Equal(2, second.BriefVersion);
                Assert.True(second.IsAuthoritative);
            });
        var roleContext = restored.RefinedSpec!;
        Assert.Equal("Replacement role output", roleContext.BehavioralContract);
        Assert.DoesNotContain("Original finding", roleContext.AcceptanceCriteria);
        Assert.Contains("Replacement finding", roleContext.AcceptanceCriteria);
        var renderedRoleContext = restoredKernel.BuildTaskBrief(goal.Id, task.Id).Content;
        Assert.Contains("Replacement role output", renderedRoleContext, StringComparison.Ordinal);
        Assert.Contains("Replacement finding", renderedRoleContext, StringComparison.Ordinal);
        Assert.DoesNotContain("Original role output", renderedRoleContext, StringComparison.Ordinal);
        Assert.DoesNotContain("Original finding", renderedRoleContext, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ReviseGoalBriefReDerivesRefinedCriteriaFromTheNewAuthoritativeBrief()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("""
            Original brief.

            ## Acceptance criteria

            1. Original criterion.
            """);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Original behavior.",
            ["Original criterion."],
            VerificationClass.TestVerifiable,
            [],
            []));

        clock.Advance();
        var result = kernel.ReviseGoalBrief(goal.Id, """
            Revised brief.

            ## Acceptance criteria

            1. First revised criterion.
            2. Second revised criterion.
            """);

        Assert.True(result.RefinedCriteriaReDerived);
        Assert.Equal(2, result.RefinedCriteriaCount);
        Assert.Equal(
            ["First revised criterion.", "Second revised criterion."],
            goal.RefinedSpec!.AcceptanceCriteria);
        Assert.Equal(2, goal.RefinedSpecVersions.Count);
        Assert.Equal(2, goal.AuthoritativeRefinedSpecVersion!.BriefVersion);
    }

    [Xunit.Fact]
    public void ReviseGoalBrief_StandaloneAcceptanceLabel_ReDerivesFourCriteriaWithoutRewritingBrief()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Original brief.");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Original behavior.",
            ["The objective is achieved."],
            VerificationClass.TestVerifiable,
            [],
            []));
        var plainLabel = "  aCcEpTaNcE cRiTeRiA:  ";
        var revisedBrief = $"""
            Revised brief.

            {plainLabel}
            1. First revised criterion.
            2. Second revised criterion.
            3. Third revised criterion.
            4. Fourth revised criterion.
            """;

        clock.Advance();
        var result = kernel.ReviseGoalBrief(goal.Id, revisedBrief);

        Assert.True(result.RefinedCriteriaReDerived);
        Assert.Equal(4, result.RefinedCriteriaCount);
        Assert.Equal(
            [
                "First revised criterion.",
                "Second revised criterion.",
                "Third revised criterion.",
                "Fourth revised criterion."
            ],
            goal.RefinedSpec!.AcceptanceCriteria);
        Assert.Equal(revisedBrief, goal.Objective);
        Assert.Equal(2, goal.BriefVersions.Count);
        Assert.Equal(2, goal.AuthoritativeRefinedSpecVersion!.BriefVersion);
    }

    [Xunit.Fact]
    public void FromSnapshot_PlainLabelBrief_DoesNotReDeriveExistingFallbackCriteria()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("""
            Existing brief.

            Acceptance criteria:
            1. Explicit criterion that was not yet revised.
            """);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Existing behavior.",
            ["The objective is achieved."],
            VerificationClass.TestVerifiable,
            [],
            []));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock).GetGoal(goal.Id);

        Assert.Equal(["The objective is achieved."], restored.RefinedSpec!.AcceptanceCriteria);
        Assert.Single(restored.RefinedSpecVersions);
        Assert.Single(restored.BriefVersions);
    }

    [Xunit.Fact]
    public void RoleContextLabelsUnstampedLegacyAnswerVersionAsUnknown()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var task = new TaskSpec(TaskId.New(), "consume clarification", AgentRole.Developer);
        var goal = kernel.CreateGoal("Original brief", [task]);
        kernel.ReviseGoalBrief(goal.Id, "Revised brief");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Contract",
            ["Criterion"],
            VerificationClass.TestVerifiable,
            [],
            [])
        {
            ClarificationAnswerHistory =
            [
                new HumanInputAnswerRecord(
                    "legacy-answer",
                    "Keep the legacy contract",
                    DateTimeOffset.UtcNow)
            ]
        });

        var roleContext = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.Contains("answered under an unknown brief version; current brief v2", roleContext, StringComparison.Ordinal);
        Assert.DoesNotContain("answered under brief v1", roleContext, StringComparison.Ordinal);
    }
}
