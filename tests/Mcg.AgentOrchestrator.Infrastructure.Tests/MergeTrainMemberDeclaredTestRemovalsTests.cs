using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its workspace, build storage, owner artifacts and TRX.
public sealed class MergeTrainMemberDeclaredTestRemovalsTests : IDisposable
{
    private const string FirstRemoval = "Example.Tests.RemovedTests.First";
    private const string SecondRemoval = "Example.Tests.RemovedTests.Second";
    private const string Retained = "Example.Tests.RetainedTests.Runs";
    private readonly string _root = CreateTempDirectory();

    [Fact]
    public void FromMembers_TrainAttributesOnlyDeclaredIdentities()
    {
        var declaring = GoalId.New();
        var other = GoalId.New();

        var records = MemberDeclaredTestRemovals.FromMembers(
            [(declaring, RemovalObjective(FirstRemoval, SecondRemoval)), (other, "No removals")]);

        Assert.Equal(
            [new MemberDeclaredTestRemoval(declaring, FirstRemoval), new(declaring, SecondRemoval)], records);
    }

    [Fact]
    public void FromMembers_FirstDeclarerWinsIgnoringCaseAndEmptyObjectivesContributeNothing()
    {
        var first = GoalId.New();
        var second = GoalId.New();
        var records = MemberDeclaredTestRemovals.FromMembers(
        [
            (GoalId.New(), null), (GoalId.New(), ""), (GoalId.New(), "No acceptance section"),
            (first, RemovalObjective(FirstRemoval, FirstRemoval.ToUpperInvariant())),
            (second, RemovalObjective(FirstRemoval.ToLowerInvariant(), SecondRemoval))
        ]);

        Assert.Equal(
            [new MemberDeclaredTestRemoval(first, FirstRemoval), new(second, SecondRemoval)], records);
    }

    [Fact]
    public void GateOptions_CarryWorkspaceMembersDeclarationsWithAttribution()
    {
        var (driver, goals, members) = CreateTrain();

        var options = GateOptions(driver, members, goals);

        Assert.Equal(
            [new MemberDeclaredTestRemoval(members[0].GoalId, FirstRemoval), new(members[0].GoalId, SecondRemoval)],
            options.MemberTestRemovals);
    }

    [Fact]
    public async Task OwnedVerifier_UnionCreditsAbsentTestsAndPreservesWorktreeOrder()
    {
        var (driver, goals, members) = CreateTrain();
        var options = GateOptions(driver, members, goals);
        // A normal train has no per-goal criteria file: its declarations must come from options.
        Assert.False(File.Exists(CriteriaPath));
        var sanctioned = await ResolveOwned(options);
        Assert.Equal([FirstRemoval, SecondRemoval], sanctioned);
        var coverage = Evaluate(sanctioned, FirstRemoval, SecondRemoval);
        Assert.True(coverage.Passed, coverage.Summary);
        Assert.Empty(coverage.MissingTests);
        AssertCredited(coverage, FirstRemoval, SecondRemoval);

        WriteWorktreeCriteria(SecondRemoval.ToUpperInvariant(), "Worktree.Tests.Removed");
        var withWorktree = await ResolveOwned(options);
        Assert.Equal([SecondRemoval.ToUpperInvariant(), "Worktree.Tests.Removed", FirstRemoval], withWorktree);
        Assert.Equal(members[0].GoalId, options.MemberTestRemovals![1].GoalId);
    }

