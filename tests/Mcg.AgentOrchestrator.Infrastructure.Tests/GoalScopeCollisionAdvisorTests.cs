using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalScopeCollisionAdvisorTests
{
    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_reports_no_overlap_only_with_explicit_evidence")]
    public void ReportsNoOverlapOnlyWithExplicitEvidence()
    {
        var goal = CreateGoal("Change src/Beta/File.cs.");

        var report = GoalScopeCollisionAdvisor.Build(
            ["Change src/Alpha/File.cs."],
            [goal]);

        Xunit.Assert.Equal(ScopeCollisionVerdict.NoOverlapDetected, report.Verdict);
        Xunit.Assert.Empty(report.Collisions);
        Xunit.Assert.Empty(report.EvidenceGaps);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_reports_exact_path_and_goal")]
    public void ReportsExactPathAndGoal()
    {
        var goal = CreateGoal("Change src/Feature/File.cs.");

        var report = GoalScopeCollisionAdvisor.Build(
            ["Also change src/Feature/File.cs."],
            [goal]);

        var collision = Xunit.Assert.Single(report.Collisions);
        Xunit.Assert.Equal(ScopeCollisionVerdict.OverlapDetected, report.Verdict);
        Xunit.Assert.Equal(ScopeCollisionKind.ExactFile, collision.Kind);
        Xunit.Assert.Equal(goal.Id.Value, collision.GoalId);
        Xunit.Assert.Equal("src/Feature/File.cs", collision.ProposedPath);
        Xunit.Assert.Equal("src/Feature/File.cs", collision.ConflictingPath);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_reports_directory_prefix_paths")]
    public void ReportsDirectoryPrefixPaths()
    {
        var goal = CreateGoal("Change src/Feature.");

        var report = GoalScopeCollisionAdvisor.Build(
            ["Also change src/Feature/File.cs."],
            [goal]);

        var collision = Xunit.Assert.Single(report.Collisions);
        Xunit.Assert.Equal(ScopeCollisionKind.DirectoryPrefix, collision.Kind);
        Xunit.Assert.Equal("src/Feature/File.cs", collision.ProposedPath);
        Xunit.Assert.Equal("src/Feature", collision.ConflictingPath);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_inferred_active_scope_cannot_clear")]
    public void InferredActiveScopeCannotClear()
    {
        var goal = CreateGoal(GeneratedObjective("src/Beta/File.cs"));

        var report = GoalScopeCollisionAdvisor.Build(
            ["Change src/Alpha/File.cs."],
            [goal]);

        Xunit.Assert.Equal(ScopeCollisionVerdict.InsufficientEvidence, report.Verdict);
        Xunit.Assert.NotEqual(ScopeCollisionVerdict.NoOverlapDetected, report.Verdict);
        Xunit.Assert.Contains(
            report.EvidenceGaps,
            gap => gap.GoalId == goal.Id.Value && gap.Gap == ScopeEvidenceGap.ActiveExplicitScopesMissing);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_inferred_active_scope_can_raise")]
    public void InferredActiveScopeCanRaise()
    {
        var goal = CreateGoal(GeneratedObjective("src/Feature/File.cs"));

        var report = GoalScopeCollisionAdvisor.Build(
            ["Change src/Feature/File.cs."],
            [goal]);

        var collision = Xunit.Assert.Single(report.Collisions);
        Xunit.Assert.Equal(ScopeCollisionVerdict.OverlapDetected, report.Verdict);
        Xunit.Assert.Equal(FileScopeProvenance.Inferred, collision.ConflictingProvenance);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_explicit_occurrence_wins_over_generated_echo")]
    public void ExplicitOccurrenceWinsOverGeneratedEcho()
    {
        var goal = CreateGoal(
            "Operator explicitly owns src/Beta/File.cs.\n\n" +
            GeneratedObjective("src/Beta/File.cs"));

        var report = GoalScopeCollisionAdvisor.Build(
            ["Change src/Alpha/File.cs."],
            [goal]);

        Xunit.Assert.Equal(ScopeCollisionVerdict.NoOverlapDetected, report.Verdict);
        Xunit.Assert.Equal(
            FileScopeProvenance.Explicit,
            Xunit.Assert.Single(GoalFileScopeInference.FromGoal(goal)).Provenance);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_missing_proposed_scope_is_insufficient")]
    public void MissingProposedScopeIsInsufficient()
    {
        var report = GoalScopeCollisionAdvisor.Build(
            ["No repository path is declared."],
            [CreateGoal("Change src/Feature/File.cs.")]);

        Xunit.Assert.Equal(ScopeCollisionVerdict.InsufficientEvidence, report.Verdict);
        Xunit.Assert.Contains(report.EvidenceGaps, gap => gap.Gap == ScopeEvidenceGap.ProposedScopesMissing);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_missing_active_scope_is_insufficient")]
    public void MissingActiveScopeIsInsufficient()
    {
        var goal = CreateGoal("No repository path is declared.");

        var report = GoalScopeCollisionAdvisor.Build(
            ["Change src/Feature/File.cs."],
            [goal]);

        Xunit.Assert.Equal(ScopeCollisionVerdict.InsufficientEvidence, report.Verdict);
        Xunit.Assert.Contains(
            report.EvidenceGaps,
            gap => gap.GoalId == goal.Id.Value && gap.Gap == ScopeEvidenceGap.ActiveScopesMissing);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_inferred_proposed_scope_is_insufficient")]
    public void InferredProposedScopeIsInsufficient()
    {
        var report = GoalScopeCollisionAdvisor.Build(
            [GeneratedObjective("src/Alpha/File.cs")],
            [CreateGoal("Change src/Beta/File.cs.")]);

        Xunit.Assert.Equal(ScopeCollisionVerdict.InsufficientEvidence, report.Verdict);
        Xunit.Assert.Contains(
            report.EvidenceGaps,
            gap => gap.Gap == ScopeEvidenceGap.ProposedExplicitScopesMissing);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_excludes_completed_and_metadata_goals")]
    public void ExcludesCompletedAndMetadataGoals()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Change src/Feature/File.cs.",
            [new TaskSpec(TaskId.New(), "Implement planned work.", AgentRole.Developer)]);
        var task = goal.Tasks.Single();
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord("manual", Environment.CurrentDirectory, 0, "pass", "", DateTimeOffset.UtcNow));
        kernel.ReconcileGoalVerificationStatus(goal.Id, "All task gates passed.");
        kernel.CompleteGoal(goal.Id, "Accepted.");

        var report = GoalScopeCollisionAdvisor.Build(
            ["Change src/Feature/File.cs."],
            kernel.Goals);

        Xunit.Assert.Equal(GoalStatus.Completed, goal.Status);
        Xunit.Assert.Equal(0, report.ComparedGoalCount);
        Xunit.Assert.Empty(report.Collisions);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_orders_multiple_conflicts_deterministically")]
    public void OrdersMultipleConflictsDeterministically()
    {
        var first = CreateGoal(
            "Change src/Alpha and src/Shared.cs.",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var second = CreateGoal(
            "Change src/Beta and src/Shared.cs.",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        const string proposed = "Change src/Alpha/File.cs, src/Beta/File.cs, and src/Shared.cs.";

        var ordered = GoalScopeCollisionAdvisor.Build([proposed], [second, first]);
        var reversed = GoalScopeCollisionAdvisor.Build([proposed], [first, second]);

        var expected = new[]
        {
            (first.Id.Value, ScopeCollisionKind.ExactFile, "src/Shared.cs", "src/Shared.cs"),
            (first.Id.Value, ScopeCollisionKind.DirectoryPrefix, "src/Alpha/File.cs", "src/Alpha"),
            (second.Id.Value, ScopeCollisionKind.ExactFile, "src/Shared.cs", "src/Shared.cs"),
            (second.Id.Value, ScopeCollisionKind.DirectoryPrefix, "src/Beta/File.cs", "src/Beta")
        };
        Xunit.Assert.Equal(expected, Project(ordered));
        Xunit.Assert.Equal(expected, Project(reversed));
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_caps_candidate_comparison_conservatively")]
    public void CapsCandidateComparisonConservatively()
    {
        var goals = Enumerable.Range(0, 25)
            .Select(index => CreateGoal(
                $"Change src/Candidate{index}/File.cs.",
                index.ToString("D32")))
            .ToArray();

        var report = GoalScopeCollisionAdvisor.Build(["Change src/Proposed/File.cs."], goals);

        Xunit.Assert.Equal(24, report.ComparedGoalCount);
        Xunit.Assert.Equal(ScopeCollisionVerdict.InsufficientEvidence, report.Verdict);
        Xunit.Assert.Contains(report.EvidenceGaps, gap => gap.Gap == ScopeEvidenceGap.ComparisonTruncated);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_build_signature_cannot_accept_io_seams")]
    public void BuildSignatureCannotAcceptIoSeams()
    {
        var method = typeof(GoalScopeCollisionAdvisor).GetMethod(nameof(GoalScopeCollisionAdvisor.Build));
        Xunit.Assert.NotNull(method);
        var parameters = method!.GetParameters();

        Xunit.Assert.Equal(2, parameters.Length);
        Xunit.Assert.Equal(typeof(IReadOnlyList<string>), parameters[0].ParameterType);
        Xunit.Assert.Equal(typeof(IReadOnlyCollection<Goal>), parameters[1].ParameterType);
        Xunit.Assert.DoesNotContain(parameters, parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
        Xunit.Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(string));
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_does_not_change_parallel_scheduler_decisions")]
    public void DoesNotChangeParallelSchedulerDecisions()
    {
        var intents = new[]
        {
            new ParallelExecutionIntent("left", "left-goal", ["src/Feature"]),
            new ParallelExecutionIntent("right", "right-goal", ["src/Feature/File.cs"])
        };
        var baseline = ParallelExecutionPlanner.Build(intents);

        var report = GoalScopeCollisionAdvisor.Build(
            ["Change src/Feature/File.cs."],
            [CreateGoal("Change src/Feature.")]);
        var afterAdvisory = ParallelExecutionPlanner.Build(intents);

        Xunit.Assert.Equal(ScopeCollisionVerdict.OverlapDetected, report.Verdict);
        Xunit.Assert.Equal(
            baseline.Batches.Select(batch => (batch.Number, IntentIds: string.Join(",", batch.IntentIds))),
            afterAdvisory.Batches.Select(batch => (batch.Number, IntentIds: string.Join(",", batch.IntentIds))));
        Xunit.Assert.Equal(
            baseline.Decisions.Select(decision => (
                decision.IntentId,
                decision.Disposition,
                decision.BatchNumber,
                Reasons: string.Join("|", decision.Reasons))),
            afterAdvisory.Decisions.Select(decision => (
                decision.IntentId,
                decision.Disposition,
                decision.BatchNumber,
                Reasons: string.Join("|", decision.Reasons))));
    }

    private static Goal CreateGoal(string objective, string? id = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement planned work.", AgentRole.Developer);
        return id is null
            ? kernel.CreateGoal(objective, [task])
            : kernel.CreateGoal(new GoalId(id), objective, [task]);
    }

    private static string GeneratedObjective(string path) =>
        $"Generated intake objective\n\n{BacklogIntakePlanner.TargetScopeHeadingLine}\n- {path}\n\nVerification:\n- Focused tests.";

    private static (string GoalId, ScopeCollisionKind Kind, string ProposedPath, string ConflictingPath)[] Project(
        GoalScopeCollisionReport report) =>
        report.Collisions
            .Select(collision => (
                collision.GoalId,
                collision.Kind,
                collision.ProposedPath,
                collision.ConflictingPath))
            .ToArray();
}
