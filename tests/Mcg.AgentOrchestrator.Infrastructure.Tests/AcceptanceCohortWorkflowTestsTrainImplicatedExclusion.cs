using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its repository, worktrees, cleanup hooks and database; no shared state or timers.
public sealed class AcceptanceCohortWorkflowTestsTrainImplicatedExclusion : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PassingRemainder_ExcludesEjectedRevisionFromBothSelectors(bool gateOnly)
    {
        WithTrain(repo => [FailedVerification(repo, "red.trx", "train"), Passing(repo)], scenario =>
        {
            Assert.Equal(MergeTrainGateOutcome.Passed, scenario.Result.Receipt!.Outcome);
            Assert.Equal(2, scenario.Verifier.RunCount);
            var dropped = Assert.Single(scenario.Result.Ejections,
                item => item.Reason == MergeTrainEjectionReason.RedNewestMember);
            Assert.Equal(scenario.Goals[2].Id, dropped.GoalId);
            var ejected = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                scenario.Driver.ProjectGateReadyCandidate(scenario.Goals[2], ConductorAutonomyPolicy.Permissive))
                .Projection;
            Assert.Equal(scenario.Selection.Members[2].CandidateRevision, ejected.CandidateRevision);
            var keys = scenario.Driver.ReadTrainImplicatedMemberKeys();
            Assert.Equal(ConductorAcceptanceCohortAttributedMembers.Key(ejected.GoalId, ejected.CandidateRevision),
                Assert.Single(keys));
            Assert.Empty(scenario.Driver.ReadSuppressedGroupedPairs());

            var a = Partner('1', ejected.MainRevision);
            var b = Partner('2', ejected.MainRevision);
            var c = Partner('3', ejected.MainRevision);
            Assert.NotNull(ConductorMergeTrainSelector.Select([Candidate(ejected), a, b]));
            Assert.Null(ConductorMergeTrainSelector.Select([Candidate(ejected), a, b],
                trainImplicatedMemberKeys: keys));
            var train = ConductorMergeTrainSelector.Select([Candidate(ejected), a, b, c],
                trainImplicatedMemberKeys: keys);
            Assert.Equal([a.GoalId, b.GoalId, c.GoalId], train!.Members.Select(member => member.GoalId));
            foreach (var pair in new[] { new[] { Candidate(ejected), a }, new[] { a, Candidate(ejected) } })
            {
                Assert.NotNull(ConductorAcceptanceCohortSelector.Select(pair).Selection);
                var decision = ConductorAcceptanceCohortSelector.Select(pair, trainImplicatedMemberKeys: keys);
                Assert.Null(decision.Selection);
                Assert.Contains(decision.Exclusions, exclusion => exclusion.FirstGoalId == ejected.GoalId &&
                    exclusion.Reason == ConductorAcceptanceCohortPairExclusionReason.TrainImplicatedMember);
            }
        }, gateOnly: gateOnly);
    }

    [Fact]
    public void PassingRemainder_NewRevisionIsEligibleForBothSelectors()
    {
        WithTrain(repo => [FailedVerification(repo, "red.trx", "train"), Passing(repo)], scenario =>
        {
            var old = scenario.Selection.Members[2];
            _ = CreateWorktreeCandidate(scenario.Repo, old.GoalId, old.LandingPaths[0], "revised");
            var changed = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                scenario.Driver.ProjectGateReadyCandidate(scenario.Goals[2], ConductorAutonomyPolicy.Permissive))
                .Projection;
            Assert.NotEqual(old.CandidateRevision, changed.CandidateRevision);
            var keys = scenario.Driver.ReadTrainImplicatedMemberKeys();
            Assert.Contains(ConductorAcceptanceCohortAttributedMembers.Key(old.GoalId, old.CandidateRevision), keys);
            var a = Partner('1', changed.MainRevision);
            var b = Partner('2', changed.MainRevision);
            var train = ConductorMergeTrainSelector.Select([Candidate(changed), a, b],
                trainImplicatedMemberKeys: keys);
            Assert.Equal(changed.GoalId, train!.Members[0].GoalId);
            foreach (var pair in new[] { new[] { Candidate(changed), a }, new[] { a, Candidate(changed) } })
                Assert.Contains(ConductorAcceptanceCohortSelector.Select(pair, trainImplicatedMemberKeys: keys)
                    .Selection!.Members, member => member.GoalId == changed.GoalId);
        });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void RedPair_IsSuppressedInBothOrdersWithoutImplicatingEjectedMember(int memberCount)
    {
        WithTrain(repo => memberCount == 3
            ? [FailedVerification(repo, "red.trx", "train"), FailedVerification(repo, "pair.trx", "pair")]
            : [FailedVerification(repo, "pair.trx", "pair")], scenario =>
        {
            Assert.Null(scenario.Result.Receipt);
            Assert.Empty(scenario.Result.MemberResults);
            Assert.Equal(memberCount == 3 ? 2 : 1, scenario.Verifier.RunCount);
            Assert.Equal(memberCount == 3 ? 1 : 0, scenario.Result.Ejections.Count);
            Assert.Empty(scenario.Driver.ReadTrainImplicatedMemberKeys());
            var pairs = scenario.Driver.ReadSuppressedGroupedPairs();
            Assert.Equal(2, pairs.Count);
            Assert.Empty(scenario.Driver.ReadSuppressedCohortPairs());
            var first = Candidate(scenario.Selection.Members[0]);
            var second = Candidate(scenario.Selection.Members[1]);
            var third = Partner('1', scenario.Selection.Members[0].MainRevision);
            foreach (var pair in new[] { new[] { first, second }, new[] { second, first } })
            {
                Assert.NotNull(ConductorAcceptanceCohortSelector.Select(pair).Selection);
                var cohort = ConductorAcceptanceCohortSelector.Select(pair, suppressedPairFingerprints: pairs);
                Assert.Null(cohort.Selection);
                Assert.Contains(cohort.Exclusions, exclusion =>
                    exclusion.Reason == ConductorAcceptanceCohortPairExclusionReason.SuppressedInteraction);
                Assert.NotNull(ConductorMergeTrainSelector.Select([pair[0], pair[1], third]));
                Assert.Null(ConductorMergeTrainSelector.Select([pair[0], pair[1], third], pairs));
            }
            // Suppression is specific to these candidates at this main, not an individual exclusion.
            var other = Partner('2', scenario.Selection.Members[0].MainRevision);
            Assert.NotNull(ConductorMergeTrainSelector.Select([first, third, other], pairs));
            Assert.NotNull(ConductorAcceptanceCohortSelector.Select([second, third],
                suppressedPairFingerprints: pairs).Selection);
            if (memberCount == 3)
            {
                var ejected = Candidate(scenario.Selection.Members[2]);
                Assert.Contains(ConductorMergeTrainSelector.Select([ejected, third, other], pairs)!.Members,
                    member => member.GoalId == ejected.GoalId);
                Assert.NotNull(ConductorAcceptanceCohortSelector.Select([ejected, third],
                    suppressedPairFingerprints: pairs).Selection);
            }
            var movedMain = new string('f', 40);
            Assert.NotNull(ConductorAcceptanceCohortSelector.Select(
                [Candidate(AtMain(scenario.Selection.Members[0], movedMain)),
                 Candidate(AtMain(scenario.Selection.Members[1], movedMain))],
                suppressedPairFingerprints: pairs).Selection);
        }, memberCount: memberCount);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void ApparatusRed_RecordsNeitherImplicationNorPairSuppression(int memberCount, bool remainderPasses)
    {
        WithTrain(repo => memberCount == 2
            ? [ApparatusRed(repo, "apparatus.trx")]
            : [ApparatusRed(repo, "apparatus.trx"),
                remainderPasses ? Passing(repo) : ApparatusRed(repo, "remainder.trx")], scenario =>
        {
            Assert.Equal(memberCount == 3 ? 2 : 1, scenario.Verifier.RunCount);
            Assert.Empty(scenario.Driver.ReadTrainImplicatedMemberKeys());
            Assert.Empty(scenario.Driver.ReadSuppressedGroupedPairs());
            var candidates = scenario.Selection.Members.Select(Candidate).ToArray();
            var a = Partner('1', scenario.Selection.Members[0].MainRevision);
            Assert.NotNull(ConductorMergeTrainSelector.Select([candidates[^1], candidates[0], a],
                scenario.Driver.ReadSuppressedGroupedPairs(),
                trainImplicatedMemberKeys: scenario.Driver.ReadTrainImplicatedMemberKeys()));
            Assert.NotNull(ConductorAcceptanceCohortSelector.Select([candidates[0], candidates[1]],
                suppressedPairFingerprints: scenario.Driver.ReadSuppressedGroupedPairs(),
                trainImplicatedMemberKeys: scenario.Driver.ReadTrainImplicatedMemberKeys()).Selection);
        }, memberCount: memberCount);
    }

    [Fact]
    public void InfrastructureRed_LeavesCompositionEligibleWithoutSuppression()
    {
        WithTrain(_ => [new AcceptanceVerificationResult(false, false, null, "missing exit code",
            [new AcceptanceCheckResult("combined", false, null, "missing exit code")], [])], scenario =>
        {
            Assert.Equal(1, scenario.Verifier.RunCount);
            Assert.Empty(scenario.Result.Ejections);
            Assert.Empty(scenario.Driver.ReadTrainImplicatedMemberKeys());
            Assert.Empty(scenario.Driver.ReadSuppressedGroupedPairs());
            var pair = scenario.Selection.Members.Select(Candidate).ToArray();
            Assert.NotNull(ConductorAcceptanceCohortSelector.Select(pair,
                suppressedPairFingerprints: scenario.Driver.ReadSuppressedGroupedPairs()).Selection);
            Assert.NotNull(ConductorMergeTrainSelector.Select(
                [pair[0], pair[1], Partner('1', scenario.Selection.Members[0].MainRevision)],
                trainImplicatedMemberKeys: scenario.Driver.ReadTrainImplicatedMemberKeys()));
        }, memberCount: 2);
    }

    [Fact]
    public void InfrastructureRemainder_DoesNotImplicateTheEjectedCandidate()
    {
        WithTrain(repo => [FailedVerification(repo, "red.trx", "train"),
            new AcceptanceVerificationResult(false, true, null, "deferred", [], [])], scenario =>
        {
            Assert.Equal(2, scenario.Verifier.RunCount);
            Assert.Equal(scenario.Goals[2].Id, Assert.Single(scenario.Result.Ejections).GoalId);
            Assert.Empty(scenario.Driver.ReadTrainImplicatedMemberKeys());
            Assert.Empty(scenario.Driver.ReadSuppressedGroupedPairs());
            Assert.NotNull(ConductorMergeTrainSelector.Select(scenario.Selection.Members.Select(Candidate).ToArray(),
                trainImplicatedMemberKeys: scenario.Driver.ReadTrainImplicatedMemberKeys()));
        });
    }

    [Fact]
    public void FailedExitWithoutFatalTestEvidence_DoesNotSuppressThePair()
    {
        WithTrain(repo => [Passing(repo) with { Passed = false, ExitCode = 1 }], scenario =>
        {
            Assert.Equal(1, scenario.Verifier.RunCount);
            Assert.Empty(scenario.Driver.ReadTrainImplicatedMemberKeys());
            Assert.Empty(scenario.Driver.ReadSuppressedGroupedPairs());
            Assert.NotNull(ConductorAcceptanceCohortSelector.Select(
                scenario.Selection.Members.Select(Candidate).ToArray(),
                suppressedPairFingerprints: scenario.Driver.ReadSuppressedGroupedPairs()).Selection);
        }, memberCount: 2);
    }

    private void WithTrain(
        Func<string, IReadOnlyList<AcceptanceVerificationResult>> results,
        Action<Scenario> assert,
        int memberCount = 3,
        bool gateOnly = true)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            string[] paths = ["tests/Mcg.AgentOrchestrator.Core.Tests/First.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Second.cs",
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Newest.cs"];
            var goals = Enumerable.Range(0, memberCount).Select(index =>
            {
                var goal = CreateCompletedGoal(kernel, $"Train member {index}", repo);
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                {
                    AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
                });
                _ = CreateWorktreeCandidate(repo, goal.Id, paths[index], $"member {index}");
                return goal;
            }).ToArray();
            var verifier = new SequenceAcceptanceVerifier(results(repo));
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var selection = new ConductorMergeTrainSelection(goals.Select(goal =>
                Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                    driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive)).Projection).ToArray());
            if (memberCount == 3)
                Assert.NotNull(ConductorMergeTrainSelector.Select(selection.Members.Select(Candidate).ToArray()));
            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive,
                gateOnly: gateOnly);
            assert(new Scenario(repo, goals, driver, selection, result, verifier));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static AcceptanceVerificationResult Passing(string repo) => new(true, false, 0, null,
        [new AcceptanceCheckResult("remainder", true, 0, null)], [WritePassingTrx(repo, "pass.trx")]);

    private static AcceptanceVerificationResult ApparatusRed(string repo, string fileName)
    {
        var path = WriteFailingTrx(repo, fileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            "outcome=\"Failed\" />",
            "outcome=\"Failed\"><Output><ErrorInfo><Message>DotnetBuildSlotsBusyException: " +
            "Stable dotnet build slots busy</Message></ErrorInfo></Output></UnitTestResult>",
            StringComparison.Ordinal));
        Assert.Contains("DotnetBuildSlotsBusyException", File.ReadAllText(path));
        var verification = new AcceptanceVerificationResult(false, false, 1, "apparatus",
            [new AcceptanceCheckResult("combined", false, 1, "apparatus")], [path]);
        Assert.Equal(AcceptanceCohortGateOutcome.Failed, ConductorDriver.ClassifyCohortVerification(verification));
        Assert.All(AcceptanceTrxFailureReader.Read(path).Failures, failure =>
            Assert.NotNull(ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace)));
        return verification;
    }

    private static ConductorSpeculativeAcceptanceCandidate Candidate(GateReadyCandidateProjection projection) =>
        new(projection.GoalId, new GateReadyCandidateProjectionResult.Ready(projection));

    private static ConductorSpeculativeAcceptanceCandidate Partner(char id, string main) => Candidate(
        new GateReadyCandidateProjection(new GoalId(new string(id, 32)), GoalLifecycleState.Verified,
            GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
            [$"src/Partner{id}.cs"], [$"partner:{id}"],
            new GateReadyMergeEvidence(new string(id, 40), main,
                GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected)));

    private static GateReadyCandidateProjection AtMain(GateReadyCandidateProjection projection, string main) =>
        new(projection.GoalId, projection.LifecycleState, projection.VerificationState, projection.ChangeRiskTier,
            projection.AutoPromotionDisposition, projection.LandingPaths, projection.ResourceKeys,
            projection.MergeEvidence with { MainRevision = main });

    private sealed record Scenario(string Repo, Goal[] Goals, ConductorDriver Driver,
        ConductorMergeTrainSelection Selection, ConductorMergeTrainRunResult Result, SequenceAcceptanceVerifier Verifier);
}
