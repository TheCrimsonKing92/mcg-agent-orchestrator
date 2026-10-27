using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record TransientSqliteLoadHold(
    int SqliteErrorCode,
    int SqliteExtendedErrorCode,
    string AttemptCount,
    string ElapsedMilliseconds,
    string Operation)
{
    internal static TransientSqliteLoadHold From(SqliteException exception, string operation) => new(
        exception.SqliteErrorCode,
        exception.SqliteExtendedErrorCode,
        exception.Data["Mcg.AttemptCount"]?.ToString() ?? "unknown",
        exception.Data["Mcg.ElapsedMilliseconds"]?.ToString() ?? "unknown",
        operation);
}
