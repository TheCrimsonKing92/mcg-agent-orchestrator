using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns a unique temp directory.
public sealed class AcceptanceLaneResultsDirectoryTests
{
    [Theory]
    [InlineData("--results-directory")]
    [InlineData("--RESULTS-DIRECTORY")]
    public void Flag_returns_the_following_directory_unchanged(string flag)
    {
        InTempDirectory(_ =>
        {
            Assert.Equal(@"C:\r", AcceptanceLaneResultsDirectory.FromArguments(
                ["dotnet", "test", flag, @"C:\r", "--logger", "x"]));
            Assert.Equal(" directory ", AcceptanceLaneResultsDirectory.FromArguments([flag, " directory "]));
        });
    }

    [Fact]
    public void Missing_flag_or_value_returns_null()
    {
        InTempDirectory(_ =>
        {
            Assert.Null(AcceptanceLaneResultsDirectory.FromArguments(["dotnet", "test"]));
            Assert.Null(AcceptanceLaneResultsDirectory.FromArguments(["dotnet", "test", "--results-directory"]));
            Assert.Null(AcceptanceLaneResultsDirectory.FromArguments(["--results-directory", ""]));
            Assert.Null(AcceptanceLaneResultsDirectory.FromArguments(["--results-directory", " \t "]));
        });
    }

    [Fact]
    public void Only_the_first_flag_is_used_even_when_its_value_is_blank()
    {
        InTempDirectory(directory =>
        {
            Assert.Equal(directory, AcceptanceLaneResultsDirectory.FromArguments(
                ["--results-directory", directory, "--results-directory", "other"]));
            Assert.Null(AcceptanceLaneResultsDirectory.FromArguments(
                ["--results-directory", " ", "--results-directory", directory]));
        });
    }

    [Fact]
    public void Probe_creation_requires_a_results_directory()
    {
        InTempDirectory(directory =>
        {
            Assert.Null(AcceptanceLaneResultsDirectory.ProbeFor(["dotnet", "test"]));
            Assert.Null(AcceptanceLaneResultsDirectory.ProbeFor(["--results-directory"]));
            Assert.Null(AcceptanceLaneResultsDirectory.ProbeFor(["--results-directory", " "]));
            var probe = Assert.IsType<TrxStreamProgressProbe>(AcceptanceLaneResultsDirectory.ProbeFor(
                ["dotnet", "test", "--results-directory", directory]));
            Assert.Null(probe.Sample());
            var path = Path.Combine(directory, "trx-stream-1.bin");
            File.WriteAllBytes(path, [1]);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc));
            Assert.NotNull(probe.Sample());
        });
    }

    private static void InTempDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lane-results-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
