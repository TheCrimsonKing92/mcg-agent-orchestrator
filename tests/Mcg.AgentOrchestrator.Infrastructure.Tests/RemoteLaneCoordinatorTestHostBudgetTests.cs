using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: unique temporary roots, an injected clock and executor, no global overrides.
public sealed class RemoteLaneCoordinatorTestHostBudgetTests
{
    [Fact]
    public async Task RunRemoteAsync_MatchingHistory_SubmitsAndRecordsBudget()
    {
        using var fixture = new Fixture(useHistory: true);
        var request = await fixture.RunAsync();

        Assert.Equal(1500, request.TestHostTimeoutSeconds);
        using var json = fixture.Json();
        var budget = json.RootElement.GetProperty("test_host_budget");
        Assert.Equal(1500, budget.GetProperty("seconds").GetInt32());
        Assert.Equal("remote-history", budget.GetProperty("source").GetString());
        Assert.Equal(2100, budget.GetProperty("host_budget_seconds").GetInt32());
        Assert.Equal(500, budget.GetProperty("remote_median_seconds").GetDouble());
        Assert.Equal(3, budget.GetProperty("remote_samples").GetInt32());
        Assert.Equal(3, budget.GetProperty("multiplier").GetDouble());
        Assert.Equal(600, budget.GetProperty("floor_seconds").GetInt32());
        Assert.Equal(request.TestHostBudget, Assert.Single(RemoteExecutorHealthLedger.ReadAll(fixture.Ledger)).TestHostBudget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunRemoteAsync_NoMatchingHistory_PreservesBaselineRow(bool differentFilter)
    {
        using var baseline = new Fixture();
        using var fixture = new Fixture(useHistory: differentFilter, differentFilter: differentFilter);
        var baselineRequest = await baseline.RunAsync();
        var request = await fixture.RunAsync();

        var expected = RemoteLaneTestHostBudget.Resolve(Fixture.Timeout);
        Assert.Equal(expected, baselineRequest.TestHostTimeoutSeconds);
        Assert.Equal(expected, request.TestHostTimeoutSeconds);
        using var baselineJson = baseline.Json();
        using var json = fixture.Json();
        Assert.Equal("host-budget", baselineJson.RootElement.GetProperty("test_host_budget").GetProperty("source").GetString());
        Assert.Equal("host-budget", json.RootElement.GetProperty("test_host_budget").GetProperty("source").GetString());
        // The injected clock and fixed identity make every field deterministic; no normalization.
        var baselineFields = OtherFields(baselineJson);
        var fields = OtherFields(json);
        Assert.Equal(baselineFields.Keys.Order(), fields.Keys.Order());
        foreach (var field in baselineFields) Assert.Equal(field.Value, fields[field.Key]);
    }

    private static Dictionary<string, string> OtherFields(JsonDocument json) =>
        json.RootElement.EnumerateObject().Where(property => property.Name != "test_host_budget")
            .ToDictionary(property => property.Name, property => property.Value.GetRawText());

    private sealed class Fixture : IDisposable
    {
        internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(40);
        private const string Lane = "infrastructure tests: TestHostBudget";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "remote-host-budget-" + Guid.NewGuid().ToString("N"));
        private readonly FakeRemoteLaneExecutor _executor = new();
        private readonly RemoteLaneExecutorEntry _entry = new("executor", 60, RunnerAlias: "runner-host");
        private readonly Check _check = new()
        {
            Name = Lane, Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~TestHostBudgetTests"]
        };
        private readonly RemoteLaneCoordinator _coordinator;
        internal string Ledger => RemoteExecutorHealthLedger.ResolveStorePath(_root);

        internal Fixture(bool useHistory = false, bool differentFilter = false)
        {
            Directory.CreateDirectory(_root);
            Assert.True(GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(_check, out _, out var filter));
            var history = new Dictionary<(string Lane, string Filter), RemoteExecutorLaneSeconds>();
            if (useHistory) history.Add((Lane, GoalAcceptanceVerifier.ShortHash(differentFilter ? "other" : filter)),
                new(Lane, 3, 500, 500, null, null));
            _coordinator = new(new([_entry], [Lane], null),
                new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"),
                _root, null, _executor, new ManualRemoteLaneClock(), TimeSpan.FromMilliseconds(1), null)
                { RemoteLaneHistory = history };
            Assert.True(_coordinator.IsEligible(_check));
            Assert.Equal(_entry, _coordinator.TryClaimIdleExecutor());
        }

        internal async Task<RemoteLaneRequest> RunAsync()
        {
            var outcome = await _coordinator.RunRemoteAsync(_check, _entry, Timeout, CancellationToken.None);
            var request = Assert.Single(_executor.Requests);
            Assert.Equal(request, outcome.Request);
            return request;
        }

        internal JsonDocument Json() => JsonDocument.Parse(Assert.Single(File.ReadAllLines(Ledger)));
        public void Dispose() { _coordinator.Dispose(); Directory.Delete(_root, recursive: true); }
    }
}
