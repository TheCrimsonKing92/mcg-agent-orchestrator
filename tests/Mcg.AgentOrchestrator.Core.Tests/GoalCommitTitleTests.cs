using Mcg.AgentOrchestrator.Core;

public sealed class GoalCommitTitleTests
{
    [Xunit.Theory]
    [Xunit.InlineData("# Readable commit subjects\nmore lines", "Readable commit subjects")]
    [Xunit.InlineData("\n  \nSecond line\nThird line", "Second line")]
    [Xunit.InlineData("A  \t  spaced   title", "A spaced title")]
    [Xunit.InlineData("", "Task purpose")]
    [Xunit.InlineData("  \n \t  ", "Task purpose")]
    public void ResolvesFirstReadableLineOrTaskPurpose(string objective, string expected) =>
        Xunit.Assert.Equal(expected, GoalCommitTitle.Resolve(objective, "Task purpose"));
}
