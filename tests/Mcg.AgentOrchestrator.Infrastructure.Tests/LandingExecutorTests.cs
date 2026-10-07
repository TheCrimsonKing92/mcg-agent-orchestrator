using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorTests
{
    private static readonly Lazy<string> GitRepositoryTemplate = new(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-landing-template", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            InfrastructureTestSupport.RunGitWithCommitPostcondition(root, ["init", "-b", "main"]);
            InfrastructureTestSupport.RunGitWithCommitPostcondition(root, ["config", "user.email", "tests@example.invalid"]);
            InfrastructureTestSupport.RunGitWithCommitPostcondition(root, ["config", "user.name", "Tests"]);
            File.AppendAllText(
                Path.Combine(root, ".git", "info", "exclude"),
                ".orchestrator-test-remotes/" + Environment.NewLine +
                ".orchestrator/" + Environment.NewLine);
            File.WriteAllText(Path.Combine(root, "README.md"), "initial" + Environment.NewLine);
            InfrastructureTestSupport.RunGitWithCommitPostcondition(root, ["add", "README.md"]);
            InfrastructureTestSupport.RunGitWithCommitPostcondition(root, ["commit", "-m", "Initial"]);
            return root;
        }
        catch (Exception exception)
        {
            TryDeleteDirectory(root);
            throw new InvalidOperationException("Landing repository template could not be created.", exception);
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    [Xunit.Fact(DisplayName = "LandingExecutor_failed_count_excludes_auto_recovered_empty_output_flake")]
    public void LandingExecutorFailedCountExcludesAutoRecoveredEmptyOutputFlake()
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer);
        var task = goal.Tasks.Single();

        FlakeThenPass(kernel, goal, task);

        Assert.Equal(0, LandingExecutor.CountFailedVerifications(goal));
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_failed_count_includes_recovered_real_failure")]
    public void LandingExecutorFailedCountIncludesRecoveredRealFailure()
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer);
        var task = goal.Tasks.Single();

        FailThenPass(kernel, goal, task);

        Assert.Equal(1, LandingExecutor.CountFailedVerifications(goal));
    }

    [Xunit.Fact(DisplayName = "LandingDecision_escalates_when_two_genuine_failed_tasks_recovered")]
    public void LandingDecisionEscalatesWhenTwoGenuineFailedTasksRecovered()
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer, AgentRole.Tester);

        foreach (var task in goal.Tasks)
        {
            FailThenPass(kernel, goal, task);
        }

        var failedCount = LandingExecutor.CountFailedVerifications(goal);
        var decision = LandingDecisionEngine.Decide(new LandingInputs(
            RepositoryChangeClassifier.Classify(["README.md"]),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: failedCount));

        Assert.Equal(LandingDecisionEngine.RepeatedFailureThreshold, failedCount);
        if (decision is not LandingDecision.Escalate escalation)
        {
            throw new InvalidOperationException("Expected landing escalation.");
        }

        Assert.True(escalation.Reason.Contains("repeated failures", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact]
    public void SecondEvaluationAfterLanding_SkipsAcceptanceAndEscalation()
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(
                repo,
                "src/already-landed.txt");
            var first = LandingExecutor.Execute(kernel, goal, workspace);
            Assert.True(first.MainAdvanced, first.Message);
            GoalOperationJournal.Completed(repo, goal, "conductor:land", first.Message);

            GoalOperationJournal.Begin(repo, goal, "conductor:land", "redundant post-landing evaluation");
            var second = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(second.MainAdvanced);
            Assert.IsType<LandingDecision.Promote>(second.Decision);
            Assert.Contains("already landed", second.Message, StringComparison.OrdinalIgnoreCase);
            var inbox = OperatorInbox.Build(
                kernel,
                [],
                WorkerProfileCatalog.Default(),
                workspace,
                goal.Id.Value[..8]);
            Assert.DoesNotContain(inbox.Items, item => item.Kind == OperatorInboxKind.LandingEscalation);
            Assert.Contains(
                GoalOperationJournal.Read(repo, goal.Id).Entries,
                entry => entry.Operation.Equals("conductor:post-landing-skip", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void SecondEvaluationAfterLanding_DoesNotMutateRefsOrLoseEvidence()
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(
                repo,
                "src/no-duplicate-merge.txt");
            var first = LandingExecutor.Execute(kernel, goal, workspace);
            Assert.True(first.MainAdvanced, first.Message);
            GoalOperationJournal.Completed(repo, goal, "conductor:land", first.Message);
            var mainBefore = ReadGit(repo, "rev-parse", "main");
            var integrationBefore = ReadGit(repo, "rev-parse", LandingExecutor.IntegrationBranchName);
            var commitCountBefore = ReadGit(repo, "rev-list", "--count", "main");

            GoalOperationJournal.Begin(repo, goal, "conductor:land", "redundant post-landing evaluation");
            var second = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.IsType<LandingDecision.Promote>(second.Decision);
            Assert.False(second.MainAdvanced);
            Assert.Equal(mainBefore, ReadGit(repo, "rev-parse", "main"));
            Assert.Equal(integrationBefore, ReadGit(repo, "rev-parse", LandingExecutor.IntegrationBranchName));
            Assert.Equal(commitCountBefore, ReadGit(repo, "rev-list", "--count", "main"));

            GoalOperationJournal.Failed(repo, goal, "conductor:land", second.Message);
            Assert.True(GoalOperationJournal.HasCompletedLandingEvidence(
                GoalOperationJournal.Read(repo, goal.Id)));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void PostLandingReachabilityFailure_UsesDistinctEscalation()
    {
        var repo = CreateGitRepository();
        var oldGitRunner = LandingExecutor.GitRunner;
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(
                repo,
                "src/post-landing-check.txt");
            var first = LandingExecutor.Execute(kernel, goal, workspace);
            Assert.True(first.MainAdvanced, first.Message);
            Assert.NotNull(first.MergeCommitSha);
            GoalOperationJournal.Completed(repo, goal, "conductor:land", first.Message);
            LandingExecutor.GitRunner = (workingDirectory, args) =>
                args is ["merge-base", "--is-ancestor", var revision, "main"] &&
                revision.Equals(first.MergeCommitSha, StringComparison.OrdinalIgnoreCase)
                    ? new GitCli.GitResult(1, string.Empty, string.Empty)
                    : oldGitRunner(workingDirectory, args);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.True(LandingExecutor.IsPostLandingConfirmationEscalation(escalation.Reason));
            Assert.Contains("landed previously", escalation.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("not passed", escalation.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            LandingExecutor.GitRunner = oldGitRunner;
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void UnacceptedGoal_EscalatesWithoutLanding()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "src/not-accepted.txt", "goal work");
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            var mainBefore = ReadGit(repo, "rev-parse", "main");
            var candidate = ReadGit(worktree, "rev-parse", "HEAD")[..8];

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Equal($"no passed acceptance outcome for candidate {candidate}", escalation.Reason);
            Assert.Equal(mainBefore, ReadGit(repo, "rev-parse", "main"));
            Assert.False(IsBranchReachableFromMain(repo, goalBranch));
            var inbox = OperatorInbox.Build(
                kernel,
                [],
                WorkerProfileCatalog.Default(),
                workspace,
                goal.Id.Value[..8]);
            Assert.Contains(inbox.Items, item => item.Kind == OperatorInboxKind.LandingEscalation);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void DirectLandingRecordsAcceptanceOwnedCriterionEvidenceBeforeMainMutation()
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/direct-evidence.txt");
            var candidateSha = ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
                goal.Objective,
                ["The deterministic full acceptance gate passes."],
                VerificationClass.TestVerifiable,
                [],
                []));
            kernel.MapCriterionEvidenceOwner(
                goal.Id,
                0,
                1,
                CriterionEvidenceOwner.Acceptance,
                "test",
                CriterionEvidenceScopes.FullAcceptanceGate,
                expectedCandidateSha: candidateSha);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced, result.Message);
            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
            Assert.Equal(candidateSha, obligation.CandidateSha);
            Assert.Equal($"full-acceptance:{candidateSha}", obligation.ReceiptId);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void DirectLandingWithOperatorOwnedCriterionHoldsBeforeMainMutation()
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/direct-held.txt");
            var candidateSha = ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var mainBefore = ReadGit(repo, "rev-parse", "main");
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
                goal.Objective,
                ["The deterministic full acceptance gate passes.", "The operator observes the native result."],
                VerificationClass.TestVerifiable,
                [],
                []));
            kernel.MapCriterionEvidenceOwner(
                goal.Id,
                0,
                1,
                CriterionEvidenceOwner.Acceptance,
                "test",
                CriterionEvidenceScopes.FullAcceptanceGate,
                expectedCandidateSha: candidateSha);
            kernel.MapCriterionEvidenceOwner(
                goal.Id,
                1,
                1,
                CriterionEvidenceOwner.Operator,
                "test",
                "operator:native-observation",
                expectedCandidateSha: candidateSha);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            Assert.Equal(mainBefore, ReadGit(repo, "rev-parse", "main"));
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Contains("required criterion evidence remains outstanding", escalation.Reason, StringComparison.Ordinal);
            Assert.Contains(nameof(CriterionEvidenceOwner.Operator), escalation.Reason, StringComparison.Ordinal);
            var obligations = goal.CriterionEvidenceObligations.OrderBy(item => item.CriterionIndex).ToArray();
            Assert.Equal(CriterionEvidenceState.Satisfied, obligations[0].State);
            Assert.Equal(CriterionEvidenceState.Pending, obligations[1].State);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void DirectLandingCannotRecordAcceptanceOwnedCriterionWithoutMatchingPassedGate()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "src/no-gate-evidence.txt", "goal work");
            var candidateSha = ReadGit(repo, "rev-parse", goalBranch);
            var mainBefore = ReadGit(repo, "rev-parse", "main");
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
                goal.Objective,
                ["The deterministic full acceptance gate passes."],
                VerificationClass.TestVerifiable,
                [],
                []));
            kernel.MapCriterionEvidenceOwner(
                goal.Id,
                0,
                1,
                CriterionEvidenceOwner.Acceptance,
                "test",
                CriterionEvidenceScopes.FullAcceptanceGate,
                expectedCandidateSha: candidateSha);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            Assert.Equal(mainBefore, ReadGit(repo, "rev-parse", "main"));
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Contains("no deterministic passed acceptance outcome matches candidate", escalation.Reason, StringComparison.Ordinal);
            Assert.Equal(
                CriterionEvidenceState.Pending,
                Assert.Single(goal.CriterionEvidenceObligations).State);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Theory(DisplayName = "LandingExecutor accepted sibling cannot carry an unaccepted candidate onto main")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void AcceptedSiblingCannotCarryUnacceptedCandidateOntoMain(bool attemptUnacceptedGoalFirst)
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, unaccepted) = CreateVerifiedGoal(repo);
            var unacceptedBranch = GoalWorktrees.BranchName(unaccepted.Id);
            AddGoalBranchCommit(repo, unacceptedBranch, "src/refused.txt", "must remain off main");
            var unacceptedWorktree = GoalWorktrees.Ensure(repo, unaccepted.Id);

            if (attemptUnacceptedGoalFirst)
            {
                var refused = LandingExecutor.Execute(kernel, unaccepted, workspace);
                Assert.False(refused.MainAdvanced);
                Assert.Equal(
                    $"no passed acceptance outcome for candidate {ReadGit(unacceptedWorktree, "rev-parse", "HEAD")[..8]}",
                    Assert.IsType<LandingDecision.Escalate>(refused.Decision).Reason);
            }

            var (siblingKernel, sibling) = CreateVerifiedGoal(repo);
            var siblingBranch = GoalWorktrees.BranchName(sibling.Id);
            AddGoalBranchCommit(repo, siblingBranch, "src/accepted.txt", "independent change");
            var siblingWorktree = GoalWorktrees.Ensure(repo, sibling.Id);
            GoalOperationJournal.AcceptancePassed(
                repo,
                sibling,
                "conductor:acceptance",
                ReadGit(siblingWorktree, "rev-parse", "HEAD"),
                ReadGit(repo, "rev-parse", "main"),
                "passing acceptance for the sibling");

            var landed = LandingExecutor.Execute(siblingKernel, sibling, workspace);

            Assert.True(landed.MainAdvanced, landed.Message);
            Assert.Equal("independent change", ReadGit(repo, "show", "main:src/accepted.txt"));
            Assert.False(
                GitCli.Run(repo, "cat-file", "-e", "main:src/refused.txt").Succeeded,
                "The sibling landing carried an unaccepted candidate onto main after that candidate was refused.");
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void PassedCandidateWithProspectiveWaitEscalatesWithHoldDetails()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "src/pending-evidence.txt", "goal work");
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            GoalOperationJournal.AcceptancePassed(
                repo,
                goal,
                "conductor:acceptance",
                ReadGit(worktree, "rev-parse", "HEAD"),
                ReadGit(repo, "rev-parse", "main"),
                "passing acceptance for the candidate");
            var wait = kernel.RequestHumanInput(
                goal.Id,
                goal.Tasks.Single().Id,
                "Observe the accepted candidate.",
                HumanWaitKind.ProspectiveAcceptanceEvidence);
            var mainBefore = ReadGit(repo, "rev-parse", "main");

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            Assert.Equal(mainBefore, ReadGit(repo, "rev-parse", "main"));
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Contains("acceptance passed", escalation.Reason, StringComparison.Ordinal);
            Assert.Contains(wait.Id.Value[..8], escalation.Reason, StringComparison.Ordinal);
            Assert.Contains(nameof(HumanWaitKind.ProspectiveAcceptanceEvidence), escalation.Reason, StringComparison.Ordinal);
            Assert.NotEqual("acceptance verification not passed", escalation.Reason);
            var inbox = OperatorInbox.Build(
                kernel,
                [],
                WorkerProfileCatalog.Default(),
                workspace,
                goal.Id.Value[..8]);
            var landingEscalation = Assert.Single(inbox.Items.Where(item => item.Kind == OperatorInboxKind.LandingEscalation));
            Assert.Contains(escalation.Reason, landingEscalation.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void HoldDescriptionCapsPendingWaitIdsAtFive()
    {
        var repo = CreateGitRepository();
        try
        {
            var (_, kernel, goal) = CreateAcceptedCandidate(repo, "src/bounded-waits.txt");
            var waits = Enumerable.Range(1, 6)
                .Select(index => kernel.RequestHumanInput(
                    goal.Id,
                    goal.Tasks.Single().Id,
                    $"Observe candidate condition {index}.",
                    HumanWaitKind.ProspectiveAcceptanceEvidence))
                .OrderBy(wait => wait.RequestedAt)
                .ThenBy(wait => wait.Id.Value, StringComparer.Ordinal)
                .ToArray();

            var hold = GoalAcceptanceStatusProjector.Build(kernel, goal, repo).AcceptanceHoldDescription;

            Assert.NotNull(hold);
            Assert.All(waits.Take(5), wait => Assert.Contains(wait.Id.Value[..8], hold, StringComparison.Ordinal));
            Assert.DoesNotContain(waits[5].Id.Value[..8], hold, StringComparison.Ordinal);
            Assert.Contains("and 1 more", hold, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor refusal leaves the candidate eligible for a later accepted landing")]
    public void RefusedGoalCanLandAfterAcceptanceIsRecorded()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "src/later-accepted.txt", "eligible after acceptance");
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);

            var refused = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(refused.MainAdvanced);
            Assert.False(GitCli.Run(repo, "rev-parse", "--verify", "--quiet", "refs/heads/integration").Succeeded);
            GoalOperationJournal.AcceptancePassed(
                repo,
                goal,
                "conductor:acceptance",
                ReadGit(worktree, "rev-parse", "HEAD"),
                ReadGit(repo, "rev-parse", "main"),
                "passing acceptance after the earlier refusal");

            var landed = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(landed.MainAdvanced, landed.Message);
            Assert.Equal("eligible after acceptance", ReadGit(repo, "show", "main:src/later-accepted.txt"));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor holds and preserves integration state ahead of bound main")]
    public void IntegrationAheadOfBoundMainIsHeldWithoutMutation()
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/candidate.txt");
            RunGit(repo, "checkout", "-b", LandingExecutor.IntegrationBranchName);
            AppendCommit(repo, "src/ahead.txt", "unlanded integration state");
            RunGit(repo, "checkout", "main");
            var mainBefore = ReadGit(repo, "rev-parse", "main");
            var integrationBefore = ReadGit(repo, "rev-parse", LandingExecutor.IntegrationBranchName);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            Assert.Contains("state not present on bound main", result.Message, StringComparison.Ordinal);
            Assert.Equal(mainBefore, ReadGit(repo, "rev-parse", "main"));
            Assert.Equal(integrationBefore, ReadGit(repo, "rev-parse", LandingExecutor.IntegrationBranchName));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor recovers an interrupted publication by restoring integration predecessor")]
    public void InterruptedPublicationRestoresIntegrationPredecessorBeforeRetry()
    {
        var repo = CreateGitRepository();
        var previousGitRunner = LandingExecutor.GitRunner;
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/interrupted.txt");
            LandingExecutor.GitRunner = (workingDirectory, args) =>
                args is ["update-ref", "refs/heads/main", _, _]
                    ? throw new InvalidOperationException("simulated process interruption during main publication")
                    : GitCli.Run(workingDirectory, args);

            _ = Assert.Throws<InvalidOperationException>(() => LandingExecutor.Execute(kernel, goal, workspace));
            Assert.True(GitCli.Run(repo, "rev-parse", "--verify", "--quiet", "refs/heads/integration").Succeeded);
            LandingExecutor.GitRunner = previousGitRunner;

            var retry = LandingExecutor.Execute(kernel, goal, workspace, mutationBlocker: () => "stop after recovery");

            Assert.False(retry.MainAdvanced);
            Assert.False(GitCli.Run(repo, "rev-parse", "--verify", "--quiet", "refs/heads/integration").Succeeded);
            Assert.False(GoalOperationJournal.HasDurableLandingIntent(GoalOperationJournal.Read(repo, goal.Id)));
        }
        finally
        {
            LandingExecutor.GitRunner = previousGitRunner;
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor recovery reconciles an ancestor publication without rewinding a newer main checkout")]
    public void RecoveredAncestorPublicationDoesNotRewriteCheckoutWhenMainHasAdvanced()
    {
        var repo = CreateGitRepository();
        var previousGitRunner = LandingExecutor.GitRunner;
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/recovered-ancestor.txt");
            var boundMain = ReadGit(repo, "rev-parse", "main");
            var candidate = ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            RunGit(repo, "update-ref", "refs/heads/main", candidate, boundMain);
            AppendCommit(repo, "src/sibling-after-recovery.txt", "newer sibling publication");
            GoalOperationJournal.RecordLandingIntent(
                repo,
                goal,
                GoalWorktrees.BranchName(goal.Id),
                LandingExecutor.IntegrationBranchName,
                candidate,
                "fixture recovered ancestor",
                boundMainRevision: boundMain);
            var readTreeCalls = 0;
            LandingExecutor.GitRunner = (workingDirectory, args) =>
            {
                if (args is ["read-tree", "-m", "-u", _, _])
                {
                    readTreeCalls++;
                }
                return GitCli.Run(workingDirectory, args);
            };

            var recovered = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(recovered.MainAdvanced, recovered.Message);
            Assert.Equal(0, readTreeCalls);
            Assert.Equal("newer sibling publication", ReadGit(repo, "show", "main:src/sibling-after-recovery.txt"));
        }
        finally
        {
            LandingExecutor.GitRunner = previousGitRunner;
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor recovery holds when its ancestor probe drains late")]
    public void RecoveredPublicationDrainTimeoutFailsClosed()
    {
        var repo = CreateGitRepository();
        var previousGitRunner = LandingExecutor.GitRunner;
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/recovered-drain-timeout.txt");
            var boundMain = ReadGit(repo, "rev-parse", "main");
            var candidate = ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            RunGit(repo, "update-ref", "refs/heads/main", candidate, boundMain);
            GoalOperationJournal.RecordLandingIntent(
                repo,
                goal,
                GoalWorktrees.BranchName(goal.Id),
                LandingExecutor.IntegrationBranchName,
                candidate,
                "fixture drain timeout",
                boundMainRevision: boundMain);
            LandingExecutor.GitRunner = (workingDirectory, args) =>
                args is ["merge-base", "--is-ancestor", _, _]
                    ? new GitCli.GitResult(0, string.Empty, string.Empty, DrainTimedOut: true)
                    : GitCli.Run(workingDirectory, args);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            Assert.Contains("timed out while draining", result.Message, StringComparison.Ordinal);
            Assert.True(GoalOperationJournal.HasDurableLandingIntent(GoalOperationJournal.Read(repo, goal.Id)));
        }
        finally
        {
            LandingExecutor.GitRunner = previousGitRunner;
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor recovers a completed legacy landing intent without a bound main predecessor")]
    public void CompletedPublicationWithLegacyIntentIsReconciledBeforeMissingPredecessorHold()
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/recovered.txt");
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            var candidate = ReadGit(repo, "rev-parse", goalBranch);
            var mainBefore = ReadGit(repo, "rev-parse", "main");
            RunGit(repo, "update-ref", "refs/heads/main", candidate, mainBefore);
            GoalOperationJournal.RecordLandingIntent(
                repo,
                goal,
                goalBranch,
                LandingExecutor.IntegrationBranchName,
                candidate,
                "fixture legacy intent");

            var recovered = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(recovered.MainAdvanced, recovered.Message);
            Assert.Equal(candidate, recovered.MergeCommitSha);
            Assert.Contains("src/recovered.txt", recovered.ChangedFiles!);
            Assert.Contains(
                GoalOperationJournal.Read(repo, goal.Id).Entries,
                entry => entry.Operation.Equals("conductor:landing-recovery", StringComparison.Ordinal) &&
                    entry.Status == GoalOperationStatus.Completed);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor recovery uses the bound main revision and tombstones a reconciled intent")]
    public void RecoveredNoOpCandidateUsesBoundMainAndIsNotReplayed()
    {
        var repo = CreateGitRepository();
        try
        {
            AppendCommit(repo, "baseline.txt", "baseline change");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var boundMain = ReadGit(repo, "rev-parse", "main");
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            RunGit(repo, "branch", goalBranch, boundMain);
            GoalOperationJournal.RecordLandingIntent(
                repo,
                goal,
                goalBranch,
                LandingExecutor.IntegrationBranchName,
                boundMain,
                "fixture recovered no-op",
                boundMainRevision: boundMain);

            var recovered = LandingExecutor.Execute(kernel, goal, workspace);
            var afterRecovery = GoalOperationJournal.Read(repo, goal.Id);

            Assert.True(recovered.MainAdvanced, recovered.Message);
            Assert.Empty(recovered.ChangedFiles!);
            Assert.False(GoalOperationJournal.HasDurableLandingIntent(afterRecovery));
            Assert.Contains(afterRecovery.Entries, entry =>
                entry.Operation == "conductor:landing-recovery" && entry.Status == GoalOperationStatus.Completed);

            var replay = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(replay.MainAdvanced);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor recovery applies post-landing state effects before closing its intent")]
    public void RecoveredPublicationAppliesStateEffectsAndDoesNotReplay()
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, "src/recovered-effect.txt");
            var boundMain = ReadGit(repo, "rev-parse", "main");
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            var goalWorktree = GoalWorktrees.Ensure(repo, goal.Id);
            Directory.CreateDirectory(Path.Combine(goalWorktree, ".orchestrator-proposals"));
            File.WriteAllText(Path.Combine(goalWorktree, ".orchestrator-proposals", "backlog-add-recovered.md"), """
                ---
                kind: backlog-add
                title: Recovered follow-up
                ---
                Recovered publication proposal.
                """.ReplaceLineEndings("\n"));
            RunGit(goalWorktree, "add", ".orchestrator-proposals/backlog-add-recovered.md");
            RunGit(goalWorktree, "commit", "-m", "Add recovered state effect");
            var candidate = ReadGit(goalWorktree, "rev-parse", "HEAD");
            RunGit(repo, "update-ref", "refs/heads/main", candidate, boundMain);
            GoalOperationJournal.RecordLandingIntent(
                repo,
                goal,
                goalBranch,
                LandingExecutor.IntegrationBranchName,
                candidate,
                "fixture recovered publication",
                boundMainRevision: boundMain);

            var recovered = LandingExecutor.Execute(kernel, goal, workspace);
            var items = new BacklogStore(workspace.BacklogStorePath).ListAsync(includeAll: true).GetAwaiter().GetResult();
            var replay = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(recovered.MainAdvanced, recovered.Message);
            Assert.Contains(".orchestrator-proposals/backlog-add-recovered.md", recovered.ChangedFiles!);
            Assert.Equal("Recovered follow-up", Assert.Single(items).Title);
            Assert.False(GoalOperationJournal.HasDurableLandingIntent(GoalOperationJournal.Read(repo, goal.Id)));
            Assert.False(replay.MainAdvanced);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_applies_landed_backlog_add_proposal_once")]
    public void LandingExecutorAppliesLandedBacklogAddProposalOnce()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            RunGit(repo, "checkout", "-b", goalBranch);
            Directory.CreateDirectory(Path.Combine(repo, ".orchestrator-proposals"));
            File.WriteAllText(Path.Combine(repo, ".orchestrator-proposals", "backlog-add-proposed-follow-up.md"), """
                ---
                kind: backlog-add
                title: Proposed follow-up
                ---
                Body from a landed proposal.
                """.ReplaceLineEndings("\n"));
            RunGit(repo, "add", ".orchestrator-proposals/backlog-add-proposed-follow-up.md");
            RunGit(repo, "commit", "-m", "Add state-effect proposal");
            RunGit(repo, "checkout", "main");

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced);
            var items = new BacklogStore(workspace.BacklogStorePath).ListAsync(includeAll: true).GetAwaiter().GetResult();
            var item = Assert.Single(items);
            Assert.Equal("proposed-follow-up", item.Id);
            Assert.Equal("Proposed follow-up", item.Title);
            Assert.Equal(goal.Id.Value, item.SourceGoalId);
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation.StartsWith("conductor:state-effect:", StringComparison.Ordinal) &&
                entry.Status == GoalOperationStatus.Completed);
            Assert.Contains(kernel.GetTimeline(goal.Id), evt =>
                evt.Message.Contains("State-effect proposal applied", StringComparison.Ordinal));

            var reapplied = StateEffectProposalApplier.ApplyLandedProposals(
                kernel,
                goal,
                workspace,
                [".orchestrator-proposals/backlog-add-proposed-follow-up.md"]);

            Assert.Single(reapplied);
            Assert.False(reapplied[0].Applied);
            var afterReapply = new BacklogStore(workspace.BacklogStorePath).ListAsync(includeAll: true).GetAwaiter().GetResult();
            Assert.Single(afterReapply);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor rereads circuit at integration merge mutation boundary")]
    public void LandingExecutorBlocksWhenCircuitOpensAfterAdmission()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            RunGit(repo, "checkout", "-b", goalBranch);
            File.WriteAllText(Path.Combine(repo, "must-not-land.txt"), "blocked");
            RunGit(repo, "add", "must-not-land.txt");
            RunGit(repo, "commit", "-m", "Candidate goal work");
            RunGit(repo, "checkout", "main");
            var mainBefore = GoalAcceptanceVerifier.ResolveGitText(repo, "rev-parse", "HEAD")!.Trim();
            var goalWorktree = GoalWorktrees.Ensure(repo, goal.Id);
            GoalOperationJournal.AcceptancePassed(
                repo,
                goal,
                "conductor:acceptance",
                ReadGit(goalWorktree, "rev-parse", "HEAD"),
                mainBefore,
                "passing acceptance for the candidate");
            var checks = 0;

            var result = LandingExecutor.Execute(
                kernel,
                goal,
                workspace,
                mutationBlocker: () =>
                    Interlocked.Increment(ref checks) == 1
                        ? null
                        : "acceptance circuit became Pending");

            Assert.False(result.MainAdvanced);
            Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Contains("held before merge", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, checks);
            Assert.Equal(mainBefore, GoalAcceptanceVerifier.ResolveGitText(repo, "rev-parse", "HEAD")!.Trim());
            Assert.False(GitCli.Run(repo, "rev-parse", "--verify", "--quiet", "refs/heads/integration").Succeeded);
            Assert.False(File.Exists(Path.Combine(repo, "must-not-land.txt")));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "CLI land result carries authoritative changed paths without a goal worktree")]
    public void LandingResultCarriesChangedPathsFromBranchWithoutWorktree()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            const string enginePath =
                "tests/canary-fixture/branch-only.txt";
            AddGoalBranchCommit(repo, goalBranch, enginePath, "namespace BranchOnly;");
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced, result.Message);
            Assert.Equal([enginePath], result.ChangedFiles);
            Assert.True(File.Exists(Path.Combine(repo, enginePath)));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_green_gate_non_approval_diff_auto_promotes_without_ownership_hold")]
    public void LandingExecutorGreenGateNonApprovalDiffAutoPromotesWithoutOwnershipHold()
    {
        foreach (var policy in new ConductorAutonomyPolicy?[]
        {
            null,
            ConductorAutonomyPolicy.Conservative,
            ConductorAutonomyPolicy.Manual,
            ConductorAutonomyPolicy.Permissive
        })
        {
            var repo = CreateGitRepository();
            try
            {
                var policyName = policy?.Name ?? "null";
                var workspace = OrchestratorWorkspace.ForDirectory(repo);
                var (kernel, goal) = CreateVerifiedGoal(repo);
                var goalBranch = GoalWorktrees.BranchName(goal.Id);
                AddGoalBranchCommit(repo, goalBranch, "src/Mcg.AgentOrchestrator.App/Feature.cs", "namespace TestApp; internal sealed class Feature;");

                var result = LandingExecutor.Execute(kernel, goal, workspace, policy: policy);

                Assert.True(result.MainAdvanced, policyName);
                var inbox = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
                Assert.DoesNotContain(inbox.Items, item => item.Kind == OperatorInboxKind.OwnershipHold);
            }
            finally
            {
                TryDeleteDirectory(repo);
            }
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_intent_write_failure_prevents_main_merge")]
    public void LandingExecutorIntentWriteFailurePreventsMainMerge()
    {
        var repo = CreateGitRepository();
        var previousHook = GoalOperationJournal.BeforeLandingIntentAppend;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "src/intent-write-fails.txt", "goal work");
            var anchor = $"refs/orchestrator/landing/{goal.Id.Value}";
            GoalOperationJournal.BeforeLandingIntentAppend = intent =>
            {
                Assert.Equal(intent.MergeCommitSha, ReadGit(repo, "rev-parse", "--verify", anchor));
                throw new InvalidOperationException("simulated intent write failure");
            };

            var ex = Assert.Throws<InvalidOperationException>(() => LandingExecutor.Execute(kernel, goal, workspace));

            Assert.Contains("simulated intent write failure", ex.Message);
            Assert.False(IsBranchReachableFromMain(repo, goalBranch));
            Assert.False(GitCli.Run(repo, "rev-parse", "--verify", "--quiet", anchor).Succeeded);
            Assert.False(GoalOperationJournal.HasDurableLandingIntent(GoalOperationJournal.Read(repo, goal.Id)));
        }
        finally
        {
            GoalOperationJournal.BeforeLandingIntentAppend = previousHook;
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_main_merge_failure_tombstones_landing_intent")]
    public void LandingExecutorMainMergeFailureTombstonesLandingIntent()
    {
        var repo = CreateGitRepository();
        var previousGitRunner = LandingExecutor.GitRunner;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "src/main-merge-fails.txt", "goal work");
            LandingExecutor.GitRunner = (workingDirectory, args) =>
                args is ["update-ref", "refs/heads/main", _, _]
                    ? new GitCli.GitResult(1, string.Empty, "simulated main merge failure")
                    : GitCli.Run(workingDirectory, args);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            Assert.Contains("simulated main merge failure", result.Message);
            Assert.False(IsBranchReachableFromMain(repo, goalBranch));
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.False(GoalOperationJournal.HasDurableLandingIntent(journal));
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation == GoalOperationJournal.LandingIntentOperation &&
                entry.Status == GoalOperationStatus.Failed);
        }
        finally
        {
            LandingExecutor.GitRunner = previousGitRunner;
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_success_records_one_landing_intent_and_reaches_main")]
    public void LandingExecutorSuccessRecordsOneLandingIntentAndReachesMain()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "src/intent-success.txt", "goal work");

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced);
            Assert.True(IsBranchReachableFromMain(repo, goalBranch));
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.True(GoalOperationJournal.HasDurableLandingIntent(journal));
            var intent = Assert.Single(journal.Entries.Where(entry =>
                entry.Operation == GoalOperationJournal.LandingIntentOperation &&
                entry.Status == GoalOperationStatus.Completed));
            Assert.Contains("LandingExecutor", intent.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_green_gate_approval_diff_permissive_auto_promotes_without_ownership_hold")]
    public void LandingExecutorGreenGateApprovalDiffPermissiveAutoPromotesWithoutOwnershipHold()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal, _, _, _) = CreateVerifiedOwnershipApprovalGoal(repo);

            var result = LandingExecutor.Execute(kernel, goal, workspace, policy: ConductorAutonomyPolicy.Permissive);

            Assert.True(result.MainAdvanced);
            Assert.IsType<LandingDecision.Promote>(result.Decision);
            Assert.True(IsBranchReachableFromMain(repo, GoalWorktrees.BranchName(goal.Id)));
            var inbox = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
            Assert.DoesNotContain(inbox.Items, item => item.Kind == OperatorInboxKind.OwnershipHold);
            Assert.DoesNotContain(inbox.Items, item => item.Kind == OperatorInboxKind.LandingEscalation);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_green_gate_approval_diff_conservative_records_ownership_hold")]
    public void LandingExecutorGreenGateApprovalDiffConservativeRecordsOwnershipHold()
    {
        AssertOwnershipHoldEscalates(ConductorAutonomyPolicy.Conservative);
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_green_gate_approval_diff_manual_records_ownership_hold")]
    public void LandingExecutorGreenGateApprovalDiffManualRecordsOwnershipHold()
    {
        AssertOwnershipHoldEscalates(ConductorAutonomyPolicy.Manual);
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_green_gate_approval_diff_null_policy_records_ownership_hold")]
    public void LandingExecutorGreenGateApprovalDiffNullPolicyRecordsOwnershipHold()
    {
        AssertOwnershipHoldEscalates(null);
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_git_diff_drain_timeout_fails_closed_even_with_empty_stdout")]
    public void LandingExecutorGitDiffDrainTimeoutFailsClosedEvenWithEmptyStdout()
    {
        var repo = CreateGitRepository();
        var previousGitRunner = LandingExecutor.GitRunner;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "src/Mcg.AgentOrchestrator.App/TimeoutTouched.cs", "namespace TestApp; internal sealed class TimeoutTouched;");
            LandingExecutor.GitRunner = (workingDirectory, args) =>
                args.Length == 3 &&
                args[0].Equals("diff", StringComparison.Ordinal) &&
                args[1].Equals("--name-only", StringComparison.Ordinal)
                    ? new GitCli.GitResult(0, string.Empty, string.Empty, DrainTimedOut: true)
                    : GitCli.Run(workingDirectory, args);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Contains("diff scope unknown", escalation.Reason);
            Assert.Contains("drain timed out", escalation.Reason);
            var inbox = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
            var item = Assert.Single(inbox.Items.Where(item => item.Kind == OperatorInboxKind.LandingEscalation));
            Assert.Contains("diff scope unknown", item.Message);
        }
        finally
        {
            LandingExecutor.GitRunner = previousGitRunner;
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Goal_mark_landed_persists_landed_state_before_cleanup_needed_enqueue")]
    public void GoalMarkLandedPersistsLandedStateBeforeCleanupNeededEnqueue()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var innerRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var hooks = new GoalWorktreeCleanupHooks
            {
                // Observe persisted debt irrespective of expiry; this probe tests ordering, not clocks.
                CleanupUtcNow = static () => DateTimeOffset.UnixEpoch
            };
            var stateRepository = new CountingStateRepository(
                innerRepository,
                () => GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id, hooks) is not null);
            innerRepository.SaveAsync(kernel).GetAwaiter().GetResult();
            stateRepository.ResetSaveCount();

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            var changed = CliPersistentStateRunner.ExecuteCommand(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force"],
                stateRepository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Assert.True(changed);
            Assert.True(stateRepository.SaveCount > 0, "landed state was not saved");
            Assert.True(
                stateRepository.SaveCountBeforeCleanupNeeded > 0,
                "cleanup-needed was enqueued before a landed-state save");
            var persisted = innerRepository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id);
            Assert.Equal(GoalStatus.Completed, persisted.Status);
            // Prove the same predicate eventually sees debt, so an always-false read cannot pass.
            Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id, hooks));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Goal_mark_landed_closes_linked_source_backlog_item")]
    public async Task GoalMarkLandedClosesLinkedSourceBacklogItem()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new BacklogStore(workspace.BacklogStorePath);
            var item = await store.AddAsync("Goal mark landed source");
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);
            await repository.SaveAsync(kernel);

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            var changed = CliPersistentStateRunner.ExecuteCommand(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            var closed = await store.GetByExactIdAsync(item.Id);
            Assert.True(changed);
            Assert.NotNull(closed);
            Assert.Equal(BacklogItemStatus.Done, closed!.Status);
            var note = Assert.Single(closed.Notes);
            Assert.Contains(goal.Id.Value, note.Text);
            Assert.Contains("integrateCommit=", note.Text);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Terminal_sweep_cleanup_failure_reports_blocker_without_failing_landed_goal")]
    public void TerminalSweepCleanupFailureReportsBlockerWithoutFailingLandedGoal()
    {
        var repo = CreateGitRepository();
        try
        {
            var (kernel, goal) = CreateCompletedGoalWithLeftoverWorkspace(repo);
            GoalWorktrees.RecordGoalCleanupNeeded(
                repo,
                goal.Id,
                "remove:simulated-cleanup-failure",
                new GoalWorktreeCleanupHooks { CleanupWarningSink = _ => { } });

            var result = TerminalGoalSweep.Run(kernel, repo, goal.Id);

            var goalResult = Assert.Single(result.Goals);
            var blocker = Assert.Single(goalResult.Blockers);
            Assert.Equal("completed-worktree-cleanup-needed", blocker.Kind);
            Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            Assert.DoesNotContain(
                kernel.GetGoal(goal.Id).Timeline,
                evt => evt.Message.Contains("AcceptanceFailed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Terminal_sweep_cleanup_second_run_is_noop_after_success")]
    public void TerminalSweepCleanupSecondRunIsNoopAfterSuccess()
    {
        var repo = CreateGitRepository();
        try
        {
            var (kernel, goal) = CreateCompletedGoalWithLeftoverWorkspace(repo);

            var first = TerminalGoalSweep.Run(kernel, repo, goal.Id);
            var second = TerminalGoalSweep.Run(kernel, repo, goal.Id);

            Assert.Contains(first.Goals, item => item.Repairs.Any(repair => repair.Kind == "merged-branch-cleanup"));
            Assert.Empty(second.Goals);
            Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Remote_mirror_enabled_pushes_main_goal_branch_and_tags_to_bare_remote")]
    public void RemoteMirrorEnabledPushesMainGoalBranchAndTagsToBareRemote()
    {
        var repo = CreateGitRepository();
        var remote = CreateBareRepository(repo, "mirror");
        using var _ = WithMirrorTestHooks();
        try
        {
            RunGit(repo, "remote", "add", "mirror", remote);
            WriteMirrorConfig(repo, "mirror");
            RunGit(repo, "tag", "mirror-test-tag");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "mirrored.txt", "mirrored");

            var landing = LandingExecutor.Execute(kernel, goal, workspace);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            Assert.NotEmpty(RemoteGitMirror.ReadState(repo).Entries);
            var mirror = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);

            Assert.True(landing.MainAdvanced);
            var stateEntry = RemoteGitMirror.ReadState(repo).Entries.Single();
            Assert.True(stateEntry.Status == RemoteMirrorEntryStatus.Succeeded, stateEntry.LastError);
            Assert.Contains(mirror.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorSucceeded);
            AssertGitRef(remote, "refs/heads/main");
            AssertGitRef(remote, $"refs/heads/{goalBranch}");
            AssertGitRef(remote, "refs/tags/mirror-test-tag");
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation == "conductor:mirror" &&
                entry.Status == GoalOperationStatus.Completed &&
                entry.Detail?.Contains("Mirror enqueued", StringComparison.Ordinal) == true);
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation == "conductor:mirror:mirror" &&
                entry.Status == GoalOperationStatus.Completed &&
                entry.Detail?.Contains("MirrorSucceeded", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(journal.InterruptedOperations, entry =>
                entry.Operation.StartsWith("conductor:mirror", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(repo);
            TryDeleteDirectory(remote);
        }
    }

    [Xunit.Fact(DisplayName = "Remote_mirror_success_does_not_leave_interrupted_enqueue_operation")]
    public void RemoteMirrorSuccessDoesNotLeaveInterruptedEnqueueOperation()
    {
        var repo = CreateGitRepository();
        using var _ = WithMirrorTestHooks();
        var oldGitRunner = RemoteGitMirror.GitRunner;
        try
        {
            WriteMirrorConfig(repo, "mirror");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "journal-mirror.txt", "journal");

            var landing = LandingExecutor.Execute(kernel, goal, workspace);
            RemoteGitMirror.GitRunner = (workingDirectory, args) =>
                args.Count > 0 && args[0] == "push"
                    ? new GitCli.GitResult(0, string.Empty, string.Empty)
                    : oldGitRunner(workingDirectory, args);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            var mirror = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);
            var journal = GoalOperationJournal.Read(repo, goal.Id);

            Assert.True(landing.MainAdvanced);
            Assert.Contains(mirror.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorSucceeded);
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation == "conductor:mirror" &&
                entry.Status == GoalOperationStatus.Completed);
            Assert.DoesNotContain(journal.InterruptedOperations, entry =>
                entry.Operation.StartsWith("conductor:mirror", StringComparison.Ordinal));
        }
        finally
        {
            RemoteGitMirror.GitRunner = oldGitRunner;
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Theory(DisplayName = "Remote_mirror_disabled_or_unconfigured_does_not_push")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RemoteMirrorDisabledOrUnconfiguredDoesNotPush(bool writeDisabledConfig)
    {
        var repo = CreateGitRepository();
        var remote = CreateBareRepository(repo, "mirror");
        using var _ = WithMirrorTestHooks();
        try
        {
            RunGit(repo, "remote", "add", "mirror", remote);
            if (writeDisabledConfig)
            {
                WriteMirrorConfig(repo, false, "mirror");
            }

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "no-mirror.txt", "no mirror");

            var landing = LandingExecutor.Execute(kernel, goal, workspace);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            var mirror = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);

            Assert.True(landing.MainAdvanced);
            Assert.Empty(mirror.Outcomes);
            AssertGitMissingRef(remote, "refs/heads/main");
            Assert.False(File.Exists(RemoteGitMirror.StatePath(repo)));
        }
        finally
        {
            TryDeleteDirectory(repo);
            TryDeleteDirectory(remote);
        }
    }

    [Xunit.Fact(DisplayName = "Terminal_sweep_does_not_run_remote_mirror_push_inline")]
    public void TerminalSweepDoesNotRunRemoteMirrorPushInline()
    {
        var repo = CreateGitRepository();
        var remote = CreateBareRepository(repo, "mirror");
        using var hooks = WithMirrorTestHooks();
        var pushCount = 0;
        var oldGitRunner = RemoteGitMirror.GitRunner;
        RemoteGitMirror.GitRunner = (workingDirectory, args) =>
        {
            if (args.Count > 0 && args[0] == "push")
            {
                Interlocked.Increment(ref pushCount);
            }

            return oldGitRunner(workingDirectory, args);
        };

        try
        {
            RunGit(repo, "remote", "add", "mirror", remote);
            WriteMirrorConfig(repo, "mirror");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "not-inline.txt", "not inline");

            var landing = LandingExecutor.Execute(kernel, goal, workspace);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            TerminalGoalSweep.Run(kernel, repo, goal.Id);

            Assert.True(landing.MainAdvanced);
            Assert.Equal(0, pushCount);
            Assert.Equal(RemoteMirrorEntryStatus.Pending, RemoteGitMirror.ReadState(repo).Entries.Single().Status);
            AssertGitMissingRef(remote, "refs/heads/main");
        }
        finally
        {
            RemoteGitMirror.GitRunner = oldGitRunner;
            TryDeleteDirectory(repo);
            TryDeleteDirectory(remote);
        }
    }

    [Xunit.Fact(DisplayName = "Remote_mirror_unreachable_remote_defers_and_later_retry_succeeds")]
    public void RemoteMirrorUnreachableRemoteDefersAndLaterRetrySucceeds()
    {
        var repo = CreateGitRepository();
        var remote = CreateRemotePath(repo, "mirror");
        using var _ = WithMirrorTestHooks();
        try
        {
            RunGit(repo, "remote", "add", "mirror", remote);
            WriteMirrorConfig(repo, "mirror");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "retry-mirror.txt", "retry");

            var landing = LandingExecutor.Execute(kernel, goal, workspace);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            Assert.NotEmpty(RemoteGitMirror.ReadState(repo).Entries);
            var first = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);

            Assert.True(landing.MainAdvanced);
            Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
            Assert.Contains(first.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorFailed);
            var failed = GoalOperationJournal.Read(repo, goal.Id).LatestByOperation.Single(entry =>
                entry.Operation == "conductor:mirror:mirror");
            Assert.Equal(GoalOperationStatus.Failed, failed.Status);
            Assert.Contains("classification=TRANSIENT", failed.Detail, StringComparison.Ordinal);

            Directory.CreateDirectory(remote);
            RunGit(remote, "init", "--bare");
            var second = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);

            Assert.Contains(second.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorSucceeded);
            AssertGitRef(remote, "refs/heads/main");
            AssertGitRef(remote, $"refs/heads/{goalBranch}");
        }
        finally
        {
            TryDeleteDirectory(repo);
            TryDeleteDirectory(remote);
        }
    }

    [Xunit.Fact(DisplayName = "Remote_mirror_retry_uses_landed_branch_tip_after_cleanup_deletes_goal_branch")]
    public void RemoteMirrorRetryUsesLandedBranchTipAfterCleanupDeletesGoalBranch()
    {
        var repo = CreateGitRepository();
        var remote = CreateRemotePath(repo, "mirror");
        using var _ = WithMirrorTestHooks();
        try
        {
            RunGit(repo, "remote", "add", "mirror", remote);
            WriteMirrorConfig(repo, "mirror");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "retry-after-cleanup.txt", "retry after cleanup");

            var landing = LandingExecutor.Execute(kernel, goal, workspace);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            var first = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);
            GoalOperationJournal.Completed(repo, goal, "conductor:land", "landed");
            kernel.CompleteGoal(goal.Id, "Completed after durable landing.");
            var cleanup = TerminalGoalSweep.Run(kernel, repo, goal.Id);

            Assert.True(landing.MainAdvanced);
            Assert.Contains(first.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorFailed);
            Assert.Contains(cleanup.Goals, item => item.Repairs.Any(repair => repair.Kind == "merged-branch-cleanup"));
            Assert.False(GitCli.Run(repo, "show-ref", "--verify", $"refs/heads/{goalBranch}").Succeeded);

            Directory.CreateDirectory(remote);
            RunGit(remote, "init", "--bare");
            var second = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);

            Assert.Contains(second.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorSucceeded);
            AssertGitRef(remote, $"refs/heads/{goalBranch}");
        }
        finally
        {
            TryDeleteDirectory(repo);
            TryDeleteDirectory(remote);
        }
    }

    [Xunit.Fact(DisplayName = "Remote_mirror_two_remotes_pushes_reachable_and_defers_unreachable")]
    public void RemoteMirrorTwoRemotesPushesReachableAndDefersUnreachable()
    {
        var repo = CreateGitRepository();
        var reachable = CreateBareRepository(repo, "reachable");
        var missing = CreateRemotePath(repo, "missing");
        using var _ = WithMirrorTestHooks();
        try
        {
            RunGit(repo, "remote", "add", "reachable", reachable);
            RunGit(repo, "remote", "add", "missing", missing);
            WriteMirrorConfig(repo, "reachable", "missing");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "multi-mirror.txt", "multi");

            var landing = LandingExecutor.Execute(kernel, goal, workspace);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            Assert.NotEmpty(RemoteGitMirror.ReadState(repo).Entries);
            var mirror = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);

            Assert.True(landing.MainAdvanced);
            Assert.Contains(mirror.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorSucceeded && outcome.Remote == "reachable");
            Assert.Contains(mirror.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorFailed && outcome.Remote == "missing");
            AssertGitRef(reachable, "refs/heads/main");
            AssertGitRef(reachable, $"refs/heads/{goalBranch}");
        }
        finally
        {
            TryDeleteDirectory(repo);
            TryDeleteDirectory(reachable);
            TryDeleteDirectory(missing);
        }
    }

    [Xunit.Fact(DisplayName = "Remote_mirror_processing_preserves_later_enqueued_mirror_debt")]
    public void RemoteMirrorProcessingPreservesLaterEnqueuedMirrorDebt()
    {
        var repo = CreateGitRepository();
        var remote = CreateBareRepository(repo, "mirror");
        using var _ = WithMirrorTestHooks();
        var oldGitRunner = RemoteGitMirror.GitRunner;
        var enqueuedSecondGoal = 0;
        try
        {
            RunGit(repo, "remote", "add", "mirror", remote);
            WriteMirrorConfig(repo, "mirror");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, firstGoal) = CreateVerifiedGoal(repo);
            var secondGoal = kernel.CreateGoal("Second mirror landing", [new TaskSpec(TaskId.New(), "Run Developer task.", AgentRole.Developer)]);
            kernel.ActivateGoal(secondGoal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(
                secondGoal.Id,
                secondGoal.Tasks.Single().Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            var firstBranch = GoalWorktrees.BranchName(firstGoal.Id);
            var secondBranch = GoalWorktrees.BranchName(secondGoal.Id);
            AddGoalBranchCommit(repo, firstBranch, "first-mirror.txt", "first");
            AddGoalBranchCommit(repo, secondBranch, "second-mirror.txt", "second");

            var landing = LandingExecutor.Execute(kernel, firstGoal, workspace);
            RemoteGitMirror.EnqueueAfterLanding(repo, firstGoal);
            RemoteGitMirror.GitRunner = (workingDirectory, args) =>
            {
                if (args.Count > 0 &&
                    args[0] == "push" &&
                    Interlocked.Exchange(ref enqueuedSecondGoal, 1) == 0)
                {
                    RemoteGitMirror.EnqueueAfterLanding(repo, secondGoal);
                }

                return new GitCli.GitResult(0, string.Empty, string.Empty);
            };
            var mirror = RemoteGitMirror.ProcessDue(kernel, repo, firstGoal.Id);

            Assert.True(landing.MainAdvanced);
            Assert.Contains(mirror.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorSucceeded);
            var state = RemoteGitMirror.ReadState(repo);
            Assert.Contains(state.Entries, entry =>
                entry.GoalId == firstGoal.Id.Value &&
                entry.Remote == "mirror" &&
                entry.Status == RemoteMirrorEntryStatus.Succeeded);
            Assert.Contains(state.Entries, entry =>
                entry.GoalId == secondGoal.Id.Value &&
                entry.Remote == "mirror" &&
                entry.Status == RemoteMirrorEntryStatus.Pending);
        }
        finally
        {
            RemoteGitMirror.GitRunner = oldGitRunner;
            TryDeleteDirectory(repo);
            TryDeleteDirectory(remote);
        }
    }

    private static void AssertOwnershipHoldEscalates(ConductorAutonomyPolicy? policy)
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal, developer, approvalPath, nonApprovalPath) = CreateVerifiedOwnershipApprovalGoal(repo);
            var mainBefore = ReadGit(repo, "rev-parse", "main");

            var result = LandingExecutor.Execute(kernel, goal, workspace, policy: policy);

            Assert.False(result.MainAdvanced);
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.True(LandingExecutor.IsOwnershipHoldEscalation(escalation.Reason));
            var inbox = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
            var hold = Assert.Single(inbox.Items.Where(item => item.Kind == OperatorInboxKind.OwnershipHold));
            Assert.Equal(developer.Id.Value, hold.TaskId);
            Assert.Contains(approvalPath, hold.Evidence);
            Assert.DoesNotContain(nonApprovalPath, hold.Evidence);
            Assert.DoesNotContain(inbox.Items, item => item.Kind == OperatorInboxKind.LandingEscalation);
            Assert.Equal(mainBefore, ReadGit(repo, "rev-parse", "main"));
            Assert.False(GitCli.Run(repo, "rev-parse", "--verify", "--quiet", "refs/heads/integration").Succeeded);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    internal static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Developer, string ApprovalPath, string NonApprovalPath)
        CreateVerifiedOwnershipApprovalGoal(string repo)
    {
        const string approvalPath = "src/Mcg.AgentOrchestrator.Infrastructure/OwnershipTouched.cs";
        const string nonApprovalPath = "src/Mcg.AgentOrchestrator.App/NonApprovalTouched.cs";
        var (kernel, goal) = CreateGoal(AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer);
        var developer = goal.Tasks[0];
        var tester = goal.Tasks[1];
        var reviewer = goal.Tasks[2];
        var baseCommit = ReadGit(repo, "rev-parse", "main");
        Dispatch(kernel, goal, developer, "developer");
        Dispatch(kernel, goal, tester, "tester");
        Dispatch(kernel, goal, reviewer, "reviewer");
        kernel.RecordDispatchBaseCommit(goal.Id, developer.Id, baseCommit);
        var goalBranch = GoalWorktrees.BranchName(goal.Id);
        RunGit(repo, "checkout", "-b", goalBranch);
        AppendCommit(repo, approvalPath, "namespace TestInfra; internal sealed class OwnershipTouched;");
        var developerResultCommit = ReadGit(repo, "rev-parse", "HEAD");
        AppendCommit(repo, nonApprovalPath, "namespace TestApp; internal sealed class NonApprovalTouched;");
        var testerResultCommit = ReadGit(repo, "rev-parse", "HEAD");
        RunGit(repo, "checkout", "main");
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, developerResultCommit);
        kernel.RecordDispatchBaseCommit(goal.Id, tester.Id, developerResultCommit);
        kernel.RecordDispatchResultCommit(goal.Id, tester.Id, testerResultCommit);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, baseCommit);
        kernel.RecordDispatchResultCommit(goal.Id, reviewer.Id, testerResultCommit);
        kernel.RecordTaskVerification(
            goal.Id,
            developer.Id,
            ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
        kernel.RecordTaskVerification(
            goal.Id,
            tester.Id,
            ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
        kernel.RecordTaskVerification(
            goal.Id,
            reviewer.Id,
            ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        return (kernel, goal, developer, approvalPath, nonApprovalPath);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoal(params AgentRole[] roles)
    {
        var kernel = new AgentOrchestratorKernel();
        var tasks = roles
            .Select(role => new TaskSpec(TaskId.New(), $"Run {role} task.", role))
            .ToArray();
        var goal = kernel.CreateGoal("Landing count test", tasks);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return (kernel, goal);
    }

    private static void FlakeThenPass(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        Dispatch(kernel, goal, task, "silent-agent");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "silent-agent",
            "C:\\repo",
            0,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow));
        kernel.RetryTask(goal.Id, task.Id, "Auto-retry transient empty-output dispatch flake.");
        Pass(kernel, goal, task);
    }

    private static void FailThenPass(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        Dispatch(kernel, goal, task, "failing-agent");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "failing-agent",
            "C:\\repo",
            1,
            "attempted work",
            "test failed",
            DateTimeOffset.UtcNow));
        kernel.RetryTask(goal.Id, task.Id, "Fix real failure.");
        Pass(kernel, goal, task);
    }

    private static void Pass(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        Dispatch(kernel, goal, task, "passing-agent");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "passing-agent",
            "C:\\repo",
            0,
            "WORKER_RESULT: tests pass",
            string.Empty,
            DateTimeOffset.UtcNow));
    }

    private static void Dispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string command)
    {
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "test-worker",
            command,
            "C:\\repo",
            DateTimeOffset.UtcNow));
    }

    internal static (AgentOrchestratorKernel Kernel, Goal Goal) CreateVerifiedGoal(string repo)
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer);
        var task = goal.Tasks.Single();
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        return (kernel, goal);
    }

    internal static (OrchestratorWorkspace Workspace, AgentOrchestratorKernel Kernel, Goal Goal)
        CreateAcceptedCandidate(string repo, string fileName)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(repo);
        var (kernel, goal) = CreateVerifiedGoal(repo);
        var goalBranch = GoalWorktrees.BranchName(goal.Id);
        AddGoalBranchCommit(repo, goalBranch, fileName, "goal work");
        var worktree = GoalWorktrees.Ensure(repo, goal.Id);
        var branchHead = ReadGit(worktree, "rev-parse", "HEAD");
        var mainHead = ReadGit(repo, "rev-parse", "main");
        GoalOperationJournal.AcceptancePassed(
            repo,
            goal,
            "conductor:acceptance",
            branchHead,
            mainHead,
            "passing acceptance for the candidate",
            DateTimeOffset.UtcNow.AddMinutes(-1));
        return (workspace, kernel, goal);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateCompletedGoalWithLeftoverWorkspace(string repo)
    {
        var (kernel, goal) = CreateVerifiedGoal(repo);
        var branch = GoalWorktrees.BranchName(goal.Id);
        RunGit(repo, "checkout", "-b", branch);
        File.WriteAllText(Path.Combine(repo, "landed-work.txt"), "landed");
        RunGit(repo, "add", "landed-work.txt");
        RunGit(repo, "commit", "-m", "Goal work");
        RunGit(repo, "checkout", "main");
        RunGit(repo, "merge", "--ff-only", branch);
        RunGit(repo, "worktree", "add", GoalWorktrees.WorktreePath(repo, goal.Id), branch);
        GoalOperationJournal.Completed(repo, goal, "conductor:land", "landed");
        kernel.CompleteGoal(goal.Id, "Already landed; cleanup remains.");
        return (kernel, goal);
    }

    internal static string CreateGitRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-landing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DotnetBuildEnvironmentManager.RegisterCurrentLandingTestFixtureRoot(root);
        CopyDirectory(GitRepositoryTemplate.Value, root);
        _ = CreateMigratedStateRepository(
            OrchestratorWorkspace.ForDirectory(root).SqliteStatePath);
        return root;

        static void CopyDirectory(string source, string destination)
        {
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
            }
        }
    }

    private static string CreateBareRepository(string repo, string name)
    {
        var root = CreateRemotePath(repo, name);
        Directory.CreateDirectory(root);
        RunGit(root, "init", "--bare");
        return root;
    }

    private static string CreateRemotePath(string repo, string name) =>
        Path.Combine(repo, ".orchestrator-test-remotes", name);

    internal static void AddGoalBranchCommit(string repo, string goalBranch, string fileName, string content)
    {
        RunGit(repo, "checkout", "-b", goalBranch);
        AppendCommit(repo, fileName, content);
        RunGit(repo, "checkout", "main");
    }

    internal static void AppendCommit(string repo, string fileName, string content)
    {
        var path = Path.Combine(repo, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content + Environment.NewLine);
        RunGit(repo, "add", fileName);
        RunGit(repo, "commit", "-m", $"Add {fileName}");
    }

    internal static string ReadGit(string workingDirectory, params string[] arguments)
    {
        var result = GitCli.Run(workingDirectory, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Error}");
        }

        return result.Output.Trim();
    }

    private static void WriteMirrorConfig(string repo, params string[] remotes) =>
        WriteMirrorConfig(repo, enabled: true, remotes);

    private static void WriteMirrorConfig(string repo, bool enabled, params string[] remotes)
    {
        var configDir = Path.Combine(repo, "config");
        Directory.CreateDirectory(configDir);
        var remoteList = string.Join(", ", remotes.Select(remote => $"\"{remote}\""));
        File.WriteAllText(Path.Combine(configDir, "mirror.json"), $$"""
            {
              "enabled": {{enabled.ToString().ToLowerInvariant()}},
              "remotes": [{{remoteList}}],
              "push": {
                "main": true,
                "goalBranch": true,
                "tags": true
              }
            }
            """.ReplaceLineEndings("\n"));
    }

    private static void AssertGitRef(string repository, string reference)
    {
        var result = GitCli.Run(repository, "show-ref", "--verify", reference);
        Assert.Equal(0, result.ExitCode);
    }

    private static void AssertGitMissingRef(string repository, string reference)
    {
        var result = GitCli.Run(repository, "show-ref", "--verify", reference);
        Assert.NotEqual(0, result.ExitCode);
    }

    private static bool IsBranchReachableFromMain(string repository, string branch) =>
        GitCli.Run(repository, "merge-base", "--is-ancestor", branch, "main").Succeeded;

    private static IDisposable WithMirrorTestHooks()
    {
        var oldBackoff = RemoteGitMirror.BackoffForAttempt;
        RemoteGitMirror.BackoffForAttempt = _ => TimeSpan.Zero;
        return new DelegateDisposable(() =>
        {
            RemoteGitMirror.BackoffForAttempt = oldBackoff;
        });
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var result = GitCli.Run(workingDirectory, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Error}");
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class CountingStateRepository(
        ITransactionalOrchestratorStateRepository inner,
        Func<bool>? cleanupNeededExists = null)
        : ITransactionalOrchestratorStateRepository
    {
        private int _saveCount;
        private int _saveCountBeforeCleanupNeeded;

        public int SaveCount => Volatile.Read(ref _saveCount);
        public int SaveCountBeforeCleanupNeeded => Volatile.Read(ref _saveCountBeforeCleanupNeeded);

        public void ResetSaveCount()
        {
            Volatile.Write(ref _saveCount, 0);
            Volatile.Write(ref _saveCountBeforeCleanupNeeded, 0);
        }

        public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default) =>
            inner.LoadAsync(cancellationToken);

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) =>
            inner.LoadGoalsAsync(goalIds, cancellationToken);

        public async Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default)
        {
            await inner.SaveAsync(kernel, cancellationToken).ConfigureAwait(false);
            RecordSave();
        }

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.ListGoalMetadataAsync(cancellationToken);

        public Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.ListConductLoopGoalMetadataAsync(cancellationToken);

        public Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) =>
            inner.ListGoalIdsWithCompletedHumanInputAsync(goalIds, cancellationToken);

        public Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default) =>
            inner.ListModelFitHistoryAsync(cancellationToken);

        public Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
            int windowSize = ModelOutcomeScorecard.DefaultWindowSize,
            CancellationToken cancellationToken = default) =>
            inner.BuildModelOutcomeScorecardAsync(windowSize, cancellationToken);

        public Task<ModelFitBestFit?> QueryBestFitForRoleAsync(AgentRole role, CancellationToken cancellationToken = default) =>
            inner.QueryBestFitForRoleAsync(role, cancellationToken);

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactAsync(transaction, cancellationToken);

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactAsync(transaction, cancellationToken);

        public Task<GoalSnapshot?> LoadGoalAsync(GoalId goalId, CancellationToken cancellationToken = default) =>
            inner.LoadGoalAsync(goalId, cancellationToken);

        public async Task SaveGoalSnapshotsAsync(
            IReadOnlyCollection<GoalSnapshot> goals,
            CancellationToken cancellationToken = default)
        {
            await inner.SaveGoalSnapshotsAsync(goals, cancellationToken).ConfigureAwait(false);
            if (goals.Count > 0)
            {
                RecordSave();
            }
        }

        private void RecordSave()
        {
            Interlocked.Increment(ref _saveCount);
            if (cleanupNeededExists?.Invoke() is false)
            {
                Interlocked.Increment(ref _saveCountBeforeCleanupNeeded);
            }
        }

        public Task<T> TransactGoalAsync<T>(
            GoalId goalId,
            Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactGoalAsync(goalId, transaction, cancellationToken);

        public async Task<T> TransactGoalStateAsync<T>(
            GoalId goalId,
            Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
            CancellationToken cancellationToken = default)
        {
            var saved = false;
            var result = await inner.TransactGoalStateAsync(
                    goalId,
                    async (state, token) =>
                    {
                        var mutation = await transaction(state, token).ConfigureAwait(false);
                        saved = mutation.ShouldSave && mutation.NewState is not null;
                        return mutation;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (saved)
            {
                RecordSave();
            }

            return result;
        }
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
