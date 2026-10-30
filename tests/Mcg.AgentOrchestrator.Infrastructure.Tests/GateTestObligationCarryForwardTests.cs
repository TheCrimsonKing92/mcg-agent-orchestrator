using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: unique fixture roots, private stores, context-local console capture, no Git.
public sealed class GateTestObligationCarryForwardTests
{
    private static readonly string A = new('a', 40);
    private static readonly string B = new('b', 40);
    private static readonly string C = new('c', 40);
    private const string CarryOperation = "conductor:acceptance-main-advance-carry";
    private static readonly (string Name, string Outcome)[] PassingRows =
        [("AlphaTests.Check", "Passed"), ("BetaTests.Check", "Passed")];

    [Fact]
    public void PassedAttempt_MappedCandidate_SatisfiesEveryObligation()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Evidence.WriteAttempt(PassingRows, candidateSha: A);
        fixture.Carry(A, B);

        AssertSatisfied(fixture, fixture.Evaluate(B), attempt.Id, A, B);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupedReceipt_MappedCandidate_FollowsReceiptEvidence(bool cohort)
    {
        using var fixture = new Fixture(typed: true);
        var path = GroupedGateTrxSamples.Write(fixture.Root, "carried-grouped.trx",
            new("alpha", "AlphaTests", "Check", "AlphaTests.Check", "Passed"),
            new("beta", "BetaTests", "Check", "BetaTests.Check", "Passed"));
        var receipt = ProspectiveEvidenceGateResolverGroupedReceiptTests.Save(
            fixture.Evidence, [path], cohort: cohort, candidate: A, briefVersion: 1);
        fixture.Carry(A, B);
        Assert.Empty(ProspectiveEvidenceGateResolver.ReadAttempts(
            fixture.Evidence.OrchestratorDirectory, fixture.Goal.Id, A));

        AssertSatisfied(fixture, fixture.Evaluate(B), receipt.ReceiptId, A, B);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CarriedReceipt_WrongBrief_RemainsPending(bool cohort)
    {
        using var fixture = new Fixture(typed: true);
        var path = GroupedGateTrxSamples.Write(fixture.Root, "wrong-brief.trx",
            new("alpha", "AlphaTests", "Check", "AlphaTests.Check", "Passed"),
            new("beta", "BetaTests", "Check", "BetaTests.Check", "Passed"));
        ProspectiveEvidenceGateResolverGroupedReceiptTests.Save(
            fixture.Evidence, [path], cohort: cohort, candidate: A, briefVersion: 2);
        fixture.Carry(A, B);

        AssertPending(fixture.Evaluate(B));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("overlap")]
    [InlineData("unknown-disposition")]
    [InlineData("unrelated-candidate")]
    [InlineData("candidate-prefix")]
    [InlineData("other-goal-file")]
    [InlineData("other-goal-record")]
    [InlineData("other-operation")]
    [InlineData("operation-case")]
    [InlineData("missing-verified")]
    [InlineData("missing-current")]
    [InlineData("empty-verified")]
    [InlineData("unknown-verified")]
    [InlineData("unknown-current")]
    [InlineData("main-keys-only")]
    [InlineData("branch-key-substrings")]
    [InlineData("malformed-json")]
    [InlineData("unreadable-gzip")]
    public void UnauthorizedJournal_LeavesObligationsPending(string scenario)
    {
        using var fixture = new Fixture();
        fixture.Evidence.WriteAttempt(PassingRows, candidateSha: A);
        var detail = $"verifiedBranch={A} currentBranch={B}";
        switch (scenario)
        {
            case "absent": break;
            case "overlap":
            case "unknown-disposition":
                fixture.Record($"{scenario} {detail}", "conductor:acceptance-main-advance");
                break;
            case "unrelated-candidate": fixture.Carry(A, C); break;
            case "candidate-prefix": fixture.Carry(A, B[..12]); break;
            case "other-goal-file":
            case "other-goal-record":
                var other = fixture.Evidence.Kernel.CreateGoal("Other",
                    [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
                GoalOperationJournal.AcceptancePassed(fixture.Root, other, CarryOperation, B, C, detail);
                if (scenario == "other-goal-record")
                    fixture.RawJournal(File.ReadAllText(GoalOperationJournal.PathFor(fixture.Root, other.Id)));
                break;
            case "other-operation": fixture.Record(detail, CarryOperation + "-other"); break;
            case "operation-case": fixture.Record(detail, CarryOperation.ToUpperInvariant()); break;
            case "missing-verified": fixture.Record($"currentBranch={B}"); break;
            case "missing-current": fixture.Record($"verifiedBranch={A}"); break;
            case "empty-verified": fixture.Record($"verifiedBranch= currentBranch={B}"); break;
            case "unknown-verified": fixture.Record($"verifiedBranch=unknown currentBranch={B}"); break;
            case "unknown-current": fixture.Record($"verifiedBranch={A} currentBranch=unknown"); break;
            case "main-keys-only": fixture.Record($"verifiedMain={A} currentMain={B}"); break;
            case "branch-key-substrings": fixture.Record($"notverifiedBranch={A} notcurrentBranch={B}"); break;
            case "malformed-json": fixture.RawJournal("{ broken json\n"); break;
            case "unreadable-gzip":
                fixture.RawJournal("");
                File.Delete(fixture.JournalPath);
                File.WriteAllText(fixture.JournalPath + ".gz", "not a gzip stream");
                break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        AssertPending(fixture.Evaluate(B));
        Assert.Empty(CarriedForwardGateSourceResolver.Resolve(fixture.Root, fixture.Goal.Id, B).CarriedFromShas);
    }

    [Fact]
    public void MissingJournalDirectory_DoesNotReadCarriedEvidence()
    {
        using var fixture = new Fixture();
        fixture.Evidence.WriteAttempt(PassingRows, candidateSha: A);
        fixture.Carry(A, B);

        AssertPending(GateTestObligationEvaluator.Evaluate(
            fixture.Goal, fixture.Evidence.OrchestratorDirectory, B));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyCandidate_DoesNotReadAnyEvidence(string candidate)
    {
        using var fixture = new Fixture();
        fixture.Evidence.WriteAttempt(PassingRows, candidateSha: A);
        fixture.Carry(A, B);

        AssertPending(fixture.Evaluate(candidate));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CarryChain_UsesEachAncestorWithFinalDestination(bool nearest)
    {
        using var fixture = new Fixture();
        var source = nearest ? B : A;
        var attempt = fixture.Evidence.WriteAttempt(PassingRows, candidateSha: source);
        fixture.Carry(A, B);
        fixture.Carry(B, C);

        Assert.Equal(new[] { B, A }, CarriedForwardGateSourceResolver.Resolve(
            fixture.Root, fixture.Goal.Id, C).CarriedFromShas);
        AssertSatisfied(fixture, fixture.Evaluate(C), attempt.Id, source, C);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("pair")]
    [InlineData("prefix")]
    public void CyclicMapping_DiscardsEveryCarriedSource(string scenario)
    {
        using var fixture = new Fixture();
        fixture.Evidence.WriteAttempt(PassingRows, candidateSha: A);
        fixture.Evidence.WriteAttempt(PassingRows, candidateSha: B);
        fixture.Carry(B, C);
        if (scenario == "self") fixture.Carry(B, B);
        else
        {
            fixture.Carry(scenario == "pair" ? C : A, B);
            if (scenario == "prefix") fixture.Carry(B, A);
        }

        AssertPending(fixture.Evaluate(C));
        Assert.Empty(CarriedForwardGateSourceResolver.Resolve(
            fixture.Root, fixture.Goal.Id, C).CarriedFromShas);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmbiguousMapping_DiscardsSourcesAndPrintsOneDiagnostic(bool ancestor)
    {
        using var fixture = new Fixture();
        fixture.Evidence.WriteAttempt(PassingRows, candidateSha: A);
        if (ancestor) fixture.Evidence.WriteAttempt(PassingRows, candidateSha: B);
        fixture.Carry(A, B);
        fixture.Carry(C, B);
        var candidate = ancestor ? new string('d', 40) : B;
        if (ancestor) fixture.Carry(B, candidate);
        var diagnostic = $"GATE_TEST_OBLIGATION_CARRY_FORWARD_AMBIGUOUS " +
            $"id={fixture.Goal.Id.Value[..8]} candidate={B} verified={A},{C}";
        var resolved = CarriedForwardGateSourceResolver.Resolve(fixture.Root, fixture.Goal.Id, candidate);
        Assert.Empty(resolved.CarriedFromShas);
        Assert.Equal(diagnostic, resolved.Diagnostic);
        var results = fixture.Evaluate(candidate);
        AssertPending(results);
        Assert.All(results, result => Assert.Equal(diagnostic, result.CarryForwardDiagnostic));

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var pending = LandingExecutor.EvaluatePendingHumanInput(
                fixture.Kernel, fixture.Goal, fixture.Workspace(), candidate);
            Assert.Equal(2, pending.ObligationLines.Count);
            Assert.All(pending.ObligationLines, line => Assert.Contains("pending:", line));
        });
        Assert.Equal(diagnostic, Assert.Single(Lines(output)));
    }

    [Fact]
    public void DuplicateMappings_ResolveOnceAndIgnoreMalformedNeighbors()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Evidence.WriteAttempt(PassingRows, candidateSha: A);
        fixture.Carry(A, B);
        fixture.Carry(A, B);
        File.AppendAllText(fixture.JournalPath, "{ broken json\n");
        var resolved = CarriedForwardGateSourceResolver.Resolve(fixture.Root, fixture.Goal.Id, B);

        Assert.Equal(A, Assert.Single(resolved.CarriedFromShas));
        Assert.Null(resolved.Diagnostic);
        AssertSatisfied(fixture, fixture.Evaluate(B), attempt.Id, A, B);
    }

    [Fact]
    public void CurrentEvidence_CyclicJournal_RemainsEligibleWithoutCarryProvenance()
    {
        using var fixture = new Fixture();
        fixture.Carry(A, B);
        fixture.Carry(B, A);
        var attempt = fixture.Evidence.WriteAttempt(PassingRows, candidateSha: B);

        var results = fixture.Evaluate(B);
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
            Assert.Equal(attempt.Id, result.SourceId);
            Assert.Empty(result.CarryForwardProvenance);
            Assert.Null(result.CarryForward);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceOrdering_NewerEvidenceWinsAcrossCandidates(bool newerCarried)
    {
        using var fixture = new Fixture();
        var older = DateTimeOffset.UnixEpoch;
        var newer = older.AddMinutes(1);
        var carried = fixture.Evidence.WriteAttempt(PassingRows, candidateSha: A,
            completedAt: newerCarried ? newer : older);
        var current = fixture.Evidence.WriteAttempt(PassingRows, candidateSha: B,
            completedAt: newerCarried ? older : newer);
        fixture.Carry(A, B);

        var results = fixture.Evaluate(B);
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
            Assert.Equal(newerCarried ? carried.Id : current.Id, result.SourceId);
            Assert.Equal(newerCarried ? Provenance(fixture, A, B, carried.Id) : "", result.CarryForwardProvenance);
        });
    }

    [Fact]
    public void CarriedAttempt_MissingTrx_RemainsPending()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Evidence.WriteAttempt(PassingRows, candidateSha: A);
        fixture.Carry(A, B);
        File.Delete(attempt.TrxPath);

        AssertPending(fixture.Evaluate(B));
    }

    [Fact]
    public void CarriedAttempt_FailedRows_RecordsFailureWithoutFollowProvenance()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Evidence.WriteAttempt(
            [("AlphaTests.Check", "Failed"), ("BetaTests.Check", "Failed")], candidateSha: A);
        fixture.Carry(A, B);

        var results = fixture.Evaluate(B);
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(GateTestObligationEvaluator.Outcome.Failed, result.Outcome);
            Assert.Equal(attempt.Id, result.SourceId);
            Assert.Empty(result.CarryForwardProvenance);
            Assert.Null(result.CarryForward);
        });
    }

    [Fact]
    public void Landing_CarriedPass_RemovesObligationHoldAndPrintsProvenanceOnce()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Evidence.WriteAttempt(PassingRows, candidateSha: A);
        var workspace = fixture.Workspace();
        Assert.Equal(fixture.Evidence.OrchestratorDirectory, workspace.OrchestratorDirectory);
        var before = LandingExecutor.EvaluatePendingHumanInput(fixture.Kernel, fixture.Goal, workspace, B);
        Assert.Equal(2, before.ObligationLines.Count);
        Assert.NotNull(before.HoldDescription);
        Assert.All(before.ObligationLines, line => Assert.EndsWith("source none", line));
        fixture.Carry(A, B);
        fixture.Carry(A, B);

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var after = LandingExecutor.EvaluatePendingHumanInput(fixture.Kernel, fixture.Goal, workspace, B);
            Assert.Empty(after.ObligationLines);
            Assert.Null(after.HoldDescription);
            Assert.Empty(fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id));
        });

        Assert.Equal(Provenance(fixture, A, B, attempt.Id), Assert.Single(Lines(output)));
    }

    private static string[] Lines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static string Provenance(Fixture fixture, string from, string to, string source) =>
        $"GATE_TEST_OBLIGATION_CARRY_FORWARD_FOLLOWED id={fixture.Goal.Id.Value[..8]} " +
        $"from={from} to={to} source={source}";

    private static void AssertSatisfied(Fixture fixture, IReadOnlyList<GateTestObligationEvaluator.Result> results,
        string source, string from, string to)
    {
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
            Assert.Equal(source, result.SourceId);
            Assert.Null(result.WaiverReason);
            Assert.Equal(Provenance(fixture, from, to, source), result.CarryForwardProvenance);
            Assert.Null(result.CarryForwardDiagnostic);
        });
    }

    private static void AssertPending(IReadOnlyList<GateTestObligationEvaluator.Result> results)
    {
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, result.Outcome);
            Assert.Null(result.SourceId);
            Assert.Null(result.WaiverReason);
            Assert.Null(result.CarryForward);
            Assert.Empty(result.CarryForwardProvenance);
        });
    }

    private sealed class Fixture : IDisposable
    {
        internal ProspectiveEvidenceGateFixture Evidence { get; } = new();
        internal string Root => Evidence.Root;
        internal string JournalPath => GoalOperationJournal.PathFor(Root, Goal.Id);
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal => Kernel.GetGoal(Evidence.Goal.Id);

        internal Fixture(bool typed = false)
        {
            var snapshot = Evidence.Kernel.ExportSnapshot();
            GateTestObligation[] obligations =
            [
                new("carry-alpha", Evidence.Goal.AuthoritativeBrief.Version, ["AlphaTests"],
                    null, "Alpha gate evidence", DateTimeOffset.UnixEpoch),
                new("carry-beta", Evidence.Goal.AuthoritativeBrief.Version, ["BetaTests.Check"],
                    null, "Beta gate evidence", DateTimeOffset.UnixEpoch)
            ];
            if (typed)
                obligations = obligations.Select((obligation, index) =>
                {
                    var identity = new PlannerEvidenceIdentity(Evidence.Goal.Id.Value, "planner", 1, index + 1, obligation.Id);
                    return obligation with { Id = identity.StableId, PlannerIdentity = identity };
                }).ToArray();
            Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = [snapshot.Goals.Single() with
                {
                    GateTestObligations = obligations
                }]
            });
        }

        internal OrchestratorWorkspace Workspace() => OrchestratorWorkspace.ForDirectory(
            Root, Root, null, new OrchestratorProjectRegistry(
                Path.Combine(Root, "registry"), Path.Combine(Root, "data")));

        internal IReadOnlyList<GateTestObligationEvaluator.Result> Evaluate(string candidate) =>
            GateTestObligationEvaluator.Evaluate(Goal, Evidence.OrchestratorDirectory, candidate, journalDirectory: Root);

        internal void Carry(string from, string to) => Record(AcceptanceMainAdvanceReceipt.FormatCarryForward(
            new AcceptanceMainAdvanceDisposition.Disjoint(["src/goal.cs"], ["src/other.cs"]),
            new AcceptanceVerificationSummary(true, [], BranchHeadSha: from, MainHeadSha: C), to, C));

        internal void Record(string detail, string operation = CarryOperation) =>
            GoalOperationJournal.AcceptancePassed(Root, Goal, operation, B, C, detail);

        internal void RawJournal(string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            File.WriteAllText(JournalPath, text);
        }

        public void Dispose() => Evidence.Dispose();
    }
}
