using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its workspace, owner artifacts, event writer path and TRX.
public sealed class ConductorDriverTestsCohortMemberTestRemovals : IDisposable
{
    private const string FirstRemoval = "Example.Tests.RemovedTests.First";
    private const string SecondRemoval = "Example.Tests.RemovedTests.Second";
    private const string Retained = "Example.Tests.RetainedTests.Runs";
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Fact]
    public void FromBatch_AttributesOnlyDeclaringMemberRemovals()
    {
        var (goals, bindings) = CreateCohort();

        var selected = CohortMemberTestRemovalSelection.FromBatch(goals, bindings);

        Assert.Equal(2, bindings.Length);
        Assert.Equal(
            [new MemberDeclaredTestRemoval(bindings[0].GoalId, FirstRemoval), new(bindings[0].GoalId, SecondRemoval)],
            selected);
    }

    [Fact]
    public void FromBatch_BindingOrderPreservesFirstDeclarerAndBothMembersUnion()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = kernel.CreateGoal(RemovalObjective(FirstRemoval));
        var second = kernel.CreateGoal(RemovalObjective(FirstRemoval.ToUpperInvariant(), SecondRemoval));
        AcceptanceCohortMemberBinding[] bindings = [Binding(first.Id, 'a'), Binding(second.Id, 'b')];

        // Batch order must not determine which member owns a shared identity.
        var selected = CohortMemberTestRemovalSelection.FromBatch([second, first], bindings);

        Assert.Equal(
            [new MemberDeclaredTestRemoval(first.Id, FirstRemoval), new(second.Id, SecondRemoval)], selected);
    }

    [Fact]
    public void FromBatch_MissingBoundGoalContributesNothing()
    {
        var (goals, bindings) = CreateCohort();

        var selected = CohortMemberTestRemovalSelection.FromBatch([goals[1]], bindings);

        Assert.Empty(selected);
    }

    [Fact]
    public void FromBatch_InvalidatedEnumerationDiscardsPartialDeclarations()
    {
        var (goals, bindings) = CreateCohort();
        IEnumerable<Goal> InvalidatedBatch()
        {
            yield return goals[0];
            throw new InvalidOperationException("Batch enumeration invalidated");
        }

        var selected = CohortMemberTestRemovalSelection.FromBatch(InvalidatedBatch(), bindings);

        Assert.Empty(selected);
    }

    [Fact]
    public void CohortOptions_CarrySelectedMemberRemovals()
    {
        var (goals, bindings) = CreateCohort();
        var selected = CohortMemberTestRemovalSelection.FromBatch(goals, bindings);

        var options = GateOptions(bindings, selected);

        Assert.Equal(
            [new MemberDeclaredTestRemoval(bindings[0].GoalId, FirstRemoval), new(bindings[0].GoalId, SecondRemoval)],
            options.MemberTestRemovals);
    }

    [Fact]
    public async Task OwnedVerifier_SanctionsDeclaredCohortRemovals()
    {
        var (goals, bindings) = CreateCohort();
        var options = GateOptions(bindings, CohortMemberTestRemovalSelection.FromBatch(goals, bindings));
        Assert.False(File.Exists(CriteriaPath));

        var sanctioned = await ResolveOwned(options);

        Assert.Equal([FirstRemoval, SecondRemoval], sanctioned);
        var coverage = Evaluate(sanctioned, FirstRemoval, SecondRemoval);
        Assert.True(coverage.Passed, coverage.Summary);
        Assert.Empty(coverage.MissingTests);
        AssertCredited(coverage, FirstRemoval, SecondRemoval);
    }

    [Fact]
    public async Task Coverage_UndeclaredCohortRemovalStillFails()
    {
        const string undeclared = "Example.Tests.RemovedTests.Undeclared";
        var (goals, bindings) = CreateCohort();
        var sanctioned = await ResolveOwned(GateOptions(bindings,
            CohortMemberTestRemovalSelection.FromBatch(goals, bindings)));

        var coverage = Evaluate(sanctioned, FirstRemoval, SecondRemoval, undeclared);

        Assert.False(coverage.Passed);
        Assert.Equal(["cross-generation-count:candidate=1,minimum=2,main=4,deleted=2"], coverage.MissingTests);
        AssertCredited(coverage, FirstRemoval, SecondRemoval);
        Assert.DoesNotContain($"identity={undeclared},", coverage.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FromBatch_NonMemberBatchGoalRemovalIsExcludedAndUndeclared()
    {
        const string nonMemberRemoval = "Example.Tests.NonMemberTests.Removed";
        var (goals, bindings) = CreateCohort(nonMemberRemoval);

        var selected = CohortMemberTestRemovalSelection.FromBatch(goals, bindings);

        Assert.Equal(3, goals.Length);
        Assert.Equal(2, bindings.Length);
        Assert.Equal(
            [new MemberDeclaredTestRemoval(bindings[0].GoalId, FirstRemoval), new(bindings[0].GoalId, SecondRemoval)],
            selected);
        Assert.DoesNotContain(selected, removal => removal.GoalId == goals[2].Id);
        var sanctioned = await ResolveOwned(GateOptions(bindings, selected));
        Assert.Equal([FirstRemoval, SecondRemoval], sanctioned);

        var coverage = Evaluate(sanctioned, FirstRemoval, SecondRemoval, nonMemberRemoval);
        Assert.False(coverage.Passed);
        Assert.Equal(["cross-generation-count:candidate=1,minimum=2,main=4,deleted=2"], coverage.MissingTests);
        AssertCredited(coverage, FirstRemoval, SecondRemoval);
        Assert.DoesNotContain($"identity={nonMemberRemoval},", coverage.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CohortOptions_WithoutMemberRemovalsKeepWorktreeList()
    {
        WriteWorktreeCriteria(SecondRemoval, FirstRemoval, FirstRemoval.ToUpperInvariant());
        var (_, bindings) = CreateCohort();
        var identity = CohortIdentity(bindings);
        var writer = EventWriter();
        // Omit both optional arguments to exercise the existing factory call shape unchanged.
        var omittedOptions = ConductorDriver.CreateCohortGateExecutionOptions(writer, identity, bindings);
        Assert.Null(omittedOptions.MemberTestRemovals);

        var omitted = await ResolveOwned(omittedOptions);
        var empty = await ResolveOwned(GateOptions(bindings, []));

        Assert.Equal([SecondRemoval, FirstRemoval], omitted);
        Assert.Equal(omitted, empty);
        Assert.True(Evaluate(omitted, FirstRemoval, SecondRemoval).Passed);
    }

    private string CriteriaPath => Path.Combine(_root, ".orchestrator", "goal-acceptance-criteria.json");

    private static string RemovalObjective(params string[] identities) =>
        "## Acceptance criteria\n" + string.Join('\n', identities.Select(identity => $"- test-removal: `{identity}`"));

    private static (Goal[] Goals, AcceptanceCohortMemberBinding[] Bindings) CreateCohort(string? nonMemberRemoval = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var goals = new List<Goal>
        {
            kernel.CreateGoal(RemovalObjective(FirstRemoval, SecondRemoval)),
            kernel.CreateGoal("Other cohort member with no removals")
        };
        AcceptanceCohortMemberBinding[] bindings = [Binding(goals[0].Id, 'a'), Binding(goals[1].Id, 'b')];
        if (nonMemberRemoval is not null)
            goals.Add(kernel.CreateGoal(RemovalObjective(nonMemberRemoval)));
        return (goals.ToArray(), bindings);
    }

    private static AcceptanceCohortMemberBinding Binding(GoalId goalId, char revision) => new(
        goalId, new string(revision, 40), new string(revision, 40),
        ["tests/Example.Tests/RemovedTests.cs"], ["ownership:tests"],
        ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");

    private static AcceptanceCohortIdentity CohortIdentity(IReadOnlyList<AcceptanceCohortMemberBinding> bindings) =>
        AcceptanceCohortIdentity.Create(bindings, new string('c', 40), new string('d', 40), "manifest-v1");

    private ConductEventLogWriter EventWriter() => new(Path.Combine(_root, ConductEventLogWriter.CurrentFileName));

    private AcceptanceRunExecutionOptions GateOptions(IReadOnlyList<AcceptanceCohortMemberBinding> bindings,
        IReadOnlyList<MemberDeclaredTestRemoval> selected) =>
        ConductorDriver.CreateCohortGateExecutionOptions(EventWriter(), CohortIdentity(bindings), bindings,
            memberTestRemovals: selected);

    private async Task<string[]> ResolveOwned(AcceptanceRunExecutionOptions options)
    {
        await using var owner = AcceptanceExecutionOwners.CreateAttempt(_root,
            options: options with { ResultsPrefix = Path.Combine(_root, "results", Guid.NewGuid().ToString("N")) });
        var source = new GoalAcceptanceVerifier(
            (string[] _, string _, TimeSpan _, CancellationToken _) =>
                throw new InvalidOperationException("Resolving sanctioned removals must not launch a command"));
        var verifier = new GoalAcceptanceVerifier(source, (IAcceptanceRunExecutionContext)owner);
        return verifier.SanctionedTestRemovalsForTests(_root);
    }

    private void WriteWorktreeCriteria(params string[] identities)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CriteriaPath)!);
        File.WriteAllText(CriteriaPath, JsonSerializer.Serialize(identities.Select(identity =>
            new AcceptanceCriterion(identity, "test-removal", TestIdentity: identity))));
    }

    private TestCoverageInvariantResult Evaluate(string[] sanctioned, params string[] absentTests)
    {
        var trx = Path.Combine(_root, "coverage.trx");
        File.WriteAllText(trx, $"""
            <TestRun><TestDefinitions>
              <UnitTest id="1" name="{Retained}"><TestMethod className="Example.Tests.RetainedTests" name="Runs" /></UnitTest>
            </TestDefinitions><Results>
              <UnitTestResult testId="1" testName="{Retained}" outcome="Passed" />
            </Results></TestRun>
            """);
        var mainTests = new[] { Retained }.Concat(absentTests).ToArray();
        var locations = mainTests.ToDictionary(identity => identity, _ => "tests/Example.Tests/RetainedTests.cs");
        return TestCoverageInvariant.Evaluate([Retained], [new TestPartitionCoverage("cohort", true, [trx])],
            mainTests, [], mainDiscoveredTestSourceFiles: locations, sanctionedRemovedTests: sanctioned);
    }

    private static void AssertCredited(TestCoverageInvariantResult coverage, params string[] identities)
    {
        foreach (var identity in identities)
            Assert.Contains($"source=declared-test-removal,identity={identity},status=corroborated,credit=1",
                coverage.Summary, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