    [Fact]
    public async Task Coverage_UndeclaredRemovalStillFailsWithDeclaredRemovalsCredited()
    {
        const string undeclared = "Example.Tests.RemovedTests.Undeclared";
        var (driver, goals, members) = CreateTrain();
        var sanctioned = await ResolveOwned(GateOptions(driver, members, goals));

        var coverage = Evaluate(sanctioned, FirstRemoval, SecondRemoval, undeclared);

        Assert.False(coverage.Passed);
        Assert.Equal(["cross-generation-count:candidate=1,minimum=2,main=4,deleted=2"], coverage.MissingTests);
        AssertCredited(coverage, FirstRemoval, SecondRemoval);
        Assert.DoesNotContain($"identity={undeclared},", coverage.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GateOptions_EjectedMemberDeclarationIsExcludedOnRecomposition()
    {
        const string ejectedRemoval = "Example.Tests.EjectedTests.Removed";
        var (driver, goals, members) = CreateTrain(ejectedRemoval);
        var fullOptions = GateOptions(driver, members, goals);
        var fullCoverage = Evaluate(await ResolveOwned(fullOptions), FirstRemoval, SecondRemoval, ejectedRemoval);
        Assert.True(fullCoverage.Passed, fullCoverage.Summary);
        AssertCredited(fullCoverage, FirstRemoval, SecondRemoval, ejectedRemoval);

        // The objective source still contains the ejected goal, as RunMergeTrain's goalsById does.
        var workspaceMembers = members[..2];
        var remainingOptions = GateOptions(driver, workspaceMembers, goals);
        Assert.Equal(3, goals.Count);
        Assert.Equal(2, remainingOptions.MemberTestRemovals!.Count);
        Assert.DoesNotContain(remainingOptions.MemberTestRemovals, removal => removal.GoalId == members[2].GoalId);
        var sanctioned = await ResolveOwned(remainingOptions);
        Assert.Equal([FirstRemoval, SecondRemoval], sanctioned);

        var coverage = Evaluate(sanctioned, FirstRemoval, SecondRemoval, ejectedRemoval);
        Assert.False(coverage.Passed);
        Assert.Equal(["cross-generation-count:candidate=1,minimum=2,main=4,deleted=2"], coverage.MissingTests);
        AssertCredited(coverage, FirstRemoval, SecondRemoval);
        Assert.DoesNotContain($"identity={ejectedRemoval},", coverage.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SoloOptions_AbsentOrEmptyMemberRemovalsPreserveWorktreeList()
    {
        WriteWorktreeCriteria(SecondRemoval, FirstRemoval, FirstRemoval.ToUpperInvariant());

        var omitted = await ResolveOwned(new AcceptanceRunExecutionOptions());
        var empty = await ResolveOwned(new AcceptanceRunExecutionOptions(MemberTestRemovals: []));

        Assert.Equal([SecondRemoval, FirstRemoval], omitted);
        Assert.Equal(omitted, empty);
        Assert.True(Evaluate(omitted, FirstRemoval, SecondRemoval).Passed);
        string[] original = ["verbatim ", "VERBATIM ", SecondRemoval];
        Assert.Same(original, MemberDeclaredTestRemovals.UnionSanctioned(original, null));
        Assert.Same(original, MemberDeclaredTestRemovals.UnionSanctioned(original, []));
    }

    private string CriteriaPath => Path.Combine(_root, ".orchestrator", "goal-acceptance-criteria.json");

    private static string RemovalObjective(params string[] identities) =>
        "## Acceptance criteria\n" + string.Join('\n', identities.Select(identity => $"- test-removal: `{identity}`"));

    private (ConductorDriver Driver, IReadOnlyDictionary<GoalId, Goal> Goals, MergeTrainMemberBinding[] Members)
        CreateTrain(string? thirdRemoval = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var goals = new List<Goal>
        {
            kernel.CreateGoal(RemovalObjective(FirstRemoval, SecondRemoval)),
            kernel.CreateGoal("Other member with no removals")
        };
        if (thirdRemoval is not null)
            goals.Add(kernel.CreateGoal(RemovalObjective(thirdRemoval)));
        var verifier = new GoalAcceptanceVerifier(
            (string[] _, string _, TimeSpan _, CancellationToken _) =>
                throw new InvalidOperationException("The options fixture must not launch a command"));
        var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(_root), verifier,
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
            cleanupHooks: new GoalWorktreeCleanupHooks { BuildStorageRoot = new(Path.Combine(_root, "build-storage")) });
        var members = goals.Select((goal, index) => new MergeTrainMemberBinding(
            goal.Id, new string((char)('a' + index), 40), new string((char)('a' + index), 40),
            [$"tests/Member{index}Tests.cs"], [$"member:{index}"], ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected", new string('d', 40))).ToArray();
        return (driver, goals.ToDictionary(goal => goal.Id), members);
    }

    private static AcceptanceRunExecutionOptions GateOptions(ConductorDriver driver,
        MergeTrainMemberBinding[] workspaceMembers, IReadOnlyDictionary<GoalId, Goal> goals) =>
        driver.CreateMergeTrainGateExecutionOptions(
            MergeTrainIdentity.Create(workspaceMembers, new string('e', 40), new string('f', 40), "manifest-v1"),
            workspaceMembers, ConductorDriver.TrainGateRunIdentity(workspaceMembers.Select(member => member.GoalId.Value)), goals);

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
        return TestCoverageInvariant.Evaluate([Retained], [new TestPartitionCoverage("train", true, [trx])],
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
