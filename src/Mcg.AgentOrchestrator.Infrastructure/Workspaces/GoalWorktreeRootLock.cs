using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Synchronous and thread-affine: creation and sweeping hold this for their entire operation.
// The identity belongs to the worktree root, not a goal, process, or scheduler instance.
internal sealed class GoalWorktreeRootLock : IDisposable
{
    private readonly Mutex mutex;

    private GoalWorktreeRootLock(Mutex mutex) => this.mutex = mutex;

    internal static string NameFor(string worktreesRoot)
    {
        var identity = GoalWorktreeLayout.NormalizePath(worktreesRoot);
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        var prefix = OperatingSystem.IsWindows() ? @"Global\mcg-worktree-root-" : "mcg-worktree-root-";
        return prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    internal static GoalWorktreeRootLock? TryAcquire(string worktreesRoot, TimeSpan timeout)
    {
        var mutex = new Mutex(false, NameFor(worktreesRoot));
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return new GoalWorktreeRootLock(mutex);
            mutex.Dispose();
            return null;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { mutex.ReleaseMutex(); }
        finally { mutex.Dispose(); }
    }
}
