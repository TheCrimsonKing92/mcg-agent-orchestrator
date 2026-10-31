using Mcg.AgentOrchestrator.App.OwnerConsole;
using Xunit;

// Parallel-safe: fixture-owned stores and AsyncLocal console capture, with no global mutations.
public sealed class CliAttentionShowDecisionsTextTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unscoped_MixedQuestions_MatchesConsoleOrderAndCountsHidden(bool readOnly)
    {
        using var fixture = new CliAttentionShowDecisionsFixture();
        await fixture.SeedAsync();
        var console = await fixture.ConsoleModelAsync();
        Assert.Equal(new[] { fixture.HumanId, fixture.ClarificationId }, console.Decisions.Select(row => row.Id));
        Assert.Equal(new[] { OwnerQuestionKind.HumanInput, OwnerQuestionKind.Clarification },
            console.Decisions.Select(row => row.Kind));
        Assert.True(console.Decisions[1].RaisedAt < console.Decisions[0].RaisedAt);
        Assert.Equal(1, console.Status.HiddenQuestions);

        var output = fixture.Run(["attention", "show"], readOnly);

        Assert.StartsWith("Decisions (2):" + Environment.NewLine, output, StringComparison.Ordinal);
        const string hidden = "Hidden decisions: 1 (non-blocking-kind=1)";
        var hiddenIndex = output.IndexOf(hidden, StringComparison.Ordinal);
        Assert.True(hiddenIndex > 0, output);
        var decisions = output[..hiddenIndex];
        var rows = decisions.Split('\n').Where(line => line.StartsWith("  [", StringComparison.Ordinal)).ToArray();
        Assert.Equal(console.Decisions.Length, rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var expected = console.Decisions[index];
            Assert.StartsWith($"  [{expected.Kind}] {expected.GoalPrefix} item={expected.Id} age=",
                rows[index], StringComparison.Ordinal);
            Assert.Matches(@"age=\S+: ", rows[index]);
            Assert.EndsWith(": " + expected.FullText, rows[index].TrimEnd('\r'), StringComparison.Ordinal);
        }
        Assert.DoesNotContain(fixture.HiddenId, decisions, StringComparison.Ordinal);
        Assert.DoesNotContain("Already answered?", decisions, StringComparison.Ordinal);
        Assert.Contains("Human waits", output[(hiddenIndex + hidden.Length)..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unscoped_EmptyWorkspace_PrintsExplicitEmptyAndZeroHidden(bool readOnly)
    {
        using var fixture = new CliAttentionShowDecisionsFixture();
        await fixture.SeedAsync(empty: true);
        Assert.Empty((await fixture.ConsoleModelAsync()).Decisions);

        var output = fixture.Run(["attention", "show"], readOnly);

        Assert.StartsWith(string.Join(Environment.NewLine,
            "Decisions (0):", "  none", "Hidden decisions: 0", ""), output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scoped_ShowForms_DoNotAddDecisions(bool explicitGoal)
    {
        using var fixture = new CliAttentionShowDecisionsFixture();
        await fixture.SeedAsync();
        var prefix = (await fixture.ConsoleModelAsync()).Decisions[0].GoalPrefix;
        var args = explicitGoal ? new[] { "attention", "show", "--goal", prefix } : ["attention", "show", prefix];

        foreach (var readOnly in new[] { true, false })
        {
            var output = fixture.Run(args, readOnly);
            Assert.Contains(fixture.HumanId[..8], output, StringComparison.Ordinal);
            Assert.DoesNotContain("Decisions (", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Hidden decisions:", output, StringComparison.Ordinal);
        }
    }
}
