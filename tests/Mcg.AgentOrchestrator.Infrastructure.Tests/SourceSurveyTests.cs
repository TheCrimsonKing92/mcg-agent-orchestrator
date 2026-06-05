using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class SourceSurveyTests
{
    [Xunit.Fact(DisplayName = "SourceSurvey_excludes_generated_scratch_and_prototype_state")]
    public void SourceSurveyExcludesGeneratedScratchAndPrototypeState()
    {
        var root = CreateTempDirectory();
        Write(root, "src/App.cs");
        Write(root, "tests/AppTests.cs");
        Write(root, "src/bin/Debug/generated.cs");
        Write(root, "src/obj/Debug/generated.cs");
        Write(root, ".scratch/browser-profile/file.json");
        Write(root, ".orchestrator-prototype/workspace/.orchestrator/state.json");

        var report = SourceSurvey.Build(root);

        Assert.True(report.Files.Contains("src/App.cs"));
        Assert.True(report.Files.Contains("tests/AppTests.cs"));
        Assert.False(report.Files.Any(file => file.Contains("/bin/", StringComparison.OrdinalIgnoreCase)));
        Assert.False(report.Files.Any(file => file.Contains("/obj/", StringComparison.OrdinalIgnoreCase)));
        Assert.False(report.Files.Any(file => file.StartsWith(".scratch/", StringComparison.OrdinalIgnoreCase)));
        Assert.False(report.Files.Any(file => file.StartsWith(".orchestrator-prototype/", StringComparison.OrdinalIgnoreCase)));
        Assert.True(report.ExcludedDirectoryNames.Contains("bin"));
        Assert.True(report.ExcludedDirectoryNames.Contains("obj"));
        Assert.True(report.ExcludedDirectoryNames.Contains(".scratch"));
        Assert.Contains(report.RecommendedCommand, text => text.Contains("!**/.scratch/**", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "SourceSurvey_limits_returned_files_but_reports_total_matches")]
    public void SourceSurveyLimitsReturnedFilesButReportsTotalMatches()
    {
        var root = CreateTempDirectory();
        Write(root, "a.txt");
        Write(root, "b.txt");
        Write(root, "c.txt");

        var report = SourceSurvey.Build(root, maxFiles: 2);

        Assert.Equal(2, report.ReturnedFiles);
        Assert.Equal(3, report.TotalMatchedFiles);
        Assert.Equal(2, report.Files.Count);
    }

    private static void Write(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");
    }
}
