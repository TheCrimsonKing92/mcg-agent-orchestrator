using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class GoalWorktreeOrphanSweepScheduler
{
    private readonly GoalWorktreeCleanupHooks hooks;
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> lastSweepByDirectory = new(StringComparer.OrdinalIgnoreCase);

    public GoalWorktreeOrphanSweepScheduler(GoalWorktreeCleanupHooks hooks)
    {
        this.hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        Options = hooks.CleanupOptions().Validate();
    }

    public GoalWorktreeCleanupOptions Options { get; }

    public GoalWorktreeSweepResult SweepNow(
        string executionDirectory,
        AgentOrchestratorKernel? kernel = null,
        string? callerRole = null)
    {
        var result = GoalWorktrees.SweepOrphanedWorktrees(executionDirectory, kernel, hooks, callerRole);
        lock (gate)
        {
            lastSweepByDirectory[Normalize(executionDirectory)] = hooks.CleanupUtcNow();
        }

        return result;
    }

    public GoalWorktreeSweepResult SweepIfDue(
        string executionDirectory,
        AgentOrchestratorKernel? kernel = null,
        string? callerRole = null)
    {
        lock (gate)
        {
            if (lastSweepByDirectory.TryGetValue(Normalize(executionDirectory), out var lastSweep) &&
                hooks.CleanupUtcNow() - lastSweep < Options.SweepInterval)
            {
                return new GoalWorktreeSweepResult(0, []);
            }
        }

        return SweepNow(executionDirectory, kernel, callerRole);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
