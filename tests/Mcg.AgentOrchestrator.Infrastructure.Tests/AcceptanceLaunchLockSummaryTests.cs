using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceLaunchLockSummaryTests : IDisposable
{
    private const string OwnerSourcePath =
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceLaunchLockSummary.cs";
    private const string Summary = "LAUNCH_LOCK_SUMMARY pid=4242 launches=2";

    // Parallel-safe: each test owns its temporary directory; repository source is read-only.
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();
    private string AttemptDirectory => Path.Combine(_root, "attempt");
    private string TrxPath => Path.Combine(AttemptDirectory, "lane.trx");
    private string AttemptPrefix => Path.Combine(AttemptDirectory, "acceptance");
    private string SummaryPath => Path.Combine(AttemptDirectory, "lane.launch-lock-summary.txt");

    [Fact]
    public void Preserve_MultipleStderrSummaries_WritesLastLineExactly()
    {
        var stderr = "LAUNCH_LOCK_SUMMARY pid=7 launches=0\ndiagnostic\n" + Summary;

        AcceptanceLaunchLockSummary.Preserve("unused output", stderr, TrxPath, AttemptPrefix);

        Assert.Equal(Summary + Environment.NewLine, File.ReadAllText(SummaryPath));
        Assert.Equal(SummaryPath, Assert.Single(Directory.GetFiles(AttemptDirectory)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Preserve_AbsentStderr_ReadsOutput(string? stderr)
    {
        AcceptanceLaunchLockSummary.Preserve(Summary, stderr, TrxPath, AttemptPrefix);

        Assert.Equal(Summary + Environment.NewLine, File.ReadAllText(SummaryPath));
    }

    [Theory]
    [InlineData("diagnostic only")]
    [InlineData(" \t")]
    public void Preserve_NonEmptyStderr_TakesPrecedenceOverOutput(string stderr)
    {
        AcceptanceLaunchLockSummary.Preserve(Summary, stderr, TrxPath, AttemptPrefix);

        Assert.False(File.Exists(SummaryPath));
        Assert.False(Directory.Exists(AttemptDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void Preserve_InvalidAttemptPrefix_CreatesNothing(string? attemptPrefix)
    {
        AcceptanceLaunchLockSummary.Preserve(Summary, Summary, TrxPath, attemptPrefix);

        Assert.False(File.Exists(SummaryPath));
        Assert.False(Directory.Exists(AttemptDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData("diagnostic only")]
    [InlineData(" LAUNCH_LOCK_SUMMARY pid=4242 launches=2")]
    [InlineData("diagnostic LAUNCH_LOCK_SUMMARY pid=4242 launches=2")]
    public void Preserve_NoStartingSummary_CreatesNothing(string output)
    {
        AcceptanceLaunchLockSummary.Preserve(output, null, TrxPath, AttemptPrefix);

        Assert.False(File.Exists(SummaryPath));
        Assert.False(Directory.Exists(AttemptDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Preserve_NoSummary_RemovesStaleFile()
    {
        Directory.CreateDirectory(AttemptDirectory);
        File.WriteAllText(SummaryPath, "stale summary");
        Assert.True(File.Exists(SummaryPath));

        AcceptanceLaunchLockSummary.Preserve("diagnostic only", null, TrxPath, AttemptPrefix);

        Assert.False(File.Exists(SummaryPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(AttemptDirectory));
    }

    [Fact]
    public void Verifier_DelegatesToOwner_RemovesFormerPartialAndMethod()
    {
        var repository = VerifiedRepositoryRoot.Find();
        var workspaces = Path.Combine(repository,
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces");
        var verifier = File.ReadAllText(Path.Combine(workspaces, "GoalAcceptanceVerifier.cs"));

        Assert.Contains("AcceptanceLaunchLockSummary.Preserve(", verifier);
        Assert.False(File.Exists(Path.Combine(workspaces, "GoalAcceptanceVerifier.LaunchLockSummary.cs")));

        var formerMethod = "Preserve" + "LaunchLockSummary";
        var sourceFiles = Directory.EnumerateFiles(Path.Combine(repository, "src"), "*",
            SearchOption.AllDirectories).Where(path =>
        {
            var relative = Path.GetRelativePath(repository, path).Replace('\\', '/');
            return relative != OwnerSourcePath && !relative.Split('/').Any(segment =>
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".scratch", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".orchestrator-prototype", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("TestResults", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("playwright-report", StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        Assert.NotEmpty(sourceFiles);
        foreach (var sourceFile in sourceFiles)
        {
            Assert.DoesNotContain(formerMethod, File.ReadAllText(sourceFile));
        }
    }

    [Fact]
    public void OwnerSourceChange_TriggersPostLandingCanary()
    {
        Assert.True(PostLandingCanaryTrigger.Evaluate([OwnerSourcePath]).ShouldRun);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
