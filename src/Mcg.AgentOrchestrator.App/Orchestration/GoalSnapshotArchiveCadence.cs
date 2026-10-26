using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class GoalSnapshotArchiveCadence
{
    internal static readonly TimeSpan RetentionAge = TimeSpan.FromDays(2);

    internal static GoalSnapshotArchiveCadenceResult Run(Func<DateTimeOffset, int> archive, DateTimeOffset now)
    {
        var cutoff = now - RetentionAge;
        try
        {
            return new(cutoff, archive(cutoff), "completed", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(cutoff, 0, "failed", ex.GetType().Name);
        }
    }

    internal static string Format(GoalSnapshotArchiveCadenceResult result)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"GOAL_SNAPSHOT_ARCHIVE status={result.Status} archived={result.Archived} cutoff={result.Cutoff:O} ageDays={(int)RetentionAge.TotalDays}");
        return result.Status == "failed" ? $"{line} exception={result.ExceptionType}" : line;
    }
}
