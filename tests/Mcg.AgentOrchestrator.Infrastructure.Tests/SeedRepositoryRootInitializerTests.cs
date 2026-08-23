using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SeedRepositoryRootInitializerTests
{
    [Xunit.Fact]
    public void SecondAttemptRunsTheInitializerAfterATransientFailure()
    {
        var invocations = 0;
        var initializer = new SeedRepositoryRootInitializer(() =>
        {
            if (Interlocked.Increment(ref invocations) == 1)
            {
                throw new IOException("transient failure");
            }

            return "seed-root";
        });

        _ = Assert.Throws<IOException>(() => initializer.EnsureInitialized());

        Assert.Equal("seed-root", initializer.EnsureInitialized());
        Assert.Equal(2, invocations);
    }

    [Xunit.Fact]
    public void SuccessfulInitializationIsNotRepeatedForLaterCallers()
    {
        var invocations = 0;
        var initializer = new SeedRepositoryRootInitializer(() =>
        {
            Interlocked.Increment(ref invocations);
            return "seed-root";
        });

        Assert.Equal("seed-root", initializer.EnsureInitialized());
        Assert.Equal("seed-root", initializer.EnsureInitialized());
        Assert.Equal(1, invocations);
    }

    [Xunit.Fact]
    public async Task ConcurrentFirstCallersRunTheInitializerExactlyOnce()
    {
        const int callerCount = 8;
        var invocations = 0;
        using var callersReady = new Barrier(callerCount);
        var initializer = new SeedRepositoryRootInitializer(() =>
        {
            Interlocked.Increment(ref invocations);
            return "seed-root";
        });
        var calls = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Run(() =>
            {
                callersReady.SignalAndWait();
                return initializer.EnsureInitialized();
            }))
            .ToArray();

        var roots = await Task.WhenAll(calls);

        Assert.All(roots, root => Assert.Equal("seed-root", root));
        Assert.Equal(1, invocations);
    }

    [Xunit.Fact]
    public void PermanentFailureFailsEveryCallerWithoutInternalRetry()
    {
        var invocations = 0;
        var initializer = new SeedRepositoryRootInitializer(() =>
        {
            Interlocked.Increment(ref invocations);
            throw new InvalidOperationException("permanent failure");
        });

        for (var caller = 0; caller < 3; caller++)
        {
            _ = Assert.Throws<InvalidOperationException>(() => initializer.EnsureInitialized());
        }

        Assert.Equal(3, invocations);
    }

    [Xunit.Fact]
    public void FailureNamesThePathDeleteOutcomeAndUnderlyingException()
    {
        const string processRoot = @"C:\seed-root";
        var deleteResult = new TempRootJanitorDeleteResult(
            processRoot,
            TempRootJanitorDeleteStatus.Failed,
            "System.IO.IOException",
            @"C:\seed-root\pending",
            ReadOnlyAttributesCleared: 0);
        var underlying = new UnauthorizedAccessException("access denied");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SeedRepositoryRootInitializer.CreateProcessRoot(
                processRoot,
                _ => deleteResult,
                _ => throw underlying));

        Assert.Contains("Seed repository root initialization failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains(processRoot, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException", exception.Message, StringComparison.Ordinal);
        Assert.Contains(deleteResult.FailurePath!, exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(UnauthorizedAccessException).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains(underlying.Message, exception.Message, StringComparison.Ordinal);
        Assert.Same(underlying, exception.InnerException);
    }

    [Xunit.Fact]
    public void ProcessRootIsDeletedBeforeItIsCreated()
    {
        const string processRoot = "seed-root";
        var operations = new List<string>();

        var result = SeedRepositoryRootInitializer.CreateProcessRoot(
            processRoot,
            path =>
            {
                operations.Add($"delete:{path}");
                return new TempRootJanitorDeleteResult(
                    path,
                    TempRootJanitorDeleteStatus.Deleted,
                    ExceptionType: null,
                    FailurePath: null,
                    ReadOnlyAttributesCleared: 0);
            },
            path => operations.Add($"create:{path}"));

        Assert.Equal(processRoot, result);
        Assert.Equal(["delete:seed-root", "create:seed-root"], operations);
    }
}
