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
            "new Process" + "StartInfo(" + quote + "git" + quote + ")",
            "FileName = " + quote + "git.exe" + quote + ",",
            "Process.Start(" + quote + "git" + quote + ")",
            "RunCommand(" + quote + "git" + quote + ")",
            "Executable = " + quote + "git" + quote,
            "dotnetPath: " + quote + "git.exe" + quote
        };
        var matches = FindDirectLaunches("fixture.cs", string.Join("\n", lines)).ToArray();
        Assert.Equal(["fixture.cs:1", "fixture.cs:2", "fixture.cs:3"], matches);
    }

    [Fact]
    public void GateEnvironmentExceptionDoesNotHideAnotherLaunch()
    {
        var path = Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(),
            "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "HermeticVerificationEnvironmentTestsGateGitRealGit.cs");
        var source = File.ReadAllText(path);
        Assert.Empty(FindDirectLaunches(path, source));

        var extraLaunch = source + "\nnew ProcessStartInfo(\"git\")";
        Assert.Single(FindDirectLaunches(path, extraLaunch));
    }

    private static IEnumerable<string> FindDirectLaunches(string path, string source)
    {
        var allowedGateLaunch = FindGateEnvironmentLaunch(path, source);
        foreach (Match match in DirectLaunch.Matches(source))
        {
            if (match.Index == allowedGateLaunch) continue;
            var line = 1;
            for (var index = 0; index < match.Index; index++)
            {
                if (source[index] == '\n') line++;
            }
            yield return $"{path}:{line}";
        }
    }

    private static int FindGateEnvironmentLaunch(string path, string source)
    {
        // This method must run real git under the gate's environment to test its pinned global config.
        if (!Path.GetFileName(path).Equals("HermeticVerificationEnvironmentTestsGateGitRealGit.cs", StringComparison.Ordinal))
            return -1;

        const string method = "private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(";
        var declaration = source.IndexOf(method, StringComparison.Ordinal);
        if (declaration < 0) return -1;

        var openingBrace = source.IndexOf('{', declaration + method.Length);
        if (openingBrace < 0) return -1;
        var depth = 1;
        var closingBrace = openingBrace + 1;
        for (; closingBrace < source.Length && depth > 0; closingBrace++)
        {
            if (source[closingBrace] == '{') depth++;
            else if (source[closingBrace] == '}') depth--;
        }
        if (depth != 0) return -1;

        var launches = DirectLaunch.Matches(source).Cast<Match>()
            .Where(match => match.Index > openingBrace && match.Index < closingBrace
                && match.Value.Equals("new ProcessStartInfo(\"git\"", StringComparison.Ordinal))
            .ToArray();
        return launches.Length == 1 ? launches[0].Index : -1;
    }
}
