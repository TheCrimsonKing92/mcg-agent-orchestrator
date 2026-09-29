using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeDeleteRetryWaitTests
{
    [Xunit.Fact]
    public void SuppliedWaitSeesEachFailedAttemptAndStopsAfterSuccess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = NewDirectory();
        using var held = new FileStream(Path.Combine(path, "held.log"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var waits = new List<(string Path, int Attempt, TimeSpan Delay)>();
            var result = GoalWorktrees.DeleteDirectoryWithReason(path, (p, attempt, delay) =>
            {
                waits.Add((p, attempt, delay));
                if (attempt == 3) held.Dispose();
            });

            Xunit.Assert.True(result.Succeeded);
            Xunit.Assert.Equal(new[]
            {
                (path, 1, TimeSpan.FromMilliseconds(100)),
                (path, 2, TimeSpan.FromMilliseconds(200)),
                (path, 3, TimeSpan.FromMilliseconds(400))
            }, waits);
            Xunit.Assert.False(Directory.Exists(path));
        }
        finally
        {
            held.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Xunit.Fact]
    public void SuppliedWaitIsNotCalledAfterFinalFailedAttempt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = NewDirectory();
        using var held = new FileStream(Path.Combine(path, "held.log"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var waits = new List<(string Path, int Attempt, TimeSpan Delay)>();
            var result = GoalWorktrees.DeleteDirectoryWithReason(path,
                (p, attempt, delay) => waits.Add((p, attempt, delay)));

            Xunit.Assert.False(result.Succeeded);
            Xunit.Assert.Equal(new[]
            {
                (path, 1, TimeSpan.FromMilliseconds(100)),
                (path, 2, TimeSpan.FromMilliseconds(200)),
                (path, 3, TimeSpan.FromMilliseconds(400)),
                (path, 4, TimeSpan.FromMilliseconds(800)),
                (path, 5, TimeSpan.FromMilliseconds(1600))
            }, waits);
        }
        finally
        {
            held.Dispose();
            Directory.Delete(path, recursive: true);
        }
    }

    [Xunit.Fact]
    public void HooksAndBuilderPassOptionalWaitThroughDefaultCleanupDelete()
    {
        Xunit.Assert.Null(new GoalWorktreeCleanupHooks().DeleteRetryWait);
        Xunit.Assert.Null(GoalWorktreeCleanupHooks.ForConfiguration(GoalWorktreeCleanupOptions.Default).DeleteRetryWait);
        if (!OperatingSystem.IsWindows()) return;

        foreach (var build in new Func<Action<string, int, TimeSpan>, GoalWorktreeCleanupHooks>[]
        {
            wait => new GoalWorktreeCleanupHooks { DeleteRetryWait = wait },
            wait => new GoalWorktreeCleanupHooksBuilder { DeleteRetryWait = wait }.Build()
        })
        {
            var path = NewDirectory();
            using var held = new FileStream(Path.Combine(path, "held.log"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var waits = new List<(string Path, int Attempt, TimeSpan Delay)>();
                var hooks = build((p, attempt, delay) =>
                {
                    waits.Add((p, attempt, delay));
                    held.Dispose();
                });
                Xunit.Assert.True(hooks.DeleteDirectoryForCleanup(path).Succeeded);
                Xunit.Assert.Equal((path, 1, TimeSpan.FromMilliseconds(100)), Xunit.Assert.Single(waits));
            }
            finally
            {
                held.Dispose();
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }
    }

    [Xunit.Fact]
    public void NoWaitSuppliedStillDeletesUnlockedDirectory()
    {
        var path = NewDirectory();
        Xunit.Assert.True(GoalWorktrees.DeleteDirectoryWithReason(path).Succeeded);
        Xunit.Assert.False(Directory.Exists(path));
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "worktree-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
