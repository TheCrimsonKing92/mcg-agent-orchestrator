using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record StartupSqliteRetryOptions(
    Action<TimeSpan>? Sleep = null,
    Func<TimeSpan>? Elapsed = null,
    Action<string, string>? OnAttempt = null,
    Func<TimeSpan, CancellationToken, Task>? SleepAsync = null);

public static class StartupSqliteRetry
{
    public static T Run<T>(string databasePath, string operation, Func<T> action,
        StartupSqliteRetryOptions? options = null)
    {
        var retry = new RetryBudget(databasePath, operation, options);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                options?.OnAttempt?.Invoke(databasePath, operation);
                return action();
            }
            catch (Exception ex) when (Transient(ex) is not null)
            {
                var delay = retry.NextDelay(ex, attempt);
                if (delay is null) throw;
                (options?.Sleep ?? Thread.Sleep)(delay.Value);
            }
        }
    }

    public static void Run(string databasePath, string operation, Action action,
        StartupSqliteRetryOptions? options = null) =>
        Run(databasePath, operation, () => { action(); return true; }, options);

    public static async Task<T> RunAsync<T>(string databasePath, string operation, Func<Task<T>> action,
        StartupSqliteRetryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var retry = new RetryBudget(databasePath, operation, options);
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                options?.OnAttempt?.Invoke(databasePath, operation);
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (Transient(ex) is not null)
            {
                var delay = retry.NextDelay(ex, attempt);
                if (delay is null) throw;
                await (options?.SleepAsync ?? Task.Delay)(delay.Value, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static SqliteException? Transient(Exception ex)
    {
        var sqlite = ex as SqliteException ?? (ex is InvalidOperationException ? ex.InnerException as SqliteException : null);
        return sqlite is not null && SqliteOrchestratorStateRepository.IsTransientLock(sqlite) ? sqlite : null;
    }

    private sealed class RetryBudget(string databasePath, string operation, StartupSqliteRetryOptions? options)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _delayMilliseconds = 50;

        public TimeSpan? NextDelay(Exception ex, int attempt)
        {
            var elapsed = options?.Elapsed?.Invoke() ?? _clock.Elapsed;
            var remaining = TimeSpan.FromMilliseconds(StateDbConnectionFactory.DefaultBusyTimeoutMilliseconds) - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                Stamp(ex);
                if (Transient(ex) is { } inner && !ReferenceEquals(ex, inner)) Stamp(inner);
                return null;
            }
            var delay = TimeSpan.FromMilliseconds(Math.Min(_delayMilliseconds, remaining.TotalMilliseconds));
            _delayMilliseconds = Math.Min(_delayMilliseconds * 2, 1000);
            return delay;

            void Stamp(Exception error)
            {
                error.Data["Mcg.DatabasePath"] = databasePath;
                error.Data["Mcg.Operation"] = operation;
                error.Data["Mcg.AttemptCount"] = attempt;
                error.Data["Mcg.ElapsedMilliseconds"] = elapsed.TotalMilliseconds;
            }
        }
    }
}
