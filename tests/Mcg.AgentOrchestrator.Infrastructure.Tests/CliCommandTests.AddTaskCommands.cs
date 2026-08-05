using Mcg.AgentOrchestrator.App.Cli;

public sealed class CliCommandTestsAddTaskCommands : CliCommandTestBase
{
    [Xunit.Fact]
    public void NormalizeArgs_TextFileBeforeRole_PreservesExactArgv()
    {
        var args = new[]
        {
            "add-task",
            "Tester",
            "--text-file",
            "task.md",
            "--before-role",
            "Reviewer"
        };

        var normalized = CliArgumentParser.NormalizeArgs(args);

        Xunit.Assert.Equal(args, normalized);
    }

    [Xunit.Fact]
    public void SplitCommand_QuotedTextFileBeforeRole_PreservesPath()
    {
        var parts = CliArgumentParser.SplitCommand(
            "add-task Tester --text-file \"C:\\task briefs\\verify.md\" --before-role Reviewer");

        Xunit.Assert.Equal(
            ["add-task", "Tester", "--text-file", "C:\\task briefs\\verify.md", "--before-role", "Reviewer"],
            parts);
    }

    [Xunit.Fact]
    public void NormalizeArgs_BeforeRoleBeforeTextFile_PreservesInverseOrder()
    {
        var args = new[]
        {
            "add-task",
            "Tester",
            "--before-role",
            "Reviewer",
            "--text-file",
            "task.md"
        };

        var normalized = CliArgumentParser.NormalizeArgs(args);

        Xunit.Assert.Equal(args, normalized);
    }

    [Xunit.Fact]
    public void SplitCommand_InlineDescriptionBeforeRole_PreservesText()
    {
        var parts = CliArgumentParser.SplitCommand(
            "add-task Tester Preserve the inline description --before-role Reviewer");

        Xunit.Assert.Equal(
            ["add-task", "Tester", "Preserve the inline description", "--before-role", "Reviewer"],
            parts);
    }
}
