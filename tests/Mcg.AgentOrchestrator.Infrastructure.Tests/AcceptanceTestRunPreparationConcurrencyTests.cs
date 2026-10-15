using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: every test owns a unique directory and invocation-local publication seams.
public sealed class AcceptanceTestRunPreparationConcurrencyTests
{
    [Fact]
    public async Task Prepare_ConcurrentSharedDirectory_PublishesCurrentOwnerWithoutDebris()
    {
        using var directory = new TestDirectory();
        using var barrier = new Barrier(16);
        var moves = 0;
        var operations = new AcceptanceTestRunPreparation.PublicationOperations((source, destination) =>
        {
            Interlocked.Increment(ref moves);
            File.Move(source, destination, overwrite: true);
        }, _ => throw new InvalidOperationException("Same-process publication must not need a retry."));
        var calls = Enumerable.Range(0, 16).Select(index => Task.Factory.StartNew(() =>
            Record.Exception(() =>
            {
                if (!barrier.SignalAndWait(TimeSpan.FromMinutes(1)))
                    throw new TimeoutException("The 16 preparation callers did not reach the barrier.");
                AcceptanceTestRunPreparation.Prepare([directory.Receipt($"case-{index}.trx")], operations);
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var failures = await Task.WhenAll(calls);

        Assert.All(failures, failure => Assert.Null(failure));
        Assert.Equal(1, moves);
        AssertCurrentOwner(directory);
        Assert.Equal(new[] { directory.Sidecar }, Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void Prepare_DifferentOwner_ReplacesSidecar()
    {
        using var directory = new TestDirectory();
        WriteOwner(directory, -1, CurrentOwnerStartedAt());

        AcceptanceTestRunPreparation.Prepare([directory.Receipt()]);

        AssertCurrentOwner(directory);
        AssertNoTemporaryFiles(directory);
    }

    [Fact]
    public void Prepare_ReusedProcessId_ReplacesStaleStartTime()
    {
        using var directory = new TestDirectory();
        WriteOwner(directory, Environment.ProcessId, CurrentOwnerStartedAt().AddDays(-1));

        AcceptanceTestRunPreparation.Prepare([directory.Receipt()]);

        AssertCurrentOwner(directory);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"ownerProcessId\":\"invalid\",\"ownerProcessStartedAt\":0}")]
    public void Prepare_InvalidOwnership_ReplacesSidecar(string contents)
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.Sidecar, contents);

        AcceptanceTestRunPreparation.Prepare([directory.Receipt()]);

        AssertCurrentOwner(directory);
        AssertNoTemporaryFiles(directory);
    }

    [Fact]
    public void Prepare_CurrentOwner_PreservesBytesAndClearsStaleReceipt()
    {
        using var directory = new TestDirectory();
        WriteOwner(directory, Environment.ProcessId, CurrentOwnerStartedAt());
        var original = File.ReadAllBytes(directory.Sidecar);
        var receipt = directory.Receipt();
        File.WriteAllText(receipt, "stale receipt");
        var operations = new AcceptanceTestRunPreparation.PublicationOperations(
            (_, _) => throw new InvalidOperationException("Same-owner publication must skip the move."),
            _ => throw new InvalidOperationException("Same-owner publication must skip the delay."));

        AcceptanceTestRunPreparation.Prepare([receipt], operations);

        Assert.Equal(original, File.ReadAllBytes(directory.Sidecar));
        Assert.False(File.Exists(receipt));
        AssertNoTemporaryFiles(directory);
    }

    [Fact]
    public void Prepare_CurrentOwnerInFirstDirectory_StillPublishesOtherDirectory()
    {
        using var first = new TestDirectory();
        using var second = new TestDirectory();
        WriteOwner(first, Environment.ProcessId, CurrentOwnerStartedAt());
        var original = File.ReadAllBytes(first.Sidecar);

        AcceptanceTestRunPreparation.Prepare([first.Receipt(), second.Receipt()]);

        Assert.Equal(original, File.ReadAllBytes(first.Sidecar));
        AssertCurrentOwner(second);
        AssertNoTemporaryFiles(first);
        AssertNoTemporaryFiles(second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prepare_TwoTransientMoveFailures_SucceedsOnThirdAttempt(bool ioFailure)
    {
        using var directory = new TestDirectory();
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var operations = new AcceptanceTestRunPreparation.PublicationOperations((source, destination) =>
        {
            attempts++;
            if (attempts <= 2)
                throw MoveFailure(ioFailure);
            File.Move(source, destination, overwrite: true);
        }, delays.Add);

        AcceptanceTestRunPreparation.Prepare([directory.Receipt()], operations);

        Assert.Equal(3, attempts);
        Assert.Equal(new[] { 10, 20 }.Select(value => TimeSpan.FromMilliseconds(value)), delays);
        AssertCurrentOwner(directory);
        AssertNoTemporaryFiles(directory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prepare_PersistentMoveFailure_ThrowsFifthFailureAndCleansUp(bool ioFailure)
    {
        using var directory = new TestDirectory();
        var attempts = 0;
        var delays = new List<TimeSpan>();
        Exception? lastFailure = null;
        var operations = new AcceptanceTestRunPreparation.PublicationOperations((_, _) =>
        {
            attempts++;
            lastFailure = MoveFailure(ioFailure);
            throw lastFailure;
        }, delays.Add);

        var failure = Record.Exception(() =>
            AcceptanceTestRunPreparation.Prepare([directory.Receipt()], operations));

        Assert.Equal(5, attempts);
        Assert.Same(lastFailure, failure);
        Assert.Equal(new[] { 10, 20, 40, 80 }.Select(value => TimeSpan.FromMilliseconds(value)), delays);
        Assert.False(File.Exists(directory.Sidecar));
        AssertNoTemporaryFiles(directory);
    }

    [Fact]
    public void Prepare_NonRetryableMoveFailure_ThrowsImmediatelyAndCleansUp()
    {
        using var directory = new TestDirectory();
        var expected = new InvalidOperationException("non-retryable");
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var operations = new AcceptanceTestRunPreparation.PublicationOperations((_, _) =>
        {
            attempts++;
            throw expected;
        }, delays.Add);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            AcceptanceTestRunPreparation.Prepare([directory.Receipt()], operations));

        Assert.Same(expected, failure);
        Assert.Equal(1, attempts);
        Assert.Empty(delays);
        AssertNoTemporaryFiles(directory);
    }

    private static Exception MoveFailure(bool ioFailure) => ioFailure
        ? new IOException("injected move failure")
        : new UnauthorizedAccessException("injected move failure");

    private static DateTime CurrentOwnerStartedAt()
    {
        using var owner = Process.GetCurrentProcess();
        return owner.StartTime.ToUniversalTime();
    }

    private static void WriteOwner(TestDirectory directory, int processId, DateTime startedAt)
        => File.WriteAllText(directory.Sidecar, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, machineName = Environment.MachineName,
            ownerProcessId = processId, ownerProcessStartedAt = startedAt.ToString("o"),
            createdAt = DateTimeOffset.UnixEpoch, runLabel = "sentinel-existing-owner"
        }));

    private static void AssertCurrentOwner(TestDirectory directory)
    {
        Assert.True(File.Exists(directory.Sidecar));
        using var document = JsonDocument.Parse(File.ReadAllText(directory.Sidecar));
        var root = document.RootElement;
        Assert.Equal(Environment.ProcessId, root.GetProperty("ownerProcessId").GetInt32());
        Assert.Equal(new DateTimeOffset(CurrentOwnerStartedAt()),
            root.GetProperty("ownerProcessStartedAt").GetDateTimeOffset());
    }

    private static void AssertNoTemporaryFiles(TestDirectory directory)
        => Assert.Empty(Directory.GetFiles(directory.Path, ".mtp-run-ownership.json.tmp-*"));

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "acceptance-ownership-" + Guid.NewGuid().ToString("N"));
        public string Sidecar => System.IO.Path.Combine(Path, ".mtp-run-ownership.json");
        public string Receipt(string name = "case.trx") => System.IO.Path.Combine(Path, name);

        public TestDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
