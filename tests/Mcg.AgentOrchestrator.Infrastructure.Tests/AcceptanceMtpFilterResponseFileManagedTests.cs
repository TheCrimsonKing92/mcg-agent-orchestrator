using static MtpTestRunnerScriptTests;

// Real managed discovery shares the repository's isolated process-spawning collection.
[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class AcceptanceMtpFilterResponseFileManagedTests
{
    [Fact]
    public void Discovery_LongResponseFileFilters_MatchesShortInlineUidSet()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"mtp-discovery-response-{Guid.NewGuid():N}");
        var longDirectory = Path.Combine(root, "long");
        var shortDirectory = Path.Combine(root, "short");
        Directory.CreateDirectory(longDirectory);
        Directory.CreateDirectory(shortDirectory);
        try
        {
            var exclusions = Enumerable.Range(0, 600)
                .SelectMany(index => new[] { "--filter-not-class", $"*NoSuchLaneClass{index:D4}*" }).ToArray();
            Assert.True(string.Join(' ', exclusions).Length >= 20000);
            var longSet = DiscoverManagedTests(["--results-directory", longDirectory,
                "--filter-class", "*MtpNoBuildReceiptIdentityTests*", .. exclusions]);
            var shortSet = DiscoverManagedTests("--results-directory", shortDirectory,
                "--filter-class", "*MtpNoBuildReceiptIdentityTests*");
            Assert.NotEmpty(longSet);
            Assert.NotEmpty(shortSet);
            Assert.Equal(shortSet.Keys.Order(StringComparer.Ordinal), longSet.Keys.Order(StringComparer.Ordinal));
            Assert.Single(Directory.GetFiles(longDirectory, "filter-*.rsp"));
            Assert.Empty(Directory.GetFiles(shortDirectory, "filter-*.rsp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
