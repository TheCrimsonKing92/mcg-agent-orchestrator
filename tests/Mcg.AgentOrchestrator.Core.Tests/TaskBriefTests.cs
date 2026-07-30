using Mcg.AgentOrchestrator.Core;

public sealed class TaskBriefTests
{
    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_goal_task_role_and_timeline")]
    public void BuildTaskBriefIncludesGoalTaskRoleAndTimeline()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Build worker adapter");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RetryTask(goal.Id, task.Id, "Developer retry note.");

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id);

    Assert.Equal(goal.Id, brief.GoalId);
    Assert.Equal(task.Id, brief.TaskId);
    Assert.Equal(AgentRole.Developer, brief.Role);
    Assert.Contains("Build worker adapter", brief.Content, StringComparison.Ordinal);
    Assert.Contains($"Goal id: {goal.Id.Value}", brief.Content, StringComparison.Ordinal);
    Assert.Contains("do not attempt to reach dashboard APIs or orchestrator state", brief.Content, StringComparison.Ordinal);
    Assert.True(!brief.Content.Contains("Goal work summary:", StringComparison.Ordinal));
    Assert.True(!brief.Content.Contains("/api/goals/", StringComparison.Ordinal));
    Assert.True(!brief.Content.Contains("/api/system/dashboard-host", StringComparison.Ordinal));
    Assert.Contains(task.Description, brief.Content, StringComparison.Ordinal);
    Assert.Contains("Developer retry note.", brief.Content, StringComparison.Ordinal);
    Assert.Contains("HUMAN_INPUT:", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Report only changed files", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Keep the response concise", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Model fit: <provider>/<model or launcher> - adequate|overkill|underpowered - <task shape> - <short reason>", brief.Content, StringComparison.Ordinal);
    Assert.Contains("**/bin/**", brief.Content, StringComparison.Ordinal);
    Assert.Contains("**/obj/**", brief.Content, StringComparison.Ordinal);
    Assert.Contains("/api/source-survey?max=8", brief.Content, StringComparison.Ordinal);
    Assert.Contains("## Verification Plan", brief.Content, StringComparison.Ordinal);
    Assert.Contains(task.VerificationPlan!, brief.Content, StringComparison.Ordinal);
    Assert.True(!brief.Content.Contains("Context files:", StringComparison.Ordinal));
    Assert.True(!brief.Content.Contains("Complete this task as the assigned SDLC role", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_prefers_bounded_source_survey_for_complex_tasks")]
    public void BuildTaskBriefPrefersBoundedSourceSurveyForComplexTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Reduce paid model costs",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production architecture for cost-aware model routing across CLI, dashboard, API, workers, and tests.",
                AgentRole.Researcher)
        ]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("/api/source-survey?max=8", brief, StringComparison.Ordinal);
    Assert.Contains("before broad recursive file reads", brief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_reviewer_renders_effective_criteria_overlay_above_refined_spec_acceptance")]
    public void BuildTaskBriefReviewerRendersEffectiveCriteriaOverlayAboveRefinedSpecAcceptance()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var developer = new TaskSpec(TaskId.New(), "Implement corrected contract", AgentRole.Developer);
    var reviewer = new TaskSpec(TaskId.New(), "Review corrected contract", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Correct prompt criteria", [developer, reviewer]);
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        "Ship corrected prompt behavior",
        ["run the full Infrastructure suite before review", "keep unrelated findings blocking"],
        VerificationClass.TestVerifiable,
        [],
        []));
    kernel.RecordTaskNote(
        goal.Id,
        developer.Id,
        "CRITERIA CORRECTION: supersedes=\"run the full Infrastructure suite before review\"; correction=\"focused build-check evidence is sufficient for this slice\"");

    var developerBrief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;
    var brief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;

    var overlayIndex = brief.IndexOf("## EFFECTIVE ACCEPTANCE CRITERIA - OPERATOR CORRECTIONS", StringComparison.Ordinal);
    var acceptanceIndex = brief.IndexOf("Acceptance criteria:", StringComparison.Ordinal);
    Assert.True(overlayIndex >= 0);
    Assert.True(acceptanceIndex > overlayIndex);
    Assert.Contains("## EFFECTIVE ACCEPTANCE CRITERIA - OPERATOR CORRECTIONS", developerBrief, StringComparison.Ordinal);
    Assert.Contains("Operator corrections in this overlay supersede conflicting brief text", brief, StringComparison.Ordinal);
    Assert.Contains("Do not enforce or re-raise findings that apply only to superseded criteria", brief, StringComparison.Ordinal);
    Assert.Contains("focused build-check evidence is sufficient for this slice", brief, StringComparison.Ordinal);
    Assert.Contains("category: spec-defect", brief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_reviewer_convergence_scope_preserves_finding_severity")]
    public void BuildTaskBriefReviewerConvergenceScopePreservesFindingSeverity()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var reviewer = new TaskSpec(TaskId.New(), "Review severity-aware convergence.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Preserve advisory severity across review rounds", [reviewer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    kernel.RecordTaskVerification(
        goal.Id,
        reviewer.Id,
        new TaskVerificationRecord(
            "review",
            "C:\\repo",
            0,
            """
            WORKER_RESULT:
            files: none
            commands: review
            tests: not-run - Reviewer is read-only
            commit: none
            blockers: exact-blocker - B-1 correctness blocker
            findings: [{"stable_id":"B-1","state":"open","severity":"blocking","location":{"file":"src/B.cs","region":"B.Run"},"description":"Correctness blocker fixed."}]
            touched_anchors: []
            verdict: needs-work
            model_fit: Anthropic/claude-opus-5 - adequate - focused review - severity receipt
            skills: none
            confidence: high
            END_WORKER_RESULT
            """,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true));
    kernel.RetryTask(goal.Id, reviewer.Id, "Address B-1 before the next review.");
    clock.Advance();
    kernel.RecordTaskVerification(
        goal.Id,
        reviewer.Id,
        new TaskVerificationRecord(
            "review",
            "C:\\repo",
            0,
            """
            WORKER_RESULT:
            files: none
            commands: review
            tests: not-run - Reviewer is read-only
            commit: none
            blockers: none
            findings: [{"stable_id":"A-1","state":"open","severity":"advisory","location":{"file":"src/A.cs","region":"A.Run"},"description":"Readability follow-up."},{"stable_id":"B-1","state":"resolved","severity":"blocking","location":{"file":"src/B.cs","region":"B.Run"},"description":"Correctness blocker fixed."}]
            touched_anchors: []
            verdict: pass
            model_fit: Anthropic/claude-opus-5 - adequate - focused review - severity receipt
            skills: none
            confidence: high
            END_WORKER_RESULT
            """,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true));
    kernel.RetryTask(goal.Id, reviewer.Id, "Run the next review round.");

    var brief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;

    Assert.Contains(
        "- A-1 | severity=advisory | src/A.cs::A.Run | Readability follow-up.",
        brief,
        StringComparison.Ordinal);
    Assert.Contains(
        "- B-1 | severity=blocking | src/B.cs::B.Run | carry forward; do not re-review unless this exact anchor was touched.",
        brief,
        StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "AgentTaskRunner_prefers_bounded_source_survey_for_research_prompts")]
    public async Task AgentTaskRunnerPrefersBoundedSourceSurveyForResearchPrompts()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Reduce paid model costs",
        [
            new TaskSpec(
                TaskId.New(),
                "Inspect dashboard, API, CLI, worker, and test model-routing behavior; report current cost controls.",
                AgentRole.Researcher)
        ]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var provider = new FakeModelProvider("OpenAI", "research complete");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]));

    await runner.RunAsync(goal.Id, task.Id);

    Assert.True(provider.LastRequest is not null);
    Assert.Contains("/api/source-survey?max=8", provider.LastRequest!.Messages.Single().Content, StringComparison.Ordinal);
    Assert.Contains("Model fit: OpenAI/gpt-5.5 - adequate|overkill|underpowered - <task shape> - <short reason>", provider.LastRequest!.Messages.Single().Content, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_trims_noisy_goal_and_task_primary_context")]
    public void BuildTaskBriefTrimsNoisyGoalAndTaskPrimaryContext()
{
    var objective = $"goal-start {new string('g', 1700)} goal-middle-omitted {new string('h', 1200)} goal-tail";
    var description = $"task-start {new string('t', 1700)} task-middle-omitted {new string('u', 1200)} task-tail";
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        objective,
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id);

    Assert.Contains("goal-start", brief.Content, StringComparison.Ordinal);
    Assert.Contains("goal-tail", brief.Content, StringComparison.Ordinal);
    Assert.Contains("task-start", brief.Content, StringComparison.Ordinal);
    Assert.Contains("task-tail", brief.Content, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief.Content, StringComparison.Ordinal);
    Assert.True(!brief.Content.Contains("goal-middle-omitted", StringComparison.Ordinal));
    Assert.True(!brief.Content.Contains("task-middle-omitted", StringComparison.Ordinal));
    Assert.True(brief.Title.Length < description.Length);
    Assert.True(!brief.Title.Contains("task-tail", StringComparison.Ordinal));
    Assert.Equal(objective, goal.Objective);
    Assert.Equal(description, task.Description);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_uses_smaller_primary_context_budget_for_simple_tasks")]
    public void BuildTaskBriefUsesSmallerPrimaryContextBudgetForSimpleTasks()
{
    var objective = $"simple-goal-start {new string('g', 900)} simple-goal-middle {new string('h', 500)} simple-goal-tail";
    var description = $"simple-task-start {new string('t', 900)} simple-task-middle {new string('u', 500)} simple-task-tail";
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        objective,
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("simple-goal-start", brief, StringComparison.Ordinal);
    Assert.Contains("simple-goal-tail", brief, StringComparison.Ordinal);
    Assert.Contains("simple-task-start", brief, StringComparison.Ordinal);
    Assert.Contains("simple-task-tail", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("simple-goal-middle", StringComparison.Ordinal));
    Assert.True(!brief.Contains("simple-task-middle", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_larger_primary_context_budget_for_complex_tasks")]
    public void BuildTaskBriefKeepsLargerPrimaryContextBudgetForComplexTasks()
{
    var objective = $"complex-goal-start {new string('g', 900)} complex-goal-middle {new string('h', 500)} complex-goal-tail";
    var description = $"Design and implement production architecture. complex-task-start {new string('t', 900)} complex-task-middle {new string('u', 500)} complex-task-tail";
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        objective,
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("complex-goal-middle", brief, StringComparison.Ordinal);
    Assert.Contains("complex-task-middle", brief, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_uses_full_role_requirements_for_complex_tasks")]
    public void BuildTaskBriefUsesFullRoleRequirementsForComplexTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Maintain dashboard views",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("Developer Requirements", brief, StringComparison.Ordinal);
    Assert.Contains("dashboard or orchestrator blocks the ideal path", brief, StringComparison.Ordinal);
    Assert.Contains("Complete this task as the assigned SDLC role", brief, StringComparison.Ordinal);
    Assert.Contains("Avoid generic status summaries", brief, StringComparison.Ordinal);
    Assert.Contains("Keep the response evidence-focused", brief, StringComparison.Ordinal);
    Assert.Contains("omit generic progress and long logs", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("Keep the response concise", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_adds_high_risk_reviewer_enumeration_contract_only_for_stored_intake_labels")]
    public void BuildTaskBriefAddsHighRiskReviewerEnumerationContractOnlyForStoredIntakeLabels()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var highRiskReviewer = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var complexReviewer = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var normalReviewer = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var highRiskGoal = kernel.CreateGoal("Review stored high-risk goal", [highRiskReviewer]);
    var complexGoal = kernel.CreateGoal("Review stored complex goal", [complexReviewer]);
    var normalGoal = kernel.CreateGoal("Review stored normal goal", [normalReviewer]);
    kernel.RecordGoalPolicyDecision(
        highRiskGoal.Id,
        "Intake pipeline decision (auto): developer-reviewer; reasons: high-risk objective needs pre-acceptance review; risk labels: high-risk, multi-scope.");
    kernel.RecordGoalPolicyDecision(
        complexGoal.Id,
        "Intake pipeline decision (auto): developer-reviewer; reasons: complex objective needs reviewer coverage before acceptance; risk labels: complex.");
    kernel.RecordGoalPolicyDecision(
        normalGoal.Id,
        "Intake pipeline decision (auto): developer-only; reasons: simple code objective; risk labels: low-risk.");

    var highRiskBrief = kernel.BuildTaskBrief(highRiskGoal.Id, highRiskReviewer.Id).Content;
    var complexBrief = kernel.BuildTaskBrief(complexGoal.Id, complexReviewer.Id).Content;
    var normalBrief = kernel.BuildTaskBrief(normalGoal.Id, normalReviewer.Id).Content;

    Assert.Contains("High-risk review enumeration contract", highRiskBrief, StringComparison.Ordinal);
    Assert.Contains("list all acceptance-blocking findings in one ranked pass", highRiskBrief, StringComparison.Ordinal);
    Assert.Contains("do not stop at the first blocker", highRiskBrief, StringComparison.Ordinal);
    Assert.Contains("put exactly the complete ranked open blocking set in `blockers`", highRiskBrief, StringComparison.Ordinal);
    Assert.Contains("High-risk review enumeration contract", complexBrief, StringComparison.Ordinal);
    Assert.DoesNotContain("High-risk review enumeration contract", normalBrief, StringComparison.Ordinal);
    Assert.DoesNotContain("list all acceptance-blocking findings in one ranked pass", normalBrief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_reviewer_staleness_policy")]
    public void BuildTaskBriefIncludesReviewerStalenessPolicy()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var simpleReviewer = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var complexReviewer = new TaskSpec(
        TaskId.New(),
        "Review a complex distributed integration change with migration risk and concurrency risk.",
        AgentRole.Reviewer);
    var simpleGoal = kernel.CreateGoal("Review normal goal", [simpleReviewer]);
    var complexGoal = kernel.CreateGoal(
        "Review complex multi-service architecture migration with concurrency and rollback risks.",
        [complexReviewer]);

    var simpleBrief = kernel.BuildTaskBrief(simpleGoal.Id, simpleReviewer.Id).Content;
    var complexBrief = kernel.BuildTaskBrief(complexGoal.Id, complexReviewer.Id).Content;

    Assert.Contains("Branch-behind-main alone is NOT a blocker", simpleBrief, StringComparison.Ordinal);
    Assert.Contains("block only on concrete conflict, semantic overlap, or a non-applying diff", simpleBrief, StringComparison.Ordinal);
    Assert.Contains("Branch-behind-main alone is NOT a blocker", complexBrief, StringComparison.Ordinal);
    Assert.Contains("block only on concrete conflict, semantic overlap, or a non-applying diff", complexBrief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "AgentTaskRunner_includes_complex_response_budget_guidance")]
    public async Task AgentTaskRunnerIncludesComplexResponseBudgetGuidance()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Maintain dashboard views",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var provider = new FakeModelProvider("OpenAI", "complex work complete");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]));

    await runner.RunAsync(goal.Id, task.Id);

    Assert.True(provider.LastRequest is not null);
    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("Response guidance: Keep the response evidence-focused", prompt, StringComparison.Ordinal);
    Assert.Contains("omit generic progress and long logs", prompt, StringComparison.Ordinal);
    Assert.Contains("Model fit: OpenAI/gpt-5.5 - adequate|overkill|underpowered - <task shape> - <short reason>", prompt, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_pending_human_input_and_verification")]
    public void BuildTaskBriefIncludesPendingHumanInputAndVerification()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Brief with context");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    kernel.RequestHumanInput(goal.Id, task.Id, "Which test command?");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "", "failed", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id);

    Assert.Contains("Which test command?", brief.Content, StringComparison.Ordinal);
    Assert.Contains("dotnet test", brief.Content, StringComparison.Ordinal);
    Assert.Contains("failed", brief.Content, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_trims_noisy_model_and_verification_evidence")]
    public async Task BuildTaskBriefTrimsNoisyModelAndVerificationEvidence()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Trim noisy brief evidence");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var modelOutput = $"model-start {new string('a', 1600)} model-tail";
    var stdout = $"stdout-start {new string('b', 1600)} stdout-tail";
    var stderr = $"stderr-start {new string('c', 1600)} stderr-tail";
    var runner = new AgentTaskRunner(
        kernel,
        agents,
        new InMemoryModelProviderRegistry([new FakeModelProvider("OpenAI", modelOutput)]),
        clock);

    await runner.RunAsync(goal.Id, task.Id);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, stdout, stderr, clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("model-start", brief, StringComparison.Ordinal);
    Assert.Contains("model-tail", brief, StringComparison.Ordinal);
    Assert.Contains("stdout-start", brief, StringComparison.Ordinal);
    Assert.Contains("stdout-tail", brief, StringComparison.Ordinal);
    Assert.Contains("stderr-start", brief, StringComparison.Ordinal);
    Assert.Contains("stderr-tail", brief, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains(new string('a', 1600), StringComparison.Ordinal));
    Assert.True(!brief.Contains(new string('b', 1600), StringComparison.Ordinal));
    Assert.True(!brief.Contains(new string('c', 1600), StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_skips_current_evidence_events_already_shown_in_sections")]
    public async Task BuildTaskBriefSkipsCurrentEvidenceEventsAlreadyShownInSections()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Avoid duplicate brief evidence");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var runner = new AgentTaskRunner(
        kernel,
        agents,
        new InMemoryModelProviderRegistry([new FakeModelProvider("OpenAI", "model-output-unique")]),
        clock);

    await runner.RunAsync(goal.Id, task.Id);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "stdout-unique", "stderr-unique", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("## Last Model Output", brief, StringComparison.Ordinal);
    Assert.Contains("model-output-unique", brief, StringComparison.Ordinal);
    Assert.Contains("## Last Verification", brief, StringComparison.Ordinal);
    Assert.Contains("stdout-unique", brief, StringComparison.Ordinal);
    Assert.Contains("stderr-unique", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("TaskOutputRecorded", StringComparison.Ordinal));
    Assert.True(!brief.Contains("TaskVerificationRecorded", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_skips_current_dispatch_event_already_shown_in_section")]
    public void BuildTaskBriefSkipsCurrentDispatchEventAlreadyShownInSection()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Avoid duplicate dispatch evidence");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", "C:\\repo", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("## Last Dispatch", brief, StringComparison.Ordinal);
    Assert.Contains("codex-cli", brief, StringComparison.Ordinal);
    Assert.Contains("codex exec prompt.md", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("TaskDispatchRecorded", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_uses_smaller_evidence_budget_for_simple_tasks")]
    public async Task BuildTaskBriefUsesSmallerEvidenceBudgetForSimpleTasks()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Keep simple evidence prompt budget small",
        [new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer)]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var modelOutput = $"simple-model-start {new string('a', 460)} simple-model-middle {new string('b', 260)} simple-model-tail";
    var stdout = $"simple-stdout-start {new string('c', 460)} simple-stdout-middle {new string('d', 260)} simple-stdout-tail";
    var stderr = $"simple-stderr-start {new string('e', 460)} simple-stderr-middle {new string('f', 260)} simple-stderr-tail";
    var runner = new AgentTaskRunner(
        kernel,
        agents,
        new InMemoryModelProviderRegistry([new FakeModelProvider("OpenAI", modelOutput)]),
        clock);

    await runner.RunAsync(goal.Id, task.Id);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, stdout, stderr, clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("simple-model-start", brief, StringComparison.Ordinal);
    Assert.Contains("simple-model-tail", brief, StringComparison.Ordinal);
    Assert.Contains("simple-stdout-start", brief, StringComparison.Ordinal);
    Assert.Contains("simple-stdout-tail", brief, StringComparison.Ordinal);
    Assert.Contains("simple-stderr-start", brief, StringComparison.Ordinal);
    Assert.Contains("simple-stderr-tail", brief, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("simple-model-middle", StringComparison.Ordinal));
    Assert.True(!brief.Contains("simple-stdout-middle", StringComparison.Ordinal));
    Assert.True(!brief.Contains("simple-stderr-middle", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_larger_evidence_budget_for_complex_tasks")]
    public async Task BuildTaskBriefKeepsLargerEvidenceBudgetForComplexTasks()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Keep enough evidence for complex work",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var modelOutput = $"complex-model-start {new string('a', 460)} complex-model-middle {new string('b', 260)} complex-model-tail";
    var stdout = $"complex-stdout-start {new string('c', 460)} complex-stdout-middle {new string('d', 260)} complex-stdout-tail";
    var stderr = $"complex-stderr-start {new string('e', 460)} complex-stderr-middle {new string('f', 260)} complex-stderr-tail";
    var runner = new AgentTaskRunner(
        kernel,
        agents,
        new InMemoryModelProviderRegistry([new FakeModelProvider("OpenAI", modelOutput)]),
        clock);

    await runner.RunAsync(goal.Id, task.Id);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, stdout, stderr, clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("complex-model-middle", brief, StringComparison.Ordinal);
    Assert.Contains("complex-stdout-middle", brief, StringComparison.Ordinal);
    Assert.Contains("complex-stderr-middle", brief, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_trims_noisy_timeline_messages")]
    public void BuildTaskBriefTrimsNoisyTimelineMessages()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Trim noisy timeline");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var noisyMessage = $"event-start {new string('x', 900)} event-tail";
    kernel.RetryTask(goal.Id, task.Id, noisyMessage);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("event-start", brief, StringComparison.Ordinal);
    Assert.Contains("event-tail", brief, StringComparison.Ordinal);
    var timeline = SectionFrom(brief, "## Recent Timeline");
    var timelineLine = timeline.Split(Environment.NewLine).Single(text =>
        text.Contains("TaskRetried", StringComparison.Ordinal) &&
        text.Contains("event-start", StringComparison.Ordinal));
    Assert.True(timelineLine.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!timelineLine.Contains(new string('x', 900), StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_uses_smaller_timeline_message_budget_for_simple_tasks")]
    public void BuildTaskBriefUsesSmallerTimelineMessageBudgetForSimpleTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep routine brief timeline entries small",
        [new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Planner)]);
    var task = goal.Tasks.Single();
    var note = $"simple-brief-event-start {new string('s', 110)} simple-brief-event-middle {new string('m', 50)} simple-brief-event-tail";
    kernel.RetryTask(goal.Id, task.Id, note);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("simple-brief-event-start", brief, StringComparison.Ordinal);
    Assert.Contains("simple-brief-event-tail", brief, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("simple-brief-event-middle", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_larger_timeline_message_budget_for_complex_tasks")]
    public void BuildTaskBriefKeepsLargerTimelineMessageBudgetForComplexTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep enough brief timeline detail for complex work",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var task = goal.Tasks.Single();
    var note = $"complex-brief-event-start {new string('c', 110)} complex-brief-event-middle {new string('m', 50)} complex-brief-event-tail";
    kernel.RetryTask(goal.Id, task.Id, note);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("complex-brief-event-start", brief, StringComparison.Ordinal);
    Assert.Contains("complex-brief-event-middle", brief, StringComparison.Ordinal);
    Assert.Contains("complex-brief-event-tail", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("[truncated", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_prefers_decision_timeline_events_over_lifecycle_noise")]
    public void BuildTaskBriefPrefersDecisionTimelineEventsOverLifecycleNoise()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Focus brief timeline");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "routine completed lifecycle noise");
    kernel.RetryTask(goal.Id, task.Id, "retry-critical-note");

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("retry-critical-note", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("routine completed lifecycle noise", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_omits_lifecycle_only_timeline_for_simple_tasks")]
    public void BuildTaskBriefOmitsLifecycleOnlyTimelineForSimpleTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep routine brief lifecycle noise out",
        [new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer)]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.True(!brief.Contains("GoalCreated", StringComparison.Ordinal));
    Assert.True(!brief.Contains("TaskDelegated", StringComparison.Ordinal));
    Assert.True(!brief.Contains("Goal created.", StringComparison.Ordinal));
    Assert.True(!brief.Contains("Delegated Developer task", StringComparison.Ordinal));
    Assert.True(!brief.Contains("## Recent Timeline", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_lifecycle_fallback_timeline_for_complex_tasks")]
    public void BuildTaskBriefKeepsLifecycleFallbackTimelineForComplexTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep lifecycle context for complex brief work",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("GoalCreated", brief, StringComparison.Ordinal);
    Assert.Contains("TaskDelegated", brief, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_uses_smaller_timeline_budget_for_simple_tasks")]
    public void BuildTaskBriefUsesSmallerTimelineBudgetForSimpleTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep routine prompt budget small",
        [new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    AddRetryNotes(kernel, goal.Id, task.Id, "simple-brief-note", 10);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    var timeline = SectionFrom(brief, "## Recent Timeline");
    Assert.True(!timeline.Contains("simple-brief-note-02", StringComparison.Ordinal));
    Assert.Contains("simple-brief-note-03", timeline, StringComparison.Ordinal);
    Assert.Contains("simple-brief-note-10", timeline, StringComparison.Ordinal);
    Assert.Contains("## Recent Timeline", brief, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_larger_timeline_budget_for_complex_tasks")]
    public void BuildTaskBriefKeepsLargerTimelineBudgetForComplexTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep enough context for complex work",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var task = goal.Tasks.Single();

    AddRetryNotes(kernel, goal.Id, task.Id, "complex-brief-note", 10);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("complex-brief-note-01", brief, StringComparison.Ordinal);
    Assert.Contains("complex-brief-note-10", brief, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_accumulated_retry_feedback_for_tester")]
    public void BuildTaskBriefIncludesAccumulatedRetryFeedbackForTester()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt regeneration.", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Test retry prompt regeneration.", AgentRole.Tester);
    var goal = kernel.CreateGoal("Fix retry prompt regeneration", [developer, tester]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    kernel.RetryTask(goal.Id, developer.Id, "stale duplicate retry feedback");
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "stale duplicate retry feedback");
    clock.Advance();
    kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Failed, "prior developer outcome for tester redispatch");
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "latest developer retry feedback");
    var latestRetryAt = clock.UtcNow;
    clock.Advance();
    kernel.RecordTaskNote(goal.Id, developer.Id, "operator recovery note for redispatch");

    var testerBrief = kernel.BuildTaskBrief(goal.Id, tester.Id).Content;
    var developerBrief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;

    Assert.Contains("## Accumulated retry/review feedback", testerBrief, StringComparison.Ordinal);
    Assert.Contains("Most recent retry: Retry 3 of 3", testerBrief, StringComparison.Ordinal);
    Assert.Contains(latestRetryAt.ToString("u"), testerBrief, StringComparison.Ordinal);
    Assert.Contains("Task 1 Developer", testerBrief, StringComparison.Ordinal);
    Assert.Contains("Prior outcome:", testerBrief, StringComparison.Ordinal);
    Assert.Contains("TaskFailed: prior developer outcome for tester redispatch", testerBrief, StringComparison.Ordinal);
    Assert.Contains("[still-open] Retry 3 of 3", testerBrief, StringComparison.Ordinal);
    Assert.Contains("latest developer retry feedback", testerBrief, StringComparison.Ordinal);
    Assert.Contains("operator recovery note for redispatch", testerBrief, StringComparison.Ordinal);
    Assert.Contains("[superseded] Retry 1 of 3", testerBrief, StringComparison.Ordinal);
    Assert.Contains("stale duplicate retry feedback", testerBrief, StringComparison.Ordinal);
    Assert.Equal(1, CountOccurrences(testerBrief, "## Accumulated retry/review feedback"));

    Assert.Contains("## Accumulated retry/review feedback", developerBrief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_round_three_developer_carries_all_prior_review_bounces_with_status")]
    public void BuildTaskBriefRoundThreeDeveloperCarriesAllPriorReviewBouncesWithStatus()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt aggregation.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Carry cross-round review feedback", [developer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    kernel.RetryTask(goal.Id, developer.Id, "review finding round one: preserve the first pivot");
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "review finding round two: add sibling tester direction");

    var brief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;

    Assert.Contains("## Accumulated retry/review feedback", brief, StringComparison.Ordinal);
    Assert.Contains("[still-open] Retry 2 of 2", brief, StringComparison.Ordinal);
    Assert.Contains("review finding round two: add sibling tester direction", brief, StringComparison.Ordinal);
    Assert.Contains("[superseded] Retry 1 of 2", brief, StringComparison.Ordinal);
    Assert.Contains("review finding round one: preserve the first pivot", brief, StringComparison.Ordinal);
    Assert.True(
        brief.IndexOf("review finding round two", StringComparison.Ordinal) <
        brief.IndexOf("review finding round one", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_accumulated_retry_feedback_ignores_automatic_task_notes_before_cap")]
    public void BuildTaskBriefAccumulatedRetryFeedbackIgnoresAutomaticTaskNotesBeforeCap()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt aggregation.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Keep automatic notes out of accumulated retry feedback", [developer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    kernel.RetryTask(goal.Id, developer.Id, "review finding round one: preserve the earlier pivot");
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "review finding round two: keep the latest direction");

    var automaticNotes = new[]
    {
        "CLASSIFIER rule=succeeded-dispatch-completion-evidence; verdict=VerifiedSuccess",
        "RESOURCE goal=12345678 task=abcdefgh cpu_ms=10 peak_mem_bytes=20 io_bytes=30 accounting_source=snapshot",
        "TaskOutputCommitted: sha=abcdef1; provenance=worker-result.",
        "Ignored stale dispatch execution evidence from 2026-07-18 20:00:00Z; latest retry was 2026-07-18 20:01:00Z.",
        "Ignored duplicate dispatch execution evidence for already settled dispatch: codex exec prompt.md",
        "Reconciled failed dispatch verification to Completed from structured WORKER_RESULT evidence and commit provenance.",
        "Auto-cleared stale LastProcess.IsRunning before dispatch; pid 42 had exit artifact exit.txt with exit 0.",
        "StaleDispatchAutoRequeued: stale dispatch auto-requeue receipt",
        "StaleDispatchAutoRequeueCapExhausted: stale dispatch auto-requeue cap receipt",
        "CLASSIFIER rule=committed-worker-result-evidence; verdict=VerifiedSuccess"
    };
    foreach (var note in automaticNotes)
    {
        clock.Advance();
        kernel.RecordTaskNote(goal.Id, developer.Id, note);
    }

    var brief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;
    var feedback = SectionFrom(brief, "## Accumulated retry/review feedback");

    Assert.Contains("review finding round two: keep the latest direction", feedback, StringComparison.Ordinal);
    Assert.Contains("review finding round one: preserve the earlier pivot", feedback, StringComparison.Ordinal);
    Assert.DoesNotContain("CLASSIFIER rule=", feedback, StringComparison.Ordinal);
    Assert.DoesNotContain("RESOURCE goal=", feedback, StringComparison.Ordinal);
    Assert.DoesNotContain("TaskOutputCommitted:", feedback, StringComparison.Ordinal);
    Assert.DoesNotContain("StaleDispatchAutoRequeued:", feedback, StringComparison.Ordinal);
    Assert.DoesNotContain("Omitted", feedback, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_accumulated_retry_feedback_marks_resolved_and_truncates_oldest")]
    public void BuildTaskBriefAccumulatedRetryFeedbackMarksResolvedAndTruncatesOldest()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement bounded retry prompt aggregation.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Bound accumulated retry feedback", [developer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    kernel.RetryTask(goal.Id, developer.Id, "oldest retry feedback should be truncated");
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "finding alpha needs aggregation");
    clock.Advance();
    kernel.RecordTaskNote(goal.Id, developer.Id, "resolved finding alpha needs aggregation in the next round verdict");
    for (var index = 3; index <= 8; index++)
    {
        clock.Advance();
        kernel.RetryTask(goal.Id, developer.Id, $"bounded retry feedback {index}");
    }

    var brief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;

    Assert.Contains("- Omitted 1 oldest retry/review feedback entry to preserve prompt budget.", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("oldest retry feedback should be truncated", StringComparison.Ordinal));
    Assert.Contains("[resolved-in-round-2] Retry 2 of 8", brief, StringComparison.Ordinal);
    Assert.Contains("finding alpha needs aggregation", brief, StringComparison.Ordinal);
    Assert.Contains("[still-open] Retry 8 of 8", brief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_tester_receives_operator_pivot_after_developer_retry")]
    public void BuildTaskBriefTesterReceivesOperatorPivotAfterDeveloperRetry()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement the original pinned behavior.", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Verify the current behavior, not stale pins.", AgentRole.Tester);
    var goal = kernel.CreateGoal("Pin the 2a891e56 stale-tester scenario", [developer, tester]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    kernel.RetryTask(goal.Id, developer.Id, "Operator pivot: drop the original pin and verify the latest retry direction.");

    var testerBrief = kernel.BuildTaskBrief(goal.Id, tester.Id).Content;

    Assert.Contains("## Accumulated retry/review feedback", testerBrief, StringComparison.Ordinal);
    Assert.Contains("[still-open] Retry 1 of 1", testerBrief, StringComparison.Ordinal);
    Assert.Contains("Operator pivot: drop the original pin and verify the latest retry direction.", testerBrief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_successful_verification_does_not_resolve_unreferenced_retry_feedback")]
    public void BuildTaskBriefSuccessfulVerificationDoesNotResolveUnreferencedRetryFeedback()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement the pivoted behavior.", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Verify the current retry direction.", AgentRole.Tester);
    var goal = kernel.CreateGoal("Keep pivot open until downstream verifies it", [developer, tester]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    kernel.RetryTask(goal.Id, developer.Id, "Operator pivot: verify the new branch behavior, not the original pin.");
    clock.Advance();
    kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
        "dotnet test --filter Pivot",
        "C:\\repo",
        0,
        "Passed PivotTests",
        string.Empty,
        clock.UtcNow));

    var testerBrief = kernel.BuildTaskBrief(goal.Id, tester.Id).Content;

    Assert.Contains("[still-open] Retry 1 of 1", testerBrief, StringComparison.Ordinal);
    Assert.Contains("Operator pivot: verify the new branch behavior, not the original pin.", testerBrief, StringComparison.Ordinal);
    Assert.True(!testerBrief.Contains("[resolved-in-round-1] Retry 1 of 1", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_omits_downstream_invalidation_retries_from_accumulated_feedback")]
    public void BuildTaskBriefOmitsDownstreamInvalidationRetriesFromAccumulatedFeedback()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt aggregation.", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Test retry prompt aggregation.", AgentRole.Tester);
    var reviewer = new TaskSpec(TaskId.New(), "Review retry prompt aggregation.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Keep invalidation noise out of feedback", [developer, tester, reviewer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    for (var index = 1; index <= 5; index++)
    {
        kernel.RecordTaskVerification(goal.Id, tester.Id, new TaskVerificationRecord(
            $"dotnet test --filter InvalidationNoise{index}",
            "C:\\repo",
            0,
            $"Passed tester invalidation setup {index}",
            string.Empty,
            clock.UtcNow));
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
            $"review --round {index}",
            "C:\\repo",
            0,
            $"Passed reviewer invalidation setup {index}",
            string.Empty,
            clock.UtcNow));
        clock.Advance();
        kernel.RetryTask(goal.Id, developer.Id, $"real review finding {index:00}");
        clock.Advance();
    }

    var developerBrief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;
    var feedback = SectionFrom(developerBrief, "## Accumulated retry/review feedback");

    Assert.Contains("Most recent retry: Retry 5 of 5", feedback, StringComparison.Ordinal);
    Assert.Contains("real review finding 01", feedback, StringComparison.Ordinal);
    Assert.Contains("real review finding 05", feedback, StringComparison.Ordinal);
    Assert.True(!feedback.Contains("Invalidated Tester task", StringComparison.Ordinal));
    Assert.True(!feedback.Contains("Invalidated Reviewer task", StringComparison.Ordinal));
    Assert.True(!feedback.Contains("Omitted", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_acceptance_retry_includes_structured_failure_receipt")]
    public void BuildTaskBriefAcceptanceRetryIncludesStructuredFailureReceipt()
{
    var workingDirectory = Path.Combine(Path.GetTempPath(), "mcg-taskbrief-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(workingDirectory, ".orchestrator", "goal-operations"));
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Fix acceptance failure.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Recover failed acceptance", [developer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var acceptanceFailedAt = clock.UtcNow;
    var journalPath = Path.Combine(workingDirectory, ".orchestrator", "goal-operations", $"{goal.Id.Value}.jsonl");
    File.WriteAllText(journalPath,
        $$"""
        {"idempotencyKey":"{{goal.Id.Value}}:conductor:acceptance","goalId":{"value":"{{goal.Id.Value}}"},"operation":"conductor:acceptance","status":"Failed","at":"{{acceptanceFailedAt:O}}","detail":"Acceptance failed (exit 1). Acceptance output tail: Failed Tests:\n  ReceiptTests.AcceptanceTailPinsNames\ncompiler error CS1002: ; expected\nstdout: C:\\repo\\.orchestrator\\logs\\acceptance.out.log\nstderr: C:\\repo\\.orchestrator\\logs\\acceptance.err.log"}
        """ + Environment.NewLine);
    clock.Advance();
    kernel.RecordAcceptanceFailure(goal.Id, ["test tamper guard: 1 test degradation signal(s)"]);
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "Fix the acceptance failure.");

    var brief = kernel.BuildTaskBrief(goal.Id, developer.Id, workingDirectory: workingDirectory).Content;

    Assert.Contains("## ACCEPTANCE FAILURE - FIX FIRST", brief, StringComparison.Ordinal);
    Assert.Contains("Structured failure receipt (bounded):", brief, StringComparison.Ordinal);
    Assert.Contains("test tamper guard: 1 test degradation signal(s)", brief, StringComparison.Ordinal);
    Assert.Contains("Acceptance operation: conductor:acceptance", brief, StringComparison.Ordinal);
    Assert.Contains("Acceptance failed (exit 1)", brief, StringComparison.Ordinal);
    Assert.Contains("ReceiptTests.AcceptanceTailPinsNames", brief, StringComparison.Ordinal);
    Assert.Contains("compiler error CS1002", brief, StringComparison.Ordinal);
    Assert.Contains(journalPath, brief, StringComparison.Ordinal);
    Assert.Contains("C:\\repo\\.orchestrator\\logs\\acceptance.out.log", brief, StringComparison.Ordinal);
    Assert.Contains("C:\\repo\\.orchestrator\\logs\\acceptance.err.log", brief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_acceptance_retry_renders_clean_baseline_attribution")]
    public void BuildTaskBriefAcceptanceRetryRendersCleanBaselineAttribution()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Fix acceptance failure.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Recover attributed acceptance failure", [developer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    kernel.RecordAcceptanceFailure(
        goal.Id,
        ["inherited check", "introduced check", "unknown check"],
        "branch123456",
        "main123456",
        [
            new AcceptanceCheckAttribution("inherited check", AcceptanceFailureOrigin.Inherited, "also failed for goal deadbeef at main main1234"),
            new AcceptanceCheckAttribution("introduced check", AcceptanceFailureOrigin.Introduced, "main main1234 is attested green"),
            new AcceptanceCheckAttribution("unknown check", AcceptanceFailureOrigin.Unattributed, "no baseline evidence at main main1234")
        ],
        "attested-red; identical check failed across two goals");
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "Fix attributable failures.");

    var brief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;

    Assert.Contains("Clean-test baseline: main main1234 attested-red", brief, StringComparison.Ordinal);
    Assert.Contains("- inherited check [inherited:", brief, StringComparison.Ordinal);
    Assert.Contains("- introduced check [introduced:", brief, StringComparison.Ordinal);
    Assert.Contains("- unknown check [unattributed:", brief, StringComparison.Ordinal);
    Assert.DoesNotContain("Do NOT attempt to fix these", brief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_operator_retry_includes_last_failed_verification_receipt")]
    public void BuildTaskBriefOperatorRetryIncludesLastFailedVerificationReceipt()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt regeneration.", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Test retry prompt regeneration.", AgentRole.Tester);
    var goal = kernel.CreateGoal("Fix retry prompt regeneration", [developer, tester]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
        "dotnet test --filter RetryReceipt",
        "C:\\repo",
        1,
        "Failed Mcg.AgentOrchestrator.Core.Tests.RetryReceiptTests.OperatorRetryIncludesReceipt",
        "Assert.Contains() Failure",
        clock.UtcNow,
        StandardOutputPath: "C:\\repo\\.orchestrator\\logs\\developer.out.log",
        StandardErrorPath: "C:\\repo\\.orchestrator\\logs\\developer.err.log"));
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "Please fix the retry receipt.");

    var testerBrief = kernel.BuildTaskBrief(goal.Id, tester.Id).Content;

    Assert.Contains("## Accumulated retry/review feedback", testerBrief, StringComparison.Ordinal);
    Assert.Contains("Structured failure receipt (bounded):", testerBrief, StringComparison.Ordinal);
    Assert.Contains("TaskVerificationRecorded: Verification failed (1): dotnet test --filter RetryReceipt", testerBrief, StringComparison.Ordinal);
    Assert.Contains("Verification command: dotnet test --filter RetryReceipt", testerBrief, StringComparison.Ordinal);
    Assert.Contains("Verification exit code: 1", testerBrief, StringComparison.Ordinal);
    Assert.Contains("RetryReceiptTests.OperatorRetryIncludesReceipt", testerBrief, StringComparison.Ordinal);
    Assert.Contains("C:\\repo\\.orchestrator\\logs\\developer.out.log", testerBrief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_failure_receipt_is_capped_and_tail_preferred")]
    public void BuildTaskBriefFailureReceiptIsCappedAndTailPreferred()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Fix noisy acceptance failure.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Recover noisy failed acceptance", [developer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var stdout = $"stdout-start {new string('s', 3000)} stdout-tail failing TestName";
    kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
        "acceptance",
        "C:\\repo",
        1,
        stdout,
        $"stderr-start {new string('e', 1200)} stderr-tail exit 1",
        clock.UtcNow,
        StandardOutputPath: "C:\\repo\\.orchestrator\\logs\\acceptance.out.log"));
    clock.Advance();
    kernel.RecordAcceptanceFailure(goal.Id, ["acceptance check"]);
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "Retry with receipt.");

    var brief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;
    var receiptStart = brief.IndexOf("Structured failure receipt (bounded):", StringComparison.Ordinal);
    var receiptEnd = brief.IndexOf("<!-- ACCEPTANCE_FAILURE_END -->", receiptStart, StringComparison.Ordinal);
    var receipt = brief[receiptStart..receiptEnd].Trim();

    Assert.True(receipt.Length <= 2000);
    Assert.Contains("...[truncated", receipt, StringComparison.Ordinal);
    Assert.Contains("chars before failure tail]...", receipt, StringComparison.Ordinal);
    Assert.Contains("stdout-tail failing TestName", receipt, StringComparison.Ordinal);
    Assert.Contains("stderr-tail exit 1", receipt, StringComparison.Ordinal);
    Assert.True(!receipt.Contains(new string('s', 3000), StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_accumulated_retry_feedback_for_reviewer")]
    public void BuildTaskBriefIncludesAccumulatedRetryFeedbackForReviewer()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt regeneration.", AgentRole.Developer);
    var reviewer = new TaskSpec(TaskId.New(), "Review retry prompt regeneration.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Fix retry prompt regeneration", [developer, reviewer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    kernel.RetryTask(goal.Id, developer.Id, "first stale retry feedback");
    clock.Advance();
    kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Failed, "prior developer outcome for reviewer redispatch");
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "latest developer retry feedback for review");
    var latestRetryAt = clock.UtcNow;
    clock.Advance();
    kernel.RecordTaskNote(goal.Id, developer.Id, "operator recovery note for reviewer redispatch");
    clock.Advance();
    kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "Reviewer requested proof that the retry prompt pin changed.");
    clock.Advance();
    kernel.RecordReviewerEvidenceRunRecorded(goal.Id, reviewer.Id, "Evidence run recorded: prompt contains latest developer retry feedback for review.");

    var reviewerBrief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;
    var developerBrief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;

    Assert.Contains("## Accumulated retry/review feedback", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("Most recent retry: Retry 2 of 2", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains(latestRetryAt.ToString("u"), reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("Task 1 Developer", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("Prior outcome:", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("TaskFailed: prior developer outcome for reviewer redispatch", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("latest developer retry feedback for review", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("operator recovery note for reviewer redispatch", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("ReviewerEvidenceRequestReceived: Reviewer requested proof", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("ReviewerEvidenceRunRecorded: Evidence run recorded", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("[superseded] Retry 1 of 2", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("first stale retry feedback", reviewerBrief, StringComparison.Ordinal);
    Assert.Equal(1, CountOccurrences(reviewerBrief, "## Accumulated retry/review feedback"));
    Assert.Contains("## Accumulated retry/review feedback", developerBrief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_reviewer_includes_executed_test_evidence_with_provenance")]
    public void BuildTaskBriefReviewerIncludesExecutedTestEvidenceWithProvenance()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement receipt propagation.", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Verify receipt propagation.", AgentRole.Tester);
    var reviewer = new TaskSpec(TaskId.New(), "Review receipt propagation.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Attach executed evidence", [developer, tester, reviewer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec developer-prompt.md",
        "C:\\repo",
        clock.UtcNow,
        BaseCommit: "base-dev",
        ResultCommit: "dev-result-sha"));
    kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
        "dotnet test --filter TaskBriefReceipt",
        "C:\\repo",
        0,
        "WORKER_RESULT:\nfiles: src/Foo.cs\ncommands: dotnet test --filter TaskBriefReceipt\ntests: pass - TaskBriefReceipt 3/3; trx developer.trx\ncommit: dev-worker-sha\nblockers: none\nEND_WORKER_RESULT",
        string.Empty,
        clock.UtcNow,
        StandardOutputPath: "C:\\repo\\.orchestrator\\logs\\developer.out.log",
        StandardErrorPath: "C:\\repo\\.orchestrator\\logs\\developer.err.log",
        WorkerResultPresent: true));
    clock.Advance();
    kernel.RecordTaskVerification(goal.Id, tester.Id, new TaskVerificationRecord(
        "manual verification: inspected trx receipts",
        "C:\\repo",
        0,
        "manual verification passed: operator checked tester.trx with Passed=7 Failed=0",
        string.Empty,
        clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;

    Assert.Contains("## Executed Test Evidence", brief, StringComparison.Ordinal);
    Assert.Contains("Reviewer is read-only", brief, StringComparison.Ordinal);
    Assert.Contains("provenance: Task 1 Developer verification", brief, StringComparison.Ordinal);
    Assert.Contains("result: pass (exit 0)", brief, StringComparison.Ordinal);
    Assert.Contains("dotnet test --filter TaskBriefReceipt @ C:\\repo", brief, StringComparison.Ordinal);
    Assert.Contains("freshness: verified commit dev-worker-sha", brief, StringComparison.Ordinal);
    Assert.Contains("artifacts: stdout C:\\repo\\.orchestrator\\logs\\developer.out.log, stderr C:\\repo\\.orchestrator\\logs\\developer.err.log", brief, StringComparison.Ordinal);
    Assert.Contains("provenance: Task 1 Developer WORKER_RESULT tests", brief, StringComparison.Ordinal);
    Assert.Contains("tests: pass - TaskBriefReceipt 3/3; trx developer.trx", brief, StringComparison.Ordinal);
    Assert.Contains("provenance: Task 2 Tester verification", brief, StringComparison.Ordinal);
    Assert.Contains("manual verification: inspected trx receipts @ C:\\repo", brief, StringComparison.Ordinal);
    Assert.Contains("evidence: stdout manual verification passed: operator checked tester.trx with Passed=7 Failed=0", brief, StringComparison.Ordinal);
    Assert.Contains("freshness: unknown commit", brief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_reviewer_without_receipts_says_no_executed_test_evidence_exists")]
    public void BuildTaskBriefReviewerWithoutReceiptsSaysNoExecutedTestEvidenceExists()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var developer = new TaskSpec(TaskId.New(), "Implement receipt propagation.", AgentRole.Developer);
    var reviewer = new TaskSpec(TaskId.New(), "Review receipt propagation.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Attach executed evidence", [developer, reviewer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    var brief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;

    Assert.Contains("## Executed Test Evidence", brief, StringComparison.Ordinal);
    Assert.Contains("No executed test evidence exists for this goal yet.", brief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_reviewer_includes_current_head_pre_review_receipt_before_start")]
    public void BuildTaskBriefReviewerIncludesCurrentHeadPreReviewReceiptBeforeStart()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var reviewer = new TaskSpec(TaskId.New(), "Review current candidate.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Gate review on focused evidence", [reviewer]);
        kernel.RecordPreReviewEvidence(
            goal.Id,
            reviewer.Id,
            new PreReviewEvidenceReceipt(
                goal.Id.Value,
                2,
                "candidate123",
                ["dotnet test --filter FullyQualifiedName~ConductorDriverTests"],
                PreReviewEvidenceDisposition.Green,
                1,
                0,
                [
                    new PreReviewEvidenceCheckReceipt(
                        "focused ConductorDriverTests",
                        "dotnet test --filter FullyQualifiedName~ConductorDriverTests",
                        true,
                        0,
                        "C:\\receipts\\green")
                ],
                [],
                "Orchestration source mapped to its focused test class.",
                "C:\\receipts\\green\\result.trx",
                DateTimeOffset.UtcNow));

        var brief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;

        Assert.Contains("## Current-HEAD Pre-Review Evidence", brief, StringComparison.Ordinal);
        Assert.Contains("reviewer_round=2", brief, StringComparison.Ordinal);
        Assert.Contains("candidate_sha=candidate123", brief, StringComparison.Ordinal);
        Assert.Contains("C:\\receipts\\green\\result.trx", brief, StringComparison.Ordinal);
        Assert.True(
            brief.IndexOf("## Current-HEAD Pre-Review Evidence", StringComparison.Ordinal) <
            brief.IndexOf("## Executed Test Evidence", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BuildTaskBrief_retry_caps_operational_noise_without_dropping_structured_findings")]
    public void BuildTaskBriefRetryCapsOperationalNoiseWithoutDroppingStructuredFindings()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var developer = new TaskSpec(TaskId.New(), "Repair findings.", AgentRole.Developer);
        var reviewer = new TaskSpec(TaskId.New(), "Review repairs.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Preserve every actionable finding", [developer, reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var findings = Enumerable.Range(0, 14)
            .Select(index => new ReviewFinding(
                $"finding-{index:D2}",
                ReviewFindingState.Open,
                new ReviewFindingLocation($"src/{index:D2}.cs", $"Method{index:D2}"),
                $"Distinct actionable finding {index:D2}."))
            .ToArray();
        kernel.RecordTaskVerification(
            goal.Id,
            reviewer.Id,
            new TaskVerificationRecord(
                "review",
                "C:\\repo",
                0,
                "structured review",
                "",
                DateTimeOffset.UtcNow,
                MergedReviewFindings: findings));
        for (var index = 0; index < 12; index++)
        {
            kernel.RecordTaskNote(goal.Id, developer.Id, $"operational retry noise {index:D2} {new string('x', 300)}");
        }

        kernel.RetryTask(goal.Id, developer.Id, "retry after structured review");

        var brief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;

        Assert.Contains("Structured actionable findings (not subject to operational retry caps)", brief, StringComparison.Ordinal);
        Assert.Contains("finding-00", brief, StringComparison.Ordinal);
        Assert.Contains("Distinct actionable finding 00.", brief, StringComparison.Ordinal);
        Assert.Contains("finding-13", brief, StringComparison.Ordinal);
        Assert.Contains("Distinct actionable finding 13.", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("structured_finding_overflow", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BuildTaskBrief_reviewer_executed_test_evidence_is_newest_first_and_capped")]
    public void BuildTaskBriefReviewerExecutedTestEvidenceIsNewestFirstAndCapped()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement receipt propagation.", AgentRole.Developer);
    var reviewer = new TaskSpec(TaskId.New(), "Review receipt propagation.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Attach executed evidence", [developer, reviewer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    for (var index = 1; index <= 14; index++)
    {
        kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
            $"dotnet test --filter Receipt{index:00}",
            "C:\\repo",
            0,
            $"receipt-{index:00}",
            string.Empty,
            clock.UtcNow));
        clock.Advance();
    }

    var brief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;
    var evidenceStart = brief.IndexOf("## Executed Test Evidence", StringComparison.Ordinal);
    var evidenceEnd = brief.IndexOf("## Prior Task Evidence", evidenceStart, StringComparison.Ordinal);
    var evidence = evidenceEnd < 0 ? brief[evidenceStart..] : brief[evidenceStart..evidenceEnd];

    Assert.Contains("Receipt14", evidence, StringComparison.Ordinal);
    Assert.Contains("Receipt03", evidence, StringComparison.Ordinal);
    Assert.True(!evidence.Contains("Receipt02", StringComparison.Ordinal));
    Assert.True(!evidence.Contains("Receipt01", StringComparison.Ordinal));
    Assert.True(evidence.IndexOf("Receipt14", StringComparison.Ordinal) < evidence.IndexOf("Receipt13", StringComparison.Ordinal));
    Assert.Equal(12, evidence.Split(Environment.NewLine).Count(line => line.Contains("provenance: Task 1 Developer verification", StringComparison.Ordinal)));
    Assert.Contains("Omitted 2 older executed-test evidence line(s).", evidence, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_trims_noisy_verification_plan")]
    public void BuildTaskBriefTrimsNoisyVerificationPlan()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Trim noisy verification plan");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var plan = $"plan-start {new string('p', 460)} plan-middle {new string('q', 260)} plan-tail";
    kernel.SetTaskVerificationPlan(goal.Id, task.Id, plan);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("plan-start", brief, StringComparison.Ordinal);
    Assert.Contains("plan-tail", brief, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("plan-middle", StringComparison.Ordinal));
    Assert.Equal(plan, task.VerificationPlan);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_larger_verification_plan_budget_for_complex_tasks")]
    public void BuildTaskBriefKeepsLargerVerificationPlanBudgetForComplexTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep complex verification detail",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var task = goal.Tasks.Single();
    var plan = $"complex-plan-start {new string('p', 460)} complex-plan-middle {new string('q', 260)} complex-plan-tail";
    kernel.SetTaskVerificationPlan(goal.Id, task.Id, plan);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("complex-plan-start", brief, StringComparison.Ordinal);
    Assert.Contains("complex-plan-middle", brief, StringComparison.Ordinal);
    Assert.Contains("complex-plan-tail", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("[truncated", StringComparison.Ordinal));
    Assert.Equal(plan, task.VerificationPlan);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_trims_noisy_dispatch_command")]
    public void BuildTaskBriefTrimsNoisyDispatchCommand()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Trim noisy dispatch command");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var command = $"cmd-start {new string('d', 1600)} cmd-tail";
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("custom", command, "C:\\repo", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("cmd-start", brief, StringComparison.Ordinal);
    Assert.Contains("cmd-tail", brief, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains(new string('d', 1600), StringComparison.Ordinal));
    Assert.Equal(command, task.LastDispatch!.Command);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_trims_noisy_pending_human_input")]
    public void BuildTaskBriefTrimsNoisyPendingHumanInput()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Trim noisy pending input");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var question = $"question-start {new string('h', 1600)} question-tail";
    var request = kernel.RequestHumanInput(goal.Id, task.Id, question);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("question-start", brief, StringComparison.Ordinal);
    Assert.Contains("question-tail", brief, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains(new string('h', 1600), StringComparison.Ordinal));
    Assert.Equal(question, kernel.GetHumanInputRequest(request.Id).Question);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_prior_task_evidence_for_completed_earlier_task")]
    public void BuildTaskBriefIncludesPriorTaskEvidenceForCompletedEarlierTask()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var task1Spec = new TaskSpec(TaskId.New(), "Plan the implementation", AgentRole.Planner);
    var task2Spec = new TaskSpec(TaskId.New(), "Implement the plan", AgentRole.Developer);
    var goal = kernel.CreateGoal("Build a feature", [task1Spec, task2Spec]);
    var task1 = goal.Tasks[0];
    kernel.RecordTaskVerification(goal.Id, task1.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "prior-stdout-evidence", "", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task2Spec.Id).Content;

    Assert.Contains("## Prior Task Evidence", brief, StringComparison.Ordinal);
    Assert.Contains("Planner", brief, StringComparison.Ordinal);
    Assert.Contains("prior-stdout-evidence", brief, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_omits_prior_task_evidence_for_single_task_goal")]
    public void BuildTaskBriefOmitsPriorTaskEvidenceForSingleTaskGoal()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Single task goal", [new TaskSpec(TaskId.New(), "Do the work", AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.True(!brief.Contains("## Prior Task Evidence", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "AgentTaskRunner_includes_prior_task_evidence_in_api_run_prompt")]
    public async Task AgentTaskRunnerIncludesPriorTaskEvidenceInApiRunPrompt()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var task1Spec = new TaskSpec(TaskId.New(), "Plan the implementation", AgentRole.Planner);
    var task2Spec = new TaskSpec(TaskId.New(), "Implement the plan", AgentRole.Developer);
    var goal = kernel.CreateGoal("Build a feature", [task1Spec, task2Spec]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    kernel.RecordTaskVerification(goal.Id, task1Spec.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "prior-task-verification-stdout", "", clock.UtcNow));
    var provider = new FakeModelProvider("OpenAI", "task 2 complete");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task2Spec.Id);

    Assert.True(provider.LastRequest is not null);
    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("prior-task-verification-stdout", prompt, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_collapses_prior_evidence_to_digest_pointer_for_file_context")]
    public void BuildTaskBriefCollapsesPriorEvidenceToDigestPointerForFileContext()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var priorTask = new TaskSpec(TaskId.New(), "Plan the implementation", AgentRole.Planner);
    var currentTask = new TaskSpec(TaskId.New(), "Implement the plan", AgentRole.Developer);
    var goal = kernel.CreateGoal("Build a feature", [priorTask, currentTask]);
    var noisyEvidence = $"prior-evidence-head {new string('x', 1800)} prior-evidence-tail";
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, noisyEvidence, "", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(
        goal.Id,
        currentTask.Id,
        workingDirectory: "C:\\repo",
        contextDirectory: "C:\\repo\\.orchestrator-context\\goal").Content;

    Assert.Contains("digest.md", brief, StringComparison.Ordinal);
    Assert.Contains("prior-task-summaries.md", brief, StringComparison.Ordinal);
    Assert.Contains("Read prior-task-summaries.md first", brief, StringComparison.Ordinal);
    Assert.Contains("prior-task-evidence.md", brief, StringComparison.Ordinal);
    Assert.True(brief.IndexOf("prior-task-summaries.md", StringComparison.Ordinal) < brief.IndexOf("prior-task-evidence.md", StringComparison.Ordinal));
    Assert.Contains("verification exit 0", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("prior-evidence-head", StringComparison.Ordinal));
    Assert.True(!brief.Contains("prior-evidence-tail", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_prior_evidence_inline_for_api_no_file_briefs")]
    public void BuildTaskBriefKeepsPriorEvidenceInlineForApiNoFileBriefs()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var priorTask = new TaskSpec(TaskId.New(), "Plan the implementation", AgentRole.Planner);
    var currentTask = new TaskSpec(TaskId.New(), "Implement the plan", AgentRole.Developer);
    var goal = kernel.CreateGoal("Build a feature", [priorTask, currentTask]);
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "api-inline-prior-evidence", "", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, currentTask.Id).Content;

    Assert.Contains("api-inline-prior-evidence", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("Read digest.md first", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_budgets_late_pipeline_file_context_without_dropping_api_evidence")]
    public void BuildTaskBriefBudgetsLatePipelineFileContextWithoutDroppingApiEvidence()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var priorPlanner = new TaskSpec(TaskId.New(), "Plan the implementation sequence.", AgentRole.Planner);
    var priorResearcher = new TaskSpec(TaskId.New(), "Inspect the prompt construction path.", AgentRole.Researcher);
    var priorTester = new TaskSpec(TaskId.New(), "Verify previous prompt behavior.", AgentRole.Tester);
    var priorReviewer = new TaskSpec(TaskId.New(), "Review prompt evidence retention.", AgentRole.Reviewer);
    var currentTask = new TaskSpec(
        TaskId.New(),
        $"Design and implement production prompt budget controls. task-start {new string('t', 1800)} task-tail",
        AgentRole.Developer,
        $"verification-plan-start {new string('v', 1400)} verification-plan-tail");
    var goal = kernel.CreateGoal(
        $"Reduce subscription prompt bloat for late pipeline work. goal-start {new string('g', 1800)} goal-tail",
        [priorPlanner, priorResearcher, priorTester, priorReviewer, currentTask]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    CompleteWithEvidence(kernel, goal.Id, priorPlanner.Id, "planner evidence");
    CompleteWithEvidence(kernel, goal.Id, priorResearcher.Id, "researcher evidence");
    CompleteWithEvidence(kernel, goal.Id, priorTester.Id, "tester evidence");
    CompleteWithEvidence(kernel, goal.Id, priorReviewer.Id, $"api-inline-evidence-token {new string('p', 1600)} reviewer evidence tail");
    kernel.RecordTaskDispatch(goal.Id, currentTask.Id, new TaskDispatchRecord(
        "codex-cli",
        $"codex exec prompt-start {new string('c', 1800)} prompt-tail",
        "C:\\repo",
        clock.UtcNow));
    kernel.RecordTaskVerification(goal.Id, currentTask.Id, new TaskVerificationRecord(
        "dotnet test",
        "C:\\repo",
        1,
        $"current-verification-stdout-start {new string('s', 1800)} current-verification-stdout-tail",
        $"current-verification-stderr-start {new string('e', 1800)} current-verification-stderr-tail",
        clock.UtcNow));
    for (var index = 1; index <= 24; index++)
    {
        kernel.RecordTaskNote(goal.Id, currentTask.Id, $"timeline-overflow-note-{index:00} {new string('n', 420)}");
    }

    var fileAccessBrief = kernel.BuildTaskBrief(
        goal.Id,
        currentTask.Id,
        workingDirectory: "C:\\repo",
        contextDirectory: "C:\\repo\\.orchestrator-context\\goal").Content;
    var apiBrief = kernel.BuildTaskBrief(goal.Id, currentTask.Id).Content;

    Assert.True(fileAccessBrief.Length <= 9000);
    Assert.Contains("current-task.md in the context directory", fileAccessBrief, StringComparison.Ordinal);
    Assert.Contains("inline verification output collapsed", fileAccessBrief, StringComparison.Ordinal);
    Assert.Contains("prior-task-summaries.md", fileAccessBrief, StringComparison.Ordinal);
    Assert.True(!fileAccessBrief.Contains("api-inline-evidence-token", StringComparison.Ordinal));
    Assert.Contains("api-inline-evidence-token", apiBrief, StringComparison.Ordinal);
    Assert.Contains("reviewer evidence tail", apiBrief, StringComparison.Ordinal);
    Assert.True(apiBrief.Length > fileAccessBrief.Length);
}

    [Xunit.Fact(DisplayName = "SdlcRoleRequirements_researcher_brief_includes_no_modify_repository_line")]
    public void SdlcRoleRequirementsResearcherBriefIncludesNoModifyRepositoryLine()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var researcherGoal = kernel.CreateGoal("Inspect configuration behavior", [new TaskSpec(TaskId.New(), "Survey configuration modules and report patterns.", AgentRole.Researcher)]);
    var researcherTask = researcherGoal.Tasks.Single();

    var researcherBrief = kernel.BuildTaskBrief(researcherGoal.Id, researcherTask.Id).Content;

    Assert.Contains("Do not modify repository files", researcherBrief, StringComparison.Ordinal);
    Assert.Contains("implementation belongs to the Developer task", researcherBrief, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "SdlcRoleRequirements_developer_brief_does_not_include_no_modify_repository_line")]
    public void SdlcRoleRequirementsDeveloperBriefDoesNotIncludeNoModifyRepositoryLine()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var developerGoal = kernel.CreateGoal("Fix a bug", [new TaskSpec(TaskId.New(), "Fix the null reference in the parser.", AgentRole.Developer)]);
    var developerTask = developerGoal.Tasks.Single();

    var developerBrief = kernel.BuildTaskBrief(developerGoal.Id, developerTask.Id).Content;

    Assert.True(!developerBrief.Contains("Do not modify repository files", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_worker_result_block_fields_and_no_self_commit_instruction")]
    public void BuildTaskBriefIncludesWorkerResultBlockFieldsAndNoSelfCommitInstruction()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Ship a feature", [new TaskSpec(TaskId.New(), "Implement the feature and write tests.", AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("WORKER_RESULT:", brief, StringComparison.Ordinal);
    Assert.Contains("files:", brief, StringComparison.Ordinal);
    Assert.Contains("commit:", brief, StringComparison.Ordinal);
    Assert.Contains("model_fit:", brief, StringComparison.Ordinal);
    Assert.Contains("END_WORKER_RESULT", brief, StringComparison.Ordinal);
    Assert.Contains("Do not stage or commit changes; the orchestrator commits verified Developer/Tester diffs.", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("Git commit all changes in the working directory before reporting results.", StringComparison.Ordinal));
    Assert.True(!brief.Contains("Final: WORKER_RESULT.", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_worker_result_block_matches_shared_template")]
    public void BuildTaskBriefWorkerResultBlockMatchesSharedTemplate()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Verify shared template", [new TaskSpec(TaskId.New(), "Apply the shared output contract.", AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    foreach (var templateLine in AgentOutputDirectives.WorkerResultTemplateLines)
    {
        Assert.Contains(templateLine, brief, StringComparison.Ordinal);
    }
}

    [Xunit.Theory(DisplayName = "BuildTaskBrief_role_worker_result_contract_contains_guardrail_fields")]
    [Xunit.InlineData(AgentRole.Researcher)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void BuildTaskBriefRoleWorkerResultContractContainsGuardrailFields(AgentRole role)
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Verify role output contract", [new TaskSpec(TaskId.New(), "Report role-specific evidence.", role)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    foreach (var field in AgentOutputDirectives.RequiredWorkerResultFieldNamesForRole(role))
    {
        Assert.Contains($"{field}:", brief, StringComparison.Ordinal);
    }
}

    [Xunit.Fact(DisplayName = "EngineeringPracticeRegistry_matches_by_goal_and_changed_file_scope")]
    public void EngineeringPracticeRegistryMatchesByGoalAndChangedFileScope()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Refactor neutral orchestration text",
        [new TaskSpec(TaskId.New(), "Implement the neutral change.", AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var matches = kernel.MatchEngineeringPractices(
        goal,
        task,
        ["src/Mcg.AgentOrchestrator.Core/Application/RepositoryChangeClassifier.cs"]);

    Assert.Contains(matches, match => match.Practice.Id == "classifier-positive-evidence");
    Assert.DoesNotContain(matches, match => match.Practice.Id == "process-dispatch-hygiene");
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_injects_classifier_practice_and_excludes_process_practice")]
    public void BuildTaskBriefInjectsClassifierPracticeAndExcludesProcessPractice()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Fix repository change classifier fault classification",
        [new TaskSpec(TaskId.New(), "Implement classifier decision-table behavior.", AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
    var practices = SectionFrom(brief, "## PRACTICES");

    Assert.Contains("classifier-positive-evidence", practices, StringComparison.Ordinal);
    Assert.Contains("Positive-evidence classification", practices, StringComparison.Ordinal);
    Assert.Contains("b0807c0e", practices, StringComparison.Ordinal);
    Assert.Contains("prompt-size delta +", practices, StringComparison.Ordinal);
    Assert.DoesNotContain("process-dispatch-hygiene", practices, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_injects_process_practice_and_excludes_classifier_practice")]
    public void BuildTaskBriefInjectsProcessPracticeAndExcludesClassifierPractice()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Harden dispatch child process liveness",
        [new TaskSpec(TaskId.New(), "Implement process/dispatch supervision around worker exit artifacts.", AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
    var practices = SectionFrom(brief, "## PRACTICES");

    Assert.Contains("process-dispatch-hygiene", practices, StringComparison.Ordinal);
    Assert.Contains("async output drains", practices, StringComparison.Ordinal);
    Assert.Contains("c86de253", practices, StringComparison.Ordinal);
    Assert.DoesNotContain("classifier-positive-evidence", practices, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_reviewer_receives_practice_checklist")]
    public void BuildTaskBriefReviewerReceivesPracticeChecklist()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var reviewer = new TaskSpec(TaskId.New(), "Review classifier behavior and fault-path tests.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Review repository classifier classification change", [reviewer]);

    var brief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;
    var checklist = SectionFrom(brief, "## PRACTICES REVIEW CHECKLIST");

    Assert.Contains("classifier-positive-evidence", checklist, StringComparison.Ordinal);
    Assert.Contains("Violation is a finding named \"Positive-evidence classification\"", checklist, StringComparison.Ordinal);
    Assert.Contains("prompt-size delta +", checklist, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "AgentTaskRunner_reviewer_api_prompt_receives_practice_checklist")]
    public async Task AgentTaskRunnerReviewerApiPromptReceivesPracticeChecklist()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var reviewer = new TaskSpec(TaskId.New(), "Review process/dispatch liveness and exit artifacts.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Review dispatch child process implementation", [reviewer]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var provider = new FakeModelProvider("OpenAI", "review complete");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, reviewer.Id);

    Assert.True(provider.LastRequest is not null);
    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("## PRACTICES REVIEW CHECKLIST", prompt, StringComparison.Ordinal);
    Assert.Contains("process-dispatch-hygiene", prompt, StringComparison.Ordinal);
    Assert.Contains("Violation is a finding named \"Process/dispatch hygiene\"", prompt, StringComparison.Ordinal);
}

static void AddRetryNotes(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, string prefix, int count)
{
    for (var index = 1; index <= count; index++)
    {
        kernel.RetryTask(goalId, taskId, $"{prefix}-{index:00}");
    }
}

static void CompleteWithEvidence(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, string evidence)
{
    kernel.RecordTaskVerification(goalId, taskId, new TaskVerificationRecord(
        "dotnet test",
        "C:\\repo",
        0,
        evidence,
        string.Empty,
        DateTimeOffset.UtcNow));
}

static int CountOccurrences(string value, string needle)
{
    var count = 0;
    var index = 0;
    while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
    {
        count++;
        index += needle.Length;
    }

    return count;
}

static string SectionFrom(string value, string heading)
{
    var start = value.IndexOf(heading, StringComparison.Ordinal);
    Assert.True(start >= 0);
    var next = value.IndexOf($"{Environment.NewLine}## ", start + heading.Length, StringComparison.Ordinal);
    return next < 0 ? value[start..] : value[start..next];
}

static IReadOnlyList<AgentDefinition> DefaultAgents()
{
    static ModelProfile OpenAi(string reasoningEffort) =>
        new("OpenAI", "gpt-5.5", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, reasoningEffort);

    return
    [
        new(AgentId.New(), "Planner", AgentRole.Planner, OpenAi("high")),
        new(AgentId.New(), "Researcher", AgentRole.Researcher, OpenAi("high")),
        new(AgentId.New(), "Developer", AgentRole.Developer, OpenAi("medium")),
        new(AgentId.New(), "Tester", AgentRole.Tester, OpenAi("high")),
        new(AgentId.New(), "Reviewer", AgentRole.Reviewer, OpenAi("high"))
    ];
}
}

