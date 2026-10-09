using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerSourceSurveyInventoryTests
{
    [Fact]
    public void GeneratedProfilesDoNotDisplaceUsefulSourceSections()
    {
        var root = CreateTempDirectory();
        Write(root, "AGENTS.md", "FeatureService repository guidance");
        Write(root, ".agents/skills/example/SKILL.md", "FeatureService worker skill");
        Write(root, "src/Feature/FeatureService.cs", "public sealed class FeatureService { public void Execute() { } }");
        Write(root, "src/Feature/NewSeam.cs", "internal sealed class NewSeam { }");
        Write(root, "tests/Feature.Tests/FeatureServiceTests.cs", "public sealed class FeatureServiceTests { }");
        Write(root, ".mcg-sandbox/grok-home/bundled/skills/pptx/templates/deck.cs", "public sealed class GeneratedDeck { }");
        Write(root, ".mcg-sandbox/codex-home/plugins/cache/tool.cs", "public sealed class GeneratedTool { }");
        Write(root, ".orchestrator/state.db", "runtime");
        var task = new TaskSpec(
            new TaskId("developer"),
            "Update FeatureService through the NewSeam.",
            AgentRole.Developer,
            "Run FeatureServiceTests.");
        var goal = new AgentOrchestratorKernel().CreateGoal(
            new GoalId("source-survey"),
            "Improve FeatureService source survey coverage.",
            [task]);

        var survey = new WorkerSourceSurvey().BuildSourceSurvey(goal, task, root);

        Assert.Contains("Inventory source: filesystem-fallback", survey, StringComparison.Ordinal);
        Assert.Contains("Traversal: complete", survey, StringComparison.Ordinal);
        Assert.DoesNotContain(".mcg-sandbox", survey, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".orchestrator/state.db", survey, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/Feature/FeatureService.cs", survey, StringComparison.Ordinal);
        Assert.Contains("src/Feature/NewSeam.cs", survey, StringComparison.Ordinal);
        Assert.Contains("tests/Feature.Tests/FeatureServiceTests.cs", survey, StringComparison.Ordinal);
        AssertSectionContains(survey, "Directory Counts", "src/Feature: 2");
        AssertSectionContains(survey, "Task-Term Matches", "src/Feature/FeatureService.cs");
        AssertSectionContains(survey, "Source Sample", "src/Feature/NewSeam.cs");
        AssertSectionContains(survey, "Likely Tests", "tests/Feature.Tests/FeatureServiceTests.cs");
        AssertSectionContains(survey, "Public API Symbols", "public class FeatureService");
        AssertSectionContains(survey, "Call-Site Hints", "src/Feature/FeatureService.cs");
        AssertSectionContains(survey, "Ownership Hints", ".agents: worker skills and guidance");
        AssertSectionContains(survey, "Ownership Hints", "AGENTS.md: repository-local operator and worker instructions");
    }

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void AssertSectionContains(string survey, string heading, string expected)
    {
        var marker = $"## {heading}";
        var start = survey.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Survey section '{heading}' was not rendered.");
        var end = survey.IndexOf("## ", start + marker.Length, StringComparison.Ordinal);
        var section = end < 0 ? survey[start..] : survey[start..end];
        Assert.Contains(expected, section, StringComparison.Ordinal);
    }
}
