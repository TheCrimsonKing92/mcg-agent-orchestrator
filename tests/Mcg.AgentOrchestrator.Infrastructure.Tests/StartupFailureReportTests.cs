using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class StartupFailureReportTests
{
    [Fact]
    public void ExhaustedLockReportContainsStoreContextAndOriginalStack()
    {
        var elapsed = TimeSpan.Zero;
        var error = Assert.Throws<SqliteException>(() => StartupSqliteRetry.Run("portfolio.db", "store-plan",
            ThrowFromStore, new(delay => elapsed += delay, () => elapsed)));
        var report = ProgramStartupErrorFormatter.Format(error);
        Assert.StartsWith("SqliteException: SQLite Error 5: 'database is locked'." + Environment.NewLine, report);
        Assert.Contains("database=portfolio.db", report);
        Assert.Contains("operation=store-plan", report);
        Assert.Contains($"attempts={error.Data["Mcg.AttemptCount"]}", report);
        Assert.Contains("elapsed_ms=30000", report);
        Assert.Contains("sqlite_code=5", report);
        Assert.Contains("stack:" + Environment.NewLine, report);
        Assert.Contains(nameof(ThrowFromStore), report);
    }

    [Fact]
    public void WrappedSqliteReportReadsInnerMetadataAndDefaultsMissingFields()
    {
        var sqlite = new SqliteException("locked", 6);
        sqlite.Data["Mcg.DatabasePath"] = "inner.db";
        var report = ProgramStartupErrorFormatter.Format(new InvalidOperationException("outer wrapper", sqlite));
        Assert.StartsWith("InvalidOperationException: outer wrapper" + Environment.NewLine, report);
        Assert.Contains("database=inner.db", report);
        Assert.Contains("operation=unknown", report);
        Assert.Contains("attempts=unknown", report);
        Assert.Contains("elapsed_ms=unknown", report);
        Assert.Contains("sqlite_code=6", report);
    }

    [Fact]
    public void NonSqliteFormattingRemainsByteIdentical() =>
        Assert.Equal("InvalidOperationException: outer wrapper",
            ProgramStartupErrorFormatter.Format(new InvalidOperationException("outer wrapper")));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFromStore() => throw new SqliteException("SQLite Error 5: 'database is locked'.", 5);
}
