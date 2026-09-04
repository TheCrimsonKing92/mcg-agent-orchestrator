using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SqliteStorageMeasurementTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mcg-storage-measurement-" + Guid.NewGuid().ToString("N"));

    [Xunit.Fact(DisplayName = "SqliteStorageMeasurement_reports_main_wal_shm_transient_and_total_separately")]
    public void ReportsEachStorageComponentSeparately()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "fixture.db");
        File.WriteAllBytes(databasePath, new byte[11]);
        File.WriteAllBytes(databasePath + "-wal", new byte[13]);
        File.WriteAllBytes(databasePath + "-shm", new byte[17]);
        File.WriteAllBytes(databasePath + "-journal", new byte[19]);

        var measured = SqliteStorageSnapshot.Measure(databasePath);

        Assert.Equal(11, measured.MainDatabaseBytes);
        Assert.Equal(13, measured.WalBytes);
        Assert.Equal(17, measured.ShmBytes);
        Assert.Equal(19, measured.OtherTransientBytes);
        Assert.Equal(60, measured.TotalBytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
