using System.Globalization;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: per-test directory and deterministic clock; no real-time waits.
public sealed class OwnerConsoleRefreshTimingsFileTests
{
    [Fact]
    public void Write_ReplacesOneLineAtSixtySeconds()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "timings.txt");
            var clock = new OwnerConsoleTestClock();
            var writer = new OwnerConsoleRefreshTimingsFile(path, clock, 4242);
            writer.Write("board=1/5ms");
            var first = Assert.Single(File.ReadAllLines(path));
            Assert.Equal(clock.Now.ToString("O", CultureInfo.InvariantCulture) + " pid=4242 board=1/5ms", first);
            clock.Now += TimeSpan.FromSeconds(59);
            writer.Write("board=2/7ms");
            Assert.Equal(first, Assert.Single(File.ReadAllLines(path)));
            clock.Now += TimeSpan.FromSeconds(1);
            writer.Write("board=3/9ms");
            Assert.Equal(clock.Now.ToString("O", CultureInfo.InvariantCulture) + " pid=4242 board=3/9ms",
                Assert.Single(File.ReadAllLines(path)));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void Write_DirectoryTargetDoesNotThrow()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var writer = new OwnerConsoleRefreshTimingsFile(root, new OwnerConsoleTestClock(), 4242);
            Assert.Null(Record.Exception(() => writer.Write("board=1/5ms")));
            Assert.True(Directory.Exists(root));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
