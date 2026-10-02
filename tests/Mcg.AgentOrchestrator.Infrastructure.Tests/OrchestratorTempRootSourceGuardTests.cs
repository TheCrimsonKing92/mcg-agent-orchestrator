using System.Runtime.CompilerServices;

public sealed class OrchestratorTempRootSourceGuardTests
{
    private sealed record AllowedUse(string Path, string Snippet, int Count, string Reason);

    private static readonly AllowedUse[] Allowed =
    [
        new("Mcg.AgentOrchestrator.Infrastructure/Processes/LockAttribution.cs", "var temp = Path.GetTempPath();", 1, "Reads the temp search root only."),
        new("Mcg.AgentOrchestrator.App/Orchestration/TerminalGoalSweep.OwnedRoots.cs", "Path.GetTempPath(), OrchestratorTempRoot.GetParent(), TimeProvider.System).SummaryLine;", 1, "Janitor scans the legacy temp root for leaked artifacts."),
        new("Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs", "Path.GetTempPath(),", 1, "Fallback base for the separately excluded dotnet isolated root."),
        new("Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs", "var landingTestsRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), LandingTestsRootDirectoryName))", 1, "Read-only containment check for landing test fixtures."),
        new("Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs", "var baselineRoot = Path.Combine(Path.GetTempPath(), FocusedEvidenceBaselinesRootDirectoryName);", 1, "Existing baseline path is pinned by a test."),
        new("Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs", "return Path.Combine(Path.GetTempPath(), OwnerResultsRootDirectoryName);", 1, "Existing owner results path is pinned by a test."),
        new("Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs", "worktreePath ?? Path.Combine(Path.GetTempPath(), OwnerResultsRootDirectoryName));", 1, "Existing owner results fallback is pinned by a test."),
        new("Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs", "Path.GetTempPath(),", 1, "Test-only heartbeat fixture seam."),
        new("Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs", "var profileRoot = Path.Combine(Path.GetTempPath(), HermeticProfileRootDirectoryName);", 1, "Profile root already has a scanner exclusion."),
        new("Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.OwnerResultsRooting.cs", "return configuredRoot ?? Path.Combine(Path.GetTempPath(), OwnerResultsRootDirectoryName);", 1, "Existing owner results fallback is pinned by a test."),
        new("Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.OwnerResultsRooting.cs", "Path.Combine(Path.GetTempPath(), OwnerResultsRootDirectoryName),", 1, "Compares against the pinned owner results fallback."),
        new("Mcg.AgentOrchestrator.Infrastructure/Processes/HostScanExclusionRoots.cs", "var tempPath = Path.GetTempPath();", 1, "Reads the temp base to list scanner-exclusion roots; creates nothing.")
    ];

    [Fact]
    public void ProductionSourceUsesSharedTempRootOrDocumentedException()
    {
        var root = FindRepositoryRoot();
        var src = Path.Combine(root, "src");
        var lines = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .SelectMany(path => File.ReadAllLines(path).Select((line, index) =>
                (Path: Path.GetRelativePath(src, path).Replace('\\', '/'), Line: line.Trim(), Number: index + 1)))
            .ToArray();
        var errors = new List<string>();
        foreach (var allowed in Allowed)
        {
            Assert.False(string.IsNullOrWhiteSpace(allowed.Reason));
            var matches = lines.Count(item => item.Path == allowed.Path &&
                item.Line.Equals(allowed.Snippet, StringComparison.Ordinal));
            if (matches != allowed.Count)
                errors.Add($"Stale allowance {allowed.Path}: {allowed.Snippet} expected {allowed.Count}, found {matches}");
        }
        foreach (var item in lines)
        {
            if (item.Line.Contains("Path.GetTempFileName(", StringComparison.Ordinal) ||
                item.Line.Contains("Directory.CreateTempSubdirectory(", StringComparison.Ordinal))
                errors.Add($"{item.Path}:{item.Number}: implicit temp root construction");
            if (!item.Line.Contains("Path.GetTempPath()", StringComparison.Ordinal) ||
                item.Path == "Mcg.AgentOrchestrator.Infrastructure/Processes/OrchestratorTempRoot.cs") continue;
            if (!IsAllowed(item.Path, item.Line))
                errors.Add($"{item.Path}:{item.Number}: {item.Line}");
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void NewDirectConstructionIsRejected()
    {
        var source = "var root = Path.Combine(Path.GetTempPath(), \"mcg-new-root\");";
        Assert.False(IsAllowed("new.cs", source));
    }

    private static bool IsAllowed(string path, string line) =>
        Allowed.Any(allowed => allowed.Path == path &&
            line.Equals(allowed.Snippet, StringComparison.Ordinal));

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
             directory is not null;
             directory = directory.Parent)
            if ((Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                 File.Exists(Path.Combine(directory.FullName, ".git"))) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
                return directory.FullName;
        throw new DirectoryNotFoundException($"Repository root was not found from source path '{sourceFilePath}'.");
    }
}
