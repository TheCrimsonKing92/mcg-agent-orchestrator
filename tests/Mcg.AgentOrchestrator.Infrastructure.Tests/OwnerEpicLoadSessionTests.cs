using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its read completions, dispatch queue and session.
public sealed class OwnerEpicLoadSessionTests
{
    [Fact]
    public void ProductionDialogDelegatesLifecycleWithoutWidgetDependenciesInSession()
    {
        var folder = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Console");
        var dialog = File.ReadAllText(Path.Combine(folder, "OwnerConsoleEpicDialog.cs"));
        var session = File.ReadAllText(Path.Combine(folder, "OwnerEpicLoadSession.cs"));
        Assert.Contains("OwnerEpicLoadSession", dialog, StringComparison.Ordinal);
        Assert.Contains("controller.LoadEpicViewAsync", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("ReloadLoopAsync", dialog, StringComparison.Ordinal);
        foreach (var dependency in new[] { "Terminal.Gui", "IOwnerConsoleEpicSource", "Infrastructure", "Sqlite" })
            Assert.DoesNotContain(dependency, session, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseOrDisposeDrainsCancellationIgnoringRead(bool dispose)
    {
        var pending = new TaskCompletionSource<OwnerConsoleEpicViewModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken readToken = default;
        var publications = new List<OwnerEpicLoadResult>();
        using var session = new OwnerEpicLoadSession((_, _, token) =>
        {
            readToken = token;
            return pending.Task;
        }, action => action(), publications.Add, CancellationToken.None);
        session.Open();
        var loading = session.RefreshAsync();
        Assert.True(readToken.CanBeCanceled);

        if (dispose) session.Dispose(); else session.Close();
        await loading.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(readToken.IsCancellationRequested);
        Assert.False(pending.Task.IsCompleted);
        Assert.Empty(publications);
        pending.SetResult(Model());
        if (dispose) Assert.Throws<ObjectDisposedException>(session.Open);
        else
        {
            session.Open();
            await session.RefreshAsync().WaitAsync(TestContext.Current.CancellationToken);
            Assert.Single(publications);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeRejectsAlreadyQueuedSuccessOrFailure(bool failure)
    {
        var callbacks = new Queue<Action>();
        var publications = new List<OwnerEpicLoadResult>();
        using var session = new OwnerEpicLoadSession((_, _, _) => failure
            ? Task.FromException<OwnerConsoleEpicViewModel?>(new InvalidOperationException("failed"))
            : Task.FromResult<OwnerConsoleEpicViewModel?>(Model()), callbacks.Enqueue,
            publications.Add, CancellationToken.None);
        session.Open();
        await session.RefreshAsync().WaitAsync(TestContext.Current.CancellationToken);
        var callback = Assert.Single(callbacks);
        Assert.Empty(publications); // LastLoad drains without waiting for UI dispatch.

        session.Dispose();
        callback();

        Assert.Empty(publications);
    }

    [Fact]
    public async Task NotificationsCoalesceAndPublishEachCompletedReadUntilDrained()
    {
        var reads = Channel.CreateUnbounded<TaskCompletionSource<OwnerConsoleEpicViewModel?>>();
        var callbacks = Channel.CreateUnbounded<Action>();
        var publications = new List<OwnerEpicLoadResult>();
        var count = 0;
        using var session = new OwnerEpicLoadSession((_, _, _) =>
        {
            count++;
            var completion = new TaskCompletionSource<OwnerConsoleEpicViewModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(reads.Writer.TryWrite(completion));
            return completion.Task;
        }, action => Assert.True(callbacks.Writer.TryWrite(action)), publications.Add, CancellationToken.None);
        var token = TestContext.Current.CancellationToken;
        session.Open();
        var loading = session.RefreshAsync();
        var first = await reads.Reader.ReadAsync(token);
        for (var i = 0; i < 4; i++) Assert.Same(loading, session.RefreshAsync());
        var firstModel = Model();
        first.SetResult(firstModel);
        var firstCallback = await callbacks.Reader.ReadAsync(token);
        var second = await reads.Reader.ReadAsync(token);
        firstCallback();
        Assert.Same(firstModel, Assert.Single(publications).Model);
        Assert.True(publications[0].Refreshing);
        var secondModel = Model();
        second.SetResult(secondModel);
        await loading.WaitAsync(token);
        (await callbacks.Reader.ReadAsync(token))();
        Assert.Equal(2, count);
        Assert.Same(secondModel, publications[1].Model);
        Assert.False(publications[1].Refreshing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReversedCallbacksCannotReplaceNewerPublication(bool olderFailure)
    {
        var reads = Channel.CreateUnbounded<TaskCompletionSource<OwnerConsoleEpicViewModel?>>();
        var callbacks = Channel.CreateUnbounded<Action>();
        var publications = new List<OwnerEpicLoadResult>();
        using var session = new OwnerEpicLoadSession((_, _, _) =>
        {
            var completion = new TaskCompletionSource<OwnerConsoleEpicViewModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(reads.Writer.TryWrite(completion));
            return completion.Task;
        }, action => Assert.True(callbacks.Writer.TryWrite(action)), publications.Add, CancellationToken.None);
        session.Open();
        var token = TestContext.Current.CancellationToken;
        var loading = session.RefreshAsync();
        var first = await reads.Reader.ReadAsync(token);
        _ = session.RefreshAsync();
        if (olderFailure) first.SetException(new InvalidOperationException("old failure"));
        else first.SetResult(Model());
        var older = await callbacks.Reader.ReadAsync(token);
        var second = await reads.Reader.ReadAsync(token);
        var newest = Model();
        second.SetResult(newest);
        await loading.WaitAsync(token);
        (await callbacks.Reader.ReadAsync(token))();
        older();
        Assert.Same(newest, Assert.Single(publications).Model);
        Assert.False(publications[0].Refreshing);
    }

    private static OwnerConsoleEpicViewModel Model() => new(OwnerConsoleEpicWindow.Day, null, []);
}
