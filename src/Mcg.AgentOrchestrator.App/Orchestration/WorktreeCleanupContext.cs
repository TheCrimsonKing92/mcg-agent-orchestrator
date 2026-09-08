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
        string? attentionStoreDirectory = null,
        DotnetBuildStorageRoot? buildStorageRoot = null)
        : this(GoalWorktreeCleanupHooks.ForConfiguration(options, attentionStoreDirectory, buildStorageRoot))
    {
    }

    internal WorktreeCleanupContext(GoalWorktreeCleanupHooks hooks)
    {
        Hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        Scheduler = new GoalWorktreeOrphanSweepScheduler(Hooks);
    }

    public GoalWorktreeCleanupHooks Hooks { get; }

    public GoalWorktreeOrphanSweepScheduler Scheduler { get; }

    public static WorktreeCleanupContext Load(
        string? configurationBaseDirectory = null,
        string? attentionStoreDirectory = null,
        DotnetBuildStorageRoot? buildStorageRoot = null) =>
        new(
            WorktreeCleanupConfiguration.Load(configurationBaseDirectory ?? AppContext.BaseDirectory),
            attentionStoreDirectory,
            buildStorageRoot);
}
