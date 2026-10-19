using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure projections and a uniquely owned temporary backlog database.
public sealed class OwnerGoalTitleDisplayTests
{
    [Theory]
    [InlineData("Backlog slice: Make titles concise", "unknown", "\n", "Make titles concise")]
    [InlineData("Backlog slice: Backlog slice: Nested", "unknown", "\r\n", "Nested")]
    [InlineData("Backlog slice: HIGH: Fix the lane", "unknown", "\n", "HIGH: Fix the lane")]
    [InlineData("Backlog slice: High priority: Fix it", "unknown", "\r\n", "High priority: Fix it")]
    [InlineData("Backlog slice: Precise title", "precise", "\r\n", "Precise title")]
    [InlineData("Backlog slice: Conductor: Implement: Keep it", "unknown", "\n", "Conductor: Implement: Keep it")]
    [InlineData("Backlog slice:  Backlog slice: Nested", "unknown", "\n", "Nested")]
    [InlineData("Backlog slice: # Keep this hash", "unknown", "\n", "# Keep this hash")]
    public void Display_GeneratedBlock_RemovesOnlyRepeatedGeneratedPrefix(
        string title, string confidence, string newline, string expected)
    {
        var objective = title + newline + newline + "Body" + newline +
            "  Target files/scopes:  " + newline + "  Scope confidence: " + confidence + "  ";

        Assert.Equal(expected, OwnerGoalTitle.Display(objective));
        Assert.Equal(expected, OwnerGoalTitle.Full(objective));
        Assert.Equal(expected, OwnerGoalTitle.From(objective));
    }

    [Theory]
    [InlineData("Backlog slice: Fix it", "Backlog slice: Fix it")]
    [InlineData("Backlog slice: Fix it\nTarget files/scopes:\n\nScope confidence: unknown", "Backlog slice: Fix it")]
    [InlineData("Backlog slice: Fix it\nScope confidence: unknown", "Backlog slice: Fix it")]
    [InlineData("Backlog slice: Fix it\nTarget files/scopes:\nScope confidence: UNKNOWN", "Backlog slice: Fix it")]
    [InlineData("Backlog slice: \nTarget files/scopes:\nScope confidence: unknown", "Backlog slice:")]
    [InlineData("backlog slice: Keep case\nTarget files/scopes:\nScope confidence: unknown", "backlog slice: Keep case")]
    [InlineData("Backlog slice: Backlog slice: \nTarget files/scopes:\nScope confidence: unknown", "Backlog slice:")]
    public void Display_MissingSignalOrEmptyHeading_PreservesAuthoredWords(string objective, string expected)
    {
        Assert.Equal(expected, OwnerGoalTitle.Display(objective));
    }

    [Theory]
    [InlineData("# Meaningful title\nBody")]
    [InlineData("Owner console: keep this colon")]
    [InlineData("GOAL: x")]
    [InlineData("Implement: Critical correctness repair: y")]
    [InlineData("Priority: z")]
    [InlineData("\n\nTitle after blanks")]
    [InlineData("\r\n# CRLF title\r\nBody")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Display_UngeneratedCorpus_EqualsPreviousFullProjection(string? objective)
    {
        var expected = Baseline(objective);
        Assert.Equal(expected, OwnerGoalTitle.Display(objective));
        Assert.Equal(expected, OwnerGoalTitle.Full(objective));
        Assert.Equal(expected, OwnerGoalTitle.From(objective));
    }

    [Fact]
    public async Task Display_PlannerRoundTrip_PreservesStoredHeadingAndObjectivePrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "owner-title-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "backlog.db");
            var item = await new BacklogStore(path).AddAsync("Make titles concise", "Keep every meaningful word.");
            var planned = Assert.Single(BacklogIntakePlanner.Build(path, item.Id, 2).Items);

            Assert.Equal(item.Id, planned.Id);
            Assert.Equal(planned.Heading, OwnerGoalTitle.Display(planned.SuggestedObjective));
            using var reader = new StringReader(planned.SuggestedObjective);
            Assert.Equal("Backlog slice: " + planned.Heading, reader.ReadLine());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Baseline(string? objective) => (objective ?? "").Split(['\r', '\n'])
        .Select(line => line.Trim().TrimStart('#').Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
}
