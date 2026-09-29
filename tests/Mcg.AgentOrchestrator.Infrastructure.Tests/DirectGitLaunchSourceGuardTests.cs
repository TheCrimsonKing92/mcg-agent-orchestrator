using System.Text.RegularExpressions;
using Xunit;

public sealed class DirectGitLaunchSourceGuardTests
{
    private static readonly Regex DirectLaunch = new(
        @"\b(?:new\s+ProcessStartInfo\s*\(\s*|FileName\s*=\s*|Process\.Start\s*\(\s*)" +
        "\"(?:git|git\\.exe)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void EveryInfrastructureTestGitLaunchUsesProbe()
    {
        var root = Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        var violations = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals("InfrastructureTestSupport.cs", StringComparison.Ordinal))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment =>
                segment is "bin" or "obj" or ".scratch" or ".orchestrator-prototype"))
            .SelectMany(path => FindDirectLaunches(path, File.ReadAllText(path)))
            .ToArray();

        Assert.True(violations.Length == 0, "Direct git launches outside InfrastructureTestSupport.cs:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void DetectorRecognizesLaunchFormsWithoutFlaggingGitText()
    {
        var quote = "\"";
        var lines = new[]
        {
            "new ProcessStartInfo(" + quote + "git" + quote + ")",
            "FileName = " + quote + "git.exe" + quote + ",",
            "Process.Start(" + quote + "git" + quote + ")",
            "RunCommand(" + quote + "git" + quote + ")",
            "Executable = " + quote + "git" + quote,
            "dotnetPath: " + quote + "git.exe" + quote
        };
        var matches = FindDirectLaunches("fixture.cs", string.Join("\n", lines)).ToArray();
        Assert.Equal(["fixture.cs:1", "fixture.cs:2", "fixture.cs:3"], matches);
    }

    private static IEnumerable<string> FindDirectLaunches(string path, string source)
    {
        foreach (Match match in DirectLaunch.Matches(source))
        {
            var line = 1;
            for (var index = 0; index < match.Index; index++)
            {
                if (source[index] == '\n') line++;
            }
            yield return $"{path}:{line}";
        }
    }
}
