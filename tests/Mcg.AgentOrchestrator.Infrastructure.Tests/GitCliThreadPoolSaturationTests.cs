using Mcg.AgentOrchestrator.Infrastructure;

// Serialized: this test deliberately starves the thread pool for a few seconds, which would stall
// wall-clock-bounded siblings if it ran in a parallel collection.
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class GitCliThreadPoolSaturationTests
{
    // Under 24-way parallel test execution the gate recorded git children exiting 0 (or 128) with NOTHING
    // on either stream. GitCli drains the child's pipes with ReadToEndAsync and bounds the drain at
    // 5 s; if the reader needs a pool thread and every pool thread is blocked, the drain times out and
    // the output that is sitting in the pipe is discarded as "". This test reproduces that host state
    // deterministically: it blocks more work items than the pool will inject threads for within the
    // drain window, then runs one git command whose output must survive.
    [Xunit.Fact(DisplayName = "GitCli_Run_drains_output_while_the_thread_pool_is_saturated")]
    public void GitCliRunDrainsOutputWhileTheThreadPoolIsSaturated()
    {
        var repo = CreateSeededRepository();
        // Deliberately NOT disposed: the queued work items below outlive this method, and any item that
        // has not yet started when the method returns would call Wait() on a disposed event and throw
        // ObjectDisposedException on a pool thread, crashing the test host (observed on a copy of this
        // test in goal 715e53b7: 25 unhandled exceptions, exit 0xE0434352). A set, undisposed slim event
        // holds no kernel object and is collected with the closure.
        var release = new ManualResetEventSlim(false);
        ThreadPool.GetMinThreads(out var minWorkerThreads, out _);
        // Twice the minimum plus a margin: the pool injects roughly one or two threads per second
        // above the minimum, so the backlog outlives GitCli's 5 s drain window.
        var blockedItems = Math.Max(minWorkerThreads, Environment.ProcessorCount) * 2 + 32;
        try
        {
            for (var i = 0; i < blockedItems; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static state => ((ManualResetEventSlim)state!).Wait(), release);
            }

            // Let the queued items occupy every currently available worker before the git call.
            Thread.Sleep(250);

            var result = GitCli.Run(repo, "rev-parse", "HEAD");

            Assert.True(result.ProcessStarted, result.Error);
            Assert.False(result.DrainTimedOut, $"drain timed out: {result.Error}");
            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Output), $"empty stdout: {result.Error}");
        }
        finally
        {
            release.Set();
            try { Directory.Delete(repo, recursive: true); } catch { }
        }
    }

    private static string CreateSeededRepository()
    {
        var repo = Path.Combine(Path.GetTempPath(), "mcg-gitcli-saturation-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(repo);
        RunOrFail(repo, "init", "-q");
        RunOrFail(repo, "config", "user.email", "tests@example.com");
        RunOrFail(repo, "config", "user.name", "GitCli Saturation Tests");
        RunOrFail(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
        RunOrFail(repo, "add", "seed.txt");
        RunOrFail(repo, "commit", "-q", "-m", "Seed");
        return repo;
    }

    private static void RunOrFail(string repo, params string[] args)
    {
        var result = GitCli.Run(repo, args);
        Assert.True(
            result.Succeeded && !result.DrainTimedOut,
            $"git {string.Join(' ', args)} failed: exit={result.ExitCode}; drainTimedOut={result.DrainTimedOut}; stderr={result.Error}");
    }
}
