using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each fact owns its root and uses no process-global state.
public sealed class LandingAppBuildStoreTests
{
    [Xunit.Fact]
    public async Task Concurrent_instances_build_once_and_reuse_completed_output()
    {
        using var fixture = new StoreFixture();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        LandingAppBuildResult Build(LandingAppBuildRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(60), ct))
                throw new InvalidOperationException("The test did not release the first build event.");
            WriteDll(request);
            return new(0, "", "");
        }
        var first = new LandingAppBuildStore(fixture.Root, build: Build);
        var second = new LandingAppBuildStore(fixture.Root, build: Build,
            lockContended: name => { if (name == "aaa.lock") contended.TrySetResult(); });
        var callA = Task.Run(() => first.GetOrBuild(fixture.Root, "aaa", TimeSpan.FromMinutes(2)));
        Task<string>? callB = null;
        try
        {
            await AwaitEvent(entered.Task, "first build entered");
            callB = Task.Run(() => second.GetOrBuild(fixture.Root, "aaa", TimeSpan.FromMinutes(2)));
            await AwaitEvent(contended.Task, "second instance observed the held sha lock");
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "aaa")));
        }
        finally
        {
            release.Set();
            await AwaitEvent(callA, "first build completed");
            if (callB is not null) await AwaitEvent(callB, "second caller completed");
        }
        Assert.Equal(callA.Result, callB!.Result);
        Assert.Equal(1, calls);
        AssertPayload(callA.Result, "aaa");
        Assert.Equal(callA.Result, second.GetOrBuild(fixture.Root, "aaa", TimeSpan.FromMinutes(1)));
        Assert.Equal(1, calls);
    }

    [Xunit.Fact]
    public void Failed_build_cleans_partial_and_next_call_retries()
    {
        using var fixture = new StoreFixture();
        var calls = 0;
        var store = new LandingAppBuildStore(fixture.Root, build: (request, _) =>
        {
            WriteDll(request);
            return ++calls == 1 ? new(17, "build stdout", "build stderr") : new(0, "", "");
        });
        var failure = Assert.Throws<LandingAppBuildFailedException>(() =>
            store.GetOrBuild(fixture.Root, "aaa", TimeSpan.FromMinutes(1)));
        Assert.Equal(17, failure.Result.ExitCode);
        Assert.Equal("build stdout", failure.Result.Stdout);
        Assert.Equal("build stderr", failure.Result.Stderr);
        Assert.Empty(Directory.GetDirectories(fixture.Root));
        AssertPayload(store.GetOrBuild(fixture.Root, "aaa", TimeSpan.FromMinutes(1)), "aaa");
        Assert.Equal(2, calls);
        Assert.Empty(Directory.GetDirectories(fixture.Root, "*.partial-*"));
    }

    [Xunit.Fact]
    public void Retention_keeps_the_two_newest_completions()
    {
        using var fixture = new StoreFixture();
        var store = new LandingAppBuildStore(fixture.Root, retention: 2, build: SuccessfulBuild);
        foreach (var sha in new[] { "ccc", "bbb", "aaa" })
            AssertPayload(store.GetOrBuild(fixture.Root, sha, TimeSpan.FromMinutes(1)), sha);
        Assert.Equal(new[] { "aaa", "bbb" }, Directory.GetDirectories(fixture.Root)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Xunit.Fact]
    public void Retention_skips_locked_and_in_use_outputs()
    {
        using var fixture = new StoreFixture();
        var store = new LandingAppBuildStore(fixture.Root, retention: 1, build: SuccessfulBuild);
        var first = store.GetOrBuild(fixture.Root, "aaa", TimeSpan.FromMinutes(1));
        using (File.Open(Path.Combine(fixture.Root, "aaa.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store.GetOrBuild(fixture.Root, "bbb", TimeSpan.FromMinutes(1));
            AssertPayload(first, "aaa");
        }
        using (File.Open(Path.Combine(first, LandingAppBuildStore.AppDllName), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.GetOrBuild(fixture.Root, "ccc", TimeSpan.FromMinutes(1));
            AssertPayload(first, "aaa");
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "bbb")));
        }
        store.GetOrBuild(fixture.Root, "ddd", TimeSpan.FromMinutes(1));
        Assert.Equal(Path.Combine(fixture.Root, "ddd"), Assert.Single(Directory.GetDirectories(fixture.Root)));
    }

    [Xunit.Theory]
    [Xunit.InlineData("complete")]
    [Xunit.InlineData("head")]
    [Xunit.InlineData("dll")]
    [Xunit.InlineData("wrong-head")]
    public void Incomplete_or_mismatched_output_is_rebuilt(string damage)
    {
        using var fixture = new StoreFixture();
        var calls = 0;
        var store = new LandingAppBuildStore(fixture.Root, build: (request, ct) =>
        {
            calls++;
            return SuccessfulBuild(request, ct);
        });
        var directory = store.GetOrBuild(fixture.Root, "aaa", TimeSpan.FromMinutes(1));
        switch (damage)
        {
            case "complete": File.Delete(Path.Combine(directory, LandingAppBuildStore.CompleteMarkerName)); break;
            case "head": File.Delete(Path.Combine(directory, LandingAppBuildStore.HeadMarkerName)); break;
            case "dll": File.Delete(Path.Combine(directory, LandingAppBuildStore.AppDllName)); break;
            case "wrong-head": File.WriteAllText(Path.Combine(directory, LandingAppBuildStore.HeadMarkerName), "bbb"); break;
        }
        AssertPayload(store.GetOrBuild(fixture.Root, "aaa", TimeSpan.FromMinutes(1)), "aaa");
        Assert.Equal(2, calls);
    }

    [Xunit.Fact]
    public void Cancelled_build_cleans_partial_output()
    {
        using var fixture = new StoreFixture();
        using var cancellation = new CancellationTokenSource();
        var store = new LandingAppBuildStore(fixture.Root, build: (request, ct) =>
        {
            WriteDll(request);
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return new(0, "", "");
        });
        Assert.Throws<OperationCanceledException>(() =>
            store.GetOrBuild(fixture.Root, "aaa", TimeSpan.FromMinutes(1), cancellation.Token));
        Assert.Empty(Directory.GetDirectories(fixture.Root));
    }

    private static async Task AwaitEvent(Task task, string eventName)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { throw new InvalidOperationException($"Event did not occur: {eventName}."); }
    }

    private static LandingAppBuildResult SuccessfulBuild(LandingAppBuildRequest request, CancellationToken _)
    {
        WriteDll(request);
        return new(0, "", "");
    }

    private static void WriteDll(LandingAppBuildRequest request) =>
        File.WriteAllText(Path.Combine(request.OutputDirectory, LandingAppBuildStore.AppDllName), "fake App dll");

    private static void AssertPayload(string directory, string sha)
    {
        Assert.True(File.Exists(Path.Combine(directory, LandingAppBuildStore.AppDllName)));
        Assert.Equal(sha + Environment.NewLine, File.ReadAllText(Path.Combine(directory, LandingAppBuildStore.HeadMarkerName)));
        Assert.True(File.Exists(Path.Combine(directory, LandingAppBuildStore.CompleteMarkerName)));
        Assert.DoesNotContain(".partial-", directory);
    }

    private sealed class StoreFixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("landing-store-").FullName;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
