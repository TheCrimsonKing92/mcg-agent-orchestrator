using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class OrphanWorktreeSweep
{
    internal static GoalWorktreeSweepResult Run(
        string executionDirectory, AgentOrchestratorKernel? kernel, GoalWorktreeCleanupHooks hooks,
        Func<string, string> remove, string? callerRole)
    {
        var root = Path.Combine(Path.GetFullPath(executionDirectory), GoalWorktrees.DirectoryName);
        if (!Directory.Exists(root)) return new(0, []);

        GoalWorktreeRootLock? rootLock;
        try { rootLock = GoalWorktreeRootLock.TryAcquire(root, TimeSpan.Zero); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Warn(root, "orphan-sweep:lock-unavailable", error, hooks);
            rootLock = null;
        }
        using (rootLock)
        {
            var eligibility = new OrphanWorktreeSweepEligibility(kernel);
            var directories = Directory.GetDirectories(root);
            if (rootLock is null)
            {
                foreach (var directory in directories) Emit(directory, "lock-unavailable");
                return new(0, []);
            }

            var listing = GoalWorktreeRegistrationListing.Read(executionDirectory, hooks);
            if (!listing.Succeeded)
            {
                Warn(root, "orphan-sweep:listing-failed", new IOException(listing.FailureReason), hooks);
                foreach (var directory in directories) Emit(directory, "listing-failed");
                return new(0, []);
            }

            var referenceTime = hooks.CleanupUtcNow();
            var removed = 0;
            var leftovers = new List<string>();
            foreach (var directory in directories)
            {
                var reason = "removal-failed";
                try
                {
                    reason = eligibility.Classify(directory, listing.Paths, referenceTime);
                    if (reason == "eligible")
                    {
                        reason = "removal-failed";
                        reason = remove(directory);
                        if (reason == "removed") removed++;
                        else if (Directory.Exists(directory)) leftovers.Add(directory);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    if (Directory.Exists(directory)) leftovers.Add(directory);
                    Warn(directory, "orphan-sweep", error, hooks);
                }
                finally { Emit(directory, reason); }
            }
            return new(removed, leftovers);

            void Emit(string directory, string reason)
            {
                var outcome = new OrphanWorktreeSweepOutcome(directory, eligibility.ResolveGoal(directory), reason,
                    Environment.ProcessId, Environment.ProcessPath, callerRole);
                try { hooks.OrphanSweepOutcomeSink(outcome); }
                catch { OrphanWorktreeSweepOutcome.Write(outcome); }
            }
        }
    }

    private static void Warn(string path, string operation, Exception error, GoalWorktreeCleanupHooks hooks)
    {
        try { hooks.CleanupWarningSink(new(path, operation, error)); }
        catch { GoalWorktrees.DefaultCleanupWarningSink(new(path, operation, error)); }
    }
}
