using Mcg.AgentOrchestrator.App.Cli;

// Parallel-safe: repository source reads only; no shared state is changed.
public sealed class OperatorControlPlaneRemovalGuardTests
{
    private static readonly string[] ForbiddenLiterals =
    [
        "operator-control-plane",
        "HandleOperatorControlPlaneCommand",
        "ControlPlaneReplayHarness",
        "ControlPlaneReplayReport",
        "OperatorInboxControlPlaneProjection",
        "ControlPlaneBacklogDigestItem",
        "DeliverDailyBacklogDigestAsync",
        "RenderDailyBacklogDigest",
        "DailyBacklogDigestKey",
        "control-plane-replay"
    ];

    [Fact]
    public void SourceFiles_RemovedLiterals_HaveNoControlPlaneReplayRemnants()
    {
        var root = VerifiedRepositoryRoot.Find();
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var files = SourceFiles(root).ToArray();
        Assert.NotEmpty(files);
        var violations = files.SelectMany(file =>
            FindForbiddenLiterals(snapshot.GetText(file)).Select(literal =>
                $"'{literal}' found in {Path.GetRelativePath(root, file)}")).ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void FindForbiddenLiterals_ReplayHarness_ReportsCaseInsensitiveMatchOnce()
    {
        var matches = FindForbiddenLiterals("ControlPlaneReplayHarness controlplanereplayharness");

        Assert.Equal("ControlPlaneReplayHarness", Assert.Single(matches));
    }

    [Fact]
    public void FindForbiddenLiterals_KeptDeliveryAndStewardTypes_ReportsNoMatches()
    {
        var matches = FindForbiddenLiterals(
            "ComposeDailyBacklogDigest StewardDailyBacklogDigest " +
            "DiscordControlPlaneDeliverer ControlPlaneDecisionCard");

        Assert.Empty(matches);
    }

    [Fact]
    public void RecognizedCommands_RemovedVerb_KeepsSiblingOperatorCommands()
    {
        var commands = CliArgumentParser.RecognizedCommands;

        Assert.DoesNotContain("operator-control-plane", commands, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("operator-channel", commands, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("operator-listen", commands, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("operator-inbox", commands, StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> FindForbiddenLiterals(string sourceText) =>
        ForbiddenLiterals.Where(literal =>
            sourceText.Contains(literal, StringComparison.OrdinalIgnoreCase)).ToArray();

    internal static IEnumerable<string> SourceFiles(string root) =>
        RepositorySourceSnapshotCache.Get(root).EnumerateFiles(Path.Combine(root, "src"))
            .Where(file => !SourceGuardFileFilter.WorkspaceAndReports.Excludes(Path.GetRelativePath(root, file)));
}
