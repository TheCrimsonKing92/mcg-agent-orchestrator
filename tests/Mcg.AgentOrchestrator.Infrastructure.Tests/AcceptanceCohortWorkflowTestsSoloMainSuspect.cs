using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each scenario owns its repository, databases and circuit identity.
public sealed class AcceptanceCohortWorkflowTestsSoloMainSuspect : AcceptanceCohortWorkflowTests
{
    internal const string TestA = AcceptanceCohortWorkflowTestsMainSuspect.TestA;
    internal const string TestB = AcceptanceCohortWorkflowTestsMainSuspect.TestB;
    internal const string TestC = "Fixture.OtherMainRedTests.FailsC";
    internal const string OtherSource = "tests/MainSuspectFixture/OtherMainRedTests.cs";

    [Fact]
    public async Task TwoSoloGoals_SharedUntouchedFailure_TripCircuit()
    {
        using var scenario = CreateScenario();
        scenario.Complete(0, scenario.Summary(0, [TestB, TestA]));
        Assert.Empty(await scenario.Events.ReadAllAsync());
        Assert.Equal(AcceptanceEngineHealth.Healthy, (await scenario.Circuit.ReadAsync()).Health);

        // Recreate the production driver to prove that corroboration survives its lifetime.
        scenario.RestartDriver();
        scenario.Complete(1, scenario.Summary(1, [TestC, TestA]));

        var failure = Assert.Single(await scenario.Events.ReadAllAsync());
        Assert.Equal(PostLandingCanaryEventKind.Failed, failure.Kind);
        Assert.StartsWith($"post-landing-canary:{scenario.MainSha}:main-suspect:solo:", failure.EventId);
        Assert.Equal(PostLandingCanaryEventPayload.CanaryTag, failure.Payload.Tag);
        Assert.Equal(scenario.MainSha, failure.Payload.LandingSha);
        Assert.Equal(ConductorAcceptanceCohortMainSuspect.FailureToken, failure.Payload.FailureReason);
        Assert.Empty(failure.Payload.TriggeringPaths);
        Assert.Null(failure.Payload.StartedAt);
        Assert.Equal(1, failure.Payload.ExecutedTestCount);
        Assert.Equal(new[] { TestA }, failure.Payload.SharedFailingTests);
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await scenario.Circuit.ReadAsync()).Health);
        var line = Assert.Single(scenario.SoloLines());
        Assert.Contains($"CANARY_GATE sha={scenario.MainSha} result=failed reason=main-suspect source=solo", line.Detail);
        Assert.Contains($"goals={scenario.Goals[0].Id.Value[..8]},{scenario.Goals[1].Id.Value[..8]} tests={TestA}", line.Detail);
        Assert.All(scenario.Goals, goal =>
        {
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.NotNull(goal.LatestAcceptanceFailure);
            Assert.Equal(0, goal.Tasks.Single().CriterionRetryCount);
        });
    }

    [Fact]
    public async Task OneSoloGoal_InheritedFailure_PreservesHoldAndHealthyCircuit()
    {
        using var scenario = CreateScenario();
        scenario.Complete(0, scenario.Summary(0, [TestA]));

        Assert.Empty(await scenario.Events.ReadAllAsync());
        Assert.Empty(scenario.SoloLines());
        Assert.Equal(AcceptanceEngineHealth.Healthy, (await scenario.Circuit.ReadAsync()).Health);
        var receipt = Assert.Single(scenario.ReadReceipts());
        Assert.Equal(scenario.Goals[0].Id, receipt.GoalId);
        Assert.Equal(scenario.Candidates[0].BranchHeadSha, receipt.CandidateRevision);
        Assert.Equal(scenario.MainSha, receipt.ObservedMainRevision);
        Assert.Equal(scenario.MainSha, receipt.MergeBaseRevision);
        Assert.Equal(new[] { TestA }, receipt.InheritedTests);
        Assert.Contains(scenario.Paths[0], receipt.ChangedPaths);
    }

    [Fact]
    public async Task ReplayedCompletion_SameSharedSet_AppendsAndLogsOnce()
    {
        using var scenario = CreateScenario();
        scenario.Complete(0, scenario.Summary(0, [TestA]));
        var second = scenario.Summary(1, [TestA]);
        scenario.Complete(1, second);
        scenario.Complete(1, second);
        scenario.Complete(1, second);

        Assert.Single(await scenario.Events.ReadAllAsync());
        Assert.Single(scenario.SoloLines());
        Assert.Equal(2, scenario.ReadReceipts().Count);
    }

    [Fact]
    public async Task SharedFailures_OneSourceTouched_KeepsOnlyUntouchedIdentity()
    {
        using var scenario = CreateScenario(firstTouchesSource: true);
        scenario.Complete(0, scenario.Summary(0, [TestC, TestA]));
        scenario.Complete(1, scenario.Summary(1, [TestA, TestC]));

        var failure = Assert.Single(await scenario.Events.ReadAllAsync());
        Assert.Equal(new[] { TestC }, failure.Payload.SharedFailingTests);
    }

    [Fact]
    public async Task ReceiptStorageFault_PreservesHold_ReportsError()
    {
        using var scenario = CreateScenario();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = scenario.StorePath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE solo_inherited_receipts(broken TEXT);";
            command.ExecuteNonQuery();
        }

        scenario.Complete(0, scenario.Summary(0, [TestA]));

        Assert.Empty(await scenario.Events.ReadAllAsync());
        Assert.Equal(AcceptanceEngineHealth.Healthy, (await scenario.Circuit.ReadAsync()).Health);
        var error = Assert.Single(scenario.ReadLog().Where(evt => evt.EventKind == "solo-main-suspect"));
        Assert.Contains("result=error reason=SqliteException", error.Detail);
    }

    internal static Scenario CreateScenario(bool firstTouchesSource = false, bool secondTouchesSource = false)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            foreach (var (path, name) in new[]
                     {
                         (AcceptanceCohortWorkflowTestsMainSuspect.SharedSource, "SharedMainRedTests"),
                         (OtherSource, "OtherMainRedTests")
                     })
            {
                var fullPath = Path.Combine(repo, path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, $"namespace Fixture; public class {name} {{ }}");
                RunGit(repo, "add", path);
            }
            RunGit(repo, "commit", "-m", "Seed solo inherited sources on main");
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var kernel = new AgentOrchestratorKernel();
            var goals = new[]
            {
                CreateCompletedGoal(kernel, "First solo inherited goal", repo),
                CreateCompletedGoal(kernel, "Second solo inherited goal", repo)
            };
            var paths = new[]
            {
                firstTouchesSource ? AcceptanceCohortWorkflowTestsMainSuspect.SharedSource : "src/First.cs",
                secondTouchesSource ? AcceptanceCohortWorkflowTestsMainSuspect.SharedSource : "src/Second.cs"
            };
            var candidates = goals.Select((goal, i) => ConductorParallelAcceptanceCandidate.Create(
                goal, i, [paths[i]], CreateWorktreeCandidate(repo, goal.Id, paths[i], $"// candidate {i}"), main)).ToArray();
            Assert.NotEqual(goals[0].Id, goals[1].Id);
            Assert.NotEqual(candidates[0].BranchHeadSha, candidates[1].BranchHeadSha);
            return new Scenario(repo, kernel, goals, candidates, paths, main);
        }
        catch { DeleteDirectory(repo); throw; }
    }

    internal sealed class Scenario : IDisposable
    {
        internal Scenario(string repo, AgentOrchestratorKernel kernel, Goal[] goals,
            ConductorParallelAcceptanceCandidate[] candidates, string[] paths, string mainSha)
        {
            Repo = repo;
            Kernel = kernel;
            Goals = goals;
            Candidates = candidates;
            Paths = paths;
            MainSha = mainSha;
            Workspace = OrchestratorWorkspace.ForDirectory(repo);
            StorePath = Path.Combine(Workspace.OrchestratorDirectory, "cohort-acceptance.db");
            Events = new(new SqliteRunEventStore(Workspace.RunEventStorePath), Workspace.RunEventStorePath);
            Circuit = new(Events);
            RestartDriver();
        }

        internal string Repo { get; }
        internal AgentOrchestratorKernel Kernel { get; }
        internal OrchestratorWorkspace Workspace { get; }
        internal Goal[] Goals { get; }
        internal ConductorParallelAcceptanceCandidate[] Candidates { get; }
        internal string[] Paths { get; }
        internal string MainSha { get; }
        internal string StorePath { get; }
        internal PostLandingCanaryEventStore Events { get; }
        internal AcceptanceEngineCircuitBreaker Circuit { get; }
        private ConductorDriver _driver = null!;
        private SequenceAcceptanceVerifier _verifier = null!;

        internal void RestartDriver()
        {
            _verifier = new SequenceAcceptanceVerifier([]);
            _driver = new ConductorDriver(Kernel, Workspace, _verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(Repo).Hooks);
        }

        internal AcceptanceVerificationSummary Summary(int index, string[] tests,
            string? mainSha = null, string? baselineSha = null, bool unconfirmed = false)
        {
            var main = mainSha ?? MainSha;
            var attributions = tests.Select(test => new AcceptanceTestFailureAttribution(test,
                AcceptanceTestFailureOrigin.Inherited,
                $"same focused identity failed at merge-base {baselineSha ?? main}")).ToList();
            if (unconfirmed)
                attributions.Add(new(TestC, AcceptanceTestFailureOrigin.UnconfirmedIntroduced,
                    "candidate rerun passed", new("Passed", "candidate-rerun-receipt")));
            var check = new AcceptanceCheckResult("solo inherited tests", false, 1, "inherited failure",
                FailingTestIdentities: attributions.Select(item => item.TestIdentity).ToArray(),
                FailingTestAttributions: attributions);
            return new(false, [check], FailedChecks: [check.Name],
                BranchHeadSha: Candidates[index].BranchHeadSha, MainHeadSha: main);
        }

        internal void Complete(int index, AcceptanceVerificationSummary summary)
        {
            // Retain the failed gate evidence as the background completion path does.
            Kernel.RecordAcceptanceFailure(Goals[index].Id, summary.FailedChecks ?? [],
                summary.BranchHeadSha, summary.MainHeadSha);
            var result = _driver.CompleteParallelLandingAcceptance(Candidates[index],
                ConductorAutonomyPolicy.Permissive, summary, out var leaseHeld);
            Assert.False(leaseHeld);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Contains("pre-existing/main-red", held.Reason, StringComparison.Ordinal);
            Assert.Equal(0, _verifier.RunCount);
            Assert.Equal(GoalStatus.Verified, Goals[index].Status);
            Assert.Equal(0, Goals[index].Tasks.Single().CriterionRetryCount);
        }

        internal sealed record Receipt(GoalId GoalId, string CandidateRevision, string ObservedMainRevision,
            string MergeBaseRevision, IReadOnlyList<string> InheritedTests, IReadOnlyList<string> ChangedPaths);

        internal IReadOnlyList<Receipt> ReadReceipts()
        {
            // Inspect the durable artifact independently of the new store API, so these tests
            // also compile with the production source reverted for the negative control.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = StorePath }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='solo_inherited_receipts';";
            if (command.ExecuteScalar() is null) return [];
            command.CommandText = """
                SELECT goal_id, candidate_revision, observed_main_revision, merge_base_revision,
                    inherited_tests_json, changed_paths_json
                FROM solo_inherited_receipts WHERE observed_main_revision=$main ORDER BY sequence;
                """;
            command.Parameters.AddWithValue("$main", MainSha);
            using var reader = command.ExecuteReader();
            var receipts = new List<Receipt>();
            while (reader.Read())
                receipts.Add(new(new GoalId(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), JsonSerializer.Deserialize<string[]>(reader.GetString(4))!,
                    JsonSerializer.Deserialize<string[]>(reader.GetString(5))!));
            return receipts;
        }

        internal ConductEventRecord[] ReadLog() => File.Exists(Workspace.ConductEventsLogPath)
            ? File.ReadAllLines(Workspace.ConductEventsLogPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray()
            : [];

        internal ConductEventRecord[] SoloLines() => ReadLog().Where(evt =>
            evt.EventKind == "canary-gate" && evt.Detail.Contains("source=solo", StringComparison.Ordinal)).ToArray();

        public void Dispose()
        {
            PostLandingCanaryEmergencyCircuit.Clear(Events.Identity);
            DeleteDirectory(Repo);
        }
    }
}
