using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

internal static class StartupFailureReport
{
    internal static bool HasSqliteException(Exception exception) => Chain(exception).Any(error => error is SqliteException);

    public static string Format(Exception exception)
    {
        var errors = Chain(exception).ToArray();
        var firstLine = $"{exception.GetType().Name}: {StoreSchemaDiagnostics.Message(exception)}";
        var sqlite = errors.OfType<SqliteException>().FirstOrDefault();
        if (sqlite is null) return firstLine;
        object Value(string key) => errors.FirstOrDefault(error => error.Data.Contains(key))?.Data[key] ?? "unknown";
        return string.Join(Environment.NewLine, firstLine,
            $"database={Value("Mcg.DatabasePath")}", $"operation={Value("Mcg.Operation")}",
            $"attempts={Value("Mcg.AttemptCount")}", $"elapsed_ms={Value("Mcg.ElapsedMilliseconds")}",
            $"sqlite_code={sqlite.SqliteErrorCode}", "stack:", exception.ToString());
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            yield return current;
    }
}
