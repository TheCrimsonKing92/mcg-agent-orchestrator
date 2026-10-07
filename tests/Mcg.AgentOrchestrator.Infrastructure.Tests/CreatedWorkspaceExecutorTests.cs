using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: workspace effects are per-test recording delegates.
public sealed class CreatedWorkspaceExecutorTests
{
    [Fact]
    public void Success_CallsDelegateOnceAndReturnsPath()
    {
        var calls = 0;

        var execution = CreatedWorkspaceExecutor.Execute(() =>
        {
            calls++;
            return @"C:\tmp\workspace";
        });

        Assert.Equal(1, calls);
        Assert.Equal(@"C:\tmp\workspace", execution.CreatedPath);
        Assert.Null(execution.LeaseUnavailableMessage);
    }

    [Fact]
    public void LeaseUnavailable_CallsDelegateOnceAndReturnsMessage()
    {
        var calls = 0;

        var execution = CreatedWorkspaceExecutor.Execute(() =>
        {
            calls++;
            throw new ConductorDriver.EvidenceMutationLeaseUnavailableException("lease held");
        });

        Assert.Equal(1, calls);
        Assert.Null(execution.CreatedPath);
        Assert.Equal("lease held", execution.LeaseUnavailableMessage);
    }

    [Fact]
    public void InvalidOperation_PropagatesSameExceptionInstance()
    {
        var calls = 0;
        var expected = new InvalidOperationException("workspace failure");

        var actual = Assert.Throws<InvalidOperationException>(() => CreatedWorkspaceExecutor.Execute(() =>
        {
            calls++;
            throw expected;
        }));

        Assert.Equal(1, calls);
        Assert.Same(expected, actual);
    }

    [Fact]
    public void IOFailure_PropagatesSameExceptionInstance()
    {
        var calls = 0;
        var expected = new IOException("workspace I/O failure");

        var actual = Assert.Throws<IOException>(() => CreatedWorkspaceExecutor.Execute(() =>
        {
            calls++;
            throw expected;
        }));

        Assert.Equal(1, calls);
        Assert.Same(expected, actual);
    }
}
