using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its store, clock and writer. Delays advance a fake clock.
public sealed class CliOperatorIntentRouteTestsStatusBatch
{
    [Theory]
    [InlineData(false, OperatorIntentStatus.Applied, OperatorIntentStatus.Rejected, 0)]
    [InlineData(true, OperatorIntentStatus.Applied, OperatorIntentStatus.Applied, 0)]
    [InlineData(true, OperatorIntentStatus.Rejected, OperatorIntentStatus.Pending, 2)]
    [InlineData(true, OperatorIntentStatus.Pending, OperatorIntentStatus.Claimed, 3)]
    public void OrderedBatchUsesSharedDeadlineAndRejectedPrecedence(bool wait,
        OperatorIntentStatus first, OperatorIntentStatus second, int expected)
    {
        var clock = new ManualClock();
        var store = new ScriptedStore(new Dictionary<string, OperatorIntentRecord?[]>
        {
            ["first"] = [Record("first", first)], ["second"] = [Record("second", second)]
        });
        using var output = new StringWriter();
        var delays = new List<TimeSpan>();
        var start = clock.GetUtcNow();
        string[] args = wait ? ["operator-intent-status", "second", "first", "--wait", "2"]
            : ["operator-intent-status", "second", "first"];
        var code = CliCriterionEvidenceIntents.PrintStatus(args, store, output, clock,
            TimeSpan.FromSeconds(1), duration =>
            {
                Assert.Empty(output.ToString());
                delays.Add(duration);
                clock.Advance(duration);
            });
        Assert.Equal(expected, code);
        Assert.Equal(Line("second", second) + Line("first", first), output.ToString());
        var expires = wait && (first is OperatorIntentStatus.Pending or OperatorIntentStatus.Claimed ||
            second is OperatorIntentStatus.Pending or OperatorIntentStatus.Claimed);
        Assert.Equal(start + TimeSpan.FromSeconds(expires ? 2 : 0), clock.GetUtcNow());
        Assert.Equal(expires ? 2 : 0, delays.Count);
    }

    [Fact]
    public void PendingIdsRefreshWhileTerminalIdsAreReadOnce()
    {
        var clock = new ManualClock();
        var store = new ScriptedStore(new Dictionary<string, OperatorIntentRecord?[]>
        {
            ["first"] = [Record("first", OperatorIntentStatus.Applied)],
            ["second"] = [Record("second", OperatorIntentStatus.Pending),
                Record("second", OperatorIntentStatus.Claimed), Record("second", OperatorIntentStatus.Applied)]
        });
        using var output = new StringWriter();
        var code = CliCriterionEvidenceIntents.PrintStatus(["operator-intent-status", "first", "second", "--wait", "3"],
            store, output, clock, TimeSpan.FromSeconds(1), clock.Advance);
        Assert.Equal(0, code);
        Assert.Equal(1, store.Reads["first"]);
        Assert.Equal(3, store.Reads["second"]);
        Assert.Equal(Line("first", OperatorIntentStatus.Applied) + Line("second", OperatorIntentStatus.Applied), output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownSecondIdFailsWholeCallBeforeOutput(bool wait)
    {
        var store = new ScriptedStore(new Dictionary<string, OperatorIntentRecord?[]>
        {
            ["first"] = [Record("first", OperatorIntentStatus.Applied)]
        });
        using var output = new StringWriter();
        string[] args = wait ? ["operator-intent-status", "first", "--wait", "nonsense"]
            : ["operator-intent-status", "first", "nonsense"];
        var error = Assert.Throws<KeyNotFoundException>(() => CliCriterionEvidenceIntents.PrintStatus(args, store,
            output, new ManualClock(), TimeSpan.FromSeconds(1), _ => throw new InvalidOperationException("Must not delay")));
        Assert.Equal("Operator intent 'nonsense' was not found.", error.Message);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void DuplicateIdsPrintOncePerArgument()
    {
        var store = new ScriptedStore(new Dictionary<string, OperatorIntentRecord?[]>
        {
            ["first"] = [Record("first", OperatorIntentStatus.Applied)]
        });
        using var output = new StringWriter();
        Assert.Equal(0, CliCriterionEvidenceIntents.PrintStatus(["operator-intent-status", "first", "first"],
            store, output, new ManualClock(), TimeSpan.FromSeconds(1), _ => throw new InvalidOperationException("Must not delay")));
        Assert.Equal(Line("first", OperatorIntentStatus.Applied) + Line("first", OperatorIntentStatus.Applied), output.ToString());
    }

    private static string Line(string id, OperatorIntentStatus status) =>
        $"Operator intent {id}: verb=retry goal=12345678 task=none status={status} actor=operator channel=cli auth=local outcome=pending{Environment.NewLine}";

    private static OperatorIntentRecord Record(string id, OperatorIntentStatus status) =>
        new(id, id, "retry", "123456789abcdef0", null, "{}", [], "operator", "cli", "local",
            DateTimeOffset.UnixEpoch, Status: status);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class ScriptedStore(Dictionary<string, OperatorIntentRecord?[]> scripts) : IOperatorIntentStore
    {
        public Dictionary<string, int> Reads { get; } = [];
        public Task<OperatorIntentRecord?> GetAsync(string intentId, CancellationToken cancellationToken = default)
        {
            Reads.TryGetValue(intentId, out var count);
            Reads[intentId] = count + 1;
            if (count > 10) throw new InvalidOperationException("No terminal outcome or fake deadline after 10 reads");
            return Task.FromResult(scripts.TryGetValue(intentId, out var records) ? records[Math.Min(count, records.Length - 1)] : null);
        }
        public Task<OperatorIntentRecord> EnqueueAsync(OperatorIntentRecord intent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorIntentRecord?> ClaimNextAsync(string goalId, string claimOwner, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorIntentRecord?> ClaimNextPendingAsync(string goalId, string claimOwner, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorIntentRecord?> ClaimNextByVerbAsync(string goalId, string verb, string claimOwner, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteAsync(string intentId, string claimOwner, OperatorIntentStatus status, string outcome, DateTimeOffset completedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OperatorIntentRecord>> ListForGoalAsync(string goalId, int limit = 20, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListActionableGoalIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, ActionableOperatorIntentSummary>> ListActionableSummariesAsync(IReadOnlyCollection<string> goalIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void AcknowledgeWake(string intentId) => throw new NotSupportedException();
    }
}
