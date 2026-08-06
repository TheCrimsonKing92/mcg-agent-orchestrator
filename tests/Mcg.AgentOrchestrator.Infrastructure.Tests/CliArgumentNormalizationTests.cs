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
}
