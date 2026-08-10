using Mcg.AgentOrchestrator.App.Cli;

public sealed class CliArgumentNormalizationTests
{
    [Xunit.Fact]
    public void TextFileCommands_SpacedPath_RemainsOneArgument()
    {
        const string path = @"C:\operator records\ruling.md";

        Xunit.Assert.Equal(
            ["note", "abc123ef", "1", "--text-file", path, "--gate-deliverable", "console"],
            CliArgumentParser.NormalizeArgs(
                ["note", "abc123ef", "1", "--text-file", path, "--gate-deliverable", "console"]));
        Xunit.Assert.Equal(
            ["answer", "deadbeef", "--text-file", path, "--gate-deliverable", "console"],
            CliArgumentParser.NormalizeArgs(
                ["answer", "deadbeef", "--text-file", path, "--gate-deliverable", "console"]));
        Xunit.Assert.Equal(
            ["supersede", "abc123ef", "deadbeef", "--text-file", path],
            CliArgumentParser.NormalizeArgs(
                ["supersede", "abc123ef", "deadbeef", "--text-file", path]));
        Xunit.Assert.Equal(
            ["gate-satisfied", "deadbeef", "console", "--text-file", path],
            CliArgumentParser.NormalizeArgs(
                ["gate-satisfied", "deadbeef", "console", "--text-file", path]));
        Xunit.Assert.Equal(
            ["progress", "abc123ef", "1", "running", "--text-file", path],
            CliArgumentParser.NormalizeArgs(
                ["progress", "abc123ef", "1", "running", "--text-file", path]));
    }

    [Xunit.Theory]
    [Xunit.InlineData("progress", "failed")]
    [Xunit.InlineData("retry", null)]
    [Xunit.InlineData("verify-manual", "passed")]
    public void GoalScopedMutations_TrailingGoalWithTextFile_IsCanonicalized(
        string command,
        string? status)
    {
        const string path = @"C:\operator records\ruling.md";
        var input = status is null
            ? new[] { command, "1", "--goal", "38d0e2e9", "--text-file", path }
            : new[] { command, "1", status, "--goal", "38d0e2e9", "--text-file", path };
        var expected = status is null
            ? new[] { command, "--goal", "38d0e2e9", "1", "--text-file", path }
            : new[] { command, "--goal", "38d0e2e9", "1", status, "--text-file", path };

        Xunit.Assert.Equal(expected, CliArgumentParser.NormalizeArgs(input));
    }

    [Xunit.Theory]
    [Xunit.InlineData(
        new[] { "progress", "1", "failed", "operator note", "--goal", "38d0e2e9" },
        new[] { "progress", "--goal", "38d0e2e9", "1", "failed", "operator note" })]
    [Xunit.InlineData(
        new[] { "retry", "1", "operator note", "--goal", "38d0e2e9" },
        new[] { "retry", "--goal", "38d0e2e9", "1", "operator note" })]
    [Xunit.InlineData(
        new[] { "verify-manual", "1", "passed", "operator note", "--goal", "38d0e2e9" },
        new[] { "verify-manual", "--goal", "38d0e2e9", "1", "passed", "operator note" })]
    public void GoalScopedMutations_TrailingGoalWithInlineText_IsCanonicalized(
        string[] input,
        string[] expected)
    {
        Xunit.Assert.Equal(expected, CliArgumentParser.NormalizeArgs(input));
    }

    [Xunit.Theory]
    [Xunit.InlineData("progress 1 failed operator note --goal 38d0e2e9", new[] { "progress", "--goal", "38d0e2e9", "1", "failed", "operator note" })]
    [Xunit.InlineData("retry 1 operator note --goal 38d0e2e9", new[] { "retry", "--goal", "38d0e2e9", "1", "operator note" })]
    [Xunit.InlineData("verify-manual 1 passed operator note --goal 38d0e2e9", new[] { "verify-manual", "--goal", "38d0e2e9", "1", "passed", "operator note" })]
    [Xunit.InlineData("note 1 operator note --goal 38d0e2e9", new[] { "note", "--goal", "38d0e2e9", "1", "operator note" })]
    public void InteractiveGoalScopedMutations_FreeTextBeforeTrailingGoal_IsCanonicalized(
        string input,
        string[] expected)
    {
        Xunit.Assert.Equal(expected, CliArgumentParser.SplitCommand(input));
    }

    [Xunit.Fact]
    public void OneShotNote_FreeTextBeforeTrailingGoal_IsCanonicalized()
    {
        Xunit.Assert.Equal(
            ["note", "--goal", "38d0e2e9", "1", "operator note"],
            CliArgumentParser.NormalizeArgs(["note", "1", "operator", "note", "--goal", "38d0e2e9"]));
    }

    [Xunit.Theory]
    [Xunit.InlineData(new[] { "progress", "1", "failed", "--goal" }, "--goal requires")]
    [Xunit.InlineData(new[] { "progress", "--goal", "abc12345", "1", "failed", "note", "--goal", "def67890" }, "only once")]
    [Xunit.InlineData(new[] { "progress", "--goal=abc12345", "1", "failed", "note" }, "not supported")]
    public void GoalScopedMutations_MalformedGoalOption_FailsClosed(string[] args, string expected)
    {
        var exception = Xunit.Assert.Throws<ArgumentException>(() => CliArgumentParser.NormalizeArgs(args));

        Xunit.Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
