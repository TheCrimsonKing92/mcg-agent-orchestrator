using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>
/// Owns cleanup policy, hooks, and sweep cadence for one host operation.
/// The context is immutable after construction so concurrent hosts cannot observe each other's
/// configuration or test seams.
/// </summary>
internal sealed class WorktreeCleanupContext
{
    public WorktreeCleanupContext(
        GoalWorktreeCleanupOptions options,
        string? attentionStoreDirectory = null)
    {
        Hooks = GoalWorktreeCleanupHooks.ForConfiguration(options, attentionStoreDirectory);
        Scheduler = new GoalWorktreeOrphanSweepScheduler(Hooks);
    }

    public GoalWorktreeCleanupHooks Hooks { get; }

    public GoalWorktreeOrphanSweepScheduler Scheduler { get; }

    public static WorktreeCleanupContext Load(
        string? configurationBaseDirectory = null,
        string? attentionStoreDirectory = null) =>
        new(
            WorktreeCleanupConfiguration.Load(configurationBaseDirectory ?? AppContext.BaseDirectory),
            attentionStoreDirectory);
}
