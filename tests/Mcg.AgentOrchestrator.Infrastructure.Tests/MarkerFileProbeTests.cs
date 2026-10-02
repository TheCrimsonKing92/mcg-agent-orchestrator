using Xunit;

// Parallel-safe: each case owns a unique directory and starts no process.
public sealed class MarkerFileProbeTests
{
    public enum MarkerArrival { AlreadyExists, CreatedAfterStart, RenamedFromTemporary }

    [Theory]
    [InlineData(MarkerArrival.AlreadyExists, 0)]
    [InlineData(MarkerArrival.AlreadyExists, 1)]
    [InlineData(MarkerArrival.AlreadyExists, 2)]
    [InlineData(MarkerArrival.AlreadyExists, 3)]
    [InlineData(MarkerArrival.AlreadyExists, 4)]
    [InlineData(MarkerArrival.AlreadyExists, 5)]
    [InlineData(MarkerArrival.AlreadyExists, 6)]
    [InlineData(MarkerArrival.CreatedAfterStart, 7)]
    [InlineData(MarkerArrival.CreatedAfterStart, 8)]
    [InlineData(MarkerArrival.CreatedAfterStart, 9)]
    [InlineData(MarkerArrival.CreatedAfterStart, 10)]
    [InlineData(MarkerArrival.CreatedAfterStart, 11)]
    [InlineData(MarkerArrival.CreatedAfterStart, 12)]
    [InlineData(MarkerArrival.CreatedAfterStart, 13)]
    [InlineData(MarkerArrival.RenamedFromTemporary, 14)]
    [InlineData(MarkerArrival.RenamedFromTemporary, 15)]
    [InlineData(MarkerArrival.RenamedFromTemporary, 16)]
    [InlineData(MarkerArrival.RenamedFromTemporary, 17)]
    [InlineData(MarkerArrival.RenamedFromTemporary, 18)]
    [InlineData(MarkerArrival.RenamedFromTemporary, 19)]
    public async Task ProbeCompletesWhenMarkerArrives(MarkerArrival arrival, int row)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mcg-marker-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, $"descendant-{row}.pid");
        var temporaryMarker = marker + ".tmp";
        try
        {
            if (arrival == MarkerArrival.AlreadyExists)
                File.WriteAllText(marker, "1");
            else if (arrival == MarkerArrival.RenamedFromTemporary)
                File.WriteAllText(temporaryMarker, "1");

            var wait = MarkerFileProbe.WaitAsync(marker, TestHangGuard.Bound);
            if (arrival != MarkerArrival.AlreadyExists)
            {
                Assert.False(wait.IsCompleted);
                if (arrival == MarkerArrival.CreatedAfterStart)
                    File.WriteAllText(marker, "1");
                else
                    File.Move(temporaryMarker, marker);
            }

            await wait;
            Assert.True(wait.IsCompletedSuccessfully);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("not started")]
    [InlineData("running")]
    [InlineData("exited exitCode=7")]
    public async Task ExpiredProbeNamesMarkerEventAndReportsState(string expectedChild)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mcg-marker-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "descendant.pid");
        Func<MarkerFileProbe.ChildState>? childState = expectedChild switch
        {
            "running" => () => MarkerFileProbe.ChildState.Running,
            "exited exitCode=7" => () => MarkerFileProbe.ChildState.Exited(7),
            _ => null
        };
        try
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                MarkerFileProbe.WaitAsync(marker, TimeSpan.FromMilliseconds(50), childState));

            Assert.Contains("Hang guard: descendant PID marker file", error.Message, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(marker), error.Message, StringComparison.Ordinal);
            Assert.Contains("did not appear", error.Message, StringComparison.Ordinal);
            Assert.Contains("markerExists=False", error.Message, StringComparison.Ordinal);
            Assert.Contains($"child={expectedChild}", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
