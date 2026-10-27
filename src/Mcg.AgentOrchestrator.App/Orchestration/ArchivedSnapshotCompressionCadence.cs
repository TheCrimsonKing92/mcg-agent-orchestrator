using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ArchivedSnapshotCompressionCadence
{
    internal const long BatchSourceBytes = 67_108_864;
    internal const int MaxBatchesPerRun = 32;
    internal const string EnabledVariable = "MCG_ARCHIVED_SNAPSHOT_COMPRESSION";
    internal static readonly ArchivedSnapshotCompressionCadenceResult Disabled = new(0, 0, 0, 0, "disabled", null);

    internal static bool IsEnabledFromEnvironment() =>
        string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal);

    internal static ArchivedSnapshotCompressionCadenceResult Run(Func<long, GoalSnapshotArchiveRecompressionResult> batch)
    {
        var rows = 0;
        long sourceBytes = 0;
        long storedBytes = 0;
        var batches = 0;
        try
        {
            while (batches < MaxBatchesPerRun)
            {
                batches++;
                var result = batch(BatchSourceBytes);
                rows += result.Rows;
                sourceBytes += result.SourceBytes;
                storedBytes += result.StoredBytes;
                if (result.Rows == 0)
                    break;
            }
            return new(rows, sourceBytes, storedBytes, batches, "completed", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(rows, sourceBytes, storedBytes, batches, "failed", ex.GetType().Name);
        }
    }

    internal static string Format(ArchivedSnapshotCompressionCadenceResult result)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"ARCHIVED_SNAPSHOT_COMPRESSION status={result.Status} rows={result.Rows} sourceBytes={result.SourceBytes} storedBytes={result.StoredBytes} batches={result.Batches}");
        return result.Status == "failed" ? $"{line} exception={result.ExceptionType}" : line;
    }
}
