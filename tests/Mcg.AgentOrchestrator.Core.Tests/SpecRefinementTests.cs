using Mcg.AgentOrchestrator.Core;

public sealed class SpecRefinementTests
{
    // --- GoalLifecycle.ResolveState with HasOpenClarification ---

    [Xunit.Fact(DisplayName = "ResolveState_returns_AwaitingClarification_when_HasOpenClarification_is_set")]
    public void ResolveStateReturnsAwaitingClarificationWhenHasOpenClarificationIsSet()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Needs clarification");
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        var state = GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(HasOpenClarification: true));

        Assert.Equal(GoalLifecycleState.AwaitingClarification, state);
    }

    [Xunit.Fact(DisplayName = "ResolveState_AwaitingClarification_takes_priority_over_Blocked")]
    public void ResolveStateAwaitingClarificationTakesPriorityOverBlocked()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Both flags set");
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        var state = GoalLifecycle.ResolveState(
            goal,
            new GoalLifecycleFacts(IsBlocked: true, HasOpenClarification: true));

        Assert.Equal(GoalLifecycleState.AwaitingClarification, state);
    }

    [Xunit.Fact(DisplayName = "ResolveState_AwaitingHumanInput_takes_priority_over_AwaitingClarification")]
    public void ResolveStateAwaitingHumanInputTakesPriorityOverAwaitingClarification()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Human input wins");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Planner);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Started.");
        kernel.RequestHumanInput(goal.Id, task.Id, "Which repo?");

        var state = GoalLifecycle.ResolveState(
            goal,
            new GoalLifecycleFacts(HasOpenClarification: true));

        Assert.Equal(GoalLifecycleState.AwaitingHumanInput, state);
    }

    // --- RefinedSpec.HasOpenQuestions ---

    [Xunit.Fact(DisplayName = "RefinedSpec_HasOpenQuestions_true_when_any_question_has_Open_status")]
    public void RefinedSpecHasOpenQuestionsTrueWhenAnyOpen()
    {
        var spec = new RefinedSpec(
            "contract",
            ["criterion"],
            VerificationClass.TestVerifiable,
            [],
            [new RefinedSpecOpenQuestion("id1", "question?", "external-contract", "Open")]);

        Assert.True(spec.HasOpenQuestions);
    }

    [Xunit.Fact(DisplayName = "RefinedSpec_HasOpenQuestions_false_when_all_questions_resolved")]
    public void RefinedSpecHasOpenQuestionsFalseWhenAllResolved()
    {
        var spec = new RefinedSpec(
            "contract",
            ["criterion"],
            VerificationClass.TestVerifiable,
            [],
            [new RefinedSpecOpenQuestion("id1", "question?", "external-contract", "Resolved")]);

        Assert.False(spec.HasOpenQuestions);
    }

    // --- Snapshot roundtrip ---

    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_RefinedSpec_fields")]
    public void SnapshotRoundtripPreservesRefinedSpecFields()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Roundtrip spec");
        var spec = new RefinedSpec(
            "The system does X.",
            ["Criterion A", "Criterion B"],
            VerificationClass.RealWorldDependent,
            [new RefinedSpecDecision("Q1?", "Option A", "Standard practice.")],
            [new RefinedSpecOpenQuestion("key-1", "Q2?", "external-contract", "Open")]);
        kernel.SetGoalRefinedSpec(goal.Id, spec);

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        var restoredGoal = restored.GetGoal(goal.Id);

        Assert.True(restoredGoal.RefinedSpec is not null);
        var rs = restoredGoal.RefinedSpec!;
        Assert.Equal("The system does X.", rs.BehavioralContract);
        Assert.Equal(2, rs.AcceptanceCriteria.Count);
        Assert.Equal(VerificationClass.RealWorldDependent, rs.VerificationClass);
        Assert.Equal(1, rs.Decisions.Count);
        Assert.Equal("Option A", rs.Decisions[0].Choice);
        Assert.Equal(1, rs.OpenQuestions.Count);
        Assert.Equal("external-contract", rs.OpenQuestions[0].ForkKind);
        Assert.Equal("Open", rs.OpenQuestions[0].Status);
    }

    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_null_RefinedSpec")]
    public void SnapshotRoundtripPreservesNullRefinedSpec()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        kernel.CreateGoal("No spec", [new TaskSpec(TaskId.New(), "Do it", AgentRole.Developer)]);

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        var restoredGoal = restored.Goals.Single();

        Assert.True(restoredGoal.RefinedSpec is null);
    }

    // --- Task brief includes RefinedSpec ---

    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_behavioral_contract_and_acceptance_criteria")]
    public void BuildTaskBriefIncludesBehavioralContractAndAcceptanceCriteria()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Implement the feature");
        var spec = new RefinedSpec(
            "The system provisions a goal worktree on demand.",
            ["WorkspaceReady state entered after create", "Worktree path resolves under execution root"],
            VerificationClass.TestVerifiable,
            [],
            []);
        kernel.SetGoalRefinedSpec(goal.Id, spec);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);

        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.Contains("Refined Spec", brief, StringComparison.Ordinal);
        Assert.Contains("The system provisions a goal worktree on demand.", brief, StringComparison.Ordinal);
        Assert.Contains("WorkspaceReady state entered after create", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BuildTaskBrief_without_RefinedSpec_has_no_spec_section")]
    public void BuildTaskBriefWithoutRefinedSpecHasNoSpecSection()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Plain goal");
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);

        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.False(brief.Contains("Refined Spec", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_decisions_from_RefinedSpec")]
    public void BuildTaskBriefIncludesDecisionsFromRefinedSpec()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Feature with decisions");
        var spec = new RefinedSpec(
            "The system does Y.",
            ["Criterion"],
            VerificationClass.TestVerifiable,
            [new RefinedSpecDecision("HTTP method?", "GET", "Idempotent reads.")],
            []);
        kernel.SetGoalRefinedSpec(goal.Id, spec);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);

        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.Contains("HTTP method?", brief, StringComparison.Ordinal);
        Assert.Contains("GET", brief, StringComparison.Ordinal);
    }
}
