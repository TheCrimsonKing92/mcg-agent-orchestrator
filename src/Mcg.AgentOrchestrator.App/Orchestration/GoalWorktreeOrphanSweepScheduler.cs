using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalWorktreeOrphanSweepScheduler
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTimeOffset> LastSweepByDirectory = new(StringComparer.OrdinalIgnoreCase);

    public static GoalWorktreeCleanupOptions Options { get; private set; } = GoalWorktreeCleanupOptions.Default;

    public static void Configure(GoalWorktreeCleanupOptions options, string? attentionStoreDirectory = null)
    {
        Options = options.Validate();
        GoalWorktrees.ConfigureCleanup(Options, attentionStoreDirectory);
    }

    public static GoalWorktreeSweepResult SweepNow(
        string executionDirectory,
        AgentOrchestratorKernel? kernel = null,
        GoalWorktreeCleanupHooks? hooks = null)
    {
        var result = GoalWorktrees.SweepOrphanedWorktrees(executionDirectory, kernel, hooks);
        lock (Gate)
        {
            LastSweepByDirectory[Normalize(executionDirectory)] = DateTimeOffset.UtcNow;
        }

        return result;
    }

    public static GoalWorktreeSweepResult SweepIfDue(
        string executionDirectory,
        AgentOrchestratorKernel? kernel = null,
        GoalWorktreeCleanupHooks? hooks = null)
    {
        lock (Gate)
        {
            if (LastSweepByDirectory.TryGetValue(Normalize(executionDirectory), out var lastSweep) &&
                DateTimeOffset.UtcNow - lastSweep < Options.SweepInterval)
            {
                return new GoalWorktreeSweepResult(0, []);
            }
        }

        return SweepNow(executionDirectory, kernel, hooks);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
