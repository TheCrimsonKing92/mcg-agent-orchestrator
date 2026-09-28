using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SubscriptionHarnessContextFloorBaselineTests
{
    [Fact]
    public void MarkdownStatesMeasuredBaselineAndDeniesMaterialReduction()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var directory = Path.Combine(root, "docs", "measurements");
        var markdown = File.ReadAllText(Path.Combine(directory, "subscription-harness-context-floor-baseline.md"));
        using var baseline = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(directory, "de762740-subscription-harness-context-floor.json")));
        var medians = baseline.RootElement.GetProperty("codexCalibration").GetProperty("medians");
        Assert.Equal(82560, medians.GetProperty("baselineInputTokens").GetInt32());
        Assert.Equal(75682, medians.GetProperty("candidateInputTokens").GetInt32());
        foreach (var figure in new[] { "82560", "75682", "-8.33", "23552", "28834", "+22.43" })
            Assert.Contains(figure, markdown, StringComparison.Ordinal);
        Assert.Contains("No material reduction was achieved.", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("A material reduction was achieved.", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("context-usage", markdown, StringComparison.Ordinal);
    }
}
