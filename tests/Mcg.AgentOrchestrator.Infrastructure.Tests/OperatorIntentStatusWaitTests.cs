using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its store, writer and manual clock; no process or shared state.
public sealed class OperatorIntentStatusWaitTests
{
    private const string IntentId = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Wait_ReachesApplied_PrintsOnceAndReturnsZero()
    {
        var store = new ScriptedStore(Record(OperatorIntentStatus.Pending),
            Record(OperatorIntentStatus.Claimed), Record(OperatorIntentStatus.Applied));
        var clock = new ManualClock();
        using var output = new StringWriter();
        var delays = new List<TimeSpan>();

        var code = Run(["operator-intent-status", IntentId, "--wait", "5"], store, output, clock,
            interval => { Assert.Equal(string.Empty, output.ToString()); delays.Add(interval); clock.Advance(interval); });

        Assert.Equal(0, code);
        Assert.Equal(ExpectedLine("Applied", "pending"), output.ToString());
        Assert.Equal(3, store.Reads);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1) }, delays);
    }

    [Fact]
    public void Wait_ReachesRejected_PrintsOutcomeAndReturnsTwo()
    {
        var store = new ScriptedStore(Record(OperatorIntentStatus.Pending),
            Record(OperatorIntentStatus.Rejected) with { Outcome = "Task is already running" });
        var clock = new ManualClock();
        using var output = new StringWriter();

        var code = Run(["operator-intent-status", IntentId, "--wait"], store, output, clock,
            interval => { Assert.Equal(string.Empty, output.ToString()); clock.Advance(interval); });

        Assert.Equal(2, code);
        Assert.Equal(ExpectedLine("Rejected", "Task is already running"), output.ToString());
        Assert.Equal(2, store.Reads);
    }

    [Theory]
    [InlineData(OperatorIntentStatus.Pending)]
    [InlineData(OperatorIntentStatus.Claimed)]
    public void Wait_DeadlinePassed_PrintsLastStatusAndReturnsThree(OperatorIntentStatus status)
    {
        var store = new ScriptedStore(Record(status));
        var clock = new ManualClock();
        using var output = new StringWriter();

        var code = Run(["operator-intent-status", IntentId, "--wait", "5"], store, output, clock,
            interval => { Assert.Equal(string.Empty, output.ToString()); clock.Advance(TimeSpan.FromSeconds(6)); });

        Assert.Equal(3, code);
        Assert.Equal(ExpectedLine(status.ToString(), "pending"), output.ToString());
        Assert.Equal(Start + TimeSpan.FromSeconds(6), clock.GetUtcNow());
        Assert.Equal(2, store.Reads);
    }

    [Theory]
    [InlineData(OperatorIntentStatus.Pending)]
    [InlineData(OperatorIntentStatus.Claimed)]
    [InlineData(OperatorIntentStatus.Applied)]
    [InlineData(OperatorIntentStatus.Rejected)]
    public void WithoutWait_ReadsOnceAndPreservesOutputAndExitCode(OperatorIntentStatus status)
    {
        var store = new ScriptedStore(Record(status) with { Outcome = "recorded outcome" });
        using var output = new StringWriter();

        var code = Run(["operator-intent-status", IntentId], store, output, new UnusedClock(),
            _ => throw new InvalidOperationException("No-option command must not delay."));

        Assert.Equal(0, code);
        Assert.Equal(1, store.Reads);
        Assert.Equal(ExpectedLine(status.ToString(), "recorded outcome"), output.ToString());
    }

    [Theory]
    [InlineData(OperatorIntentStatus.Applied, 0)]
    [InlineData(OperatorIntentStatus.Rejected, 2)]
    public void Wait_AlreadyTerminal_DoesNotDelay(OperatorIntentStatus status, int expectedCode)
    {
        var store = new ScriptedStore(Record(status));
        using var output = new StringWriter();
        Assert.Equal(expectedCode, Run(["operator-intent-status", IntentId, "--wait"], store, output,
            new ManualClock(), _ => throw new InvalidOperationException("Terminal intent must not delay.")));
        Assert.Equal(1, store.Reads);
        Assert.Equal(ExpectedLine(status.ToString(), "pending"), output.ToString());
    }

    [Theory]
    [InlineData(false, "--wait", null, 300)]
    [InlineData(true, "--wait", null, 300)]
    [InlineData(false, "--wait=60", null, 60)]
    [InlineData(true, "--WAIT", "5", 5)]
    public void Wait_FormsUseRequestedDeadline(bool beforeId, string flag, string? value, int seconds)
    {
        var args = new List<string> { "operator-intent-status" };
        if (!beforeId) args.Add(IntentId);
        args.Add(flag);
        if (value is not null) args.Add(value);
        if (beforeId) args.Add(IntentId);
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var clock = new ManualClock();
        var store = new ScriptedStore(Record(OperatorIntentStatus.Pending));
        using var output = new StringWriter();
        var delays = new List<TimeSpan>();

        var code = CliCriterionEvidenceIntents.PrintStatus(args, store, output, clock,
            TimeSpan.FromSeconds(400), interval => { delays.Add(interval); clock.Advance(interval); });

        Assert.Equal(3, code);
        Assert.Equal(new[] { TimeSpan.FromSeconds(seconds) }, delays);
        Assert.Equal(Start + TimeSpan.FromSeconds(seconds), clock.GetUtcNow());
        Assert.Equal(2, store.Reads);
        Assert.Equal(ExpectedLine("Pending", "pending"), output.ToString());
    }

    [Theory]
    [InlineData("--wait", "0")]
    [InlineData("--wait", "-1")]
    [InlineData("--wait", "nonsense")]
    [InlineData("--wait", "2147483648")]
    [InlineData("--wait", "--wait")]
    [InlineData("--wait=", null)]
    [InlineData("--wait=bad", null)]
    [InlineData("--wait=-1", null)]
    [InlineData("--unknown", null)]
    public void InvalidArguments_ThrowUsageBeforeReading(string flag, string? value)
    {
        var args = new List<string> { "operator-intent-status", IntentId, flag };
        if (value is not null) args.Add(value);
        var store = new ScriptedStore(Record(OperatorIntentStatus.Pending));
        using var output = new StringWriter();
        var error = Assert.Throws<ArgumentException>(() => Run(args, store, output, new UnusedClock(),
            _ => throw new InvalidOperationException("Invalid arguments must not delay.")));
        Assert.Equal(CliCommandHelp.OperatorIntentStatusUsage, error.Message);
        Assert.Equal(0, store.Reads);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void Wait_MissingIntent_FailsFastWithoutOutput()
    {
        var store = new ScriptedStore((OperatorIntentRecord?)null);
        using var output = new StringWriter();
        var error = Assert.Throws<KeyNotFoundException>(() => Run(
            ["operator-intent-status", IntentId, "--wait"], store, output, new ManualClock(),
            _ => throw new InvalidOperationException("Missing intent must not delay.")));
        Assert.Equal($"Operator intent '{IntentId}' was not found.", error.Message);
        Assert.Equal(1, store.Reads);
        Assert.Equal(string.Empty, output.ToString());
    }

    private static int Run(IReadOnlyList<string> args, ScriptedStore store, TextWriter output,
        TimeProvider clock, Action<TimeSpan> delay) =>
        CliCriterionEvidenceIntents.PrintStatus(args, store, output, clock, TimeSpan.FromSeconds(1), delay);

    private static string ExpectedLine(string status, string outcome) =>
        $"Operator intent {IntentId}: verb=retry goal=12345678 task=abcdef01 " +
        $"status={status} actor=operator channel=cli auth=local outcome={outcome}{Environment.NewLine}";

    private static OperatorIntentRecord Record(OperatorIntentStatus status) => new(
        IntentId, IntentId, "retry", "123456789abcdef0", "abcdef0123456789", "{}", [],
        "operator", "cli", "local", Start, Status: status);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = Start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }

    private sealed class UnusedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            throw new InvalidOperationException("This command path must not read the clock.");
    }

    private sealed class ScriptedStore(params OperatorIntentRecord?[] records) : IOperatorIntentStore
    {
        public int Reads { get; private set; }
        public Task<OperatorIntentRecord?> GetAsync(string intentId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(IntentId, intentId);
            if (++Reads > 10)
                throw new InvalidOperationException("Wait loop never observed a terminal status or deadline after 10 reads.");
            return Task.FromResult(records[Math.Min(Reads - 1, records.Length - 1)]);
        }

        public Task<OperatorIntentRecord> EnqueueAsync(OperatorIntentRecord intent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorIntentRecord?> ClaimNextAsync(string goalId, string claimOwner, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorIntentRecord?> ClaimNextByVerbAsync(string goalId, string verb, string claimOwner, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteAsync(string intentId, string claimOwner, OperatorIntentStatus status, string outcome, DateTimeOffset completedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OperatorIntentRecord>> ListForGoalAsync(string goalId, int limit = 20, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListActionableGoalIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, ActionableOperatorIntentSummary>> ListActionableSummariesAsync(IReadOnlyCollection<string> goalIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void AcknowledgeWake(string intentId) => throw new NotSupportedException();
    }
}
