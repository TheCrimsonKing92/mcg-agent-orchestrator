using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class StartupSqliteRetryTests
{
    [Theory]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(5, true)]
    public void TransientLocksUseExponentialBackoffThenReturn(int code, bool wrapped)
    {
        var calls = 0;
        var clock = new Clock();
        var result = StartupSqliteRetry.Run("state.db", "open", () =>
        {
            calls++;
            if (calls <= 2)
            {
                var error = new SqliteException("database is locked", code);
                throw wrapped ? new InvalidOperationException("setup", error) : error;
            }
            return 42;
        }, clock.Options);
        Assert.Equal(42, result);
        Assert.Equal(3, calls);
        Assert.Equal([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)], clock.Sleeps);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void NonTransientErrorsEscapeImmediately(int code)
    {
        var clock = new Clock();
        var calls = 0;
        var original = new SqliteException("not a lock", code);
        var thrown = Assert.Throws<SqliteException>(() => StartupSqliteRetry.Run("state.db", "open", () =>
        {
            calls++;
            throw original;
        }, clock.Options));
        Assert.Same(original, thrown);
        Assert.Equal(1, calls);
        Assert.Empty(clock.Sleeps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistentLockPreservesIdentityStackAndContext(bool wrapped)
    {
        var clock = new Clock();
        var sqlite = new SqliteException("database is locked", 5);
        Exception original = wrapped ? new InvalidOperationException("setup", sqlite) : sqlite;
        var calls = 0;
        var thrown = Assert.ThrowsAny<Exception>(() => StartupSqliteRetry.Run("state.db", "open", () =>
        {
            calls++;
            ThrowFromStore(original);
        }, clock.Options));
        Assert.Same(original, thrown);
        Assert.Contains(nameof(ThrowFromStore), thrown.StackTrace);
        Assert.Equal(TimeSpan.FromSeconds(30), clock.Elapsed);
        Assert.All(clock.Sleeps, delay => Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        foreach (var error in new[] { thrown, sqlite })
        {
            Assert.Equal("state.db", error.Data["Mcg.DatabasePath"]);
            Assert.Equal("open", error.Data["Mcg.Operation"]);
            Assert.Equal(calls, error.Data["Mcg.AttemptCount"]);
            Assert.Equal(30000d, error.Data["Mcg.ElapsedMilliseconds"]);
        }
    }

    [Fact]
    public async Task AsyncHydrationUsesTheSameBudgetAndHonorsCancellation()
    {
        var clock = new Clock();
        var calls = 0;
        var result = await StartupSqliteRetry.RunAsync("state.db", "hydrate", () =>
            ++calls < 3 ? Task.FromException<int>(new SqliteException("locked", 5)) : Task.FromResult(42), clock.Options,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(42, result);
        Assert.Equal(3, calls);
        Assert.Equal([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)], clock.Sleeps);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupSqliteRetry.RunAsync(
            "state.db", "hydrate", () => { calls++; return Task.FromResult(0); }, cancellationToken: cancellation.Token));
        Assert.Equal(3, calls);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFromStore(Exception error) => throw error;

    private sealed class Clock
    {
        public TimeSpan Elapsed { get; private set; }
        public List<TimeSpan> Sleeps { get; } = [];
        public StartupSqliteRetryOptions Options => new(Sleep, () => Elapsed,
            SleepAsync: (delay, _) => { Sleep(delay); return Task.CompletedTask; });
        private void Sleep(TimeSpan delay) { Sleeps.Add(delay); Elapsed += delay; }
    }
}
