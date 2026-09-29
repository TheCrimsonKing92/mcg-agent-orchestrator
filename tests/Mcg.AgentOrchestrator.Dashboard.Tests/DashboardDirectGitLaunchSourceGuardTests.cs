using System.Text.RegularExpressions;

public sealed class DashboardDirectGitLaunchSourceGuardTests
{
    private static readonly Regex DirectLaunch = new(
        @"\b(?:new\s+ProcessStartInfo\s*\(\s*|FileName\s*=\s*|Process\.Start\s*\(\s*)" +
        "\"(?:git|git\\.exe)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Xunit.Fact]
    public void EveryDashboardTestGitLaunchUsesIsolatedHelper()
    {
        var root = Path.Combine(SharedTestSupport.FindRepositoryRoot(),
            "tests", "Mcg.AgentOrchestrator.Dashboard.Tests");
        var violations = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "DashboardTestGit.cs")
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment =>
                segment is "bin" or "obj" or ".scratch" or ".orchestrator-prototype"))
            .SelectMany(path => FindDirectLaunches(path, File.ReadAllText(path)))
            .ToArray();
        Xunit.Assert.Empty(violations);
    }

    [Xunit.Fact]
    public void DetectorFindsDirectLaunchesAndIgnoresGitData()
    {
        var quote = "\"";
        var source = "FileName = " + quote + "git" + quote + ";\n" +
            "new ProcessStartInfo(" + quote + "git.exe" + quote + ");\n" +
            "Assert.Equal(" + quote + "git" + quote + ", name);";
        Xunit.Assert.Equal(2, FindDirectLaunches("fixture.cs", source).Count());
    }

    private static IEnumerable<string> FindDirectLaunches(string path, string source)
    {
        foreach (Match match in DirectLaunch.Matches(source))
            yield return $"{path}:{source.AsSpan(0, match.Index).Count('\n') + 1}";
    }
}
