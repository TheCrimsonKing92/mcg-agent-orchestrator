using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Each scenario owns its temporary git repository, databases and build storage.
public sealed class AcceptanceCohortWorkflowTestsMainSuspect : AcceptanceCohortWorkflowTests
{
    internal const string SharedSource = "tests/MainSuspectFixture/SharedMainRedTests.cs";
    internal const string TestA = "Fixture.SharedMainRedTests.FailsA";
    internal const string TestB = "Fixture.SharedMainRedTests.FailsB";

    [Fact]
    public async Task SameUntouchedFailures_TripCircuitWithoutAttributingMembers()
    {
        using var scenario = CreateScenario();
        // A prior passing canary for this SHA must not collide with the new failure event.
        var sha = scenario.Selection.Members[0].MainRevision;
        var now = DateTimeOffset.UtcNow;
        await scenario.Events.AppendOnceAsync(PostLandingCanaryEventKind.Passed,
            new(PostLandingCanaryEventPayload.CanaryTag, sha, [], null, 1, "prior green", null, now),
            PostLandingCanaryEventIds.Receipt(sha), now);

        var receipt = Assert.IsType<AcceptanceCohortReceipt>(scenario.Run().Receipt);

        Assert.Equal(3, scenario.RunCount);
        Assert.Equal(AcceptanceCohortAttributionOutcome.BothMembersFailed, receipt.Attribution);
        Assert.Empty(receipt.AttributedMembers);
        Assert.Equal("main-suspect", receipt.AttributionSource);
        var persisted = Assert.IsType<AcceptanceCohortReceipt>(scenario.Store.TryReadReceipt(receipt.Identity.Value));
        Assert.Empty(persisted.AttributedMembers);
        Assert.Empty(scenario.Store.ReadSuppressedPairs());
        Assert.Null(scenario.ReadInnocentMember(receipt.Identity.Value));
        var failure = Assert.Single((await scenario.Events.ReadAllAsync())
            .Where(evt => evt.Kind == PostLandingCanaryEventKind.Failed));
        Assert.Equal(receipt.Identity.ObservedMainRevision, failure.Payload.LandingSha);
        Assert.Equal("main-suspect", failure.Payload.FailureReason);
        Assert.Contains(receipt.Identity.Value, failure.Payload.Detail, StringComparison.Ordinal);
        Assert.Contains(TestA, failure.Payload.Detail, StringComparison.Ordinal);
        var health = await scenario.Circuit.ReadAsync();
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, health.Health);
        Assert.Equal("main-suspect", health.FailureReason);
        var line = Assert.Single(scenario.ReadLog().Where(evt => evt.EventKind == "canary-gate"));
        Assert.Contains($"CANARY_GATE sha={sha} result=failed reason=main-suspect", line.Detail, StringComparison.Ordinal);
        Assert.Contains($"cohort={receipt.Identity.Value} tests={TestA}", line.Detail, StringComparison.Ordinal);
        AssertMembersUnrouted(scenario);

        // Receipt reuse and the driver's later side-effect call must not manufacture a suppression.
        scenario.Run();
        Assert.Equal(3, scenario.RunCount);
        Assert.Empty(scenario.Store.ReadSuppressedPairs());
        Assert.Single((await scenario.Events.ReadAllAsync()).Where(evt => evt.Kind == PostLandingCanaryEventKind.Failed));
        Assert.Single(scenario.ReadLog().Where(evt => evt.EventKind == "canary-gate"));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task NonQualifyingFailures_PreserveMemberAttributionAndSuppression(
        bool differentSets, bool memberTouchesSource, bool unresolvedSource)
    {
        using var scenario = CreateScenario(differentSets, memberTouchesSource, unresolvedSource);

        var receipt = Assert.IsType<AcceptanceCohortReceipt>(scenario.Run().Receipt);

        Assert.Equal(3, scenario.RunCount);
        Assert.Equal(AcceptanceCohortAttributionOutcome.BothMembersFailed, receipt.Attribution);
        Assert.Equal(2, receipt.AttributedMembers.Count);
        var persisted = Assert.IsType<AcceptanceCohortReceipt>(scenario.Store.TryReadReceipt(receipt.Identity.Value));
        Assert.Equal(2, persisted.AttributedMembers.Count);
        Assert.Equal(ConductorAcceptanceCohortSelector.PairFingerprint(
            scenario.Selection.Members[0], scenario.Selection.Members[1]),
            Assert.Single(scenario.Store.ReadSuppressedPairs()));
        Assert.Empty((await scenario.Events.ReadAllAsync()).Where(evt => evt.Payload.FailureReason == "main-suspect"));
        Assert.Equal(AcceptanceEngineHealth.Healthy, (await scenario.Circuit.ReadAsync()).Health);
        Assert.Empty(scenario.ReadLog().Where(evt => evt.EventKind == "canary-gate"));
    }

