using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed partial class GateShardPermitPool
{
    internal const int DefaultBudget = 7;
    internal const string GateShardBudgetVariable = "MCG_GATE_SHARD_BUDGET";
    private readonly string _directory;

    private GateShardPermitPool(string rootPath, int budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        Budget = budget;
        _directory = Path.Combine(rootPath, "gate-shard-permits");
        Directory.CreateDirectory(_directory);
    }

    internal int Budget { get; }

    internal static int ResolveBudget(string? raw) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var budget) && budget > 0
            ? budget
            : DefaultBudget;

    internal static GateShardPermitPool ForRoot(string rootPath, int budget) => new(rootPath, budget);

    internal bool TryAcquire(out GateShardPermit? permit)
    {
        for (var index = 0; index < Budget; index++)
        {
            var path = Path.Combine(_directory, $"permit-{index:D2}.lock");
            try
            {
                permit = new GateShardPermit(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                return true;
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                // Another gate process owns this slot.
            }
        }

        permit = null;
        return false;
    }

    internal async Task<GateShardPermit> AcquireAsync(Action onWaiting, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryAcquire(out var permit))
                return permit!;
            onWaiting();
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;
}

internal sealed class GateShardPermit(FileStream stream) : IDisposable
{
    private FileStream? _stream = stream;

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
