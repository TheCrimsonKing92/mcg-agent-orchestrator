using Mcg.AgentOrchestrator.Core;

public sealed class WaiverDispositionBriefRenderingTests
{
    private const string OverlayStart = "<!-- EFFECTIVE_ACCEPTANCE_CRITERIA_CORRECTIONS_START -->";
    private const string OverlayEnd = "<!-- EFFECTIVE_ACCEPTANCE_CRITERIA_CORRECTIONS_END -->";
    private const string ConventionLine =
        "Correction marker convention: CRITERIA CORRECTION: supersedes=\"<brief text/ref>\"; correction=\"<effective criterion>\".";

    [Xunit.Fact]
    public void DeveloperBriefRendersEveryDispositionProse()
    {
        var scenario = CreateWaiverScenario(
            [AgentRole.Developer],
            [
                "Waive this criterion.",
                "Keep the deployment observation.",
                "Keep the rollback measurement."
            ],
            [
                new CriterionDispositionRequest("2", "Deployment observation remains required."),
                new CriterionDispositionRequest("3", "Rollback measurement moves to the operator.")
            ]);

        var brief = scenario.Kernel.BuildTaskBrief(
            scenario.Goal.Id,
            scenario.Tasks.Single(task => task.RequiredRole == AgentRole.Developer).Id).Content;

        Xunit.Assert.Contains("Deployment observation remains required.", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("Rollback measurement moves to the operator.", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ReviewerBriefRendersEveryDispositionProse()
    {
        var scenario = CreateWaiverScenario(
            [AgentRole.Reviewer],
            [
                "Waive this criterion.",
                "Keep the deployment observation.",
                "Keep the rollback measurement."
            ],
            [
                new CriterionDispositionRequest("2", "Deployment observation remains required."),
                new CriterionDispositionRequest("3", "Rollback measurement moves to the operator.")
            ]);

        var brief = scenario.Kernel.BuildTaskBrief(
            scenario.Goal.Id,
            scenario.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer).Id).Content;

        Xunit.Assert.Contains("Deployment observation remains required.", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("Rollback measurement moves to the operator.", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DispositionLinesIdentifyCriteriaAndRenderInCriterionOrder()
    {
        var scenario = CreateWaiverScenario(
            [AgentRole.Developer],
            [
                "Waive this criterion.",
                "Second criterion\nwith detail.",
                "Third criterion.",
                "Fourth criterion."
            ],
            [
                new CriterionDispositionRequest("4", "Fourth disposition."),
                new CriterionDispositionRequest("2", "Second disposition."),
                new CriterionDispositionRequest("3", "Third disposition.")
            ]);
        var brief = scenario.Kernel.BuildTaskBrief(scenario.Goal.Id, scenario.Tasks.Single().Id).Content;
        var overlay = ExtractOverlay(brief);

        const string secondLine = "  Disposition: Second criterion with detail. -> Second disposition.";
        const string thirdLine = "  Disposition: Third criterion. -> Third disposition.";
        const string fourthLine = "  Disposition: Fourth criterion. -> Fourth disposition.";
        Xunit.Assert.Contains(secondLine, overlay, StringComparison.Ordinal);
        Xunit.Assert.Contains(thirdLine, overlay, StringComparison.Ordinal);
        Xunit.Assert.Contains(fourthLine, overlay, StringComparison.Ordinal);
        Xunit.Assert.True(overlay.IndexOf(secondLine, StringComparison.Ordinal) < overlay.IndexOf(thirdLine, StringComparison.Ordinal));
        Xunit.Assert.True(overlay.IndexOf(thirdLine, StringComparison.Ordinal) < overlay.IndexOf(fourthLine, StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void WaiverWithoutDispositionsRendersTodaysExactStanza()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var developer = new TaskSpec(TaskId.New(), "Implement the surviving criteria.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Preserve the legacy waiver stanza.", [developer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Preserve the legacy waiver stanza.",
            ["Waive this criterion."],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.WaiveAcceptanceCriterion(goal.Id, "1", "Requirement moved.");

        var overlay = ExtractOverlay(kernel.BuildTaskBrief(goal.Id, developer.Id).Content);

        var expected = string.Join('\n',
        [
            OverlayStart,
            "## EFFECTIVE ACCEPTANCE CRITERIA - OPERATOR CORRECTIONS",
            "Operator corrections in this overlay supersede conflicting brief text. Do not enforce or re-raise findings that apply only to superseded criteria.",
            ConventionLine,
            "- Supersedes: Waive this criterion.",
            "  Status: WAIVED — Requirement moved.",
            $"  Provenance: operator; {clock.UtcNow:u}; GoalPolicyDecision; goal timeline.",
            OverlayEnd
        ]);
        Xunit.Assert.Equal(expected, overlay);
    }

    [Xunit.Fact]
    public void NonWaiverCorrectionStanzaIsUnchanged()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var developer = new TaskSpec(TaskId.New(), "Implement the corrected criterion.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Preserve the correction stanza.", [developer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Preserve the correction stanza.",
            ["Run the full suite."],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.RecordTaskNote(
            goal.Id,
            developer.Id,
            "CRITERIA CORRECTION: supersedes=\"Run the full suite.\"; correction=\"Run the focused suite.\"");

        var overlay = ExtractOverlay(kernel.BuildTaskBrief(goal.Id, developer.Id).Content);

        var expected = string.Join('\n',
        [
            OverlayStart,
            "## EFFECTIVE ACCEPTANCE CRITERIA - OPERATOR CORRECTIONS",
            "Operator corrections in this overlay supersede conflicting brief text. Do not enforce or re-raise findings that apply only to superseded criteria.",
            ConventionLine,
            "- Supersedes: Run the full suite.",
            "  Effective criterion: Run the focused suite.",
            $"  Provenance: operator; {clock.UtcNow:u}; TaskNote; task {developer.Id.Value[..8]}.",
            OverlayEnd
        ]);
        Xunit.Assert.Equal(expected, overlay);
    }

    [Xunit.Fact]
    public void OverlongDispositionIsIndependentlyTrimmedToOneLine()
    {
        const string hiddenMiddle = "THIS_MIDDLE_MUST_BE_TRIMMED";
        var longDisposition = new string('a', 900) + hiddenMiddle + new string('b', 900);
        var scenario = CreateWaiverScenario(
            [AgentRole.Developer],
            ["Waive this criterion.", "Long disposition criterion.", "Following criterion."],
            [
                new CriterionDispositionRequest("2", longDisposition),
                new CriterionDispositionRequest("3", "Following disposition remains visible.")
            ]);

        var overlay = ExtractOverlay(scenario.Kernel.BuildTaskBrief(scenario.Goal.Id, scenario.Tasks.Single().Id).Content);
        var dispositionLines = overlay.Split('\n')
            .Where(line => line.StartsWith("  Disposition: ", StringComparison.Ordinal))
            .ToArray();

        Xunit.Assert.Equal(2, dispositionLines.Length);
        Xunit.Assert.Contains("...[truncated ", dispositionLines[0], StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(hiddenMiddle, overlay, StringComparison.Ordinal);
        Xunit.Assert.Contains(
            "  Disposition: Following criterion. -> Following disposition remains visible.",
            dispositionLines[1],
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void SentinelsAndConventionSurviveWhileOtherRolesReceiveNoOverlay()
    {
        var scenario = CreateWaiverScenario(
            [AgentRole.Planner, AgentRole.Researcher, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            ["Waive this criterion.", "Keep this criterion."],
            [new CriterionDispositionRequest("2", "This criterion remains required.")]);
        var developerBrief = scenario.Kernel.BuildTaskBrief(
            scenario.Goal.Id,
            scenario.Tasks.Single(task => task.RequiredRole == AgentRole.Developer).Id).Content;

        Xunit.Assert.Contains(OverlayStart, developerBrief, StringComparison.Ordinal);
        Xunit.Assert.Contains(OverlayEnd, developerBrief, StringComparison.Ordinal);
        Xunit.Assert.Contains(ConventionLine, developerBrief, StringComparison.Ordinal);
        foreach (var role in new[] { AgentRole.Planner, AgentRole.Researcher, AgentRole.Tester })
        {
            var brief = scenario.Kernel.BuildTaskBrief(
                scenario.Goal.Id,
                scenario.Tasks.Single(task => task.RequiredRole == role).Id).Content;
            Xunit.Assert.DoesNotContain(OverlayStart, brief, StringComparison.Ordinal);
        }
    }

    private static WaiverScenario CreateWaiverScenario(
        IReadOnlyList<AgentRole> roles,
        IReadOnlyList<string> criteria,
        IReadOnlyList<CriterionDispositionRequest> dispositions)
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var tasks = roles
            .Select(role => new TaskSpec(TaskId.New(), $"Perform {role} work.", role))
            .ToArray();
        var goal = kernel.CreateGoal("Render waiver dispositions into worker briefs.", tasks);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Render waiver dispositions into worker briefs.",
            criteria,
            VerificationClass.TestVerifiable,
            [],
            []));
        for (var criterionIndex = 1; criterionIndex < criteria.Count; criterionIndex++)
        {
            kernel.MapCriterionEvidenceOwner(
                goal.Id,
                criterionIndex,
                criterionVersion: 1,
                CriterionEvidenceOwner.Acceptance,
                "operator",
                CriterionEvidenceScopes.FullAcceptanceGate,
                expectedCandidateSha: "candidate-a");
        }

        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.WaiveAcceptanceCriterion(
            goal.Id,
            "1",
            "The waived premise moved to separate work.",
            dispositions: dispositions);
        return new WaiverScenario(kernel, goal, tasks);
    }

    private static string ExtractOverlay(string brief)
    {
        var start = brief.IndexOf(OverlayStart, StringComparison.Ordinal);
        var end = brief.IndexOf(OverlayEnd, start, StringComparison.Ordinal);
        Xunit.Assert.True(start >= 0, "The corrections overlay START sentinel was missing.");
        Xunit.Assert.True(end >= start, "The corrections overlay END sentinel was missing.");
        return brief[start..(end + OverlayEnd.Length)].ReplaceLineEndings("\n");
    }

    private sealed record WaiverScenario(
        AgentOrchestratorKernel Kernel,
        Goal Goal,
        IReadOnlyList<TaskSpec> Tasks);
}
