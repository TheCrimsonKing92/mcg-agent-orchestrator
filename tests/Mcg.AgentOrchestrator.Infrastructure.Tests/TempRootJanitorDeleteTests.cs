using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TempRootJanitorDeleteTests
{
    [Fact]
    public void HeldChildFailureRetainsMessageHResultAndRemainingPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"janitor-held-child-{Guid.NewGuid():N}");
        var child = Path.Combine(root, "repository", ".git", "held.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(child)!);
        File.WriteAllText(child, "held");

        TempRootDeleteOutcome outcome;
        using (File.Open(child, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            outcome = AssemblyTempRedirect.DeleteOwnedTree(root, _ => { });
        }

        try
        {
            Assert.Equal(TempRootDeleteStatus.Failed, outcome.Status);
            Assert.Equal(nameof(IOException), outcome.ExceptionType);
            Assert.False(string.IsNullOrWhiteSpace(outcome.ExceptionMessage));
            Assert.NotNull(outcome.ExceptionHResult);
            Assert.Equal(child, outcome.FailurePath, ignoreCase: true);
            Assert.Equal(6, outcome.DeleteAttempts);

            var diagnostic = AssemblyTempRedirect.FormatCleanupDiagnostic(
                AssemblyTempRootCleanupOwner.AssemblyFixture,
                outcome,
                elapsedMilliseconds: 1550);
            Assert.Contains("delete_attempts=6", diagnostic, StringComparison.Ordinal);
            Assert.Contains("exception_hresult=0x", diagnostic, StringComparison.Ordinal);
            Assert.Contains($"failure_path=\"{child}\"", diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("exception_message=\"", diagnostic, StringComparison.Ordinal);

            var exception = Assert.Throws<InvalidOperationException>(
                () => AssemblyTempRootCleanupFixture.EnsureSuccessful(outcome));
            Assert.Contains(outcome.ExceptionMessage!, exception.Message, StringComparison.Ordinal);
            Assert.Contains($"0x{unchecked((uint)outcome.ExceptionHResult!.Value):X8}", exception.Message, StringComparison.Ordinal);
            Assert.Contains(child, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(root);
        }
    }

    [Fact]
    public void TransientHeldChildIsDeletedAfterTheHandleReleases()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"janitor-transient-child-{Guid.NewGuid():N}");
        var child = Path.Combine(root, "repository", "transient.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(child)!);
        File.WriteAllText(child, "held briefly");
        using var held = File.Open(child, FileMode.Open, FileAccess.Read, FileShare.None);
        var released = false;

        var outcome = AssemblyTempRedirect.DeleteOwnedTree(root, _ =>
        {
            held.Dispose();
            released = true;
        });

        Assert.True(released, "The retry branch did not invoke the controlled handle-release seam.");
        Assert.Equal(TempRootDeleteStatus.Deleted, outcome.Status);
        Assert.Equal(2, outcome.DeleteAttempts);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void StateDbTrackerReportsOnlyLiveFactoryConnectionsWithinOwnedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"state-db-tracker-{Guid.NewGuid():N}");
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"state-db-tracker-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outsideRoot);

        try
        {
            using var tracker = new AssemblyTempRootCleanupFixture.StateDbOpenConnectionTracker();
            using (var outside = StateDbConnectionFactory.Open(
                       Path.Combine(outsideRoot, "state.db"),
                       StateDbConnectionProfile.ReadWrite))
            using (var inside = StateDbConnectionFactory.Open(
                       Path.Combine(root, "state.db"),
                       StateDbConnectionProfile.ReadWrite))
            {
                var open = Assert.Single(tracker.FindWithin(root));
                Assert.Equal(Path.Combine(root, "state.db"), open.DatabasePath);
                Assert.Equal(Environment.CurrentManagedThreadId, open.ThreadId);
                Assert.Contains(nameof(StateDbTrackerReportsOnlyLiveFactoryConnectionsWithinOwnedRoot), open.CreationSite);
                Assert.Contains("state-db-open-connection", open.FormatDiagnostic());

                inside.Close();
                Assert.Empty(tracker.FindWithin(root));

                inside.Open();
                using var command = inside.CreateCommand();
                command.CommandText = "SELECT 1";
                Assert.Equal(1L, command.ExecuteScalar());
                var reopened = Assert.Single(tracker.FindWithin(root));
                Assert.Equal(open.Id, reopened.Id);
            }

            Assert.Empty(tracker.FindWithin(root));
        }
        finally
        {
            TempRootJanitor.DeleteTreeWithRetry(root);
            TempRootJanitor.DeleteTreeWithRetry(outsideRoot);
        }
    }

    [Fact]
    public void StateDbTrackerDoesNotKeepAnOpenConnectionAlive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"state-db-tracker-collection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            using var tracker = new AssemblyTempRootCleanupFixture.StateDbOpenConnectionTracker();
            var connectionReference = OpenTrackedConnectionWithoutRetainingIt(root, tracker);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(connectionReference.TryGetTarget(out _));
            Assert.Empty(tracker.FindWithin(root));
        }
        finally
        {
            TempRootJanitor.DeleteTreeWithRetry(root);
        }
    }

    [Fact]
    public void StateDbDiagnosticObserverFailureDoesNotFailConnectionOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), $"state-db-observer-failure-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(root, "state.db");
        void ThrowingObserver(Microsoft.Data.Sqlite.SqliteConnection connection, string path) =>
            throw new InvalidOperationException("synthetic diagnostic failure");

        StateDbConnectionFactory.ConnectionOpenedForDiagnostics += ThrowingObserver;
        try
        {
            using var tracker = new AssemblyTempRootCleanupFixture.StateDbOpenConnectionTracker();
            using var connection = StateDbConnectionFactory.Open(
                databasePath,
                StateDbConnectionProfile.ReadWrite);
            Assert.Equal(System.Data.ConnectionState.Open, connection.State);
            Assert.Single(tracker.FindWithin(root));
        }
        finally
        {
            StateDbConnectionFactory.ConnectionOpenedForDiagnostics -= ThrowingObserver;
            TempRootJanitor.DeleteTreeWithRetry(root);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<Microsoft.Data.Sqlite.SqliteConnection> OpenTrackedConnectionWithoutRetainingIt(
        string root,
        AssemblyTempRootCleanupFixture.StateDbOpenConnectionTracker tracker)
    {
        var connection = StateDbConnectionFactory.Open(
            Path.Combine(root, "state.db"),
            StateDbConnectionProfile.ReadWrite);
        Assert.Single(tracker.FindWithin(root));
        return new WeakReference<Microsoft.Data.Sqlite.SqliteConnection>(connection);
    }
}
