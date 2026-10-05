namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class SqliteMaintenanceLease : IDisposable
{
    private readonly FileStream _stream;

    private SqliteMaintenanceLease(string path, bool exclusive, FileStream stream)
    {
        Path = path;
        IsExclusive = exclusive;
        _stream = stream;
    }

    public string Path { get; }

    public bool IsExclusive { get; }

    public static string ForDatabase(string databasePath) => System.IO.Path.GetFullPath(databasePath) + ".maintenance.lease";

    public static SqliteMaintenanceLease AcquireSharedOrThrow(string leasePath)
    {
        if (TryAcquire(leasePath, exclusive: false, out var lease))
            return lease;

        throw new InvalidOperationException(
            $"Cannot start active SQLite work while exclusive maintenance holds '{System.IO.Path.GetFullPath(leasePath)}'.");
    }

    public static bool TryAcquireShared(string leasePath, out SqliteMaintenanceLease? lease) =>
        TryAcquire(leasePath, exclusive: false, out lease);

    public static bool TryAcquireExclusive(string leasePath, out SqliteMaintenanceLease? lease) =>
        TryAcquire(leasePath, exclusive: true, out lease);

    private static bool TryAcquire(string leasePath, bool exclusive, out SqliteMaintenanceLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leasePath);
        var fullPath = System.IO.Path.GetFullPath(leasePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
        try
        {
            var stream = new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                exclusive ? FileShare.None : FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.None);
            lease = new SqliteMaintenanceLease(fullPath, exclusive, stream);
            return true;
        }
        catch (IOException)
        {
            lease = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            lease = null;
            return false;
        }
    }

    public void Dispose() => _stream.Dispose();
}
