using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

// Parallel-safe: the ledger, candidate files and journal all live in a unique test directory.
public sealed class AcceptancePartitionVerdictCacheRerunCreditTests : IDisposable
{
    private const string G = "12345678123456781234567812345678";
    private const string H = "87654321876543218765432187654321";
    private const string M = "abcdef12abcdef12abcdef12abcdef12";
    private const string Identity = "Flaky.Tests.LaneA.Method";
    private readonly string _root = Directory.CreateTempSubdirectory("probe-credit-").FullName;
    private readonly List<string> _lines = [];
    private readonly List<AcceptanceManifestCheck> _confirmations = [];
    private readonly AcceptanceManifestCheck _partition = new()
    {
        Name = "infrastructure tests: remainder", Type = "dotnet-test", Runner = "mtp",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~Flaky.Tests"]
    };

    [Fact]
    public async Task CrossGoalFlakeOutsideDiffIsCredited()
    {
        Seed(H, 1);
        var cache = CreateCache([G]);
        var probe = Green();

        var verdict = await SelectAsync(cache, First(), probe);

        Assert.Same(probe, verdict);
        Assert.True(verdict.Passed);
        var check = Assert.Single(_confirmations);
        Assert.Equal([Identity], check.FocusedEvidenceTokens.Select(token => token.Value));
        Assert.Equal(["--filter", "FullyQualifiedName~" + Identity], check.Arguments);
        Assert.Contains("verdict_source=rerun-credit", ProbeEntry().Detail, StringComparison.Ordinal);
        Assert.Equal("GREEN", ProbeEntry().PartitionVerdict);
        Assert.Contains("flake_confirmed=true", ProbeEntry().Detail, StringComparison.Ordinal);
        var rows = Ledger().Read();
        Assert.Equal(2, rows.Count);
        var credited = Assert.Single(rows, row => row.GoalId == G);
        Assert.Equal(AcceptanceLaneRerunEvidence.Flake, credited.Outcome);
        Assert.Equal([Identity], credited.FirstFailingTestIdentities);
        Assert.Equal("attempt:first:0", credited.FirstInvocationId);
        Assert.Equal(BuildInvocationIdentity(_partition, 2, "attempt"), credited.RerunInvocationId);
        Assert.Equal(["confirmation.trx"], credited.RerunTestResultPaths);
        Assert.Equal($"LANE_RERUN_CREDIT lane=\"{_partition.Name}\" identities=1 history_goals={H} confirm_invocation={credited.RerunInvocationId}", Assert.Single(_lines));
    }

    [Theory]
    [InlineData(G, 1, false)]
    [InlineData(M, 1, true)]
    [InlineData(H, 15, false)]
    public async Task HistoryOnlyFromMembersOrStaleIsDenied(string historyGoal, int age, bool train)
    {
        Seed(historyGoal, age);
        var first = First();

        var verdict = await SelectAsync(CreateCache(train ? [G, M] : [G]), first, Green());

        AssertDenied(first, verdict, "no-cross-goal-history");
        Assert.Empty(_confirmations);
        Assert.Single(Ledger().Read());
    }

