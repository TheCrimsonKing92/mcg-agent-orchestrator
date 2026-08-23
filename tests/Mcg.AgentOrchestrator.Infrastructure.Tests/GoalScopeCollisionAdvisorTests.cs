using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalScopeCollisionAdvisorTests
{
    [Xunit.Theory]
    [Xunit.InlineData(
        "src/Mcg.AgentOrchestrator.Core/Application/AlphaService.cs",
        "src/Mcg.AgentOrchestrator.Core/Application/BetaService.cs",
        true)]
    [Xunit.InlineData(
        "src/Mcg.AgentOrchestrator.App/Orchestration/AlphaSlice.cs",
        "src/Mcg.AgentOrchestrator.App/Orchestration/BetaSlice.cs",
        false)]
    public void SiblingCollision_SamePaths_AgreesWithOwnershipAndAcceptance(
        string leftPath,
        string rightPath,
        bool expectedCollision)
    {
        var scheduler = GoalScopeCollisionAdvisor.ClassifySiblingScopeCollision([leftPath], [rightPath]);
        var leftScope = RepositoryLandingScopeNormalization.Normalize([leftPath], reserveUnknownScope: true);
        var rightScope = RepositoryLandingScopeNormalization.Normalize([rightPath], reserveUnknownScope: true);
        var leftOwned = RepositoryOwnershipMap.Classify(leftPath);
        var rightOwned = RepositoryOwnershipMap.Classify(rightPath);
        string[] leftExpectedResources = leftOwned.RequiresSerialization ? [$"ownership:{leftOwned.ReservationKey}"] : [];
        string[] rightExpectedResources = rightOwned.RequiresSerialization ? [$"ownership:{rightOwned.ReservationKey}"] : [];
        var acceptanceLeft = ConductorParallelAcceptanceCandidate.Create(CreateGoal(leftPath), 0, [leftPath]);
        var acceptanceRight = ConductorParallelAcceptanceCandidate.Create(CreateGoal(rightPath), 1, [rightPath]);

        Xunit.Assert.Equal(leftExpectedResources, leftScope.ResourceKeys);
        Xunit.Assert.Equal(rightExpectedResources, rightScope.ResourceKeys);
        Xunit.Assert.Equal(expectedCollision, acceptanceLeft.Overlaps(acceptanceRight));
        Xunit.Assert.Equal(acceptanceLeft.Overlaps(acceptanceRight), scheduler.HasCollision);
    }

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

    [Xunit.Fact]
    public void ExpectedChangeLists_SameFile_StillCollide()
    {
        const string sharedPath =
            "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorParallelAcceptanceAttempts.cs";
        var activeGoal = CreateGoal(
            $"Expected to change:\n- {sharedPath}\nDo not change src/Active/Forbidden.cs.");

        var report = GoalScopeCollisionAdvisor.Build(
            [$"Expected to change:\n- {sharedPath}\nDo not change src/Proposed/Forbidden.cs."],
            [activeGoal]);

        var collision = Xunit.Assert.Single(report.Collisions);
        Xunit.Assert.Equal(ScopeCollisionVerdict.OverlapDetected, report.Verdict);
        Xunit.Assert.Equal(ScopeCollisionKind.ExactFile, collision.Kind);
        Xunit.Assert.Equal(sharedPath, collision.ProposedPath);
        Xunit.Assert.Equal(sharedPath, collision.ConflictingPath);
    }

    [Xunit.Fact]
    public void ProhibitionOnly_ProposedScope_IsInsufficientEvidence()
    {
        var report = GoalScopeCollisionAdvisor.Build(
            ["Do not change src/Feature/File.cs."],
            [CreateGoal("Change src/Other/File.cs.")]);

        Xunit.Assert.Equal(ScopeCollisionVerdict.InsufficientEvidence, report.Verdict);
        Xunit.Assert.Contains(report.EvidenceGaps, gap => gap.Gap == ScopeEvidenceGap.ProposedScopesMissing);
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
        Xunit.Assert.Equal(ScopeCollisionVerdict.OverlapDetectedIncomplete, report.Verdict);
        Xunit.Assert.Equal("overlap-detected-incomplete", report.VerdictToken);
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
        var scopes = GoalFileScopeInference.FromGoal(goal, 64_000, out var truncated);
        Xunit.Assert.False(truncated);
        Xunit.Assert.Equal(FileScopeProvenance.Explicit, Xunit.Assert.Single(scopes).Provenance);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_bounds_active_goal_text_and_reports_truncation")]
    public void BoundsActiveGoalTextAndReportsTruncation()
    {
        var goal = CreateGoal("Change src/Feature/File.cs. " + new string('x', 70_000));

        var directlyInferred = GoalFileScopeInference.FromGoal(goal, 32, out var directlyTruncated);
        var report = GoalScopeCollisionAdvisor.Build(
            ["Change src/Feature/File.cs."],
            [goal]);

        Xunit.Assert.True(directlyTruncated);
        Xunit.Assert.Contains(directlyInferred, scope => scope.Path == "src/Feature/File.cs");
        Xunit.Assert.Contains(
            report.EvidenceGaps,
            gap =>
                gap.GoalId == goal.Id.Value &&
                gap.Gap == ScopeEvidenceGap.TextTruncated &&
                gap.Message.Contains(goal.Id.Value[..8], StringComparison.Ordinal));
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

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_excludes_terminal_goals")]
    public void ExcludesTerminalGoals()
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
            (second.Id.Value, ScopeCollisionKind.ExactFile, "src/Shared.cs", "src/Shared.cs"),
            (first.Id.Value, ScopeCollisionKind.DirectoryPrefix, "src/Alpha/File.cs", "src/Alpha"),
            (second.Id.Value, ScopeCollisionKind.DirectoryPrefix, "src/Beta/File.cs", "src/Beta")
        };
        Xunit.Assert.Equal(expected, Project(ordered));
        Xunit.Assert.Equal(expected, Project(reversed));
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_compares_every_eligible_goal")]
    public void ComparesEveryEligibleGoal()
    {
        var goals = Enumerable.Range(0, 25)
            .Select(index => CreateGoal(
                index == 24
                    ? "Change src/Proposed/File.cs."
                    : $"Change src/Candidate{index}/File.cs.",
                index.ToString("D32")))
            .ToArray();

        var report = GoalScopeCollisionAdvisor.Build(["Change src/Proposed/File.cs."], goals);

        Xunit.Assert.Equal(25, report.ComparedGoalCount);
        Xunit.Assert.Equal(ScopeCollisionVerdict.OverlapDetected, report.Verdict);
        Xunit.Assert.Equal(goals[24].Id.Value, Xunit.Assert.Single(report.Collisions).GoalId);
        Xunit.Assert.DoesNotContain(report.EvidenceGaps, gap => gap.Gap == ScopeEvidenceGap.ComparisonTruncated);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_excludes_parked_goals_from_conflicts")]
    public void ExcludesParkedGoalsFromConflicts()
    {
        var kernel = new AgentOrchestratorKernel();
        var parked = kernel.CreateGoal(
            new GoalId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            "Change src/Feature/File.cs.",
            [new TaskSpec(TaskId.New(), "Parked work.", AgentRole.Developer)]);
        kernel.ParkGoal(parked.Id, "Deferred by operator.");
        var live = kernel.CreateGoal(
            new GoalId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            "Change src/Feature/File.cs.",
            [new TaskSpec(TaskId.New(), "Live work.", AgentRole.Developer)]);

        var report = GoalScopeCollisionAdvisor.Build(
            ["Change src/Feature/File.cs."],
            kernel.Goals);

        Xunit.Assert.Equal(2, report.InputGoalCount);
        Xunit.Assert.Equal(1, report.EligibleGoalCount);
        Xunit.Assert.Equal(1, report.ComparedGoalCount);
        Xunit.Assert.Equal(live.Id.Value, Xunit.Assert.Single(report.Collisions).GoalId);
        Xunit.Assert.DoesNotContain(report.Collisions, collision => collision.GoalId == parked.Id.Value);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_qualifies_overlap_when_live_goal_is_uncheckable")]
    public void QualifiesOverlapWhenLiveGoalIsUncheckable()
    {
        var scoped = CreateGoal("Change src/Feature/File.cs.");
        var scopeless = CreateGoal("No repository path is declared.");

        var report = GoalScopeCollisionAdvisor.Build(
            ["Change src/Feature/File.cs."],
            [scoped, scopeless]);

        Xunit.Assert.Equal(ScopeCollisionVerdict.OverlapDetectedIncomplete, report.Verdict);
        Xunit.Assert.Equal("overlap-detected-incomplete", report.VerdictToken);
        Xunit.Assert.Equal(2, report.InputGoalCount);
        Xunit.Assert.Equal(2, report.EligibleGoalCount);
        Xunit.Assert.Equal(2, report.ComparedGoalCount);
        Xunit.Assert.Equal(1, report.UncheckableGoalCount);
        Xunit.Assert.Equal(0, report.UncomparedGoalCount);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_excludes_projected_cleaned_up_goals")]
    public void ExcludesProjectedCleanedUpGoals()
    {
        var kernel = new AgentOrchestratorKernel();
        var cleanedUp = kernel.CreateGoal(
            "Change src/Feature/File.cs.",
            [new TaskSpec(TaskId.New(), "Completed work.", AgentRole.Developer)]);
        var task = cleanedUp.Tasks.Single();
        kernel.RecordTaskVerification(
            cleanedUp.Id,
            task.Id,
            new TaskVerificationRecord("manual", Environment.CurrentDirectory, 0, "pass", "", DateTimeOffset.UtcNow));
        kernel.ReconcileGoalVerificationStatus(cleanedUp.Id, "All task gates passed.");
        var observations = new Dictionary<string, GoalScopeLifecycleObservation>(StringComparer.Ordinal)
        {
            [cleanedUp.Id.Value] = new(GoalLifecycleState.CleanedUp)
        };

        var report = GoalScopeCollisionAdvisor.Build(
            ["Change src/Feature/File.cs."],
            kernel.Goals,
            lifecycleObservations: observations);

        Xunit.Assert.Equal(GoalStatus.Verified, cleanedUp.Status);
        Xunit.Assert.Equal(0, report.EligibleGoalCount);
        Xunit.Assert.Equal(0, report.ComparedGoalCount);
        Xunit.Assert.Empty(report.Collisions);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_journal_observer_excludes_cleaned_up_goal")]
    public void JournalObserverExcludesCleanedUpGoal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-scope-collision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(
                "Change src/Feature/File.cs.",
                [new TaskSpec(TaskId.New(), "Completed work.", AgentRole.Developer)]);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                new TaskVerificationRecord("manual", root, 0, "pass", "", DateTimeOffset.UtcNow));
            kernel.ReconcileGoalVerificationStatus(goal.Id, "All task gates passed.");
            GoalOperationJournal.Completed(root, goal, "conductor:land");
            GoalOperationJournal.Completed(root, goal, "conductor:record");
            GoalOperationJournal.Completed(root, goal, "conductor:cleanup");

            var observation = GoalMonitoringSubscriptionCommand.ReadScopeCollisionLifecycleObservation(workspace, goal);
            var report = GoalScopeCollisionAdvisor.Build(
                ["Change src/Feature/File.cs."],
                [goal],
                lifecycleObservations: new Dictionary<string, GoalScopeLifecycleObservation>(StringComparer.Ordinal)
                {
                    [goal.Id.Value] = observation
                });

            Xunit.Assert.True(observation.IsAvailable);
            Xunit.Assert.Equal(GoalLifecycleState.CleanedUp, observation.State);
            Xunit.Assert.Equal(0, report.EligibleGoalCount);
            Xunit.Assert.Empty(report.Collisions);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_journal_observer_skips_external_facts_for_non_verified_goal")]
    public void JournalObserverSkipsExternalFactsForNonVerifiedGoal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-scope-collision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(
                "Change src/Feature/File.cs.",
                [new TaskSpec(TaskId.New(), "Implement planned work.", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            goal = kernel.GetGoal(goal.Id);
            GoalOperationJournal.Failed(root, goal, "conductor:cleanup", "Fixture blocking lifecycle fact.");

            var observation = GoalMonitoringSubscriptionCommand.ReadScopeCollisionLifecycleObservation(workspace, goal);

            Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
            Xunit.Assert.True(observation.IsAvailable);
            Xunit.Assert.Equal(GoalLifecycleState.Created, observation.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_missing_lifecycle_evidence_qualifies_overlap")]
    public void MissingLifecycleEvidenceQualifiesOverlap()
    {
        var goal = CreateGoal("Change src/Feature/File.cs.");
        var observations = new Dictionary<string, GoalScopeLifecycleObservation>(StringComparer.Ordinal)
        {
            [goal.Id.Value] = new(State: null, UnavailableReason: "fixture-unavailable")
        };

        var report = GoalScopeCollisionAdvisor.Build(
            ["Change src/Feature/File.cs."],
            [goal],
            lifecycleObservations: observations);

        Xunit.Assert.Equal(ScopeCollisionVerdict.OverlapDetectedIncomplete, report.Verdict);
        Xunit.Assert.Contains(
            report.EvidenceGaps,
            gap => gap.GoalId == goal.Id.Value && gap.Gap == ScopeEvidenceGap.LifecycleEvidenceUnavailable);
        Xunit.Assert.Single(report.Collisions);
    }

    [Xunit.Fact(DisplayName = "GoalScopeCollisionAdvisor_ranks_exact_and_trusted_conflicts_globally")]
    public void RanksExactAndTrustedConflictsGlobally()
    {
        var inferredExact = CreateGoal(
            GeneratedObjective("src/Feature/File.cs"),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var explicitExact = CreateGoal(
            "Change src/Feature/File.cs.",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var inferredPrefix = CreateGoal(
            GeneratedObjective("src/Feature"),
            "00000000000000000000000000000000");

        var report = GoalScopeCollisionAdvisor.Build(
            ["Change src/Feature/File.cs."],
            [inferredPrefix, inferredExact, explicitExact]);

        Xunit.Assert.Collection(
            report.Collisions,
            collision =>
            {
                Xunit.Assert.Equal(explicitExact.Id.Value, collision.GoalId);
                Xunit.Assert.Equal(ScopeCollisionKind.ExactFile, collision.Kind);
                Xunit.Assert.Equal(FileScopeProvenance.Explicit, collision.ConflictingProvenance);
            },
            collision =>
            {
                Xunit.Assert.Equal(inferredExact.Id.Value, collision.GoalId);
                Xunit.Assert.Equal(ScopeCollisionKind.ExactFile, collision.Kind);
                Xunit.Assert.Equal(FileScopeProvenance.Inferred, collision.ConflictingProvenance);
            },
            collision =>
            {
                Xunit.Assert.Equal(inferredPrefix.Id.Value, collision.GoalId);
                Xunit.Assert.Equal(ScopeCollisionKind.DirectoryPrefix, collision.Kind);
            });
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