    internal static void AssertMembersUnrouted(Scenario scenario)
    {
        foreach (var goal in scenario.Goals)
        {
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Null(goal.LatestAcceptanceFailure);
            Assert.Equal(0, scenario.Store.ReadOvertakeCount(goal.Id));
        }
    }

    internal static Scenario CreateScenario(
        bool differentSets = false, bool memberTouchesSource = false, bool unresolvedSource = false)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            if (!unresolvedSource)
            {
                var path = Path.Combine(repo, SharedSource);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "namespace Fixture; public class SharedMainRedTests { }");
                RunGit(repo, "add", SharedSource);
                RunGit(repo, "commit", "-m", "Seed shared failing test source on main");
            }
            var kernel = new AgentOrchestratorKernel();
            var first = CreateCompletedGoal(kernel, "First main-suspect member", repo);
            var second = CreateCompletedGoal(kernel, "Second main-suspect member", repo);
            CreateWorktreeCandidate(repo, first.Id, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            CreateWorktreeCandidate(repo, second.Id, memberTouchesSource ? SharedSource : "tests/Second.cs", "second");
            var verifier = new SequenceAcceptanceVerifier([
                Failure(repo, "cohort.trx", differentSets ? [TestA, TestB] : [TestA]),
                Failure(repo, "first.trx", [TestA]),
                Failure(repo, "second.trx", differentSets ? [TestA, TestB] : [TestA])
            ]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(repo).Hooks);
            return new Scenario(repo, kernel, workspace, driver, [first, second],
                ProjectSelection(driver, first, second), () => verifier.RunCount);
        }
        catch { DeleteDirectory(repo); throw; }
    }

    private static AcceptanceVerificationResult Failure(string repo, string file, string[] tests) =>
        FailedVerification(repo, file, file) with
        { Checks = [new AcceptanceCheckResult(file, false, 1, "shared failure", FailingTestIdentities: tests)] };

    internal sealed class Scenario : IDisposable
    {
        internal Scenario(string repo, AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
            ConductorDriver driver, Goal[] goals, ConductorAcceptanceCohortSelection selection, Func<int> runCount)
        {
            Repo = repo;
            Kernel = kernel;
            Workspace = workspace;
            Driver = driver;
            Goals = goals;
            Selection = selection;
            _runCount = runCount;
            Store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            Events = new PostLandingCanaryEventStore(new SqliteRunEventStore(workspace.RunEventStorePath), workspace.RunEventStorePath);
            Circuit = new AcceptanceEngineCircuitBreaker(Events);
        }

        private readonly Func<int> _runCount;
        internal string Repo { get; }
        internal AgentOrchestratorKernel Kernel { get; }
        internal OrchestratorWorkspace Workspace { get; }
        internal ConductorDriver Driver { get; }
        internal Goal[] Goals { get; }
        internal ConductorAcceptanceCohortSelection Selection { get; }
        internal CohortAcceptanceStore Store { get; }
        internal PostLandingCanaryEventStore Events { get; }
        internal AcceptanceEngineCircuitBreaker Circuit { get; }
        internal int RunCount => _runCount();
        internal ConductorAcceptanceCohortRunResult Run() =>
            Driver.RunAcceptanceCohort(Selection, Goals, ConductorAutonomyPolicy.Permissive);

        internal ConductEventRecord[] ReadLog() => File.ReadAllLines(Workspace.ConductEventsLogPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();

        internal string? ReadInnocentMember(string cohortId)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Workspace.OrchestratorDirectory, "cohort-acceptance.db") }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT innocent_goal_id FROM cohort_attribution_effects WHERE cohort_id=$cohort;";
            command.Parameters.AddWithValue("$cohort", cohortId);
            var value = command.ExecuteScalar();
            Assert.NotNull(value);
            return value is DBNull ? null : (string)value;
        }

        public void Dispose()
        {
            PostLandingCanaryEmergencyCircuit.Clear(Events.Identity);
            DeleteDirectory(Repo);
        }
    }
}
