using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

[Xunit.Collection(TestCollections.ChaosGateGit)]
public sealed class AutoReviewRetryConvergenceBriefBuilderTests : WorkerDispatchTestSupport
{
    [Xunit.Fact(DisplayName = "BuildConvergenceBrief_preserves_every_typed_tester_finding")]
    public void BuildConvergenceBriefPreservesEveryTypedTesterFinding()
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Repair.", AgentRole.Developer);
        var tester = new TaskSpec(TaskId.New(), "Verify.", AgentRole.Tester);
        var goal = kernel.CreateGoal("Preserve tester findings", [developer, tester]);
        kernel.RecordTaskVerification(
            goal.Id,
            tester.Id,
            new TaskVerificationRecord(
                "test",
                "C:\\repo",
                1,
                "typed tester findings",
                "",
                DateTimeOffset.UtcNow,
                MergedReviewFindings:
                [
                    new ReviewFinding(
                        "tester-alpha",
                        ReviewFindingState.Open,
                        new ReviewFindingLocation("tests/A.cs", "CaseA"),
                        "First Tester failure."),
                    new ReviewFinding(
                        "tester-beta",
                        ReviewFindingState.Open,
                        new ReviewFindingLocation("tests/B.cs", "CaseB"),
                        "Second Tester failure.")
                ]));

        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            goal,
            developer,
            tester,
            "fallback blocker text",
            "reported typed failures",
            AgentRole.Developer,
            1,
            "C:\\logs\\tester.out",
            ["tests/A.cs", "tests/B.cs"]);

        Assert.Contains("stable_id: tester-alpha", brief, StringComparison.Ordinal);
        Assert.Contains("First Tester failure.", brief, StringComparison.Ordinal);
        Assert.Contains("stable_id: tester-beta", brief, StringComparison.Ordinal);
        Assert.Contains("Second Tester failure.", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("fallback blocker text", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BuildConvergenceBrief_keeps_advisories_visible_without_making_them_repair_scope")]
    public void BuildConvergenceBriefKeepsAdvisoriesVisibleWithoutMakingThemRepairScope()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Separate blockers from advisories");
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        RecordReviewerRound(
            kernel,
            goal,
            reviewer,
            "needs-work",
            [
                new ReviewFinding(
                    "BLOCKER",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/A.cs", "A.Run"),
                    "Required correctness repair.",
                    Severity: FindingSeverity.Blocking),
                new ReviewFinding(
                    "ADVISORY",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/B.cs", "B.Run"),
                    "Optional follow-up.",
                    Severity: FindingSeverity.Advisory)
            ],
            []);

        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            goal,
            developer,
            reviewer,
            "findings",
            "verdict=needs-work",
            AgentRole.Developer,
            1,
            "review.out",
            ["src/A.cs", "src/B.cs"]);
        var actionItems = brief[..brief.IndexOf("## PRESERVE_ACCEPTED", StringComparison.Ordinal)];
        var deferred = brief[brief.IndexOf("## DEFERRED_NON_BLOCKING_ADVISORIES", StringComparison.Ordinal)..];

        Assert.Contains("open_count: 1", actionItems, StringComparison.Ordinal);
        Assert.Contains("stable_id: BLOCKER", actionItems, StringComparison.Ordinal);
        Assert.DoesNotContain("stable_id: ADVISORY", actionItems, StringComparison.Ordinal);
        Assert.Contains("advisory_count: 1", deferred, StringComparison.Ordinal);
        Assert.Contains("stable_id: ADVISORY", deferred, StringComparison.Ordinal);
        Assert.Contains("not required repair scope", deferred, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_orders_spec_findings_and_preserves_legacy_shape")]
    public void OrdersSpecFindingsAndPreservesLegacyCategorylessShape()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Typed review convergence");
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        RecordReviewerRound(
            kernel,
            goal,
            reviewer,
            "needs-work",
            [
                new ReviewFinding(
                    "QUALITY",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/B.cs", "B.Run"),
                    "Polish.",
                    Category: FindingCategory.CodeQuality),
                new ReviewFinding(
                    "SPEC",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/A.cs", "A.Run"),
                    "Contract gap.",
                    Category: FindingCategory.SpecCompliance)
            ],
            []);

        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            goal, developer, reviewer, "findings", "verdict=needs-work",
            AgentRole.Developer, 1, "review.out", ["src/A.cs", "src/B.cs"]);

        Assert.True(
            brief.IndexOf("stable_id: SPEC", StringComparison.Ordinal) <
            brief.IndexOf("stable_id: QUALITY", StringComparison.Ordinal));
        Assert.Contains("category: spec-compliance", brief, StringComparison.Ordinal);

        var legacyFinding = new ReviewFinding(
            "LEGACY",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/A.cs", "A.Run"),
            "Legacy.");
        kernel.RetryTask(goal.Id, reviewer.Id, "legacy round");
        RecordReviewerRound(kernel, goal, reviewer, "needs-work", [legacyFinding], []);
        var legacyBrief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            goal, developer, reviewer, "legacy", "verdict=needs-work",
            AgentRole.Developer, 2, "review.out", ["src/A.cs"]);
        Assert.DoesNotContain("category: unspecified", legacyBrief, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_SQLite_rounds_shrink_A_B_to_accept")]
    public async Task SqliteRoundsShrinkFindingsMonotonicallyToAccept()
    {
        // Parallel-safe: the real database has a per-test GUID path and is deleted in finally.
        var db = Path.Combine(Path.GetTempPath(), $"mcg-review-convergence-{Guid.NewGuid():N}.db");
        var repositoryRoot = CreateSeededDispatchRepository();
        try
        {
            var sourceA = Path.Combine(repositoryRoot, "src", "A.cs");
            var sourceB = Path.Combine(repositoryRoot, "src", "B.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceA)!);
            File.WriteAllText(sourceA, GuardSource("A", enabled: false));
            File.WriteAllText(sourceB, GuardSource("B", enabled: false));
            CommitAll(repositoryRoot, "Add review targets", "2026-01-01T00:01:00Z");
            var round1Commit = ReadHead(repositoryRoot);
            var repository = new SqliteOrchestratorStateRepository(db);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "Structured review finding convergence");
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            var anchorA = new ReviewFindingLocation("src/A.cs", "A.Run", "guard-a");
            var anchorB = new ReviewFindingLocation("src/B.cs", "B.Run", "guard-b");

            RecordReviewerRoundFromGitDiff(
                kernel,
                goal,
                reviewer,
                repositoryRoot,
                previousReviewedCommit: null,
                round1Commit,
                "needs-work",
                [
                    new ReviewFinding("F-A", ReviewFindingState.Open, anchorA, "A is missing its guard."),
                    new ReviewFinding("F-B", ReviewFindingState.Open, anchorB, "B is missing its guard.")
                ]);
            await repository.SaveAsync(kernel);
            kernel = await repository.LoadAsync();
            goal = kernel.GetGoal(goal.Id);
            developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);

            var round1State = AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(goal, reviewer);
            Assert.Equal(2, ReviewFindingConvergence.CountOpen(round1State));
            var round1Brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
                goal, developer, reviewer, "A and B remain open.", "verdict=needs-work",
                AgentRole.Developer, 1, "round1.out", ["src/A.cs", "src/B.cs"]);
            Assert.Contains("open_count: 2", round1Brief);

            kernel.RetryTask(goal.Id, reviewer.Id, "round 2");
            File.WriteAllText(sourceA, GuardSource("A", enabled: true));
            CommitAll(repositoryRoot, "Fix A", "2026-01-01T00:02:00Z");
            var round2Commit = ReadHead(repositoryRoot);
            RecordReviewerRoundFromGitDiff(
                kernel,
                goal,
                reviewer,
                repositoryRoot,
                round1Commit,
                round2Commit,
                "needs-work",
                [
                    new ReviewFinding("F-A", ReviewFindingState.Resolved, anchorA, "A is missing its guard."),
                    new ReviewFinding("F-B", ReviewFindingState.Open, anchorB, "B is missing its guard.")
                ]);
            await repository.SaveAsync(kernel);
            kernel = await repository.LoadAsync();
            goal = kernel.GetGoal(goal.Id);
            developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);

            var round2State = AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(goal, reviewer);
            Assert.Equal(1, ReviewFindingConvergence.CountOpen(round2State));
            var round2Brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
                goal, developer, reviewer, "Only B remains open.", "verdict=needs-work",
                AgentRole.Developer, 2, "round2.out", ["src/B.cs"]);
            var actionItems = round2Brief[..round2Brief.IndexOf("## PRESERVE_ACCEPTED", StringComparison.Ordinal)];
            Assert.Contains("stable_id: F-B", actionItems);
            Assert.DoesNotContain("stable_id: F-A", actionItems);
            Assert.Contains(AutoReviewRetryConvergenceBriefBuilder.PreserveAcceptedDirective, round2Brief);
            Assert.Contains("stable_id: F-A", round2Brief);

            kernel.RetryTask(goal.Id, reviewer.Id, "round 3");
            File.WriteAllText(sourceB, GuardSource("B", enabled: true));
            CommitAll(repositoryRoot, "Fix B", "2026-01-01T00:03:00Z");
            var round3Commit = ReadHead(repositoryRoot);
            var round3Touched = PrepareReviewerRoundFromGitDiff(
                kernel,
                goal,
                reviewer,
                repositoryRoot,
                round2Commit,
                round3Commit);
            Assert.Empty(round3Touched);
            Assert.Empty(reviewer.LastDispatch!.ReviewFindingTouchedAnchors!);
            var reviewerBrief = kernel.BuildTaskBrief(
                goal.Id,
                reviewer.Id,
                reviewerScopeChangedFiles: ["src/B.cs"],
                reviewerRoundTouchedAnchors: reviewer.LastDispatch.ReviewFindingTouchedAnchors);
            Assert.Contains("OPEN_ACTIVE_RECHECK count=1", reviewerBrief.Content);
            Assert.Contains($"- F-B | severity=blocking | {anchorB}", reviewerBrief.Content);
            Assert.Contains("RESOLVED_CARRIED count=1", reviewerBrief.Content);
            var openScope = reviewerBrief.Content[
                reviewerBrief.Content.IndexOf("OPEN_ACTIVE_RECHECK", StringComparison.Ordinal)..
                reviewerBrief.Content.IndexOf("RESOLVED_CARRIED", StringComparison.Ordinal)];
            Assert.DoesNotContain("F-A", openScope);

            RecordPreparedReviewerRound(
                kernel,
                goal,
                reviewer,
                "pass",
                [
                    new ReviewFinding("F-A", ReviewFindingState.Resolved, anchorA, "A is missing its guard."),
                    new ReviewFinding("F-B", ReviewFindingState.Resolved, anchorB, "B is missing its guard.")
                ]);
            await repository.SaveAsync(kernel);
            kernel = await repository.LoadAsync();
            goal = kernel.GetGoal(goal.Id);
            reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);

            var round3State = AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(goal, reviewer);
            Assert.Equal(0, ReviewFindingConvergence.CountOpen(round3State));
            Assert.All(round3State, finding => Assert.Equal(ReviewFindingState.Resolved, finding.State));
            Assert.Contains("verdict: pass", reviewer.LastVerification!.StandardOutput);
            Assert.Equal(round3Commit, reviewer.LastVerification.ReviewedCommit);
            Assert.Empty(reviewer.LastVerification.ReviewFindingTouchedAnchors!);
        }
        finally
        {
            foreach (var path in new[] { db, $"{db}-shm", $"{db}-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            try { Directory.Delete(repositoryRoot, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_SQLite_round_diff_reopens_exact_regressed_anchor")]
    public async Task SqliteRoundDiffReopensExactRegressedAnchor()
    {
        var db = Path.Combine(Path.GetTempPath(), $"mcg-review-regression-{Guid.NewGuid():N}.db");
        var repositoryRoot = CreateSeededDispatchRepository();
        try
        {
            var source = Path.Combine(repositoryRoot, "src", "A.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, GuardSource("A", enabled: false));
            CommitAll(repositoryRoot, "Add review target", "2026-01-01T00:01:00Z");
            var round1Commit = ReadHead(repositoryRoot);
            var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
            var repository = new SqliteOrchestratorStateRepository(db);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "Structured review regression");
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);

            RecordReviewerRoundFromGitDiff(
                kernel,
                goal,
                reviewer,
                repositoryRoot,
                previousReviewedCommit: null,
                round1Commit,
                "needs-work",
                [new ReviewFinding("F-A", ReviewFindingState.Open, anchor, "A is missing its guard.")]);
            kernel.RetryTask(goal.Id, reviewer.Id, "round 2");
            File.WriteAllText(source, GuardSource("A", enabled: true));
            CommitAll(repositoryRoot, "Fix A", "2026-01-01T00:02:00Z");
            var round2Commit = ReadHead(repositoryRoot);
            RecordReviewerRoundFromGitDiff(
                kernel,
                goal,
                reviewer,
                repositoryRoot,
                round1Commit,
                round2Commit,
                "pass",
                [new ReviewFinding("F-A", ReviewFindingState.Resolved, anchor, "A is missing its guard.")]);
            await repository.SaveAsync(kernel);
            kernel = await repository.LoadAsync();
            goal = kernel.GetGoal(goal.Id);
            reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);

            kernel.RetryTask(goal.Id, reviewer.Id, "round 3 regression");
            File.WriteAllText(source, GuardSource("A", enabled: false));
            CommitAll(repositoryRoot, "Regress A", "2026-01-01T00:03:00Z");
            var round3Commit = ReadHead(repositoryRoot);
            var touched = RecordReviewerRoundFromGitDiff(
                kernel,
                goal,
                reviewer,
                repositoryRoot,
                round2Commit,
                round3Commit,
                "needs-work",
                [new ReviewFinding("F-A", ReviewFindingState.Open, anchor, "A guard regressed.")]);
            Assert.Equal(anchor, Assert.Single(touched));
            await repository.SaveAsync(kernel);
            kernel = await repository.LoadAsync();

            var state = kernel.GetReviewFindingState(goal.Id);
            Assert.Equal(ReviewFindingState.Open, Assert.Single(state).State);
            var persistedReviewer = kernel.GetGoal(goal.Id).Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            Assert.Equal(anchor, Assert.Single(persistedReviewer.LastVerification!.ReviewFindingTouchedAnchors!));
            Assert.Equal(round3Commit, persistedReviewer.LastVerification.ReviewedCommit);
        }
        finally
        {
            foreach (var path in new[] { db, $"{db}-shm", $"{db}-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            try { Directory.Delete(repositoryRoot, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "Recording_a_valid_round_is_not_blocked_by_a_previously_rejected_round")]
    public void RecordingValidRoundIsNotBlockedByPreviouslyRejectedRound()
    {
        var openedAt = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var movedTo = new ReviewFindingLocation("src/B.cs", "B.Run", "guard");
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Poisoned reviewer history");
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);

        RecordReviewerRound(
            kernel,
            goal,
            reviewer,
            "needs-work",
            [new ReviewFinding("F-1", ReviewFindingState.Open, openedAt, "Missing guard.")],
            [openedAt]);

        // Round 2 reports the finding still open at a DIFFERENT anchor, so it is rejected — but its
        // verification record stays in history.
        kernel.RetryTask(goal.Id, reviewer.Id, "round 2");
        RecordReviewerRound(
            kernel,
            goal,
            reviewer,
            "needs-work",
            [new ReviewFinding("F-1", ReviewFindingState.Open, movedTo, "Missing guard.")],
            [movedTo]);

        // Round 3 resolves the finding at its ORIGINAL anchor. Replaying the rejected round 2 must not
        // block it, nor report round 2's stale violation as though it described this submission.
        kernel.RetryTask(goal.Id, reviewer.Id, "round 3");
        RecordReviewerRound(
            kernel,
            goal,
            reviewer,
            "pass",
            [new ReviewFinding("F-1", ReviewFindingState.Resolved, openedAt, "Guard added.")],
            [openedAt]);

        var recorded = kernel.GetGoal(goal.Id).Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        Assert.Equal(WorkTaskStatus.Completed, recorded.Status);
        Assert.Equal(0, ReviewFindingConvergence.CountOpen(kernel.GetReviewFindingState(goal.Id)));
    }

    [Xunit.Fact(DisplayName = "GetReviewFindingState_skips_an_unfoldable_stored_round_instead_of_throwing")]
    public void GetReviewFindingStateSkipsUnfoldableStoredRoundInsteadOfThrowing()
    {
        var openedAt = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var movedTo = new ReviewFindingLocation("src/B.cs", "B.Run", "guard");
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Unfoldable stored round");
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);

        RecordReviewerRound(
            kernel,
            goal,
            reviewer,
            "needs-work",
            [new ReviewFinding("F-1", ReviewFindingState.Open, openedAt, "A is missing its guard.")],
            [openedAt]);

        // A round reporting a still-open finding at a different anchor is rejected when recorded, but the
        // verification record is retained in history. Replaying it must not make the state unreadable.
        kernel.RetryTask(goal.Id, reviewer.Id, "round 2");
        try
        {
            RecordReviewerRound(
                kernel,
                goal,
                reviewer,
                "needs-work",
                [new ReviewFinding("F-1", ReviewFindingState.Open, movedTo, "A is missing its guard.")],
                [movedTo]);
        }
        catch (ReviewFindingConvergenceException)
        {
            // Rejection at record time is expected; the stored round is what this test exercises.
        }

        var state = kernel.GetReviewFindingState(goal.Id, out var inconsistencies);

        var finding = Assert.Single(state);
        Assert.Equal("F-1", finding.StableId);
        Assert.Equal(ReviewFindingState.Open, finding.State);
        Assert.Equal(openedAt, finding.Location);
        Assert.Contains(
            ReviewFindingConvergence.IdentityMovedViolationCode,
            Assert.Single(inconsistencies));
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_structured_open_set_is_authoritative")]
    public void StructuredOpenSetIsNotFilteredByFreeTextBlockerFragments()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Structured findings stay authoritative");
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var findingA = new ReviewFinding(
            "F-A",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/A.cs", "A.Run", "guard-a"),
            "A is missing its guard.");
        var findingB = new ReviewFinding(
            "F-B",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/B.cs", "B.Run", "guard-b"),
            "B is missing its guard.");
        RecordReviewerRound(kernel, goal, reviewer, "needs-work", [findingA, findingB], []);

        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            goal,
            developer,
            reviewer,
            findingA.Description,
            "verdict=needs-work",
            AgentRole.Developer,
            1,
            "round1.out",
            ["src/A.cs", "src/B.cs"]);
        var actionItems = brief[..brief.IndexOf("## PRESERVE_ACCEPTED", StringComparison.Ordinal)];

        Assert.Contains("open_count: 2", actionItems);
        Assert.Contains("stable_id: F-A", actionItems);
        Assert.Contains("stable_id: F-B", actionItems);
        Assert.Contains("accepted_count: 0", brief);
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_merges_open_reviewer_and_tester_ledgers")]
    public void StructuredConvergenceBriefMergesOpenReviewerAndTesterLedgers()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Structured findings stay complete across roles");
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        RecordReviewerRound(
            kernel,
            goal,
            reviewer,
            "needs-work",
            [
                new ReviewFinding(
                    "reviewer-open",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/A.cs", "A.Run", "reviewer-guard"),
                    "Reviewer guard remains open.")
            ],
            []);
        RecordReviewerRound(
            kernel,
            goal,
            tester,
            "needs-work",
            [
                new ReviewFinding(
                    "tester-open",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("tests/A.Tests.cs", "A.Tests", "tester-case"),
                    "Tester edge case remains open.")
            ],
            []);

        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            goal,
            developer,
            tester,
            "Tester edge case remains open.",
            "structured Tester findings",
            AgentRole.Developer,
            2,
            "tester.out",
            ["src/A.cs", "tests/A.Tests.cs"]);

        Assert.Contains("open_count: 2", brief);
        Assert.Contains("stable_id: reviewer-open", brief);
        Assert.Contains("source_role: Reviewer", brief);
        Assert.Contains("stable_id: tester-open", brief);
        Assert.Contains("source_role: Tester", brief);
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_deduplicates_multi_round_findings")]
    public void BuildConvergenceBriefDeduplicatesMultiRoundFindings()
    {
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            3,
            AgentRole.Reviewer,
            new TaskId("reviewer-task-0001"),
            "verdict=needs-work",
            AgentRole.Developer,
            @"C:\tmp\reviewer.out.log",
            [
                "ConductorDriverTests still expects the raw findings passthrough.",
                "Tester receipt omits ProgressiveReviewGlanceTests.",
                "  ConductorDriverTests   still expects the raw findings passthrough.  ",
                "Reviewer still needs InquiryDispatcherTests coverage."
            ],
            [
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProgressiveReviewGlanceTests.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/InquiryDispatcherTests.cs"
            ]);

        Xunit.Assert.Contains("auto-review-retry round 3 convergence brief", brief);
        Xunit.Assert.Contains(AutoReviewRetryConvergenceBriefBuilder.AcceptedShapePreamble, brief);
        Xunit.Assert.Contains("Deduplicated residual blockers", brief);
        Xunit.Assert.Equal(1, CountOccurrences(brief, "- ConductorDriverTests still expects the raw findings passthrough."));
        Xunit.Assert.Contains("- Tester receipt omits ProgressiveReviewGlanceTests.", brief);
        Xunit.Assert.Contains("- Reviewer still needs InquiryDispatcherTests coverage.", brief);
        Xunit.Assert.Contains("Rerun these focused test classes at your final commit and quote receipts:", brief);
        Xunit.Assert.Contains("ConductorDriverTests", brief);
        Xunit.Assert.Contains("ProgressiveReviewGlanceTests", brief);
        Xunit.Assert.Contains("InquiryDispatcherTests", brief);
        Xunit.Assert.Contains(@"C:\tmp\reviewer.out.log", brief);
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_does_not_request_unverified_bare_test_classes")]
    public void BareTestClassNamesAreNotPromotedToFocusedReceipts()
    {
        var classes = AutoReviewRetryConvergenceBriefBuilder.InferFocusedTestClasses(
            [
                "requested absent ReviewFindingsTests and ReviewerWorkerResultBlockersTests",
                "rerun FullyQualifiedName~WorkerResultBlockersTests"
            ],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs"]);

        Xunit.Assert.Equal(["ConductorDriverTests", "WorkerResultBlockersTests"], classes);
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_keeps_single_round_findings")]
    public void BuildConvergenceBriefKeepsSingleRoundFindings()
    {
        var blocker = "Developer left tester receipt parsing unwired; retain this exact finding text.";
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            1,
            AgentRole.Tester,
            new TaskId("tester-task-0001"),
            "WORKER_RESULT blocker",
            AgentRole.Developer,
            @"C:\tmp\tester.out.log",
            [blocker],
            []);

        Xunit.Assert.Contains("auto-review-retry round 1 convergence brief", brief);
        Xunit.Assert.Contains(AutoReviewRetryConvergenceBriefBuilder.AcceptedShapePreamble, brief);
        Xunit.Assert.Equal(1, CountOccurrences(brief, $"- {blocker}"));
        Xunit.Assert.Contains(AutoReviewRetryConvergenceBriefBuilder.GenericRerunMandate, brief);
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_keeps_all_unique_findings")]
    public void BuildConvergenceBriefKeepsAllUniqueFindings()
    {
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            2,
            AgentRole.Reviewer,
            new TaskId("reviewer-task-0001"),
            "verdict=needs-work",
            AgentRole.Developer,
            "reviewer output",
            [
                "First blocker remains open.",
                "Second blocker remains open.",
                "Third blocker remains open."
            ],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs"]);

        Xunit.Assert.Equal(1, CountOccurrences(brief, "- First blocker remains open."));
        Xunit.Assert.Equal(1, CountOccurrences(brief, "- Second blocker remains open."));
        Xunit.Assert.Equal(1, CountOccurrences(brief, "- Third blocker remains open."));
        Xunit.Assert.Contains("ConductorDriverTests", brief);
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_accumulates_verification_history")]
    public void BuildConvergenceBriefAccumulatesVerificationHistory()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Review retry convergence");
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);

        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        FailReviewerNeedsWork(kernel, goal, reviewer, "Shared blocker stays open.");
        kernel.RetryTask(goal.Id, developer.Id, "auto-review-retry round 1 convergence brief: prior retry");
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        FailTesterBlocker(kernel, goal, tester, "  Shared   blocker stays open.  ; New tester blocker surfaced.");

        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            goal,
            developer,
            tester,
            "Shared blocker stays open.",
            "WORKER_RESULT blocker",
            AgentRole.Developer,
            2,
            @"C:\tmp\tester.out.log",
            ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs"]);

        Xunit.Assert.Equal(1, CountOccurrences(brief, "- Shared blocker stays open."));
        Xunit.Assert.Contains("- New tester blocker surfaced.", brief);
        Xunit.Assert.Contains(AutoReviewRetryConvergenceBriefBuilder.GenericRerunMandate, brief);
        Xunit.Assert.DoesNotContain("ConductorDriverTests", brief);
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_skips_manual_verification_records_in_reviewer_history")]
    public void SkipsManualVerificationRecordsInReviewerHistory()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Manual verification must not strand the retry brief");
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);

        kernel.RecordTaskVerification(
            goal.Id,
            reviewer.Id,
            ManualVerificationRecorder.Create(
                true,
                "verdict stands after environment-caused gate failure",
                @"C:\tmp",
                DateTimeOffset.UtcNow.AddMinutes(-1)));
        kernel.RetryTask(goal.Id, reviewer.Id, "reviewer re-run after manual verification");

        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard-a");
        RecordReviewerRound(
            kernel,
            goal,
            reviewer,
            "needs-work",
            [new ReviewFinding("F-A", ReviewFindingState.Open, anchor, "A is missing its guard.")],
            []);

        var state = AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(goal, reviewer);
        var finding = Assert.Single(state);
        Assert.Equal(ReviewFindingState.Open, finding.State);
        Assert.Equal(anchor, finding.Location);
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_contract_repair_brief_carries_violation_canonical_ledger_and_claimed_resolutions")]
    public void ContractRepairBriefCarriesViolationCanonicalLedgerAndClaimedResolutions()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Repair reviewer contract");
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var firstLocation = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var secondLocation = new ReviewFindingLocation("src/B.cs", "B.Run", "guard");
        var opened = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Open, firstLocation, "First advisory.", FindingSeverity.Advisory),
            new ReviewFinding("F-2", ReviewFindingState.Open, secondLocation, "Second advisory.", FindingSeverity.Advisory)
        };
        var rejected = new[]
        {
            opened[0] with { Location = new ReviewFindingLocation("src/C.cs", "C.Run", "guard") },
            opened[1] with { State = ReviewFindingState.Resolved }
        };
        var firstOutput = ReviewerOutput("pass", opened);
        var rejectedOutput = ReviewerOutput("pass", rejected);
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1",
            @"C:\tmp",
            0,
            firstOutput,
            "",
            DateTimeOffset.Parse("2026-07-30T10:00:00Z"),
            WorkerResultPresent: true));
        var violation = new ReviewFindingContractViolation(
            ReviewFindingConvergence.IdentityMovedViolationCode,
            "F-1 moved.",
            "F-1",
            "F-1",
            firstLocation,
            rejected[0].Location);
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2",
            @"C:\tmp",
            0,
            rejectedOutput,
            "",
            DateTimeOffset.Parse("2026-07-30T10:01:00Z"),
            StandardOutputPath: @"C:\tmp\reviewer.out.log",
            WorkerResultPresent: true,
            ReviewFindingContractViolation: violation));

        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildContractRepairBrief(
            goal,
            reviewer,
            violation,
            1,
            2,
            @"C:\tmp\reviewer.out.log");

        Assert.StartsWith("review-finding contract-repair: attempt 1/2", brief, StringComparison.Ordinal);
        Assert.Contains("violation_code: ERR_REVIEW_FINDING_IDENTITY_MOVED", brief, StringComparison.Ordinal);
        Assert.Contains("open_count: 2", brief, StringComparison.Ordinal);
        Assert.Contains($"stable_id: F-1 | severity=advisory | {firstLocation}", brief, StringComparison.Ordinal);
        Assert.Contains("- stable_id: F-2", brief, StringComparison.Ordinal);
        Assert.Contains("reuse stable_id and location VERBATIM", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("auto-review-retry", brief, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ReviewFindingState.Open, AutoReviewRetryConvergenceBriefBuilder
            .ReadStructuredReviewFindingState(goal, reviewer)
            .Single(finding => finding.StableId == "F-2")
            .State);
    }

    private static string ReviewerOutput(string verdict, IReadOnlyList<ReviewFinding> findings) =>
        string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - deterministic fixture",
            "blockers: none",
            $"findings: {JsonSerializer.Serialize(findings)}",
            "touched_anchors: []",
            $"verdict: {verdict}",
            "END_WORKER_RESULT");

    private static void DispatchTask(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string command = "test.exe",
        string? reviewedCommit = null,
        IReadOnlyList<ReviewFindingLocation>? touchedAnchors = null)
    {
        var dispatch = new TaskDispatchRecord(
            "test-worker",
            command,
            @"C:\tmp",
            DateTimeOffset.UtcNow,
            BaseCommit: reviewedCommit,
            ReviewFindingTouchedAnchors: touchedAnchors);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
    }

    private static void PassVerification(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        bool hasCommittedChanges = false)
    {
        DispatchTask(kernel, goal, task);
        var verification = new TaskVerificationRecord(
            "test.exe",
            @"C:\tmp",
            0,
            "ok",
            "",
            DateTimeOffset.UtcNow,
            HasCommittedChanges: hasCommittedChanges);
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
    }

    private static void FailReviewerNeedsWork(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        string blocker)
    {
        DispatchTask(kernel, goal, reviewer, "review");
        var stdout = string.Join(
            Environment.NewLine,
            "Findings first.",
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            $"blockers: {blocker}",
            $"findings: {JsonSerializer.Serialize(new[] { new ReviewFinding("finding-a", ReviewFindingState.Open, new ReviewFindingLocation("src/Test.cs", "Test.Run"), blocker) })}",
            "touched_anchors: []",
            "verdict: needs-work",
            "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "review",
            @"C:\tmp",
            1,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: @"C:\tmp\reviewer.out.log",
            WorkerResultPresent: true);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, verification);
    }

    private static void FailTesterBlocker(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec tester,
        string blocker)
    {
        DispatchTask(kernel, goal, tester, "test");
        var stdout = string.Join(
            Environment.NewLine,
            "Tests found a correctness issue.",
            "WORKER_RESULT:",
            "files: none",
            "commands: test",
            "tests: fail - focused behavior check failed",
            $"blockers: {blocker}",
            "commit: none",
            "model_fit: test",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "test",
            @"C:\tmp",
            1,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: @"C:\tmp\tester.out.log",
            WorkerResultPresent: true);
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, verification);
    }

    private static void RecordReviewerRound(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        string verdict,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyList<ReviewFindingLocation> touchedAnchors)
    {
        DispatchTask(kernel, goal, reviewer, "review", touchedAnchors: touchedAnchors);
        RecordPreparedReviewerRound(kernel, goal, reviewer, verdict, findings);
    }

    private static IReadOnlyList<ReviewFindingLocation> RecordReviewerRoundFromGitDiff(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        string repositoryRoot,
        string? previousReviewedCommit,
        string currentCommit,
        string verdict,
        IReadOnlyList<ReviewFinding> findings)
    {
        var touchedAnchors = PrepareReviewerRoundFromGitDiff(
            kernel,
            goal,
            reviewer,
            repositoryRoot,
            previousReviewedCommit,
            currentCommit);
        RecordPreparedReviewerRound(kernel, goal, reviewer, verdict, findings);
        return touchedAnchors;
    }

    private static IReadOnlyList<ReviewFindingLocation> PrepareReviewerRoundFromGitDiff(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        string repositoryRoot,
        string? previousReviewedCommit,
        string currentCommit)
    {
        var resolvedAnchors = kernel.GetReviewFindingState(goal.Id)
            .Where(finding => finding.State == ReviewFindingState.Resolved)
            .Select(finding => finding.Location)
            .ToArray();
        var touchedAnchors = new WorkerGitContext().ReadReviewerRoundTouchedAnchors(
            repositoryRoot,
            previousReviewedCommit,
            currentCommit,
            resolvedAnchors);
        DispatchTask(
            kernel,
            goal,
            reviewer,
            "review",
            reviewedCommit: currentCommit,
            touchedAnchors: touchedAnchors);
        return touchedAnchors;
    }

    private static void RecordPreparedReviewerRound(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        string verdict,
        IReadOnlyList<ReviewFinding> findings)
    {
        var blockers = findings.Any(finding => finding.State == ReviewFindingState.Open)
            ? string.Join("; ", findings.Where(finding => finding.State == ReviewFindingState.Open).Select(finding => finding.Description))
            : "none";
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - deterministic reviewer fixture",
            "commit: none",
            $"blockers: {blockers}",
            $"findings: {JsonSerializer.Serialize(findings)}",
            "touched_anchors: []",
            $"verdict: {verdict}",
            "model_fit: fixture/model - adequate - deterministic review - test seam",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "review",
            @"C:\tmp",
            verdict == "pass" ? 0 : 1,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: @"C:\tmp\reviewer.out.log",
            WorkerResultPresent: true);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, verification);
    }

    private static string GuardSource(string typeName, bool enabled) =>
        $$"""
        public sealed class {{typeName}}
        {
            public void Run()
            {
                var guard = {{enabled.ToString().ToLowerInvariant()}};
            }
        }
        """;

    private static void CommitAll(string repositoryRoot, string message, string timestamp)
    {
        var committedAt = DateTimeOffset.Parse(timestamp);
        RunGit(repositoryRoot, ["add", "-A"], committedAt);
        RunGit(repositoryRoot, ["commit", "-m", message], committedAt);
    }

    private static string ReadHead(string repositoryRoot) =>
        ReadGit(repositoryRoot, ["rev-parse", "HEAD"]).Trim();

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
