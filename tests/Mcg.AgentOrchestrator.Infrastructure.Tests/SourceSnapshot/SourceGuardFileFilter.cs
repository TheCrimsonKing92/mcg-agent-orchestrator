internal sealed class SourceGuardFileFilter
{
    public static readonly SourceGuardFileFilter BuildOutput = new("bin", "obj");
    public static readonly SourceGuardFileFilter BuildOutputAndArtifacts = new("bin", "obj", "artifacts");
    public static readonly SourceGuardFileFilter BuildOutputAndScratch = new("bin", "obj", ".scratch", ".orchestrator-prototype");
    public static readonly SourceGuardFileFilter CallerPathScan = new("bin", "obj", "artifacts", ".scratch", "TestResults");
    public static readonly SourceGuardFileFilter WorkspaceAndReports = new(
        "bin", "obj", ".scratch", ".orchestrator-prototype", ".orchestrator-worktrees", "TestResults", "playwright-report");

    private readonly string[] excludedSegments;
    private SourceGuardFileFilter(params string[] excludedSegments) => this.excludedSegments = excludedSegments;

    public bool Excludes(string path) => path.Split('/', '\\').Any(segment =>
        excludedSegments.Contains(segment, StringComparer.Ordinal));
}