    [Theory]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LaneA.cs", true)]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OtherTests.cs", true)]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LaneA.cs", false)]
    public async Task FailingClassInsideDiffIsDenied(string path, bool exists)
    {
        Seed(H, 1);
        if (exists) WriteCandidate(path, "public sealed class LaneA { }");
        var first = First();

        var verdict = await SelectAsync(CreateCache([G], [path]), first, Green());

        AssertDenied(first, verdict, "inside-diff");
        Assert.Empty(_confirmations);
        Assert.Single(Ledger().Read());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RedConfirmationOrUnknownMembersIsDenied(bool knownMembers)
    {
        Seed(H, 1);
        var first = First();
        var red = Green() with { Passed = false, ExitCode = 1 };

        var verdict = await SelectAsync(CreateCache(knownMembers ? [G] : null), first, Green(), red);

        AssertDenied(first, verdict, knownMembers ? "confirm-red" : "members-unknown");
        Assert.Equal(knownMembers ? 1 : 0, _confirmations.Count);
        Assert.Single(Ledger().Read());
        Assert.DoesNotContain(Ledger().Read(), row => row.GoalId == G);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("omits-own-goal")]
    public async Task InvalidExplicitMembershipDeniesBeforeReadingDiff(string membership)
    {
        Seed(H, 1);
        string[] members = membership switch { "empty" => [], "blank" => [G, " "], _ => [H] };
        var diffReads = 0;
        var first = First();
        var cache = CreateCache(members, resolver: (_, _, _) => { diffReads++; throw new IOException(); });

        var verdict = await SelectAsync(cache, first, Green());

        AssertDenied(first, verdict, "members-unknown");
        Assert.Equal(0, diffReads);
        Assert.Empty(_confirmations);
    }

    [Fact]
    public async Task TimeoutTrxFailuresJoinReportedFailuresBeforeHistoryCheck()
    {
        var path = WriteCandidate("first.trx", """
            <TestRun><Results><UnitTestResult testName="Flaky.Tests.LaneB.Slow" outcome="Timeout" /></Results></TestRun>
            """);
        Seed(H, 1);
        var first = First() with { TestResultPaths = [path] };

        var verdict = await SelectAsync(CreateCache([G]), first, Green());

        // Reported LaneA has history; the recovered timeout must also have its own history.
        AssertDenied(first, verdict, "no-cross-goal-history");
        Assert.Empty(_confirmations);
    }

    [Theory]
    [InlineData(AcceptanceShardCompletionPredicates.IncompleteExecution)]
    [InlineData(AcceptanceShardCompletionPredicates.NonzeroExit)]
    public async Task TimeoutIdentityRecoveredFromTrxCanEarnCredit(string predicate)
    {
        var path = WriteCandidate("first.trx", $"""
            <TestRun><Results><UnitTestResult testName="{Identity}" outcome="Timeout" /></Results></TestRun>
            """);
        Seed(H, 1);
        var first = First(predicate) with { FailingTestIdentities = [], TestResultPaths = [path] };

        var verdict = await SelectAsync(CreateCache([G]), first, Green());

        Assert.True(verdict.Passed);
        Assert.Single(_confirmations);
        Assert.Equal([Identity], Assert.Single(Ledger().Read(), row => row.GoalId == G).FirstFailingTestIdentities);
    }

    [Theory]
    [InlineData("Failed", true)]
    [InlineData("Timeout", true)]
    [InlineData("Error", false)]
    [InlineData("Aborted", false)]
    [InlineData("NotRunnable", false)]
    public async Task TrxRecoveryIsLimitedToFailedAndTimeout(string outcome, bool credited)
    {
        var path = WriteCandidate("first.trx", $"""
            <TestRun><Results><UnitTestResult testName="{Identity}" outcome="{outcome}" /></Results></TestRun>
            """);
        Seed(H, 1);
        var first = First() with { FailingTestIdentities = [], TestResultPaths = [path] };

        var verdict = await SelectAsync(CreateCache([G]), first, Green());

        Assert.Equal(credited, verdict.Passed);
        Assert.Equal(credited ? 1 : 0, _confirmations.Count);
        if (!credited)
        {
            Assert.Same(first, verdict);
            Assert.Empty(_lines);
            Assert.Single(Ledger().Read());
        }
    }

    [Theory]
    [InlineData("red")]
    [InlineData("no-decision")]
    [InlineData("red-decision")]
    [InlineData("empty")]
    [InlineData("no-trx")]
    [InlineData("failures")]
    public async Task IneligibleProbeDoesNotStartConfirmation(string scenario)
    {
        Seed(H, 1);
        var probe = scenario switch
        {
            "red" => Green() with { Passed = false },
            "no-decision" => Green() with { CompletionDecision = null },
            "red-decision" => Green() with { CompletionDecision = First().CompletionDecision },
            "empty" => Green(0),
            "no-trx" => Green() with { TestResultPaths = [] },
            _ => Green() with { FailingTestIdentities = [Identity] }
        };
        var first = First();

        var verdict = await SelectAsync(CreateCache([G]), first, probe);

        AssertDenied(first, verdict, "not-eligible", probe.Passed);
        Assert.Empty(_confirmations);
    }

    [Theory]
    [InlineData("unknown test", "unknown test")]
    [InlineData("LaneA", "LaneA")]
    [InlineData("Flaky.Tests.LaneA.Method(value: 1)", "Flaky.Tests.LaneA.Method(value: 2)")]
    [InlineData(Identity, Identity, -1)]
    public async Task IdentityAndHistoryMustMatchBeforeClassResolution(string failure, string history, int age = 1)
    {
        Seed(H, age, history);
        var first = First() with { FailingTestIdentities = [failure] };

        var verdict = await SelectAsync(CreateCache([G]), first, Green());

        AssertDenied(first, verdict, failure == history && age > 0 ? "identity-unresolved" : "no-cross-goal-history");
        Assert.Empty(_confirmations);
    }

    [Fact]
    public async Task DiffReadFailureDeniesBeforeConfirmation()
    {
        Seed(H, 1);
        var first = First();
        var cache = CreateCache([G], resolver: (_, _, _) => throw new IOException("injected diff failure"));

        var verdict = await SelectAsync(cache, first, Green());

        AssertDenied(first, verdict, "inside-diff");
        Assert.Empty(_confirmations);
    }

    [Theory]
    [InlineData("no-completion")]
    [InlineData("no-execution")]
    [InlineData("no-trx")]
    [InlineData("failures")]
    public async Task GreenConfirmationStillRequiresNarrowedExecutionEvidence(string scenario)
    {
        Seed(H, 1);
        var confirmation = scenario switch
        {
            "no-completion" => Green() with { CompletionDecision = null },
            "no-execution" => Green(0),
            "no-trx" => Green() with { TestResultPaths = [] },
            _ => Green() with { FailingTestIdentities = [Identity] }
        };
        var first = First();

        var verdict = await SelectAsync(CreateCache([G]), first, Green(), confirmation);

        AssertDenied(first, verdict, "confirm-red");
        Assert.Single(_confirmations);
        Assert.Single(Ledger().Read());
    }

    [Fact]
    public async Task EveryFailureNeedsHistoryAndBothLedgerOutcomesCount()
    {
        const string secondIdentity = "Flaky.Tests.LaneB.Other";
        Seed(H, 1);
        Ledger().Append(Assert.Single(Ledger().Read()) with
        {
            GoalId = M, FirstFailingTestIdentities = [secondIdentity],
            Outcome = AcceptanceLaneRerunEvidence.ConfirmedFailure
        });
        var first = First() with { FailingTestIdentities = [Identity, secondIdentity, Identity] };

        var verdict = await SelectAsync(CreateCache([G]), first, Green(), Green(2));

        Assert.True(verdict.Passed);
        Assert.Equal([Identity, secondIdentity], Assert.Single(_confirmations).FocusedEvidenceTokens.Select(token => token.Value));
        Assert.Equal([Identity, secondIdentity], Assert.Single(Ledger().Read(), row => row.GoalId == G).FirstFailingTestIdentities);
        Assert.Contains($"history_goals={H},{M} ", Assert.Single(_lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFilterCannotFallBackToWholeLaneConfirmation()
    {
        Seed(H, 1);
        var check = new AcceptanceManifestCheck
        {
            Name = _partition.Name, Type = _partition.Type, Runner = _partition.Runner,
            Project = _partition.Project, Arguments = []
        };
        // Test the policy directly: an invalid lane selection cannot earn credit or execute.
        var decision = await AcceptanceProbeRerunCredit.EvaluateAsync(check, First(), Green(), G, [G],
            since => Ledger().Read(check.Name, since), () => [], _root,
            selected => { _confirmations.Add(selected); return Task.FromResult(Green()); }, CancellationToken.None);

        Assert.False(decision.Granted);
        Assert.Equal("confirm-red", decision.DenialReason);
        Assert.Empty(_confirmations);
    }

    [Fact]
    public async Task NoNamedFailuresDoesNotEmitDenialOrStartConfirmation()
    {
        var first = First() with { FailingTestIdentities = [] };

        var verdict = await SelectAsync(CreateCache([G]), first, Green());

        Assert.Same(first, verdict);
        Assert.Empty(_lines);
        Assert.Empty(_confirmations);
        Assert.Empty(Ledger().Read());
    }

    private AcceptancePartitionVerdictCache CreateCache(IReadOnlyList<string>? members,
        IReadOnlyList<string>? paths = null, Func<string, string, string, IReadOnlyList<string>>? resolver = null) =>
        Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(new(
            new GoalId(G), _root, [_partition], 5, true,
            _ => "candidate", _ => "main", _ => "commit", () => "attempt", () => "manifest", () => false,
            ResolveChangedFiles: resolver ?? ((_, _, _) => paths ?? ["src/Unrelated.cs"]),
            MemberGoalIds: members, EmitDiagnostic: _lines.Add)));

    private Task<AcceptanceCheckResult> SelectAsync(AcceptancePartitionVerdictCache cache,
        AcceptanceCheckResult first, AcceptanceCheckResult probe, AcceptanceCheckResult? confirmation = null)
    {
        cache.RecordWithinAttemptRetry(_partition, first, "attempt:first:0", "attempt:probe:1",
            new AcceptanceRetainedDiagnostic("first.err", "first-sha"));
        return cache.SelectPartitionVerdictAsync(_partition, first, probe, check =>
        {
            _confirmations.Add(check);
            return Task.FromResult(confirmation ?? Green(1) with
            {
                TestResultPaths = ["confirmation.trx"], TestResultRunOrdinal = 2
            });
        });
    }

    private AcceptanceCheckResult First(string predicate = AcceptanceShardCompletionPredicates.IncompleteExecution) =>
        new(_partition.Name, false, 1, "original failure", TestResultPaths: [], FailingTestIdentities: [Identity],
            ExecutedTestCount: 350, CompletionDecision: new(false, predicate, false, 1, 351, 350, "Timeout"));

    private AcceptanceCheckResult Green(int executed = 351) => new(_partition.Name, true, 0, null,
        TestResultPaths: ["probe.trx"], FailingTestIdentities: [], ExecutedTestCount: executed,
        CompletionDecision: new(true, null, false, 0, executed, executed, "Passed"));

    private void Seed(string goal, int age, params string[] identities) => Ledger().Append(new(
        goal, "past-attempt", _partition.Name, "past-candidate", "past:first", ["past-first.trx"],
        identities.Length == 0 ? [Identity] : identities, AcceptanceShardCompletionPredicates.IncompleteExecution,
        "past:rerun", ["past-rerun.trx"], [], null, AcceptanceLaneRerunEvidence.Flake, DateTimeOffset.UtcNow.AddDays(-age)));

    private AcceptanceLaneFlakeLedger Ledger() => new(_root);

    private GoalOperationJournalEntry ProbeEntry() => Assert.Single(
        GoalOperationJournal.ReadStrict(_root, new GoalId(G)).Entries,
        entry => entry.Operation == "acceptance:partition-within-attempt-probe");

    private void AssertDenied(AcceptanceCheckResult first, AcceptanceCheckResult verdict, string reason, bool probePassed = true)
    {
        Assert.Same(first, verdict);
        Assert.False(verdict.Passed);
        Assert.Contains("verdict_source=first_run", ProbeEntry().Detail, StringComparison.Ordinal);
        Assert.Equal("RED", ProbeEntry().PartitionVerdict);
        Assert.Contains($"flake_confirmed={probePassed.ToString().ToLowerInvariant()}", ProbeEntry().Detail, StringComparison.Ordinal);
        Assert.Equal($"LANE_RERUN_CREDIT_DENIED lane=\"{_partition.Name}\" reason={reason}", Assert.Single(_lines));
    }

    private string WriteCandidate(string path, string content)
    {
        var absolute = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
        return absolute;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
