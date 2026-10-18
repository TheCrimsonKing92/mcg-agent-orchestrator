using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: pure completion over immutable registry rows and private synthetic input.
public sealed class OwnerConsoleCommandCompletionTests
{
    [Fact]
    public void Complete_EveryRegistryLiteralPrefix_IncludesItsRow()
    {
        foreach (var row in OwnerConsoleBindings.Commands)
        {
            var literal = row.Usage.Split(" <", 2, StringSplitOptions.None)[0];
            var result = OwnerConsoleCommandCompletion.Complete(literal[..^1], OwnerConsoleBindings.Commands, 120);
            Assert.Contains(row, result.Candidates);
        }
    }

    [Fact]
    public void Complete_ExtraRegistryRow_CompletesAndShowsItsHelp()
    {
        var extra = OwnerConsoleBindings.Commands.First() with
        { Command = "zz-test", Usage = "zz-test <arg>", CommandHelp = "Synthetic registry help." };
        var result = OwnerConsoleCommandCompletion.Complete("zz", OwnerConsoleBindings.Commands.Append(extra), 120);
        Assert.Equal("zz-test ", result.Text);
        Assert.Equal(extra, Assert.Single(result.Candidates));
        Assert.Equal("zz-test <arg>: Synthetic registry help.", result.Hint);
    }

    [Fact]
    public void Complete_UppercaseAmbiguousInput_KeepsTextAndMatchesInRegistryOrder()
    {
        var result = OwnerConsoleCommandCompletion.Complete("CONDUCTOR ST", OwnerConsoleBindings.Commands, 120);
        Assert.Equal("CONDUCTOR ST", result.Text);
        Assert.Equal(new[] { "conductor start", "conductor stop", "conductor status" }, result.Candidates.Select(row => row.Usage));
        Assert.Equal("matches (3): conductor start | conductor stop | conductor status", result.Hint);
    }

    [Theory]
    [InlineData("goal 2222")]
    [InlineData("GOAL  2222")]
    [InlineData("goal ")]
    public void Complete_ArgumentInput_PreservesOriginalAndShowsCommandHelp(string input)
    {
        var result = OwnerConsoleCommandCompletion.Complete(input, OwnerConsoleBindings.Commands, 120);
        Assert.Equal(input, result.Text);
        Assert.Equal("goal", Assert.Single(result.Candidates).Command);
        Assert.Equal("goal <id-prefix>: Open live goal detail, landing status and actions.", result.Hint);
    }

    [Theory]
    [InlineData("  :  CONDUCTOR   stat", ":conductor status")]
    [InlineData("con", "conductor st")]
    [InlineData("conductor  ", "conductor st")]
    [InlineData(":go", ":goal ")]
    public void Complete_NormalizedPrefix_WritesCanonicalCompletion(string input, string expected)
    {
        Assert.Equal(expected, OwnerConsoleCommandCompletion.Complete(input, OwnerConsoleBindings.Commands, 120).Text);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(120)]
    public void Complete_ListHints_FitWidthAndCountEveryOmittedEntry(int width)
    {
        foreach (var input in new[] { "", "c", "frob" })
        {
            var result = OwnerConsoleCommandCompletion.Complete(input, OwnerConsoleBindings.Commands, width);
            Assert.True(result.Hint.Length <= width, result.Hint);
            var rows = input == "frob" ? OwnerConsoleBindings.Commands.ToArray() : result.Candidates;
            var entries = rows.Select(row => row.Key is { } key ? $"[{key}] {row.Usage}" : row.Usage).ToArray();
            var shown = entries.Count(entry => result.Hint.Contains(entry, StringComparison.Ordinal));
            var suffix = Regex.Match(result.Hint, @"\+(\d+) more$");
            if (shown < rows.Count)
            {
                Assert.True(suffix.Success, result.Hint);
                Assert.Equal(rows.Count - shown, int.Parse(suffix.Groups[1].Value));
            }
            else Assert.False(suffix.Success);
        }
    }

    [Fact]
    public void Complete_LongHelp_TruncatesWithEllipsisAndHonorsMinimumWidth()
    {
        foreach (var width in new[] { 0, 20, 40 })
        {
            var result = OwnerConsoleCommandCompletion.Complete("go", OwnerConsoleBindings.Commands, width);
            Assert.Equal(Math.Max(20, width), result.Hint.Length);
            Assert.EndsWith("...", result.Hint);
        }
    }

    [Fact]
    public void Complete_UnknownNormalizedInput_PreservesOriginalAndListsRegistry()
    {
        var result = OwnerConsoleCommandCompletion.Complete(" :  frob  xyz ", OwnerConsoleBindings.Commands, 1000);
        Assert.Equal(" :  frob  xyz ", result.Text);
        Assert.Empty(result.Candidates);
        Assert.Equal("no command starts with \"frob xyz \"; commands: " + OwnerConsoleBindings.CommandPrompt, result.Hint);
    }
}
