using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

public sealed class ConductorAuthorHostFrozenFactRulingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompleteHumanInputRulingQueuesExactlyOneAgentAnswerAndRecordsOutcome()
    {
        using var harness = new Harness();
        var ruling = CompleteRuling();
        var (goal, target) = await harness.Request();
        await harness.Run(Output(ruling));

        var intent = Assert.Single(await harness.Intents.ListForGoalAsync(goal.Id.Value, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(OperatorIntentVerbs.Answer, intent.Verb);
        Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        var payload = Assert.IsType<AnswerOperatorIntentPayload>(
            JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, Json));
        Assert.Equal(OperatorActorKind.Agent, payload.ActorKind);
        Assert.Equal(OperatorAnswerTargetKind.HumanInput, payload.TargetKind);
        Assert.Equal(target, payload.TargetId);
        Assert.Equal(goal.Id.Value, payload.GoalId);
        var parsed = Assert.IsType<FrozenFactRuling>(FrozenFactRuling.TryParse(payload.Text));
        Assert.Equal(ruling.FrozenClasses, parsed.FrozenClasses);
        Assert.Equal(ruling.AmendedFacts, parsed.AmendedFacts);
        Assert.Equal(ruling.Basis, parsed.Basis);
        Assert.Equal(ruling.Unmodified, parsed.Unmodified);
        Assert.Equal(ruling.DiffCheck, parsed.DiffCheck);
        Assert.Equal(ruling.EvidenceReferences, parsed.EvidenceReferences);
        Assert.Equal("answer-submitted", harness.Outcome());
        var entry = Assert.Single(File.ReadAllLines(harness.ConductPath),
            line => line.Contains("kind=frozen-fact-ruling", StringComparison.Ordinal));
        Assert.Contains($"item={goal.Id.Value}:HumanInput:{target} kind=frozen-fact-ruling reason=intent={intent.Id}", entry);
        Assert.Null(goal.CurrentHold);
    }

    [Theory]
    [InlineData("Every other assertion and fact stays unmodified in FrozenFixtureTests and OtherFrozenTests.")]
    [InlineData("Every other assertion and fact stays unmodified in FrozenFixtureTests. OtherFrozenTests.")]
    public async Task SentenceEndingClassNamesSubmitCompleteRuling(string unmodified)
    {
        using var harness = new Harness();
        var (goal, _) = await harness.Request();
        var ruling = CompleteRuling() with { Unmodified = unmodified };
        await harness.Run(Output(ruling));

        var intent = Assert.Single(await harness.Intents.ListForGoalAsync(goal.Id.Value,
            cancellationToken: TestContext.Current.CancellationToken));
        var payload = Assert.IsType<AnswerOperatorIntentPayload>(
            JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, Json));
        Assert.Equal(unmodified, Assert.IsType<FrozenFactRuling>(FrozenFactRuling.TryParse(payload.Text)).Unmodified);
        Assert.Equal("answer-submitted", harness.Outcome());
        Assert.Null(goal.CurrentHold);
    }

    [Theory]
    [InlineData("class", "amendedFacts.class")]
    [InlineData("unmodified", "unmodified")]
    [InlineData("unmodified-qualified", "unmodified")]
    [InlineData("unmodified-longer", "unmodified")]
    [InlineData("coverage", "evidenceReferences.coverage")]
    [InlineData("missing-file", "amendedFacts.file")]
    [InlineData("diff-check", "diffCheck")]
    [InlineData("line-number", "evidenceReferences.form")]
    [InlineData("evidence-missing-file", "evidenceReferences.form")]
    [InlineData("outside-file", "amendedFacts.file")]
    [InlineData("outside-evidence", "evidenceReferences.form")]
    [InlineData("empty-list", "frozenClasses")]
    [InlineData("second-class", "amendedFacts.class")]
    [InlineData("second-file", "amendedFacts.file")]
    [InlineData("second-empty-change", "amendedFacts.allowedChange")]
    public async Task IncompleteRulingEscalatesWithRenderedRecommendationAndNeverQueuesAnswer(string scenario, string field)
    {
        using var harness = new Harness();
        var (goal, _) = await harness.Request();
        var ruling = InvalidRuling(scenario);
        await harness.Run(Output(ruling));

        Assert.Empty(await harness.Intents.ListForGoalAsync(goal.Id.Value, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("author-owner-question", goal.CurrentHold?.State);
        Assert.Contains("reason=frozen-fact-ruling-incomplete:" + field, goal.CurrentHold!.Blocker);
        Assert.Contains("recommendation=" + ruling.Render(), goal.CurrentHold.Blocker);
        Assert.Equal("owner-question", harness.Outcome());
    }

    [Fact]
    public async Task CollaborationClarificationRulingEscalatesAsUnsupportedTarget()
    {
        using var harness = new Harness();
        var (goal, _) = await harness.Request(collaboration: true);
        await harness.Run(Output(CompleteRuling()));
        Assert.Empty(await harness.Intents.ListForGoalAsync(goal.Id.Value, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("reason=frozen-fact-ruling-unsupported-target", goal.CurrentHold!.Blocker);
        Assert.Equal("owner-question", harness.Outcome());
    }

    [Fact]
    public async Task CompleteRulingStillRequiresUnchangedOwnerClassCheck()
    {
        using var harness = new Harness();
        var (goal, _) = await harness.Request(question: "May we waive the acceptance gate?");
        await harness.Run(Output(CompleteRuling()));
        Assert.Empty(await harness.Intents.ListForGoalAsync(goal.Id.Value, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("reason=acceptance-weakening", goal.CurrentHold!.Blocker);
        Assert.Equal("owner-question", harness.Outcome());
    }

    [Theory]
    [InlineData("{}", "frozenClasses")]
    [InlineData("{\"frozenClasses\":[\"FrozenFixtureTests\"],\"amendedFacts\":null}", "amendedFacts")]
    public async Task MissingJsonFieldsReachIncompleteEscalation(string fields, string firstField)
    {
        using var harness = new Harness();
        var (goal, _) = await harness.Request();
        var body = fields[1..^1];
        await harness.Run("{\"kind\":\"frozen-fact-ruling\"" + (body.Length > 0 ? "," + body : "") + "}");
        Assert.Empty(await harness.Intents.ListForGoalAsync(goal.Id.Value, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("reason=frozen-fact-ruling-incomplete:" + firstField, goal.CurrentHold!.Blocker);
    }

    [Theory]
    [InlineData("\"frozenClasses\":1")]
    [InlineData("\"frozenClasses\":[null]")]
    [InlineData("\"basis\":1")]
    [InlineData("\"amendedFacts\":[false]")]
    [InlineData("\"amendedFacts\":[{\"file\":false}]")]
    public void WrongJsonTypesAreUnparseable(string field)
    {
        Assert.Null(ConductorAuthorResultParser.Parse("{\"kind\":\"frozen-fact-ruling\"," + field + "}"));
    }

    private static FrozenFactRuling CompleteRuling() => new(
        ["FrozenFixtureTests", "OtherFrozenTests"],
        [new("FrozenFixtureTests.PinsPath", "tests/Fixture/FrozenFixtureTests.cs", "Change the path from src/Old.cs to src/Fixture/Production.cs.")],
        "The fact reads the original path; the objective deliberately moves that source.",
        "Every other assertion in PinsPath and every other fact in FrozenFixtureTests and OtherFrozenTests stays unmodified.",
        "Run git diff -- tests/Fixture/FrozenFixtureTests.cs and confirm only the path in PinsPath changed.",
        ["tests/Fixture/FrozenFixtureTests.cs:1", "src/Fixture/Production.cs:1"]);

    private static FrozenFactRuling InvalidRuling(string scenario)
    {
        var ruling = CompleteRuling();
        var fact = ruling.AmendedFacts[0];
        return scenario switch
        {
            "class" => ruling with { AmendedFacts = [fact with { Fact = "UnfrozenTests.PinsPath" }] },
            "unmodified" => ruling with { Unmodified = "Every other fact in FrozenFixtureTests stays unmodified." },
            "unmodified-qualified" => ruling with { Unmodified = "FrozenFixtureTests and OtherFrozenTests.PinsPath stay unmodified." },
            "unmodified-longer" => ruling with { Unmodified = "FrozenFixtureTests and OtherFrozenTestsExtended stay unmodified." },
            "coverage" => ruling with { EvidenceReferences = [ruling.EvidenceReferences[0]] },
            "missing-file" => ruling with { AmendedFacts = [fact with { File = "tests/Absent.cs" }] },
            "diff-check" => ruling with { DiffCheck = "" },
            "line-number" => ruling with { EvidenceReferences = [ruling.EvidenceReferences[0], "src/Fixture/Production.cs"] },
            "evidence-missing-file" => ruling with { EvidenceReferences = [ruling.EvidenceReferences[0], "src/Absent.cs:1"] },
            "outside-file" => ruling with { AmendedFacts = [fact with { File = "tests/../../outside.cs" }] },
            "outside-evidence" => ruling with { EvidenceReferences = [ruling.EvidenceReferences[0], "src/../../outside.cs:1"] },
            "empty-list" => ruling with { FrozenClasses = [] },
            "second-class" => ruling with { AmendedFacts = [fact, fact with { Fact = "UnfrozenTests.PinsPath" }] },
            "second-file" => ruling with { AmendedFacts = [fact, fact with { File = "tests/Absent.cs" }] },
            "second-empty-change" => ruling with { AmendedFacts = [fact, fact with { AllowedChange = "" }] },
            _ => throw new ArgumentException(scenario)
        };
    }

    private static string Output(FrozenFactRuling ruling) => JsonSerializer.Serialize(new
    {
        kind = "frozen-fact-ruling", ruling.FrozenClasses,
        amendedFacts = ruling.AmendedFacts.Select(fact => new { fact.Fact, fact.File, fact.AllowedChange }),
        ruling.Basis, ruling.Unmodified, ruling.DiffCheck, ruling.EvidenceReferences
    }, Json);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakeModel(string output) : IConductorAuthorModelRound
    {
        public Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
            CancellationToken cancellationToken) => Task.FromResult(output);
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "author-ruling-" + Guid.NewGuid().ToString("N"));
        private readonly OrchestratorWorkspace _workspace;
        private readonly CollaborationItemStore _collaboration;
        private ConductorAuthorHost? _host;
        internal AgentOrchestratorKernel Kernel { get; } = new(new FixedClock());
        internal SqliteOperatorIntentStore Intents { get; }
        internal string ConductPath => _workspace.ConductEventsLogPath;
        private string ClaimsPath => Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db");

        internal Harness()
        {
            Directory.CreateDirectory(Path.Combine(_root, "tests", "Fixture"));
            Directory.CreateDirectory(Path.Combine(_root, "src", "Fixture"));
            File.WriteAllText(Path.Combine(_root, "tests", "Fixture", "FrozenFixtureTests.cs"), "// pinned fact");
            File.WriteAllText(Path.Combine(_root, "src", "Fixture", "Production.cs"), "// moved source");
            _workspace = OrchestratorWorkspace.ForDirectory(_root);
            _collaboration = CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory);
            Intents = SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory);
        }

        internal async Task<(Goal Goal, string Target)> Request(bool collaboration = false, string question = "Which pinned path changes?")
        {
            var task = new TaskSpec(TaskId.New(), "Implement path move", AgentRole.Developer);
            var goal = Kernel.CreateGoal("Move source to its owning file", [task]);
            Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            if (collaboration)
            {
                var item = await _collaboration.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value,
                    "Pinned path", "Question: " + question + "\nFork kind: path-move", $"spec-clarification:{goal.Id.Value}:path");
                return (goal, item.Id);
            }
            var request = Kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, question, HumanWaitKind.SpecClarification).Request;
            return (goal, request.Id.Value);
        }

        internal async Task Run(string output)
        {
            _host = new ConductorAuthorHost(new ConductorAuthorClaimStore(ClaimsPath),
                _collaboration, new FakeModel(output), Intents,
                new SpecRefinerPrecedentStore(_workspace.SpecRefinerPrecedentsPath), _ => _root,
                new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory),
                new ConductEventLogWriter(ConductPath, utcNow: () => Now), utcNow: () => Now);
            _host.ServiceTick(Kernel);
            try { await Task.WhenAll(_host.CurrentRounds).WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException ex) { throw new TimeoutException("Hang waiting for fake Author ruling round completion.", ex); }
            _host.ServiceTick(Kernel);
        }

        internal string Outcome()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ClaimsPath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT outcome FROM author_claims";
            return Assert.IsType<string>(command.ExecuteScalar());
        }

        public void Dispose()
        {
            _host?.Stop();
            Directory.Delete(_root, recursive: true);
        }
    }
}
