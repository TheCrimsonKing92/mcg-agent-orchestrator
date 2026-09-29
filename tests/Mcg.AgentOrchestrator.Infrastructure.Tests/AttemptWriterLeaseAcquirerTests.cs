using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class AttemptWriterLeaseAcquirerTests
{
    [Xunit.Fact]
    public void SuppliedAcquirerReceivesRequestedWaitOnceAndPreservesTimeoutReceipt()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var requests = new List<(string Directory, TimeSpan Wait)>();
        string? receipt = null;

        var exception = Xunit.Assert.Throws<TimeoutException>(() =>
            StorageRetentionMaintenance.AcquireAttemptWriterLease(
                directory,
                TimeSpan.FromMilliseconds(50),
                value => receipt = value,
                (path, wait) =>
                {
                    requests.Add((path, wait));
                    return null;
                }));

        Xunit.Assert.Equal((directory, TimeSpan.FromMilliseconds(50)), Xunit.Assert.Single(requests));
        Xunit.Assert.Equal(
            $"ACCEPTANCE_ARTIFACT_LEASE_TIMEOUT goal_directory=\"{directory}\" timeout_ms=50",
            exception.Message);
        Xunit.Assert.Equal(exception.Message, receipt);
    }

    [Xunit.Fact]
    public void OmittedTimeoutStillRequestsThirtySeconds()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var requests = new List<(string Directory, TimeSpan Wait)>();
        string? receipt = null;

        var exception = Xunit.Assert.Throws<TimeoutException>(() =>
            StorageRetentionMaintenance.AcquireAttemptWriterLease(
                directory,
                receipt: value => receipt = value,
                acquirer: (path, wait) =>
                {
                    requests.Add((path, wait));
                    return null;
                }));

        Xunit.Assert.Equal((directory, TimeSpan.FromSeconds(30)), Xunit.Assert.Single(requests));
        Xunit.Assert.Equal(
            $"ACCEPTANCE_ARTIFACT_LEASE_TIMEOUT goal_directory=\"{directory}\" timeout_ms=30000",
            exception.Message);
        Xunit.Assert.Equal(exception.Message, receipt);
    }

    [Xunit.Fact]
    public void SuppliedAcquirerReturnsItsLeaseWithoutWritingReceipt()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var expected = new CancellationTokenSource();
        string? receipt = null;

        var actual = StorageRetentionMaintenance.AcquireAttemptWriterLease(
            directory,
            receipt: value => receipt = value,
            acquirer: (_, _) => expected);

        Xunit.Assert.Same(expected, actual);
        Xunit.Assert.Null(receipt);
    }
}
