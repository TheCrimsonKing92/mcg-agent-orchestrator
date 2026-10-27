using System.Text.RegularExpressions;
using Xunit;

public sealed class BuildServerShutdownSourceGuardTests
{
    private static readonly Regex ShutdownArguments = new(
        "\\bArgumentList\\s*\\.\\s*Add\\s*\\(\\s*\"build-server\"\\s*\\)" +
        "|\\{\\s*\"build-server\"\\s*,\\s*\"shutdown\"\\s*\\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void ShutdownProcessArgumentsAppearOnlyInAttributedCompilerLockRecovery()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var callers = SourceFiles(root)
            .Where(path => IsShutdown(snapshot.GetText(path)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildServerRecovery.cs"],
            callers);
    }

    [Theory]
    [InlineData("startInfo.ArgumentList.Add(\"build-server\");")]
    [InlineData("startInfo.ArgumentList . Add (\n \"build-server\"\n );")]
    [InlineData("ArgumentList = { \"build-server\", \"shutdown\" }")]
    [InlineData("ArgumentList = {\n \"build-server\",\n \"shutdown\"\n }")]
    public void DetectorRecognizesBothArgumentForms(string source) =>
        Assert.True(ShutdownArguments.IsMatch(source));

    [Theory]
    [InlineData("Advice = \"Run dotnet build-server shutdown\";")]
    [InlineData("value.Contains(\"build-server\", StringComparison.Ordinal)")]
    public void DetectorIgnoresAdviceAndCommandText(string source) =>
        Assert.False(ShutdownArguments.IsMatch(source));

    internal static IEnumerable<string> SourceFiles(string root) =>
        RepositorySourceSnapshotCache.Get(root).EnumerateFiles(Path.Combine(root, "src"))
            .Where(path => !SourceGuardFileFilter.BuildOutputAndScratch.Excludes(path));

    internal static bool IsShutdown(string source) => ShutdownArguments.IsMatch(source);
}
