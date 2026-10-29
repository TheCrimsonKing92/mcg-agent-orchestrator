using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

internal static class OwnedBriefOwnershipTestScenario
{
    internal const string FirstCriterion = "The widget count is returned correctly. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
    internal const string SecondCriterion = "The diff preserves the widget contract. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.";
    internal const string Question = "Who executes verification, and do operator steps after landing gate completion?";
    internal const string Topic = "verification-execution";
    internal const string PolicyPrefix = "Spec refiner fork settled by the brief's declared criterion owners";
    internal const string Brief = "Return the widget count.\n\n## Acceptance criteria\n1. " + FirstCriterion +
        "\n2. " + SecondCriterion + "\n\n## Scope\nAfter landing, the operator counts owner questions. This observation does not gate completion.";
    internal const string MissingOwnerBrief = "Return the widget count.\n\n## Acceptance criteria\n1. " + FirstCriterion +
        "\n2. The diff preserves the widget contract. TEST-VERIFIABLE.";
    internal const string OperatorOwnedBrief = "Return the widget count.\n\n## Acceptance criteria\n1. " + FirstCriterion +
        "\n2. The operator counts owner questions. Operator owns; Operator executes. REAL-WORLD-DEPENDENT.";
    internal static readonly TimeSpan HangBound = TimeSpan.FromSeconds(30);

    internal static string AuthorOutput(string kind, string answer = "The conductor executes verification.") =>
        kind == "ask-owner"
            ? JsonSerializer.Serialize(new { kind, question = Question, recommendation = "Confirm verification ownership." })
            : JsonSerializer.Serialize(new { kind, text = answer });

    internal sealed class RefinerScenario : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"owned-brief-refiner-{Guid.NewGuid():N}");
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal Goal { get; }
        internal FakeCollaborationItemStore Collaboration { get; } = new();
        internal GoalRefinementService Service { get; }

        internal RefinerScenario(string objective, string kind = "ownership-lifecycle")
        {
            Directory.CreateDirectory(_root);
            var output = JsonSerializer.Serialize(new
            {
                behavioralContract = "Return the widget count.",
                acceptanceCriteria = AcceptanceCriteriaParser.ParseDeclared(objective),
                verificationClass = "TestVerifiable",
                decisions = Array.Empty<object>(),
                forks = new[] { new { kind, topicKey = Topic, refinerConfidence = "low", blastRadius = "high",
                    question = Question, choice = "", rationale = "Confirm verification ownership." } }
            });
            var registry = new InMemoryModelProviderRegistry([new FakeSmokeProvider(text: output, providerName: "fake-refiner")]);
            var catalog = new ModelFunctionCatalog([
                new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                    new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
            ]);
            Service = new GoalRefinementService(registry, catalog, Collaboration,
                new SpecRefinerPrecedentStore(Path.Combine(_root, "precedents.json")),
                rawOutputDirectory: Path.Combine(_root, "logs"));
            Goal = Kernel.CreateGoal(objective);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    internal sealed class AuthorHarness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"owned-brief-author-{Guid.NewGuid():N}");
        private readonly OrchestratorWorkspace _workspace;
        private readonly CollaborationItemStore _collaboration;
        private readonly ConductorAuthorHost _host;
        private readonly FakeAuthorModel _model;
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal SqliteOperatorIntentStore Intents { get; }
        internal string ConductPath => _workspace.ConductEventsLogPath;
        internal int ModelCalls => _model.Calls;

        internal AuthorHarness(string output)
        {
            Directory.CreateDirectory(_root);
            _workspace = OrchestratorWorkspace.ForDirectory(_root);
            _collaboration = CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory);
            Intents = SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory);
            _model = new FakeAuthorModel(output);
            _host = new ConductorAuthorHost(
                new ConductorAuthorClaimStore(Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db")),
                _collaboration, _model, Intents,
                new SpecRefinerPrecedentStore(_workspace.SpecRefinerPrecedentsPath), _ => _root,
                new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory),
                new ConductEventLogWriter(_workspace.ConductEventsLogPath), enabled: true);
        }

        internal async Task<ConductorAuthorItem> Raise(Goal goal, string kind = "ownership-lifecycle", string question = Question)
        {
            var clarification = await _collaboration.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value,
                "Verification ownership", $"Question: {question}\nFork kind: {kind}",
                $"spec-clarification:{goal.Id.Value}:{Topic}").WaitAsync(HangBound);
            return new(OperatorAnswerTargetKind.Clarification, clarification.Id, goal.Id.Value, question, kind);
        }

        internal async Task RoundTrip()
        {
            _host.ServiceTick(Kernel);
            // ServiceTick can harvest an immediately completed model round in the same tick.
            // Hang-only bound: wait for any outstanding round before the next harvest.
            await Task.WhenAll(_host.CurrentRounds).WaitAsync(HangBound);
            _host.ServiceTick(Kernel);
        }

        internal string Outcome(string identity)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db"), Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT outcome FROM author_claims WHERE identity = $identity";
            command.Parameters.AddWithValue("$identity", identity);
            return Assert.IsType<string>(command.ExecuteScalar());
        }

        public void Dispose()
        {
            _host.Stop();
            Directory.Delete(_root, recursive: true);
        }

        private sealed class FakeAuthorModel(string output) : IConductorAuthorModelRound
        {
            internal int Calls { get; private set; }
            public Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
                CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(output);
            }
        }
    }
}
