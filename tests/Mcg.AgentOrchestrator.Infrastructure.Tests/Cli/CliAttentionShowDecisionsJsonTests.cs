using System.Globalization;
using System.Text.Json;
using Xunit;

// Parallel-safe: fixture-owned stores and AsyncLocal console capture, with no global mutations.
public sealed class CliAttentionShowDecisionsJsonTests
{
    [Fact]
    public async Task Unscoped_MixedQuestions_MatchesConsoleFieldsOrderAndHiddenCounts()
    {
        using var fixture = new CliAttentionShowDecisionsFixture();
        await fixture.SeedAsync();
        var console = await fixture.ConsoleModelAsync();
        Assert.Equal(new[] { fixture.HumanId, fixture.ClarificationId }, console.Decisions.Select(row => row.Id));

        using var document = JsonDocument.Parse(fixture.Run(["attention", "show", "--json"]));

        Assert.Equal("mcg.attention-show", document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var payload = document.RootElement.GetProperty("payload");
        var entries = payload.GetProperty("decisions").EnumerateArray().ToArray();
        Assert.Equal(console.Decisions.Select(row => row.Id), entries.Select(row => row.GetProperty("itemId").GetString()));
        Assert.Equal(2, entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            var expected = console.Decisions[index];
            var actual = entries[index];
            Assert.Equal(new[] { "kind", "goalPrefix", "itemId", "raisedAt", "text" },
                actual.EnumerateObject().Select(property => property.Name));
            Assert.Equal(expected.Kind.ToString(), actual.GetProperty("kind").GetString());
            Assert.Equal(expected.GoalPrefix, actual.GetProperty("goalPrefix").GetString());
            Assert.Equal(expected.FullText, actual.GetProperty("text").GetString());
            Assert.NotNull(expected.RaisedAt);
            Assert.Equal(expected.RaisedAt.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                actual.GetProperty("raisedAt").GetString());
        }
        var reason = Assert.Single(payload.GetProperty("hiddenByReason").EnumerateObject());
        Assert.Equal("non-blocking-kind", reason.Name);
        Assert.Equal(1, reason.Value.GetInt32());
        Assert.Equal(1, console.Status.HiddenQuestions);
        Assert.DoesNotContain(fixture.HiddenId, payload.GetProperty("decisions").GetRawText(), StringComparison.Ordinal);
        // Hidden decisions remain in the existing waits section.
        Assert.Contains(payload.GetProperty("waits").EnumerateArray(),
            wait => wait.GetProperty("id").GetString() == fixture.HiddenId);
    }

    [Fact]
    public async Task Unscoped_EmptyWorkspace_EmitsEmptyArrayAndObject()
    {
        using var fixture = new CliAttentionShowDecisionsFixture();
        await fixture.SeedAsync(empty: true);

        using var document = JsonDocument.Parse(fixture.Run(["attention", "show", "--json"]));

        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal("[]", payload.GetProperty("decisions").GetRawText());
        Assert.Equal("{}", payload.GetProperty("hiddenByReason").GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scoped_ShowForms_PreservePayloadWithoutDecisionKeys(bool explicitGoal)
    {
        using var fixture = new CliAttentionShowDecisionsFixture();
        await fixture.SeedAsync();
        var prefix = (await fixture.ConsoleModelAsync()).Decisions[0].GoalPrefix;
        var args = explicitGoal ? new[] { "attention", "show", "--goal", prefix, "--json" }
            : ["attention", "show", prefix, "--json"];

        using var document = JsonDocument.Parse(fixture.Run(args));

        var payload = document.RootElement.GetProperty("payload");
        Assert.False(payload.TryGetProperty("decisions", out _));
        Assert.False(payload.TryGetProperty("hiddenByReason", out _));
        Assert.Equal(fixture.HumanId, Assert.Single(payload.GetProperty("waits").EnumerateArray())
            .GetProperty("id").GetString());
    }
}
