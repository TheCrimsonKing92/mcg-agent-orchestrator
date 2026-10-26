using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: these facts inspect arguments and source without starting a build.
public sealed class WorkerBuildAnalyzerPropertiesTests
{
    [Xunit.Fact]
    public void BuildStartInfo_AppendsAnalyzerPropertiesBeforeIsolatedArguments()
    {
        const string root = @"C:\repo\project";
        const string project = @"src\Fixture.csproj";
        var info = WorkerBuildRequestHost.BuildStartInfo(root, project, "Debug",
            ["--artifacts-path", @"C:\repo\artifacts"], Path.GetTempPath());
        var arguments = info.ArgumentList.ToArray();
        string[] original = ["build", Path.Combine(root, project), "--nologo", "--configuration", "Debug",
            "--verbosity", "minimal", "-maxcpucount:1", "-p:BuildInParallel=false",
            "-p:UseSharedCompilation=false", "-tl:off"];

        Assert.Equal(original, arguments.Take(original.Length));
        var isolatedIndex = Array.IndexOf(arguments, "--artifacts-path");
        Assert.True(isolatedIndex >= original.Length);
        foreach (var property in AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties)
        {
            Assert.Single(arguments, argument => argument == property);
            Assert.InRange(Array.IndexOf(arguments, property), original.Length, isolatedIndex - 1);
        }
    }

    [Xunit.Fact]
    public void DirectScript_IsolatedArgumentsContainQuotedAnalyzerProperties()
    {
        var script = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(),
            "scripts", "Invoke-WorkerBuildCheck.ps1"));
        const string opener = "$isolatedArguments = @(";
        var start = script.IndexOf(opener, StringComparison.Ordinal);
        Assert.True(start >= 0, "The direct build isolated-arguments block is missing.");
        var lines = script[(start + opener.Length)..].Split('\n');
        var end = Array.FindIndex(lines, line => line.Trim() == ")");
        Assert.True(end >= 0, "The isolated-arguments block is not closed.");
        var block = string.Join('\n', lines.Take(end));

        foreach (var property in AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties)
            Assert.Contains($"\"{property}\"", block, StringComparison.Ordinal);
    }
}
