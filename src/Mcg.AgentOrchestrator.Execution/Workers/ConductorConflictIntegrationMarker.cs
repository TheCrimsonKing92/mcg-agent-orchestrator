using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A conductor-owned reference to one pending retry, validated against live git state.
internal static class ConductorConflictIntegrationMarker
{
    private sealed record Marker(string TaskId, DateTimeOffset RetryAt, string OriginalHead, string MainSha);

    internal static void Write(TaskSpec task, string worktree, string originalHead, string mainSha)
    {
        if (task.LatestRetryAt is not { } retryAt)
            throw new InvalidOperationException("Conductor integration requires a recorded retry time.");
        File.WriteAllText(MarkerPath(worktree), JsonSerializer.Serialize(new Marker(task.Id.Value, retryAt, originalHead, mainSha)));
    }

    internal static bool Admits(TaskSpec task, string worktree)
    {
        if (task.RequiredRole != AgentRole.Developer || task.PendingRetryCause != RetryCause.MainDriftConflict)
            return false;
        try
        {
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath(worktree)));
            return marker is not null && marker.TaskId == task.Id.Value && marker.RetryAt == task.LatestRetryAt &&
                Matches(worktree, "HEAD", marker.OriginalHead) && Matches(worktree, "MERGE_HEAD", marker.MainSha);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return false; }
    }

    internal static void Remove(string worktree) => File.Delete(MarkerPath(worktree));

    private static bool Matches(string worktree, string reference, string expected)
    {
        var result = GitCli.Run(worktree, "rev-parse", "--verify", reference);
        return result.Succeeded && !result.DrainTimedOut && result.Output.Trim() == expected;
    }

    private static string MarkerPath(string worktree)
    {
        var result = GitCli.Run(worktree, "rev-parse", "--git-path", "mcg-conductor-integration.json");
        if (!result.Succeeded || result.DrainTimedOut || string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException("Could not locate conductor integration marker: " + result.Error);
        return Path.GetFullPath(result.Output.Trim(), worktree);
    }
}
