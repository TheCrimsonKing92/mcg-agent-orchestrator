namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    private static bool TryInvalidateCachedStat(MaterializationCandidate candidate, out string failure)
    {
        try
        {
            var lastWriteTime = File.GetLastWriteTimeUtc(candidate.FullPath);
            File.SetLastWriteTimeUtc(candidate.FullPath, lastWriteTime.AddSeconds(2));
            failure = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            failure = ex.Message;
            return false;
        }
    }
}
