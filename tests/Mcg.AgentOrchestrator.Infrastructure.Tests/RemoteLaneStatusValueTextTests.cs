using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneStatusValueTextTests
{
    [Xunit.Theory]
    [Xunit.InlineData(null, null)]
    [Xunit.InlineData("", "")]
    [Xunit.InlineData("AZaz09._ -", "AZaz09._ -")]
    [Xunit.InlineData("a/b\\c\"d\r\n\t\0", "a?b?c?d????")]
    [Xunit.InlineData("éΩ中", "???")]
    [Xunit.InlineData("😀", "??")]
    public void SanitizesOnlyOutsideAsciiAllowList(string? input, string? expected)
    {
        Assert.Equal(expected, RemoteLaneStatusValueText.Sanitize(input));
    }

    [Xunit.Theory]
    [Xunit.InlineData(80, "")]
    [Xunit.InlineData(81, "...")]
    public void TruncationStartsAfterEightyCharacters(int length, string suffix)
    {
        Assert.Equal(new string('a', 80) + suffix,
            RemoteLaneStatusValueText.Sanitize(new string('a', length)));
    }
}
