using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: repositories, stores, cleanup hooks and captured gate starts are fixture-owned.
public sealed class PassedMergeTrainReceiptHoldTests(ITestOutputHelper output) : AcceptanceCohortWorkflowTests
{
    [Fact]
    [Trait("Category", "CrossTick")]
    public void CurrentBlockedReceipt_HoldsReadyMembersThenLandsWhenMemberBecomesReady()
    {
        using var fixture = CreatePassedTrain(materializeReceipt: true);
        fixture.SetBlocked(true);
        var ticks = new List<BatchTickSummary>();
        var landings = new List<ConductorLandingReceipt>();
        fixture.Driver.SuccessfulLandingSink = landings.Add;
        fixture.Driver.LandingMutationBlocker = () =>
        {
            output.WriteLine("RECEIPT_HOLD_PHASE landing=mutation-boundary");
            return null;
        };
        fixture.Run(fixture.CreateLoop(), 2, tick =>
        {
            ticks.Add(tick);
            if (tick.Tick == 1)
            {
                fixture.AssertHeld(tick, 1);
                Assert.Empty(landings);
                Assert.All(fixture.Goals, goal => Assert.NotEqual(GoalStatus.Completed, goal.Status));
                fixture.SetBlocked(false);
            }
        });

        Assert.Equal(2, ticks.Count);
        Assert.Contains($"outcome=passed attempts=1 landings=3 receipt={fixture.Receipt.ReceiptId}",
            string.Join(" | ", ticks[1].ProgressLines ?? []));
        Assert.Equal(fixture.Goals.Select(goal => goal.Id.Value).Order(),
            landings.Select(landing => landing.GoalId).Order());
        Assert.All(fixture.Goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
        Assert.Equal(3, landings.Count);
        var landedMain = RunGitOutput(fixture.Repo, "rev-parse", "main").Trim();
        Assert.NotEqual(fixture.Receipt.Identity.ObservedMainRevision, landedMain);
        Assert.All(landings, landing => Assert.Equal(landedMain, landing.LandingSha));
        Assert.Equal(fixture.Receipt.Identity.TrainTreeRevision,
            RunGitOutput(fixture.Repo, "rev-parse", "main^{tree}").Trim());
        Assert.Equal(0, fixture.Verifier.RunCount);
        Assert.Equal(0, fixture.Pending.StartCount);
        Assert.Single(fixture.EventLines("TRAIN_RECEIPT_HELD"));
        Assert.Empty(fixture.EventLines("TRAIN_RECEIPT_RELEASED"));
    }

    [Fact]
    public void CurrentBlockedReceipt_ReleasesOnceAtTickTenAndAllowsAdmission()
    {
        var fixture = new MemoryFixture { Blocked = true };
        var ticks = new List<MemoryTick>();
        for (var tick = 1; tick <= 12; tick++)
        {
            var result = fixture.Tick(tick);
            ticks.Add(result);
            if (tick < PassedMergeTrainReceiptHolds.PassedTrainReceiptHoldTickLimit)
            {
                fixture.AssertHeld(result, tick);
                Assert.Empty(result.Eligible);
                Assert.Null(ConductorMergeTrainSelector.Select(result.Candidates));
                Assert.Null(ConductorAcceptanceCohortSelector.Select(result.Candidates).Selection);
            }
            else
            {
                Assert.DoesNotContain("reason=PassedTrainReceiptHeld", string.Join(" | ", result.Progress), StringComparison.Ordinal);
                Assert.Equal(fixture.Goals, result.Eligible);
                Assert.Equal(fixture.Goals.Take(2).Select(goal => goal.Id),
                    ConductorAcceptanceCohortSelector.Select(result.Candidates).Selection!.Members.Select(member => member.GoalId));
            }
            if (tick == 10)
            {
                Assert.Single(fixture.EventLines("TRAIN_RECEIPT_RELEASED"));
            }
        }
        Assert.Equal(12, ticks.Count);
        Assert.Equal(9, fixture.EventLines("TRAIN_RECEIPT_HELD").Length);
        Assert.Contains($"receipt={fixture.Receipt.ReceiptId} reason=hold-limit",
            Assert.Single(fixture.EventLines("TRAIN_RECEIPT_RELEASED")));
        Assert.Empty(fixture.LandingRequests);
        Assert.All(fixture.Goals, goal => Assert.NotEqual(GoalStatus.Completed, goal.Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeldReceipt_MainOrBlockedMemberMoves_EndsHoldWithExistingStaleEvent(bool moveMember)
    {
        var fixture = new MemoryFixture { Blocked = true };
        fixture.AssertHeld(fixture.Tick(1), 1);
        if (moveMember)
        {
            fixture.Revisions[fixture.Goals[2].Id] = new string('d', 40);
        }
        else
        {
            fixture.MainRevision = new string('e', 40);
        }
        var tick = fixture.Tick(2);
        var moved = moveMember ? $"member:{fixture.Goals[2].Id.Value[..8]}" : "main";
        Assert.Contains($"receipt={fixture.Receipt.ReceiptId} moved={moved}",
            Assert.Single(fixture.EventLines("TRAIN_RECEIPT_STALE")));
        Assert.Single(fixture.EventLines("TRAIN_RECEIPT_HELD"));
        Assert.Empty(fixture.EventLines("TRAIN_RECEIPT_RELEASED"));
        Assert.DoesNotContain("reason=PassedTrainReceiptHeld", string.Join(" | ", tick.Progress), StringComparison.Ordinal);
        Assert.NotNull(ConductorAcceptanceCohortSelector.Select(tick.Candidates).Selection);
        Assert.Empty(fixture.LandingRequests);
        Assert.All(fixture.Goals, goal => Assert.NotEqual(GoalStatus.Completed, goal.Status));
        Assert.Empty(fixture.Holds.HeldGoalIds);
    }

    [Fact]
    [Trait("Category", "CrossTick")]
    public void BlockedReceipt_ReleasesAtTickTenAndReadmitsMembers()
    {
        using var fixture = CreatePassedTrain();
        fixture.SetBlocked(true);
        var ticks = new List<BatchTickSummary>();
        var landings = new List<ConductorLandingReceipt>();
        fixture.Driver.SuccessfulLandingSink = landings.Add;
        fixture.Run(fixture.CreateLoop(), 11, tick =>
        {
            ticks.Add(tick);
            if (tick.Tick < PassedMergeTrainReceiptHolds.PassedTrainReceiptHoldTickLimit)
            {
                fixture.AssertHeld(tick, tick.Tick);
                Assert.Empty(landings);
                Assert.Empty(fixture.EventLines("TRAIN_RECEIPT_RELEASED"));
                return;
            }

            var progress = tick.ProgressLines ?? [];
            Assert.DoesNotContain(progress, line => line.Contains("reason=PassedTrainReceiptHeld", StringComparison.Ordinal));
            if (tick.Tick == 10)
            {
                Assert.Equal($"TRAIN_RECEIPT_RELEASED train={fixture.Receipt.Identity.Value} " +
                    $"receipt={fixture.Receipt.ReceiptId} reason=hold-limit",
                    Assert.Single(progress.Where(line => line.StartsWith("TRAIN_RECEIPT_RELEASED ", StringComparison.Ordinal))));
            }
            else
            {
                Assert.Equal(1, fixture.Pending.StartCount);
                Assert.Equal(1, fixture.Pending.PendingCount);
                Assert.All(fixture.Goals.Take(2), goal =>
                    Assert.Contains(goal.Id.Value, fixture.Driver.GetActiveCohortGateMemberGoalIds()));
                Assert.DoesNotContain(progress, line => line.StartsWith("TRAIN_RECEIPT_RELEASED ", StringComparison.Ordinal));
            }
        });

        Assert.Equal(11, ticks.Count);
        Assert.Equal(9, fixture.EventLines("TRAIN_RECEIPT_HELD").Length);
        Assert.Single(fixture.EventLines("TRAIN_RECEIPT_RELEASED"));
        Assert.Empty(landings);
        Assert.Equal(0, fixture.Verifier.RunCount);
        Assert.All(fixture.Goals, goal => Assert.NotEqual(GoalStatus.Completed, goal.Status));
    }

    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public void BatchLoop_HeldReceiptMainMoves_ReportsStaleAndReadmitsMembers()
    {
        using var fixture = CreatePassedTrain();
        fixture.SetBlocked(true);
        var landings = new List<ConductorLandingReceipt>();
        fixture.Driver.SuccessfulLandingSink = landings.Add;
        var ticks = new List<BatchTickSummary>();
        fixture.Run(fixture.CreateLoop(), 3, tick =>
        {
            ticks.Add(tick);
            if (tick.Tick == 1)
            {
                fixture.AssertHeld(tick, 1);
                Assert.Empty(landings);
                File.WriteAllText(Path.Combine(fixture.Repo, "unrelated-main.txt"), "later main");
                RunGit(fixture.Repo, "add", "unrelated-main.txt");
                RunGit(fixture.Repo, "commit", "-m", "Advance main during receipt hold");
                return;
            }

            var progress = tick.ProgressLines ?? [];
            Assert.DoesNotContain(progress, line => line.Contains("reason=PassedTrainReceiptHeld", StringComparison.Ordinal));
            Assert.DoesNotContain(progress, line => line.StartsWith("TRAIN_RECEIPT_HELD ", StringComparison.Ordinal));
            if (tick.Tick == 2)
            {
                Assert.Equal($"TRAIN_RECEIPT_STALE tick=2 train={fixture.Receipt.Identity.Value} " +
                    $"receipt={fixture.Receipt.ReceiptId} moved=main",
                    Assert.Single(progress.Where(line => line.StartsWith("TRAIN_RECEIPT_STALE ", StringComparison.Ordinal))));
            }
            Assert.Equal(1, fixture.Pending.StartCount);
            Assert.All(fixture.Goals.Take(2), goal =>
                Assert.Contains(goal.Id.Value, fixture.Driver.GetActiveCohortGateMemberGoalIds()));
        });

        Assert.Equal(3, ticks.Count);
        Assert.Single(fixture.EventLines("TRAIN_RECEIPT_HELD"));
        Assert.Single(fixture.EventLines("TRAIN_RECEIPT_STALE"));
        Assert.Empty(fixture.EventLines("TRAIN_RECEIPT_RELEASED"));
        Assert.Empty(landings);
        Assert.Equal(0, fixture.Verifier.RunCount);
        Assert.All(fixture.Goals, goal => Assert.NotEqual(GoalStatus.Completed, goal.Status));
    }

    [Fact]
    public void CriterionEvidenceGap_HoldsEveryMemberIncludingSoloEligibleMember()
    {
        var fixture = new MemoryFixture();
        var blockedGoal = fixture.Goals[2];
        var member = fixture.Receipt.Identity.Members.Single(member => member.GoalId == blockedGoal.Id);
        fixture.Kernel.MapCriterionEvidenceOwner(blockedGoal.Id, 0, 1,
            CriterionEvidenceOwner.Operator, "test", expectedCandidateSha: member.CandidateRevision);
        Assert.Contains(blockedGoal.Id.Value, ConductorBatchLoop.TrainIneligibleCriterionEvidenceGoalIds(fixture.Goals));
        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(fixture.Project(blockedGoal));
        // The candidate remains available for solo admission before the receipt policy runs.
        Assert.Contains(fixture.Candidates(), candidate => candidate.GoalId == blockedGoal.Id &&
            candidate.ProjectionResult is GateReadyCandidateProjectionResult.Ready);
        for (var tick = 1; tick <= 2; tick++)
        {
            var result = fixture.Tick(tick);
            fixture.AssertHeld(result, tick, "ineligible-criterion-evidence");
            Assert.Empty(result.Eligible);
            Assert.Empty(result.Candidates);
            Assert.Null(ConductorMergeTrainSelector.Select(result.Candidates));
            Assert.Null(ConductorAcceptanceCohortSelector.Select(result.Candidates).Selection);
            Assert.Empty(fixture.LandingRequests);
        }
        fixture.Kernel.MapCriterionEvidenceOwner(blockedGoal.Id, 0, 1,
            CriterionEvidenceOwner.Acceptance, "test", expectedCandidateSha: member.CandidateRevision);
        var readyTick = fixture.Tick(3);
        Assert.Contains($"outcome=passed attempts=1 landings=3 receipt={fixture.Receipt.ReceiptId}", string.Join(" | ", readyTick.Progress));
        Assert.Equal(fixture.Goals.Select(goal => goal.Id),
            Assert.Single(fixture.LandingRequests).Members.Select(member => member.GoalId));
        Assert.All(fixture.Goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
        Assert.Equal(3, fixture.LandingRequests.SelectMany(selection => selection.Members).Count());
        Assert.Empty(readyTick.Eligible);
        Assert.Empty(readyTick.Candidates);
        Assert.Equal(2, fixture.EventLines("TRAIN_RECEIPT_HELD").Length);
        Assert.Empty(fixture.EventLines("TRAIN_RECEIPT_RELEASED"));
    }

    [Fact]
    public void ReceiptObservation_UnrelatedCompletedHistory_DoesNotResolveHeads()
    {
        var fixture = new MemoryFixture();
        var landed = fixture.Tick(1);
        Assert.Contains("outcome=passed", string.Join(" | ", landed.Progress));
        Assert.All(fixture.Goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
        var resolved = new List<GoalId>();
        var observed = new List<string>();
        var receiptReads = new List<GoalId>();
        var selections = fixture.Find([], new HashSet<string>(),
            (_, _) => Assert.Fail("Unrelated history was checked for staleness."),
            (receipt, _) => observed.Add(receipt.ReceiptId), fixture.Goals, new HashSet<GoalId>(),
            goal => { resolved.Add(goal.Id); return ("later-candidate", "later-main"); },
            goalId => { receiptReads.Add(goalId); return [fixture.Receipt]; });
        Assert.Empty(selections);
        Assert.Empty(observed);
        Assert.Empty(resolved);
        Assert.Empty(receiptReads);
    }

    [Fact]
    public void ReceiptObservation_EligibleGoalWithoutReceipt_DoesNotResolveHeads()
    {
        var fixture = new MemoryFixture();
        var goal = fixture.Goals[0];
        var resolved = new List<GoalId>();
        var receiptReads = new List<GoalId>();
        var selections = fixture.Find([], new HashSet<string>(),
            (_, _) => Assert.Fail("A goal without a receipt was checked for staleness."),
            (_, _) => Assert.Fail("A goal without a receipt was observed."), [goal], new HashSet<GoalId> { goal.Id },
            member => { resolved.Add(member.Id); return ("candidate", "main"); },
            goalId => { receiptReads.Add(goalId); return []; });
        Assert.Empty(selections);
        Assert.Empty(resolved);
        Assert.Equal([goal.Id], receiptReads);
    }

    [Fact]
    public void HeldReceipt_MembersLeaveEligibility_StillReportsStaleMain()
    {
        var fixture = new MemoryFixture { Blocked = true };
        fixture.AssertHeld(fixture.Tick(1), 1);
        foreach (var member in fixture.Goals)
        {
            fixture.Kernel.CancelGoal(member.Id, "Leave admission eligibility during receipt hold");
        }
        fixture.MainRevision = new string('e', 40);
        var tick = fixture.Tick(2);
        Assert.All(fixture.Goals, goal => Assert.Equal(GoalStatus.Cancelled, goal.Status));
        Assert.Contains($"receipt={fixture.Receipt.ReceiptId} moved=main",
            Assert.Single(fixture.EventLines("TRAIN_RECEIPT_STALE")));
        Assert.Single(fixture.EventLines("TRAIN_RECEIPT_HELD"));
        Assert.DoesNotContain("reason=PassedTrainReceiptHeld", string.Join(" | ", tick.Progress), StringComparison.Ordinal);
        Assert.Empty(tick.Eligible);
        Assert.Empty(tick.Candidates);
        Assert.Empty(fixture.LandingRequests);
        Assert.Empty(fixture.Holds.HeldGoalIds);
    }

    private sealed record MemoryTick(Goal[] Eligible, ConductorSpeculativeAcceptanceCandidate[] Candidates,
        Dictionary<string, ParallelLandingOutcome> Results, List<string> Progress);

    // Only external observation and landing are substituted. Selector, tick budget,
    // exclusion reasons, event emission and admission filtering execute production code.
    private sealed class MemoryFixture
    {
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal[] Goals { get; }
        internal MergeTrainReceipt Receipt { get; }
        internal PassedMergeTrainReceiptHolds Holds { get; } = new();
        internal Dictionary<GoalId, string> Revisions { get; } = [];
        internal string MainRevision { get; set; } = new string('a', 40);
        internal bool Blocked { get; set; }
        internal List<ConductorMergeTrainSelection> LandingRequests { get; } = [];
        private readonly List<string> _events = [];
        private readonly HashSet<string> _reportedStale = [];

        internal MemoryFixture()
        {
            Goals = Enumerable.Range(0, 3).Select(index =>
                CreateCompletedGoal(Kernel, $"Memory receipt member {index}", "in-memory")).ToArray();
            foreach (var goal in Goals)
            {
                Revisions[goal.Id] = new string('b', 40);
                Kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                {
                    AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
                });
                Kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                    "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: Revisions[goal.Id]);
            }
            var selection = new ConductorMergeTrainSelection(Goals.Select(goal =>
                ((GateReadyCandidateProjectionResult.Ready)Project(goal)).Projection).ToArray());
            var members = selection.BindMembers().Select(member => member.WithRebasedHead(new string('c', 40))).ToArray();
            Receipt = new MergeTrainReceipt("memory-passed-receipt",
                MergeTrainIdentity.Create(members, MainRevision, new string('c', 40), "memory-manifest"),
                MergeTrainGateOutcome.Passed, DateTimeOffset.UnixEpoch, 0, [], 0, []);
        }

        internal GateReadyCandidateProjectionResult Project(Goal goal) => Blocked && goal.Id == Goals[2].Id
            ? new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.OwnerReviewHold)
            : new GateReadyCandidateProjectionResult.Ready(new GateReadyCandidateProjection(
                goal.Id, GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied,
                ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
                [$"src/{goal.Id.Value}.cs"], [$"resource:{goal.Id.Value}"],
                new GateReadyMergeEvidence(Revisions[goal.Id], MainRevision,
                    GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected)));

        internal ConductorSpeculativeAcceptanceCandidate[] Candidates() => Goals
            .Where(goal => goal.Status == GoalStatus.Verified)
            .Select(goal => new ConductorSpeculativeAcceptanceCandidate(goal.Id, Project(goal))).ToArray();

        internal IReadOnlyList<(ConductorMergeTrainSelection Selection, MergeTrainReceipt Receipt)> Find(
            IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates, IReadOnlySet<string> ineligible,
            Action<MergeTrainReceipt, string> stale,
            Action<MergeTrainReceipt, PassedMergeTrainReceiptHolds.Observation> blocked,
            IReadOnlyList<Goal> scopedGoals, IReadOnlySet<GoalId> observed,
            Func<Goal, (string?, string?)>? heads = null,
            Func<GoalId, IReadOnlyList<MergeTrainReceipt>>? receipts = null) =>
            PassedMergeTrainReceiptSelector.Find(candidates, ineligible, stale, blocked, scopedGoals, observed,
                heads ?? (goal => (Revisions[goal.Id], MainRevision)), Goals,
                receipts ?? (_ => [Receipt]), () => new HashSet<string>(),
                (selection, receipt) => selection.Members.Select(member => member.GoalId)
                    .SequenceEqual(receipt.Identity.Members.Select(member => member.GoalId)));

        internal MemoryTick Tick(int tick)
        {
            var results = new Dictionary<string, ParallelLandingOutcome>();
            var progress = new List<string>();
            void Record(string line) { progress.Add(line); _events.Add(line); }
            var operations = new PassedMergeTrainReceiptAdmission.Operations(
                (candidates, ineligible, stale, blocked, scopedGoals, observed) =>
                    Find(candidates, ineligible, stale, blocked, scopedGoals, observed),
                (goal, _) => Project(goal),
                (selection, _, _) =>
                {
                    LandingRequests.Add(selection);
                    // Stand in for the external landing mutation; the admission pass still
                    // selects the passed receipt and accounts for every member result.
                    foreach (var member in selection.Members)
                    {
                        var goal = Goals.Single(goal => goal.Id == member.GoalId);
                        foreach (var obligation in goal.OutstandingCriterionEvidenceObligations.ToArray())
                        {
                            Kernel.RecordCriterionEvidence(goal.Id, obligation.Id, CriterionEvidenceOwner.Acceptance,
                                member.CandidateRevision, Receipt.ReceiptId, CriterionEvidenceScopes.FullAcceptanceGate,
                                true, "In-memory landing boundary");
                        }
                        Kernel.CompleteGoal(goal.Id, "In-memory landing boundary");
                    }
                    return new ConductorMergeTrainRunResult(Receipt,
                        selection.Members.ToDictionary(member => member.GoalId.Value, member =>
                            Result(member.GoalId, new ConductorAdvanceOutcome.Done(GoalLifecycleState.CleanedUp))),
                        [], $"outcome=passed attempts=1 landings=3 receipt={Receipt.ReceiptId}");
                });
            var admission = PassedMergeTrainReceiptAdmission.Apply(operations, ConductorAutonomyPolicy.Permissive,
                Goals.Where(goal => goal.Status == GoalStatus.Verified).ToArray(), Candidates(), Goals,
                results, tick, [], Holds, _reportedStale, Record, (line, _) => Record(line),
                (goal, reason) => Result(goal.Id, new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, reason)));
            return new(admission.Eligible, admission.Candidates, results, progress);
        }

        private static ConductorAdvanceResult Result(GoalId id, ConductorAdvanceOutcome outcome) =>
            new(id.Value, id.Value[..8], "Permissive", outcome);

        internal string[] EventLines(string tag) => _events.Where(line =>
            line.StartsWith(tag + " ", StringComparison.Ordinal)).ToArray();

        internal void AssertHeld(MemoryTick tick, int heldTicks, string reason = "OwnerReviewHold")
        {
            var line = Assert.Single(tick.Progress.Where(line => line.StartsWith("TRAIN_RECEIPT_HELD ", StringComparison.Ordinal)));
            Assert.StartsWith($"TRAIN_RECEIPT_HELD tick={heldTicks} train={Receipt.Identity.Value} ", line);
            Assert.Contains($"receipt={Receipt.ReceiptId} member={Goals[2].Id.Value[..8]} reason={reason} heldTicks={heldTicks}", line);
            foreach (var goal in Goals)
            {
                Assert.Contains(tick.Progress, line => line.StartsWith("ADMISSION ", StringComparison.Ordinal) &&
                    line.Contains("reason=PassedTrainReceiptHeld", StringComparison.Ordinal) &&
                    line.Contains($"goal={goal.Id.Value[..8]}", StringComparison.Ordinal));
                Assert.True(tick.Results[goal.Id.Value].Result.IsHeld);
                Assert.Equal(GoalStatus.Verified, goal.Status);
            }
            Assert.Empty(LandingRequests);
        }
    }
    private Fixture CreatePassedTrain(bool materializeReceipt = false)
    {
        output.WriteLine("RECEIPT_HOLD_PHASE setup=repository-start");
        var repo = CreateReducedAcceptanceCohortRepository();
        Directory.CreateDirectory(Path.Combine(repo, "config"));
        File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), "{}");
        RunGit(repo, "add", "config/acceptance-manifest.json");
        // Keep the common fixture setup in the seed commit, before any member branches exist.
        RunGit(repo, "commit", "--amend", "--no-edit");
        output.WriteLine("RECEIPT_HOLD_PHASE setup=repository-ready");
        var kernel = new AgentOrchestratorKernel();
        var paths = new[]
        {
            "tests/Mcg.AgentOrchestrator.Core.Tests/HeldFirst.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/HeldSecond.cs",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/HeldThird.cs"
        };
        var goals = paths.Select((path, index) =>
        {
            output.WriteLine($"RECEIPT_HOLD_PHASE setup=member-start member={index}");
            var goal = CreateCompletedGoal(kernel, $"Held train {index}", repo);
            var candidate = CreateWorktreeCandidate(repo, goal.Id, path, $"member {index}");
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
            {
                AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
            });
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
            output.WriteLine($"RECEIPT_HOLD_PHASE setup=member-ready member={index}");
            return goal;
        }).ToArray();
        var workspace = OrchestratorWorkspace.ForDirectory(repo);
        var verifier = new SequenceAcceptanceVerifier([]);
        var cleanup = CreateIsolatedCleanupContext(repo);
        var driver = new ConductorDriver(kernel, workspace, verifier,
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
            runAcceptanceAttemptsInCurrentProcess: true, cleanupHooks: cleanup.Hooks);
        var selection = ProjectTrainSelection(driver, goals);
        // Seed the passed gate boundary; the readiness scenario needs the actual train tree
        // and effective plan so the existing receipt-only landing path can replay it.
        var main = selection.Members[0].MainRevision;
        var members = selection.BindMembers().Select(member => member.WithRebasedHead(member.CandidateRevision)).ToArray();
        var identity = MergeTrainIdentity.Create(members, main,
            RunGitOutput(repo, "rev-parse", "main^{tree}").Trim(), "held-train-fixture");
        if (materializeReceipt)
        {
            output.WriteLine("RECEIPT_HOLD_PHASE setup=materialization-start");
            using var train = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, selection.BindMembers(), cleanup.Hooks);
            Assert.Empty(train.Ejections);
            Assert.Equal(3, train.Members.Count);
            var changedFiles = train.Members.SelectMany(member => member.LandingPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var manifest = ((IGoalAcceptanceVerifier)verifier).ComputeEffectivePlanIdentity(train.Path, changedFiles);
            identity = MergeTrainIdentity.Create(train.Members, main, train.TreeRevision, manifest);
            Assert.NotEqual(RunGitOutput(repo, "rev-parse", "main^{tree}").Trim(), train.TreeRevision);
            output.WriteLine("RECEIPT_HOLD_PHASE setup=materialization-ready");
        }
        var seeded = new MergeTrainReceipt("held-train-fixture-receipt",
            identity,
            MergeTrainGateOutcome.Passed, DateTimeOffset.UnixEpoch, 0, [], 0,
            [WritePassingTrx(workspace.OrchestratorDirectory, "held-train-green.trx")], ValidForLanding: true);
        var store = new MergeTrainAcceptanceStore(
            Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
        store.SaveGateReceipt(seeded);
        var receipt = Assert.Single(store.ReadPassedReceiptsForGoal(goals[0].Id));
        Assert.Equal(3, receipt.Identity.Members.Count);
        Assert.Equal(MergeTrainGateOutcome.Passed, receipt.Outcome);
        Assert.True(receipt.HasAuthoritativeLandingEvidence);
        Assert.Equal(0, verifier.RunCount);
        Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
        var pending = new BackgroundGateStartHarness();
        pending.Capture(driver);
        output.WriteLine($"RECEIPT_HOLD_PHASE setup=ready receipt={receipt.ReceiptId}");
        return new(repo, kernel, goals, driver, verifier, receipt, pending, output.WriteLine);
    }

    private sealed record Fixture(string Repo, AgentOrchestratorKernel Kernel, Goal[] Goals,
        ConductorDriver Driver, SequenceAcceptanceVerifier Verifier, MergeTrainReceipt Receipt,
        BackgroundGateStartHarness Pending, Action<string> Trace) : IDisposable
    {
        private string LogPath => Path.Combine(Repo, "held-conduct.jsonl");

        internal ConductorBatchLoop CreateLoop() => new ConductorBatchLoop(
            conductEventLogWriter: new ConductEventLogWriter(LogPath),
            janitorialPhaseProbe: phase => Trace($"RECEIPT_HOLD_PHASE loop={phase}"))
            .WithStepProbe(step => Trace($"RECEIPT_HOLD_PHASE step={step} completed=true"));

        internal void Run(ConductorBatchLoop loop, int count, Action<BatchTickSummary> onTick)
        {
            var observedEventCount = File.Exists(LogPath) ? File.ReadAllLines(LogPath).Length : 0;
            Trace($"RECEIPT_HOLD_PHASE loop=start requestedTicks={count}");
            loop.Run(Kernel, Driver, ConductorAutonomyPolicy.Permissive,
                Path.Combine(Repo, "stop-does-not-exist"), maxIterations: count,
                watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false, onTick: tick =>
                {
                    Trace($"RECEIPT_HOLD_PHASE tick={tick.Tick} callback=start");
                    // Retain the loop's measured phase costs in the TRX, including a timed-out
                    // run whose temporary repository and conduct log are later cleaned up.
                    // These measurements are diagnostic only; assertions remain tick-based.
                    foreach (var timing in (tick.ProgressLines ?? []).Where(line =>
                                 line.StartsWith("PHASE_TIMING ", StringComparison.Ordinal)))
                    {
                        Trace($"RECEIPT_HOLD_PHASE timing={timing}");
                    }
                    // Release and stale events are emitted directly, outside the tick summary.
                    // Observe their actual JSON details once per tick, retaining duplicates so
                    // the existing exact-value and single-event assertions still detect faults.
                    var events = File.ReadAllLines(LogPath);
                    var receiptEvents = events.Skip(observedEventCount).Select(line =>
                    {
                        using var document = JsonDocument.Parse(line);
                        var kind = document.RootElement.GetProperty("eventKind").GetString();
                        return kind is "train-receipt-released" or "train-receipt-stale"
                            ? document.RootElement.GetProperty("detail").GetString()!
                            : null;
                    }).OfType<string>().ToArray();
                    observedEventCount = events.Length;
                    onTick(tick with { ProgressLines = (tick.ProgressLines ?? []).Concat(receiptEvents).ToArray() });
                    Trace($"RECEIPT_HOLD_PHASE tick={tick.Tick} callback=returned");
                });
            Trace("RECEIPT_HOLD_PHASE loop=returned");
        }

        internal void SetBlocked(bool blocked)
        {
            var member = Receipt.Identity.Members.Single(member => member.GoalId == Goals[2].Id);
            if (blocked)
            {
                // An operator evidence gap only excludes grouped admission and still allows a solo
                // gate. Use the existing owner-review hold to keep this member Verified across ticks.
                string[] checks = ["owner-protected configuration"];
                Kernel.RecordAcceptanceFailure(Goals[2].Id, checks, member.CandidateRevision,
                    Receipt.Identity.ObservedMainRevision);
                var failure = Goals[2].LatestAcceptanceFailure!;
                GoalOperationJournal.OwnerReviewHoldEntered(Repo, Goals[2], member.CandidateRevision,
                    Receipt.Identity.ObservedMainRevision,
                    new OwnerReviewHoldReceipt(null, failure.OccurredAt, "Waiting for owner review."), checks);
                Assert.Equal(GateReadyCandidateExclusionReason.OwnerReviewHold,
                    Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(
                        Driver.ProjectGateReadyCandidate(Goals[2], ConductorAutonomyPolicy.Permissive)).Reason);
                Assert.Null(Driver.TryBuildParallelAcceptanceCandidate(Goals[2],
                    ConductorAutonomyPolicy.Permissive, 0, out _));
            }
            else
            {
                Kernel.ClearAcceptanceFailure(Goals[2].Id);
                Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                    Driver.ProjectGateReadyCandidate(Goals[2], ConductorAutonomyPolicy.Permissive));
            }
        }

        internal string[] EventLines(string tag) => File.ReadAllLines(LogPath)
            .Where(line => line.Contains(tag + " ", StringComparison.Ordinal)).ToArray();

        internal void AssertHeld(BatchTickSummary tick, int heldTicks)
        {
            var progress = tick.ProgressLines ?? [];
            var line = Assert.Single(progress.Where(line => line.StartsWith("TRAIN_RECEIPT_HELD ", StringComparison.Ordinal)));
            Assert.StartsWith($"TRAIN_RECEIPT_HELD tick={heldTicks} train={Receipt.Identity.Value} ", line);
            Assert.Contains($"receipt={Receipt.ReceiptId} member={Goals[2].Id.Value[..8]} " +
                $"reason=OwnerReviewHold heldTicks={heldTicks}", line);
            foreach (var goal in Goals.Take(2))
            {
                Assert.Contains(progress, line => line.StartsWith("ADMISSION ", StringComparison.Ordinal) &&
                    line.Contains("reason=PassedTrainReceiptHeld", StringComparison.Ordinal) &&
                    line.Contains($"goal={goal.Id.Value[..8]}", StringComparison.Ordinal));
            }
            Assert.Equal(0, Verifier.RunCount);
            Assert.Equal(0, Pending.StartCount);
            Assert.All(Goals, goal =>
            {
                Assert.Equal(GoalStatus.Verified, goal.Status);
                Assert.False(Driver.ParallelAcceptanceAttemptCoordinator.HasLiveAttempt(goal.Id.Value));
            });
        }

        public void Dispose()
        {
            Trace("RECEIPT_HOLD_PHASE cleanup=start");
            DeleteDirectory(Repo);
            Trace("RECEIPT_HOLD_PHASE cleanup=returned");
        }
    }
}
